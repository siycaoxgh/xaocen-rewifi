using System.Diagnostics;
using System.Text.RegularExpressions;

namespace XAOCEN.ReWiFi;

public sealed class StartupManager
{
    private const string TaskName = "XAOCEN ReWiFi";
    private const string LegacyTaskName = "XAOCEN WiFiFix";

    public bool IsEnabled()
    {
        return QueryTaskState() == StartupTaskState.Enabled;
    }

    public bool SetEnabled(bool enabled)
    {
        if (enabled)
        {
            return CreateTask();
        }

        var state = QueryTaskState();
        return state switch
        {
            StartupTaskState.Missing => true,
            StartupTaskState.Enabled or StartupTaskState.Disabled => DeleteTask(),
            _ => false
        };
    }

    public bool EnsureCurrentExecutable()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var configuredPath = GetConfiguredExecutablePath();
        if (QueryTaskState() != StartupTaskState.Enabled ||
            !string.Equals(configuredPath, executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return CreateTask();
        }

        // Power-setting hardening is best effort. A failure here must not make the
        // UI claim that an otherwise valid logon task has been disabled.
        ConfigurePowerSettings();
        return true;
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

        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
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

    private static StartupTaskState QueryTaskState()
    {
        const string script = "$task = @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -eq 'XAOCEN ReWiFi' } | Select-Object -First 1); if ($task.Count -eq 0) { 'MISSING' } elseif ([int]$task[0].State -eq 1) { 'DISABLED' } else { 'ENABLED' }";
        var result = RunPowerShell(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]);
        if (result.ExitCode != 0)
        {
            AppLogger.Warning($"查询开机任务状态失败：{result.Output}");
            return StartupTaskState.Unknown;
        }

        var marker = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim();
        return marker switch
        {
            "ENABLED" => StartupTaskState.Enabled,
            "DISABLED" => StartupTaskState.Disabled,
            "MISSING" => StartupTaskState.Missing,
            _ => StartupTaskState.Unknown
        };
    }

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
        => RunProcess("schtasks.exe", arguments);

    private static (int ExitCode, string Output) RunPowerShell(IReadOnlyList<string> arguments)
        => RunProcess("powershell.exe", arguments);

    private static (int ExitCode, string Output) RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
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
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(milliseconds: 1_000);
                }
                catch
                {
                    // The command may have exited while the timeout was handled.
                }

                AppLogger.Warning($"系统命令执行超过 10 秒，已终止：{fileName}");
                return (-1, "命令执行超时。");
            }

            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
            var output = outputTask.Result + errorTask.Result;
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"系统命令执行失败：{fileName}", ex);
            return (-1, string.Empty);
        }
    }

    private enum StartupTaskState
    {
        Unknown,
        Missing,
        Disabled,
        Enabled
    }
}
