using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace XAOCEN.ReWiFi;

internal static class FeedbackService
{
    public static bool TryOpenSupport()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProductInfo.SupportUrl)
            {
                UseShellExecute = true
            });
            AppLogger.Info("打开 XAOCEN 统一反馈页面（产品：rewifi）。");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("打开 XAOCEN 统一反馈页面失败。", ex);
            return false;
        }
    }

    public static bool TryCopyDiagnostic(AppConfig config)
    {
        try
        {
            Clipboard.SetText(DiagnosticReport.Build(config));
            AppLogger.Info("已复制脱敏诊断信息。");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("复制脱敏诊断信息失败。", ex);
            return false;
        }
    }
}

internal static class DiagnosticReport
{
    public static string Build(AppConfig config)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var builder = new StringBuilder();
        builder.AppendLine("XAOCEN ReWiFi 脱敏诊断信息");
        builder.AppendLine($"对外版本：{AppLogger.Version}");
        builder.AppendLine($"文件版本：{version}");
        builder.AppendLine($"内部构建号：{AppLogger.BuildRevision}");
        builder.AppendLine($"Windows：{Environment.OSVersion.Version}");
        builder.AppendLine($"架构：{RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"目标 Wi-Fi 已配置：{!string.IsNullOrWhiteSpace(config.TargetSsid)}");
        builder.AppendLine($"Wi-Fi 网卡已配置：{!string.IsNullOrWhiteSpace(config.AdapterName)}");
        builder.AppendLine($"异常等待：{config.FailureDelaySeconds} 秒");
        builder.AppendLine($"恢复冷却：{config.CooldownSeconds} 秒");
        builder.AppendLine($"自动恢复：{config.AutoRecovery}");
        builder.AppendLine($"开机启动：{config.AutoStart}");
        builder.AppendLine($"连通性探测：{config.EnableConnectivityProbe}");
        builder.AppendLine("\n注意：此信息未包含 SSID、网卡名称、IP、MAC、文件路径、账号信息、令牌或完整运行日志。");
        return builder.ToString();
    }
}
