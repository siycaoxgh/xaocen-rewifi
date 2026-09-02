using System.Diagnostics;
using System.Drawing;

namespace XAOCEN.ReWiFi;

public sealed class SettingsForm : Form
{
    private readonly TextBox _ssidTextBox = new();
    private readonly TextBox _adapterTextBox = new();
    private readonly NumericUpDown _failureDelay = new();
    private readonly NumericUpDown _cooldown = new();
    private readonly CheckBox _autoRecovery = new();
    private readonly CheckBox _autoStart = new();
    private readonly AppConfig _initialConfig;
    private readonly WifiController _wifiController;
    private readonly AccountSessionManager _accountSessionManager;
    private readonly OfflineAuthorizationManager _offlineAuthorizationManager;
    private readonly Action<AppConfig> _saveAction;
    private readonly Label _accountSummary = CreateSummaryLabel();
    private readonly Label _authorizationSummary = CreateSummaryLabel();

    internal SettingsForm(
        AppConfig config,
        WifiController wifiController,
        AccountSessionManager accountSessionManager,
        OfflineAuthorizationManager offlineAuthorizationManager,
        Action<AppConfig> saveAction)
    {
        _initialConfig = config.Clone();
        _wifiController = wifiController;
        _accountSessionManager = accountSessionManager;
        _offlineAuthorizationManager = offlineAuthorizationManager;
        _saveAction = saveAction;
        Text = $"{ProductInfo.ProductName} 设置";
        BackColor = Color.FromArgb(247, 249, 251);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        Icon = LoadApplicationIcon();
        ClientSize = new Size(820, 720);
        _ssidTextBox.Text = _initialConfig.TargetSsid;
        _adapterTextBox.Text = _initialConfig.AdapterName;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 198));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        var overview = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        overview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        overview.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        overview.Controls.Add(new PictureBox
        {
            Image = LoadProductImage(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 16, 0)
        }, 0, 0);
        overview.SetRowSpan(overview.Controls[0], 2);
        overview.Controls.Add(new Label
        {
            Text = $"{ProductInfo.ProductName}\n{ProductInfo.ChineseName}工具   ·   Version: v{AppLogger.Version}\n持续监测 Windows Wi-Fi，网络异常时自动恢复。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 2, 0, 0),
            Font = new Font(Font.FontFamily, 10F)
        }, 1, 0);
        var overviewActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        var githubLink = new LinkLabel
        {
            Text = "GitHub 更新页面",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 6, 0, 0),
            LinkColor = Color.FromArgb(8, 123, 192),
            ActiveLinkColor = Color.FromArgb(20, 196, 211),
            VisitedLinkColor = Color.FromArgb(8, 123, 192)
        };
        githubLink.LinkClicked += (_, _) => OpenExternalUrl(ProductInfo.GitHubUrl);
        overviewActions.Controls.Add(githubLink);
        overview.Controls.Add(overviewActions, 1, 1);
        layout.Controls.Add(overview, 0, 0);

        var accountGroup = CreateAccountGroup();
        layout.Controls.Add(accountGroup, 0, 1);

        var settingsGroup = new GroupBox
        {
            Text = "网络恢复设置",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(23, 33, 43)
        };
        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 4,
            Margin = new Padding(0)
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        settings.Controls.Add(new Label { Text = "目标 Wi-Fi：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _ssidTextBox.Dock = DockStyle.Fill;
        settings.Controls.Add(_ssidTextBox, 1, 0);
        settings.SetColumnSpan(_ssidTextBox, 3);

        settings.Controls.Add(new Label { Text = "Wi-Fi 网卡：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        var detectPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        _adapterTextBox.Width = 92;
        detectPanel.Controls.Add(_adapterTextBox);
        var detectButton = new Button { Text = "自动识别当前连接", AutoSize = true, Height = 24 };
        detectButton.Click += async (_, _) => await DetectCurrentWifiAsync(detectButton);
        detectPanel.Controls.Add(detectButton);
        settings.Controls.Add(detectPanel, 1, 1);
        settings.SetColumnSpan(detectPanel, 3);

        ConfigureNumber(_failureDelay, _initialConfig.FailureDelaySeconds);
        ConfigureNumber(_cooldown, _initialConfig.CooldownSeconds);
        settings.Controls.Add(new Label { Text = "异常等待：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        settings.Controls.Add(CreateValuePanel(_failureDelay, "秒"), 1, 2);
        settings.Controls.Add(new Label { Text = "恢复冷却：", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 2);
        settings.Controls.Add(CreateValuePanel(_cooldown, "秒"), 3, 2);

        _autoRecovery.Text = "自动恢复";
        _autoRecovery.Checked = _initialConfig.AutoRecovery;
        _autoStart.Text = "开机启动";
        _autoStart.Checked = _initialConfig.AutoStart;
        settings.Controls.Add(_autoRecovery, 0, 3);
        settings.SetColumnSpan(_autoRecovery, 2);
        settings.Controls.Add(_autoStart, 2, 3);
        settings.SetColumnSpan(_autoStart, 2);

        settingsGroup.Controls.Add(settings);
        layout.Controls.Add(settingsGroup, 0, 2);

        layout.Controls.Add(CreateHelpGroup(), 0, 3);

        var buttonBar = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var saveButton = new Button
        {
            Text = "保存",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, 189, 74),
            ForeColor = Color.FromArgb(23, 33, 43)
        };
        saveButton.FlatAppearance.BorderColor = Color.FromArgb(246, 169, 28);
        saveButton.Click += SaveButtonOnClick;
        buttonBar.Controls.Add(saveButton);
        buttonBar.Resize += (_, _) => saveButton.Left = buttonBar.ClientSize.Width - saveButton.Width;
        layout.Controls.Add(buttonBar, 0, 4);

        Controls.Add(layout);
        AcceptButton = saveButton;
        Shown += (_, _) =>
        {
            _ssidTextBox.Focus();
            RefreshAuthorizationSummary();
        };
        _accountSessionManager.StatusChanged += AccountStateChanged;
        _offlineAuthorizationManager.StateChanged += AuthorizationStateChanged;
        FormClosed += (_, _) =>
        {
            _accountSessionManager.StatusChanged -= AccountStateChanged;
            _offlineAuthorizationManager.StateChanged -= AuthorizationStateChanged;
        };
    }

    private GroupBox CreateAccountGroup()
    {
        var group = new GroupBox
        {
            Text = "账号与离线授权",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(255, 252, 242),
            ForeColor = Color.FromArgb(23, 33, 43)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        table.Controls.Add(CreateSummaryPanel("XAOCEN Account 会话", _accountSummary), 0, 0);
        table.Controls.Add(CreateSummaryPanel("离线授权状态", _authorizationSummary), 1, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 2, 0, 0)
        };
        var centerButton = CreateSectionButton("打开授权中心", Color.FromArgb(255, 189, 74));
        centerButton.Click += (_, _) => OpenAuthorizationCenter();
        actions.Controls.Add(centerButton);
        table.Controls.Add(actions, 0, 1);
        table.SetColumnSpan(actions, 2);
        group.Controls.Add(table);
        return group;
    }

    private GroupBox CreateHelpGroup()
    {
        var group = new GroupBox
        {
            Text = "帮助与文档",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(23, 33, 43)
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0)
        };
        var onlineDocsButton = CreateSectionButton("在线文档");
        onlineDocsButton.Click += (_, _) => DocumentationRouter.OpenOnline();
        var localDocsButton = CreateSectionButton("本地文档");
        localDocsButton.Click += (_, _) => DocumentationRouter.OpenLocal();
        var supportButton = CreateSectionButton("报告问题");
        supportButton.Click += (_, _) =>
        {
            if (!FeedbackService.TryOpenSupport())
                MessageBox.Show(this, "无法打开问题反馈页面。", ProductInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        var logButton = CreateSectionButton("运行日志");
        logButton.Click += (_, _) => OpenRunLog();
        actions.Controls.Add(onlineDocsButton);
        actions.Controls.Add(localDocsButton);
        actions.Controls.Add(supportButton);
        actions.Controls.Add(logButton);
        group.Controls.Add(actions);
        return group;
    }

    private async Task DetectCurrentWifiAsync(Button button)
    {
        button.Enabled = false;
        var originalText = button.Text;
        button.Text = "识别中…";
        try
        {
            var connection = await _wifiController.DetectCurrentWifiAsync();
            if (connection is null)
            {
                MessageBox.Show(this, "当前没有检测到已连接的 Wi-Fi。请先连接 Wi-Fi 后再试。", ProductInfo.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _ssidTextBox.Text = connection.Value.Ssid;
            _adapterTextBox.Text = connection.Value.AdapterName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"识别当前 Wi-Fi 失败：{ex.Message}", ProductInfo.ProductName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            button.Text = originalText;
            button.Enabled = true;
        }
    }

    private void SaveButtonOnClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_ssidTextBox.Text) || string.IsNullOrWhiteSpace(_adapterTextBox.Text))
        {
            MessageBox.Show(this, "目标 Wi-Fi 和网卡名称不能为空。", ProductInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var config = _initialConfig.Clone();
        config.TargetSsid = _ssidTextBox.Text.Trim();
        config.AdapterName = _adapterTextBox.Text.Trim();
        config.FailureDelaySeconds = (int)_failureDelay.Value;
        config.CooldownSeconds = (int)_cooldown.Value;
        config.AutoRecovery = _autoRecovery.Checked;
        config.AutoStart = _autoStart.Checked;

        try
        {
            _saveAction(config);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存设置失败：{ex.Message}", ProductInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenAuthorizationCenter()
    {
        using var form = new AuthorizationForm(_accountSessionManager, _offlineAuthorizationManager);
        form.ShowDialog(this);
        RefreshAuthorizationSummary();
    }

    private void RefreshAuthorizationSummary()
    {
        var session = _accountSessionManager.CurrentSession;
        if (session is null)
        {
            _accountSummary.Text = "状态：未连接\n未保存或未恢复账号会话。\n离线授权仍可独立使用。";
            _accountSummary.ForeColor = Color.FromArgb(80, 92, 104);
        }
        else
        {
            var accountName = !string.IsNullOrWhiteSpace(session.Value.Profile.Email)
                ? session.Value.Profile.Email
                : !string.IsNullOrWhiteSpace(session.Value.Profile.DisplayName)
                    ? session.Value.Profile.DisplayName
                    : "账号信息已验证";
            var entitlement = _accountSessionManager.CurrentEntitlements?.FirstOrDefault(item =>
                string.Equals(item.ProductId, ProductInfo.AccountProductId, StringComparison.OrdinalIgnoreCase));
            var entitlementText = entitlement is null || string.IsNullOrWhiteSpace(entitlement.Value.ProductId)
                ? "ReWiFi 权益：尚未获取"
                : $"ReWiFi 权益：{FormatEntitlementStatus(entitlement.Value.Status)} · {FormatEntitlementExpiry(entitlement.Value.ExpiresAt)}";
            _accountSummary.Text = $"状态：已连接\n{accountName}\n{entitlementText}";
            _accountSummary.ForeColor = Color.DarkGreen;
        }

        var onlineCheck = _offlineAuthorizationManager.LastDecision?.Mode switch
        {
            OfflineAuthorizationMode.Online => "已通过 Account 联网核验",
            OfflineAuthorizationMode.Offline => "网络不可用，已本地校验",
            OfflineAuthorizationMode.Rejected => "校验未通过",
            OfflineAuthorizationMode.NoLicense => "未配置授权文件",
            _ => "尚未执行本次联网检查"
        };
        var offline = _offlineAuthorizationManager.ValidateStoredLicense();
        if (!offline.IsValid || offline.Payload is null)
        {
            _authorizationSummary.Text = $"状态：{onlineCheck}\n本地授权：{offline.Message}";
            _authorizationSummary.ForeColor = Color.Firebrick;
            return;
        }

        var payload = offline.Payload;
        var licenseType = FormatLicenseType(payload.LicenseType);
        var entitlementExpiry = payload.LicenseType?.Equals("perpetual", StringComparison.OrdinalIgnoreCase) == true
            ? "永久"
            : payload.EntitlementExpiresAt is null ? "未单独设置" : FormatAuthorizationDate(payload.EntitlementExpiresAt);
        _authorizationSummary.Text = string.Join(Environment.NewLine,
            $"状态：{onlineCheck}",
            $"本地授权：有效 · {licenseType}",
            $"权益到期：{entitlementExpiry}",
            $"下次联网检查：{FormatAuthorizationDate(payload.NextOnlineCheckAt)}",
            $"最迟重新授权：{FormatAuthorizationDate(payload.HardReauthorizeAt)}");
        _authorizationSummary.ForeColor = _offlineAuthorizationManager.LastDecision?.Mode == OfflineAuthorizationMode.Rejected
            ? Color.Firebrick
            : Color.DarkGreen;
    }

    private void AccountStateChanged(string _) => RefreshAuthorizationSummaryOnUiThread();

    private void AuthorizationStateChanged() => RefreshAuthorizationSummaryOnUiThread();

    private void RefreshAuthorizationSummaryOnUiThread()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(RefreshAuthorizationSummary));
        }
        catch (InvalidOperationException)
        {
            // The settings form can close while a background authorization check completes.
        }
    }

    private static string FormatEntitlementStatus(string? status) => status switch
    {
        "active" => "有效",
        "expired" => "已过期",
        "revoked" => "已撤销",
        _ => string.IsNullOrWhiteSpace(status) ? "未知" : status
    };

    private static string FormatEntitlementExpiry(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "永久" : FormatAuthorizationDate(value);

    private static string FormatLicenseType(string? value) => value switch
    {
        "perpetual" => "永久授权",
        "subscription" => "订阅授权",
        "trial" => "试用授权",
        "public_test" => "公开测试授权",
        _ => string.IsNullOrWhiteSpace(value) ? "未知类型" : value
    };

    private static string FormatAuthorizationDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : string.IsNullOrWhiteSpace(value) ? "未知" : value;
    }

    private static string FormatAuthorizationDate(DateTimeOffset? value)
    {
        return value is { } timestamp
            ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "未设置";
    }

    private void AddField(TableLayoutPanel layout, int row, string labelText, Control control, string suffix = "")
    {
        layout.Controls.Add(new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        if (string.IsNullOrEmpty(suffix))
        {
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(control, 1, row);
            return;
        }

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        control.Width = 85;
        panel.Controls.Add(control);
        panel.Controls.Add(new Label { Text = suffix, AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        layout.Controls.Add(panel, 1, row);
    }

    private static Control CreateValuePanel(Control control, string suffix)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        panel.Controls.Add(control);
        panel.Controls.Add(new Label { Text = suffix, AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        return panel;
    }

    private static void ConfigureNumber(NumericUpDown control, int value)
    {
        control.Minimum = 1;
        control.Maximum = 86400;
        control.Value = Math.Clamp(value, 1, 86400);
        control.Width = 85;
    }

    private static Label CreateSummaryLabel() => new()
    {
        Text = "未读取",
        AutoSize = false,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(80, 92, 104),
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
        Margin = new Padding(0, 5, 0, 5)
    };

    private static Control CreateSummaryPanel(string title, Label summary)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 8, 4),
            Padding = new Padding(10, 6, 10, 6)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(23, 33, 43),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        panel.Controls.Add(summary, 0, 1);
        return panel;
    }

    private static Button CreateSectionButton(string text, Color? backColor = null)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor ?? Color.FromArgb(245, 247, 249)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(176, 190, 200);
        return button;
    }

    private static Image LoadProductImage()
    {
        var resourceName = typeof(SettingsForm).Assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Assets.xaocen-rewifi_rounded.png", StringComparison.OrdinalIgnoreCase));
        if (resourceName is not null)
        {
            using var stream = typeof(SettingsForm).Assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                using var image = Image.FromStream(stream);
                return new Bitmap(image);
            }
        }

        return new Bitmap(1, 1);
    }

    private static Icon LoadApplicationIcon()
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

    private static void OpenRunLog()
    {
        try
        {
            AppLogger.Info("用户从设置页面打开运行日志。");
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLogger.LogPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("从设置页面打开运行日志失败", ex);
        }
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error("打开外部链接失败", ex);
        }
    }
}
