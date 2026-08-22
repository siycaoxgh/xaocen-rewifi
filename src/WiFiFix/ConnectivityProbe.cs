using System.Net;

namespace XAOCEN.ReWiFi;

public sealed class ConnectivityProbe
{
    private static readonly Uri[] ProbeUris =
    [
        new Uri("https://www.google.com/generate_204"),
        new Uri("https://www.baidu.com/")
    ];

    private readonly IReadOnlyList<Uri> _probeUris;
    private readonly Lazy<HttpClient> _httpClient;

    public ConnectivityProbe() : this(CreateDirectHandler(), ProbeUris)
    {
    }

    internal ConnectivityProbe(HttpMessageHandler handler, IReadOnlyList<Uri> probeUris)
    {
        _probeUris = probeUris;
        _httpClient = new Lazy<HttpClient>(() => new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
    }

    public async Task<ConnectivityProbeResult> CheckAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 15));
        var checks = _probeUris.Select(uri => CheckOneAsync(uri, timeout, cancellationToken));
        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        return new ConnectivityProbeResult(results);
    }

    private async Task<ConnectivityProbeItem> CheckOneAsync(
        Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            using var response = await _httpClient.Value.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            var reachable = (int)response.StatusCode >= 200 &&
                            (int)response.StatusCode < 500 &&
                            response.StatusCode != System.Net.HttpStatusCode.ProxyAuthenticationRequired;
            AppLogger.Info($"连通性探测：{uri.Host}; reachable={reachable}; status={(int)response.StatusCode}");
            return new ConnectivityProbeItem(uri.Host, reachable, (int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warning($"连通性探测超时：{uri.Host}");
            return new ConnectivityProbeItem(uri.Host, false, null, "timeout");
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"连通性探测失败：{uri.Host}; {ex.Message}");
            return new ConnectivityProbeItem(uri.Host, false, null, ex.Message);
        }
    }

    internal static HttpClientHandler CreateDirectHandler() => new()
    {
        UseProxy = false,
        Proxy = null,
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All
    };
}

public readonly record struct ConnectivityProbeItem(
    string Host,
    bool Reachable,
    int? StatusCode,
    string? Error);

public sealed class ConnectivityProbeResult
{
    public ConnectivityProbeResult(IReadOnlyList<ConnectivityProbeItem> results)
    {
        Results = results;
    }

    public IReadOnlyList<ConnectivityProbeItem> Results { get; }
    public int ReachableCount => Results.Count(item => item.Reachable);
    public bool AllReachable => Results.Count > 0 && ReachableCount == Results.Count;
    public bool NoneReachable => Results.Count > 0 && ReachableCount == 0;
}
