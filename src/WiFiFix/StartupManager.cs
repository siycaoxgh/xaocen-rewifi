using System.Diagnostics;
using System.Text.RegularExpressions;

namespace XAOCEN.ReWiFi;

public sealed class StartupManager
{
    private const string TaskName = "XAOCEN ReWiFi";
    private const string LegacyTaskName = "XAOCEN WiFiFix";

    public bool IsEnabled()
    {
        var result = RunSchtasks(["/Query", "/TN", TaskName, "/FO", "CSV", "/NH"]);
        return result.ExitCode == 0;
    }

    public bool SetEnabled(bool enabled)
    {
        return enabled ? CreateTask() : DeleteTask();
    }

    public bool EnsureCurrentExecutable()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var configuredPath = GetConfiguredExecutablePath();
        if (!IsEnabled() || !string.Equals(configuredPath, executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return CreateTask();
        }

        return ConfigurePowerSettings();
    }

    public bool RefreshSettings() => ConfigurePowerSettings();

    public string? GetConfiguredExecutablePath()
    {
        var result = RunSchtasks(["/Query", "/TN", TaskName, "/XML"]);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var match = Regex.Match(result.Output, "<Command>(.*?)</Command>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value.Trim();
    }

    private static bool CreateTask()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var result = RunSchtasks([
            "/Create", "/TN", TaskName, "/TR", $"\"{executablePath}\"", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"]);
        if (result.ExitCode != 0)
        {
            return false;
        }

        DeleteTask(LegacyTaskName);

        // The default Task Scheduler settings can stop this tray process when a laptop
        // switches to battery power. Override those defaults for a background utility.
        ConfigurePowerSettings();
        AppLogger.Info($"已创建/更新开机任务，任务动作路径={executablePath}");
        return true;
    }

    private static bool DeleteTask() => DeleteTask(TaskName);

    private static bool DeleteTask(string taskName) => RunSchtasks(["/Delete", "/TN", taskName, "/F"]).ExitCode == 0;

    private static bool ConfigurePowerSettings()
    {
        var command = $"$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable; Set-ScheduledTask -TaskName '{TaskName}' -Settings $settings";
        var result = RunPowerShell(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command]);
        if (result.ExitCode != 0)
        {
            AppLogger.Warning($"更新任务电源设置失败：{result.Output}");
        }

        return result.ExitCode == 0;
    }

    private static (int ExitCode, string Output) RunSchtasks(IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        }
        ;

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start()) return (-1, string.Empty);
            var output = process.StandardOutput.ReadToEnd();
            output += process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }

    private static (int ExitCode, string Output) RunPowerShell(IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start()) return (-1, string.Empty);
            var output = process.StandardOutput.ReadToEnd();
            output += process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }
}
