using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace XAOCEN.ReWiFi;

public sealed class WifiController
{
    private static readonly Encoding NetshEncoding = CreateNetshEncoding();

    public async Task<WifiConnectionInfo?> DetectCurrentWifiAsync(CancellationToken cancellationToken = default)
    {
        var wirelessAdapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                        x.OperationalStatus == OperationalStatus.Up)
            .ToList();
        if (wirelessAdapters.Count == 0)
        {
            AppLogger.Info("自动识别 Wi-Fi：没有找到处于连接状态的无线网卡。");
            return null;
        }

        var result = await RunNetshAsync(["wlan", "show", "interfaces"], cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            AppLogger.Warning("自动识别 Wi-Fi：netsh 查询失败。");
            return null;
        }

        var connection = FindConnectedInterface(result.Output, wirelessAdapters);
        if (connection is not null)
        {
            AppLogger.Info($"自动识别 Wi-Fi：SSID={connection.Value.Ssid}; 网卡={connection.Value.AdapterName}");
            return connection;
        }

        var ssid = FindNetshValue(result.Output, "SSID");
        if (string.IsNullOrWhiteSpace(ssid))
        {
            AppLogger.Info("自动识别 Wi-Fi：当前没有已连接的 SSID。");
            return null;
        }

        if (wirelessAdapters.Count != 1)
        {
            AppLogger.Warning("自动识别 Wi-Fi：无法从 netsh 输出匹配网卡 GUID，且存在多块已启用无线网卡。为避免选错网卡，本次不自动填写。");
            return null;
        }

