using System.Text.Json;

namespace XAOCEN.ReWiFi;

public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string TargetSsid { get; set; } = "XIAOCEN_HOME";
    public string AdapterName { get; set; } = "Wi-Fi";
    public int FailureDelaySeconds { get; set; } = 5;
    public int CooldownSeconds { get; set; } = 30;
    public bool AutoRecovery { get; set; } = true;
    public bool AutoStart { get; set; } = true;
    public bool EnableConnectivityProbe { get; set; } = true;
    public int ConnectivityProbeTimeoutSeconds { get; set; } = 4;
    public bool WelcomeShown { get; set; }
    public string? LegalNoticeVersion { get; set; }

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XAOCEN ReWiFi");

    private static string LegacyDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XAOCEN WiFiFix");

    private static string LegacyFilePath => Path.Combine(LegacyDirectoryPath, "config.json");

    public static string FilePath => Path.Combine(DirectoryPath, "config.json");

    public static AppConfig Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
                if (config is not null)
                {
                    config.Normalize();
                    if (!string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        Save(config);
                    }
                    return config;
                }
            }
        }
        catch
        {
            // A malformed local config should not prevent the tray app from starting.
        }

        var defaults = new AppConfig();
        Save(defaults);
        return defaults;
    }

    public static void Save(AppConfig config)
    {
        config.Normalize();
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, JsonOptions));
    }

    public AppConfig Clone() => new()
    {
        TargetSsid = TargetSsid,
        AdapterName = AdapterName,
        FailureDelaySeconds = FailureDelaySeconds,
        CooldownSeconds = CooldownSeconds,
        AutoRecovery = AutoRecovery,
        AutoStart = AutoStart,
        EnableConnectivityProbe = EnableConnectivityProbe,
        ConnectivityProbeTimeoutSeconds = ConnectivityProbeTimeoutSeconds,
        LegalNoticeVersion = LegalNoticeVersion,
        WelcomeShown = WelcomeShown
    };

    private void Normalize()
    {
        TargetSsid = (TargetSsid ?? string.Empty).Trim();
        AdapterName = (AdapterName ?? string.Empty).Trim();
        FailureDelaySeconds = Math.Clamp(FailureDelaySeconds, 1, 3600);
        CooldownSeconds = Math.Clamp(CooldownSeconds, 1, 86400);
        ConnectivityProbeTimeoutSeconds = Math.Clamp(ConnectivityProbeTimeoutSeconds, 1, 15);
    }
}
