using System.Reflection;
using System.Text;

namespace XAOCEN.ReWiFi;

public static class AppLogger
{
    private static readonly object Sync = new();
    private static readonly string LogFilePath = Path.Combine(AppConfig.DirectoryPath, "runtime.log");

    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    public static string BuildRevision =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "XAOCEN-BuildRevision", StringComparison.Ordinal))?.Value
        ?? "0";

    public static string LogPath => LogFilePath;

    public static void StartSession()
    {
        Info($"启动 v{Version}; 构建号={BuildRevision}; PID={Environment.ProcessId}; 路径={Environment.ProcessPath}");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message} | {exception}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppConfig.DirectoryPath);
                if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > 4 * 1024 * 1024)
                {
                    File.WriteAllText(LogFilePath, $"{DateTimeOffset.Now:O} [INFO] 日志超过 4 MB，已重新开始。{Environment.NewLine}", Encoding.UTF8);
                }

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [PID {Environment.ProcessId}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never terminate the tray application.
        }
    }
}
