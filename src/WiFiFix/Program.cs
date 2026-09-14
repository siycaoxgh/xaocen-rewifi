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
        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(false, MutexName);
        var ownsSingleInstance = TryAcquire(singleInstance, TimeSpan.Zero);
        if (!ownsSingleInstance)
        {
            var response = SingleInstanceCoordinator.NotifyExistingInstance();
            if (response != SingleInstanceResponse.RestartApproved)
            {
                if (response == SingleInstanceResponse.Unavailable)
                {
                    ShowForegroundMessage($"{ProductInfo.DisplayName} 已经在运行。\n\n如果正在运行的是旧版本，请先从系统托盘退出旧版，再重新打开新版文件。");
                }
                return;
            }

            ownsSingleInstance = TryAcquire(singleInstance, TimeSpan.FromSeconds(20));
            if (!ownsSingleInstance)
            {
                ShowForegroundMessage("旧版本未能在限定时间内退出。请从系统托盘退出旧版后，再重新打开新版文件。");
                return;
            }
        }

        try
        {
            RunApplication();
        }
        finally
        {
            if (ownsSingleInstance)
            {
                singleInstance.ReleaseMutex();
            }
        }
    }

    private static void RunApplication()
    {
        AppLogger.StartSession();
        var config = AppConfig.Load();
        AppLogger.Info($"配置：SSID={config.TargetSsid}; 网卡={config.AdapterName}; 异常等待={config.FailureDelaySeconds}s; 冷却={config.CooldownSeconds}s; 自动恢复={config.AutoRecovery}; 开机启动={config.AutoStart}; 连通性探测={config.EnableConnectivityProbe}; 探测超时={config.ConnectivityProbeTimeoutSeconds}s");
        var telemetry = new TelemetryClient();
        if (config.LegalNoticeVersion != "2026-09-13")
        {
            using var welcome = new WelcomeForm(telemetry.Consent.AllUploadsDisabled);
            if (welcome.ShowDialog() != DialogResult.OK) { telemetry.Dispose(); return; }
            var consent = telemetry.Consent;
            telemetry.SetConsent(consent.Analytics, consent.Crash, welcome.DisableStatistics);
            config.LegalNoticeVersion = "2026-09-13";
            AppConfig.Save(config);
        }
        telemetry.InstallGlobalExceptionHandlers();
        telemetry.AddSensitiveValue(config.TargetSsid);
        telemetry.AddSensitiveValue(config.AdapterName);
        telemetry.InitializeSession();
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
        using var instanceCoordinator = new SingleInstanceCoordinator();
        SettingsForm? settingsForm = null;
        AuthorizationForm? authorizationForm = null;
        var exitStarted = 0;

        _ = RestoreAccountSessionAsync();


        watcher.StatusChanged += tray.SetStatus;
        watcher.NotificationRequested += tray.ShowNotification;
        tray.RecoveryRequested += () =>
        {
            telemetry.MarkFeatureUsed("manual-recovery");
            _ = watcher.TriggerManualRecoveryAsync();
        };
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
        tray.SettingsRequested += ShowSettings;
        instanceCoordinator.LaunchRequested = request => tray.InvokeOnUiAsync(() =>
        {
            if (IsNewerBuild(request))
            {
                var samePublicVersion = CompareVersions(request.Version, AppLogger.Version) == 0;
                AppLogger.Info($"检测到新构建请求：当前构建={AppLogger.BuildRevision}; 新构建={request.BuildRevision}; 新文件={request.ExecutablePath}");
                using var dialog = new UpdateDetectedForm(AppLogger.Version, request.Version, request.ExecutablePath, samePublicVersion);
                var owner = GetVisibleApplicationForm();
                var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                return result == DialogResult.OK
                    ? SingleInstanceResponse.RestartApproved
                    : SingleInstanceResponse.Activated;
            }

            ActivateExistingWindowOrSettings();
            return SingleInstanceResponse.Activated;
        });
        instanceCoordinator.RestartApproved += () => tray.PostToUi(() => BeginExit("新版接续启动"));
        instanceCoordinator.Start();
        tray.AccountRequested += () => ShowAuthorizationCenter(null);
        tray.DocumentationRequested += () => _ = DocumentationRouter.OpenAsync();
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
            BeginExit("用户从托盘退出");
        };

        if (!config.WelcomeShown && DocumentationRouter.OpenAsync().GetAwaiter().GetResult())
        {
            config.WelcomeShown = true;
            AppConfig.Save(config);
            AppLogger.Info("首次启动已打开产品文档。");
        }

        watcher.Start();
        telemetry.RecordCoreActivation(System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? "online" : "offline");
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

        async Task ExitAsync()
        {
            try
            {
                await watcher.DisposeAsync();
                accountSessionManager.Dispose();
                using var telemetryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await telemetry.ShutdownAsync(telemetryTimeout.Token);
                AppLogger.Info("退出前清理完成。");
            }
            catch (Exception ex)
            {
                AppLogger.Error("退出清理发生异常", ex);
            }
            finally
            {
                telemetry.Dispose();
                tray.Dispose();
                Application.Exit();
                Application.ExitThread();
            }
        }

        void BeginExit(string reason)
        {
            if (Interlocked.Exchange(ref exitStarted, 1) != 0)
            {
                return;
            }

            AppLogger.Info($"开始退出：{reason}。");
            _ = ForceExitIfNeededAsync();
            _ = ExitAsync();
        }

        static async Task ForceExitIfNeededAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            AppLogger.Warning("正常退出超过 10 秒，执行进程退出兜底。");
            Environment.Exit(0);
        }

        void ShowSettings()
        {
            if (settingsForm is not null && !settingsForm.IsDisposed)
            {
                ActivateForm(settingsForm);
                return;
            }

            telemetry.MarkFeatureUsed("settings.opened");
            settingsForm = new SettingsForm(config, controller, accountSessionManager, telemetry, ShowAuthorizationCenter, updatedConfig =>
            {
                var startupApplied = startupManager.SetEnabled(updatedConfig.AutoStart);
                updatedConfig.AutoStart = updatedConfig.AutoStart ? startupApplied : !startupApplied;
                config = updatedConfig;
                AppConfig.Save(config);
                AppLogger.Info("设置窗口保存配置。");
                watcher.UpdateConfig(config);
                tray.ApplyConfig(config);
            });
            settingsForm.FormClosed += (_, _) => settingsForm = null;
            settingsForm.Show();
            ActivateForm(settingsForm);
        }

        void ShowAuthorizationCenter(IWin32Window? owner)
        {
            if (authorizationForm is not null && !authorizationForm.IsDisposed)
            {
                ActivateForm(authorizationForm);
                return;
            }

            telemetry.MarkFeatureUsed("account.opened");
            authorizationForm = new AuthorizationForm(accountSessionManager);
            authorizationForm.FormClosed += (_, _) => authorizationForm = null;
            if (owner is null)
            {
                authorizationForm.Show();
            }
            else
            {
                authorizationForm.Show(owner);
            }
            ActivateForm(authorizationForm);
        }

        void ActivateExistingWindowOrSettings()
        {
            var form = GetVisibleApplicationForm();
            if (form is null)
            {
                ShowSettings();
                return;
            }

            ActivateForm(form);
        }
    }

    private static bool TryAcquire(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static int CompareVersions(string left, string right)
    {
        return ParseVersion(left).CompareTo(ParseVersion(right));
    }

    private static bool IsNewerBuild(SingleInstanceLaunchRequest request)
    {
        var publicComparison = CompareVersions(request.Version, AppLogger.Version);
        if (publicComparison > 0)
        {
            return true;
        }

        if (publicComparison < 0 || string.IsNullOrWhiteSpace(request.BuildRevision))
        {
            return false;
        }

        if (!long.TryParse(request.BuildRevision, out var incomingRevision))
        {
            return !string.Equals(request.BuildRevision, AppLogger.BuildRevision, StringComparison.Ordinal);
        }

        return !long.TryParse(AppLogger.BuildRevision, out var currentRevision) || incomingRevision > currentRevision;
    }

    private static Version ParseVersion(string value)
    {
        var normalized = value.Split(['-', '+'], 2)[0];
        return Version.TryParse(normalized, out var version) ? version : new Version(0, 0);
    }

    private static Form? GetVisibleApplicationForm()
    {
        return Application.OpenForms.Cast<Form>().LastOrDefault(form => form.Visible && !form.IsDisposed);
    }

    private static void ActivateForm(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.Show();
        form.BringToFront();
        form.Activate();
        form.TopMost = true;
        form.TopMost = false;
    }

    private static void ShowForegroundMessage(string message)
    {
        using var owner = new Form
        {
            ShowInTaskbar = false,
            TopMost = true,
            Opacity = 0,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(1, 1)
        };
        owner.Show();
        MessageBox.Show(owner, message, ProductInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