        var adapter = wirelessAdapters[0];
        AppLogger.Info($"自动识别 Wi-Fi：SSID={ssid}; 网卡={adapter.Name}");
        return new WifiConnectionInfo(ssid, adapter.Name);
    }

    public async Task<RecoveryResult> RecoverAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        AppLogger.Info($"Wi-Fi 控制器开始恢复：网卡={config.AdapterName}; SSID={config.TargetSsid}");
        if (!IsAdministrator())
        {
            return RecoveryResult.Failed($"{ProductInfo.ProductName} 需要管理员权限才能重新启动 Wi-Fi 网络适配器。", notify: true);
        }

        if (!AdapterExists(config.AdapterName))
        {
            return RecoveryResult.Failed($"未找到 Wi-Fi 网络适配器：{config.AdapterName}", notify: true);
        }

        if (string.IsNullOrWhiteSpace(config.TargetSsid))
        {
            return RecoveryResult.Failed("未设置目标 Wi-Fi 名称。", notify: true);
        }

        if (!await ProfileExistsAsync(config.TargetSsid, cancellationToken).ConfigureAwait(false))
        {
            AppLogger.Warning($"Wi-Fi Profile 不存在：{config.TargetSsid}");
            return RecoveryResult.Failed(
                $"无法连接 {config.TargetSsid}。请先使用 Windows 手动连接一次该 Wi-Fi，并保存网络。", notify: true);
        }

        var adapterMayBeDisabled = false;
        try
        {
            // Mark the adapter before starting netsh: cancellation can arrive after
            // Windows applied the command but before the process result is observed.
            adapterMayBeDisabled = true;
            var disabled = await RunNetshAsync(
                ["interface", "set", "interface", $"name={config.AdapterName}", "admin=disabled"], cancellationToken)
                .ConfigureAwait(false);
            if (!disabled.Success)
            {
                return RecoveryResult.Failed($"关闭 Wi-Fi 失败：{disabled.Error}", notify: true);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            var enabled = await RunNetshAsync(
                ["interface", "set", "interface", $"name={config.AdapterName}", "admin=enabled"], cancellationToken)
                .ConfigureAwait(false);
            if (!enabled.Success)
            {
                return RecoveryResult.Failed($"开启 Wi-Fi 失败：{enabled.Error}", notify: true);
            }

            adapterMayBeDisabled = false;
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

            var connected = await RunNetshAsync(
                ["wlan", "connect", $"name={config.TargetSsid}", $"interface={config.AdapterName}"], cancellationToken)
                .ConfigureAwait(false);
            if (!connected.Success)
            {
                return RecoveryResult.Failed(
                    $"无法连接 {config.TargetSsid}。请先使用 Windows 手动连接一次该 Wi-Fi，并保存网络。", notify: true);
            }

            return RecoveryResult.Succeeded();
        }
        finally
        {
            if (adapterMayBeDisabled)
            {
                await EnsureAdapterEnabledAsync(config.AdapterName).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool AdapterExists(string adapterName) =>
        NetworkInterface.GetAllNetworkInterfaces().Any(x =>
            string.Equals(x.Name, adapterName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Description, adapterName, StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> ProfileExistsAsync(string ssid, CancellationToken cancellationToken)
    {
        var result = await RunNetshAsync(["wlan", "show", "profile", $"name={ssid}"], cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    private static async Task<CommandResult> RunNetshAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var commandText = string.Join(" ", arguments.Select(QuoteArgument));
        AppLogger.Info($"执行 netsh：{commandText}");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = NetshEncoding,
                StandardErrorEncoding = NetshEncoding
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return new CommandResult(false, string.Empty, "无法启动 netsh.exe");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            var commandError = string.IsNullOrWhiteSpace(error) ? output : error;
            AppLogger.Info($"netsh 结束：exitCode={process.ExitCode}; 结果={TrimForLog(commandError)}");
            return new CommandResult(process.ExitCode == 0, output, commandError);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The command may have exited between the cancellation and cleanup.
            }

            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"netsh 执行异常：{commandText}", ex);
            return new CommandResult(false, string.Empty, ex.Message);
        }
    }

    private static async Task EnsureAdapterEnabledAsync(string adapterName)
    {
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cleanup = await RunNetshAsync(
                ["interface", "set", "interface", $"name={adapterName}", "admin=enabled"], cleanupTimeout.Token)
                .ConfigureAwait(false);
            if (cleanup.Success)
            {
                AppLogger.Info($"恢复流程中断后的网卡启用兜底已完成：{adapterName}");
            }
            else
            {
                AppLogger.Error($"恢复流程中断后的网卡启用兜底失败：{cleanup.Error}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("恢复流程中断后的网卡启用兜底发生异常", ex);
        }
    }

    private static string QuoteArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;

    private static string TrimForLog(string value)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 240 ? compact : compact[..240] + "…";
    }

    private static string? FindNetshValue(string output, string key)
    {
        var match = Regex.Match(output, $"^\\s*{Regex.Escape(key)}\\s*:\\s*(.+?)\\s*$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static WifiConnectionInfo? FindConnectedInterface(
        string output,
        IReadOnlyList<NetworkInterface> wirelessAdapters)
    {
        Guid? currentInterfaceId = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var guidMatch = Regex.Match(
                line,
                @"(?<guid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
            if (guidMatch.Success && Guid.TryParse(guidMatch.Groups["guid"].Value, out var parsedId))
            {
                currentInterfaceId = parsedId;
                continue;
            }

            if (currentInterfaceId is null)
            {
                continue;
            }

            var ssidMatch = Regex.Match(line, @"^\s*SSID\s*:\s*(?<ssid>.+?)\s*$", RegexOptions.IgnoreCase);
            if (!ssidMatch.Success)
            {
                continue;
            }

            var adapter = wirelessAdapters.FirstOrDefault(item =>
                Guid.TryParse(item.Id, out var adapterId) && adapterId == currentInterfaceId.Value);
            if (adapter is not null)
            {
                return new WifiConnectionInfo(ssidMatch.Groups["ssid"].Value.Trim(), adapter.Name);
            }
        }

        return null;
    }

    private static Encoding CreateNetshEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.Default;
        }
    }

    private readonly record struct CommandResult(bool Success, string Output, string Error);
}

public readonly record struct WifiConnectionInfo(string Ssid, string AdapterName);

public readonly record struct RecoveryResult(bool Success, string? ErrorMessage, bool Notify)
{
    public static RecoveryResult Succeeded() => new(true, null, false);
    public static RecoveryResult Failed(string message, bool notify) => new(false, message, notify);
}
