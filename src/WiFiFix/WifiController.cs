using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace XAOCEN.ReWiFi;

public sealed class WifiController
{
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

        var ssid = FindNetshValue(result.Output, "SSID");
        if (string.IsNullOrWhiteSpace(ssid))
        {
            AppLogger.Info("自动识别 Wi-Fi：当前没有已连接的 SSID。");
            return null;
        }

        var reportedName = FindNetshValue(result.Output, "Name");
        var adapter = wirelessAdapters.FirstOrDefault(x =>
                          string.Equals(x.Name, reportedName, StringComparison.OrdinalIgnoreCase)) ??
                      wirelessAdapters[0];
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

    private static bool IsAdministrator()
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
        var result = await RunNetshAsync(["wlan", "show", "profiles"], cancellationToken).ConfigureAwait(false);
        return result.Success && result.Output.Contains(ssid, StringComparison.Ordinal);
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
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
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
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"netsh 执行异常：{commandText}", ex);
            return new CommandResult(false, string.Empty, ex.Message);
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

    private readonly record struct CommandResult(bool Success, string Output, string Error);
}

public readonly record struct WifiConnectionInfo(string Ssid, string AdapterName);

public readonly record struct RecoveryResult(bool Success, string? ErrorMessage, bool Notify)
{
    public static RecoveryResult Succeeded() => new(true, null, false);
    public static RecoveryResult Failed(string message, bool notify) => new(false, message, notify);
}
