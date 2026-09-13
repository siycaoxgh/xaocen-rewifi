using System.Diagnostics;

namespace XAOCEN.ReWiFi;

public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _autoRecoveryItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _recoverItem;
    private readonly ContextMenuStrip _menu;
    private readonly SynchronizationContext _uiContext;
    private readonly int _uiThreadId;
    private AppConfig _config;
    private bool _disposed;

    public TrayManager(AppConfig config)
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _config = config.Clone();
        _menu = new ContextMenuStrip();

        var title = new ToolStripMenuItem($"{ProductInfo.DisplayName} v{AppLogger.Version}") { Enabled = false };
        _statusItem = new ToolStripMenuItem("状态：网络正常") { Enabled = false };
        var versionItem = new ToolStripMenuItem($"运行版本：v{AppLogger.Version}") { Enabled = false };
        _recoverItem = new ToolStripMenuItem("立即恢复 Wi-Fi");
        _recoverItem.Click += (_, _) => RecoveryRequested?.Invoke();
        _menu.Items.AddRange([title, versionItem, _statusItem, _recoverItem, new ToolStripSeparator()]);

        _autoRecoveryItem = new ToolStripMenuItem("自动恢复") { CheckOnClick = true, Checked = _config.AutoRecovery };
        _autoRecoveryItem.Click += (_, _) =>
        {
            _config.AutoRecovery = _autoRecoveryItem.Checked;
            AutoRecoveryChanged?.Invoke(_autoRecoveryItem.Checked);
        };

        _autoStartItem = new ToolStripMenuItem("开机启动") { CheckOnClick = true, Checked = _config.AutoStart };
        _autoStartItem.Click += (_, _) => AutoStartChanged?.Invoke(_autoStartItem.Checked);

        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        var accountItem = new ToolStripMenuItem("账号中心（可选登录）");
        accountItem.Click += (_, _) => AccountRequested?.Invoke();
        var documentationItem = new ToolStripMenuItem("产品介绍与使用帮助");
        documentationItem.ToolTipText = "在线时打开官网产品页，无法联网时打开内置离线副本。";
        documentationItem.Click += (_, _) => DocumentationRequested?.Invoke();
        var documentationMenu = new ToolStripMenuItem("文档与帮助");
        documentationMenu.DropDownItems.Add(documentationItem);
        var termsItem = new ToolStripMenuItem("用户协议（在线优先）");
        termsItem.ToolTipText = "优先打开官网协议，官网暂不可用时打开内置副本。";
        termsItem.Click += async (_, _) => await DocumentationRouter.OpenLegalAsync("terms");
        var privacyItem = new ToolStripMenuItem("隐私说明（在线优先）");
        privacyItem.ToolTipText = "优先打开官网隐私说明，官网暂不可用时打开内置副本。";
        privacyItem.Click += async (_, _) => await DocumentationRouter.OpenLegalAsync("privacy");
        documentationMenu.DropDownItems.AddRange([new ToolStripSeparator(), termsItem, privacyItem]);
        var feedbackItem = new ToolStripMenuItem("报告问题");
        feedbackItem.Click += (_, _) => FeedbackRequested?.Invoke();
        var diagnosticItem = new ToolStripMenuItem("复制脱敏诊断信息");
        diagnosticItem.Click += (_, _) => DiagnosticRequested?.Invoke();
        var logItem = new ToolStripMenuItem("查看运行日志");
        logItem.Click += (_, _) => OpenLog();
        var supportMenu = new ToolStripMenuItem("帮助与反馈");
        supportMenu.DropDownItems.AddRange([feedbackItem, diagnosticItem, logItem]);
        _menu.Items.AddRange([
            _autoRecoveryItem,
            _autoStartItem,
            settingsItem,
            accountItem,
            documentationMenu,
            supportMenu,
            new ToolStripSeparator()
        ]);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateWifiIcon(),
            Text = $"{ProductInfo.ProductName} v{AppLogger.Version} - 网络正常",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    public event Action? RecoveryRequested;
    public event Action<bool>? AutoRecoveryChanged;
    public event Action<bool>? AutoStartChanged;
    public event Action? SettingsRequested;
    public event Action? AccountRequested;
    public event Action? DocumentationRequested;
    public event Action? FeedbackRequested;
    public event Action? DiagnosticRequested;
    public event Action? ExitRequested;

    public void ApplyConfig(AppConfig config)
    {
        _config = config.Clone();
        _autoRecoveryItem.Checked = _config.AutoRecovery;
        _autoStartItem.Checked = _config.AutoStart;
    }

    public void SetStatus(WatcherStatus status, string text)
    {
        RunOnUi(() =>
        {
            _statusItem.Text = $"状态：{text}";
            _notifyIcon.Text = $"{ProductInfo.ProductName} v{AppLogger.Version} - {text}";
        });
    }

    public void ShowNotification(string message)
    {
        RunOnUi(() =>
        {
            _notifyIcon.BalloonTipTitle = ProductInfo.ProductName;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(5000);
        });
    }

    internal Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUi(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    internal void PostToUi(Action action) => RunOnUi(action);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private void RunOnUi(Action action)
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            action();
            return;
        }

        _uiContext.Post(_ =>
        {
            if (!_disposed) action();
        }, null);
    }

    private static void OpenLog()
    {
        try
        {
            AppLogger.Info("用户打开运行日志。");
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLogger.LogPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("打开运行日志失败", ex);
        }
    }

    private static Icon CreateWifiIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            using var extracted = Icon.ExtractAssociatedIcon(processPath);
            if (extracted is not null)
            {
                return (Icon)extracted.Clone();
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
