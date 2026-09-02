using System.Threading;

namespace XAOCEN.ReWiFi;

internal static class Program
{
    // Keep the mutex stable across upgrades so an older and a newer build
    // cannot run side by side and both try to control the Wi-Fi adapter.
    private const string MutexName = "Global\\XAOCEN.ReWiFi";

    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            AppLogger.Warning("检测到已有实例，当前启动被阻止。");
            MessageBox.Show($"{ProductInfo.DisplayName} 已经在运行。", ProductInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        AppLogger.StartSession();
        var config = AppConfig.Load();
        AppLogger.Info($"配置：SSID={config.TargetSsid}; 网卡={config.AdapterName}; 异常等待={config.FailureDelaySeconds}s; 冷却={config.CooldownSeconds}s; 自动恢复={config.AutoRecovery}; 开机启动={config.AutoStart}; 连通性探测={config.EnableConnectivityProbe}; 探测超时={config.ConnectivityProbeTimeoutSeconds}s");
        var startupManager = new StartupManager();
        if (config.AutoStart)
        {
            config.AutoStart = startupManager.EnsureCurrentExecutable();
            AppConfig.Save(config);
        }
        AppLogger.Info($"启动任务状态：存在={startupManager.IsEnabled()}; 任务路径={startupManager.GetConfiguredExecutablePath()}; 当前进程路径={Environment.ProcessPath}");

        var controller = new WifiController();
        var accountClient = new AccountClient();
        var offlineAuthorizationManager = new OfflineAuthorizationManager(accountClient);
        var accountSessionManager = new AccountSessionManager(accountClient, devicePublicKeyProvider: offlineAuthorizationManager.GetDevicePublicKey);
        var watcher = new NetworkWatcher(config, controller, new ConnectivityProbe());
        var tray = new TrayManager(config);

        _ = RestoreAccountSessionAsync();
        _ = EvaluateOfflineAuthorizationAsync();

        watcher.StatusChanged += tray.SetStatus;
        watcher.NotificationRequested += tray.ShowNotification;
        tray.RecoveryRequested += () => _ = watcher.TriggerManualRecoveryAsync();
        tray.AutoRecoveryChanged += enabled =>
        {
            config.AutoRecovery = enabled;
            AppConfig.Save(config);
            AppLogger.Info($"托盘切换自动恢复：{enabled}");
            watcher.UpdateConfig(config);
        };
        tray.AutoStartChanged += enabled =>
        {
            var applied = startupManager.SetEnabled(enabled);
            config.AutoStart = enabled ? applied : !applied;
            tray.ApplyConfig(config);
            AppConfig.Save(config);
            AppLogger.Info($"托盘切换开机启动：请求={enabled}; 结果={config.AutoStart}");
        };
        tray.SettingsRequested += () =>
        {
            using var form = new SettingsForm(config, controller, accountSessionManager, offlineAuthorizationManager, updatedConfig =>
            {
                var startupApplied = startupManager.SetEnabled(updatedConfig.AutoStart);
                updatedConfig.AutoStart = updatedConfig.AutoStart ? startupApplied : !startupApplied;
                config = updatedConfig;
                AppConfig.Save(config);
                AppLogger.Info("设置窗口保存配置。");
                watcher.UpdateConfig(config);
                tray.ApplyConfig(config);
            });
            form.ShowDialog();
        };
        tray.AccountRequested += () =>
        {
            using var form = new AuthorizationForm(accountSessionManager, offlineAuthorizationManager);
            form.ShowDialog();
        };
        tray.OnlineDocumentationRequested += () => DocumentationRouter.OpenOnline();
        tray.LocalDocumentationRequested += () => DocumentationRouter.OpenLocal();
        tray.FeedbackRequested += () => FeedbackService.TryOpenSupport();
        tray.DiagnosticRequested += () =>
        {
            if (!FeedbackService.TryCopyDiagnostic(config))
            {
                MessageBox.Show("无法复制脱敏诊断信息。", ProductInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                tray.ShowNotification("脱敏诊断信息已复制，可粘贴到问题反馈中。");
            }
        };
        tray.ExitRequested += () =>
        {
            _ = ExitAsync();
        };

        if (!config.WelcomeShown && DocumentationRouter.OpenAsync().GetAwaiter().GetResult())
        {
            config.WelcomeShown = true;
            AppConfig.Save(config);
            AppLogger.Info("首次启动已打开产品文档。");
        }

        watcher.Start();
        AppLogger.Info($"日志文件：{AppLogger.LogPath}");
        Application.Run();

        async Task RestoreAccountSessionAsync()
        {
            try
            {
                await accountSessionManager.TryRestoreAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"XAOCEN Account 会话恢复失败：{ex.Message}");
            }
        }

        async Task EvaluateOfflineAuthorizationAsync()
        {
            try
            {
                var decision = await offlineAuthorizationManager.EvaluateAsync();
                if (decision.Mode != OfflineAuthorizationMode.NoLicense)
                {
                    AppLogger.Info($"离线授权检查：模式={decision.Mode}; 结果={decision.LocalResult.Status}。");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"离线授权检查失败：{ex.Message}");
            }
        }

        async Task ExitAsync()
        {
            await watcher.DisposeAsync();
            tray.Dispose();
            accountSessionManager.Dispose();
            Application.ExitThread();
        }
    }
}
