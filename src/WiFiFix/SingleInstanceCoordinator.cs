using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace XAOCEN.ReWiFi;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string PipeName = "XAOCEN.ReWiFi.SingleInstance.v1";
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listenerTask;

    public Func<SingleInstanceLaunchRequest, Task<SingleInstanceResponse>>? LaunchRequested { get; set; }

    public event Action? RestartApproved;

    public void Start()
    {
        _listenerTask ??= Task.Run(ListenAsync);
    }

    public static SingleInstanceResponse NotifyExistingInstance()
    {
        var request = new SingleInstanceLaunchRequest
        {
            Command = "activate",
            Version = AppLogger.Version,
            BuildRevision = AppLogger.BuildRevision,
            ExecutablePath = Environment.ProcessPath ?? string.Empty
        };

        for (var attempt = 0; attempt < 6; attempt++)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(300);
            }
            catch when (attempt < 5)
            {
                pipe?.Dispose();
                Thread.Sleep(200);
                continue;
            }
            catch
            {
                pipe?.Dispose();
                break;
            }

            using (pipe)
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
            using (var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            {
                try
                {
                    writer.WriteLine(JsonSerializer.Serialize(request));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var responseLine = reader.ReadLineAsync(timeout.Token).GetAwaiter().GetResult();
                    return Enum.TryParse<SingleInstanceResponse>(responseLine, ignoreCase: true, out var response)
                        ? response
                        : SingleInstanceResponse.Unavailable;
                }
                catch
                {
                    return SingleInstanceResponse.Unavailable;
                }
            }
        }

        return SingleInstanceResponse.Unavailable;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // The listener is best-effort during process shutdown.
        }

        _cancellation.Dispose();
    }

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cancellation.Token);
                await ProcessRequestAsync(pipe);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"单实例通信异常：{ex.GetType().Name}");
            }
        }
    }

    private async Task ProcessRequestAsync(Stream pipe)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync(_cancellation.Token);
        var request = ParseRequest(line);
        var handler = LaunchRequested;
        var response = request is null || handler is null
            ? SingleInstanceResponse.Unavailable
            : await handler(request);

        await writer.WriteLineAsync(response.ToString());
        await writer.FlushAsync(_cancellation.Token);

        if (response == SingleInstanceResponse.RestartApproved)
        {
            RestartApproved?.Invoke();
        }
    }

    private static SingleInstanceLaunchRequest? ParseRequest(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 40_000)
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<SingleInstanceLaunchRequest>(line);
            if (request is null ||
                !string.Equals(request.Command, "activate", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(request.Version) || request.Version.Length > 64 ||
                request.BuildRevision.Length > 64 ||
                request.ExecutablePath.Length > 32_768)
            {
                return null;
            }

            return request;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class SingleInstanceLaunchRequest
{
    public string Command { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string BuildRevision { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
}

internal enum SingleInstanceResponse
{
    Unavailable,
    Activated,
    RestartApproved
}
