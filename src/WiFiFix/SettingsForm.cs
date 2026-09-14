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
    private readonly TelemetryClient _telemetry;
    private readonly Action<IWin32Window?> _openAccountCenter;
    private readonly Action<AppConfig> _saveAction;
    private readonly Label _accountSummary = CreateSummaryLabel();
    private readonly Label _authorizationSummary = CreateSummaryLabel();
    private readonly CheckBox _analyticsConsent = new();
    private readonly CheckBox _crashConsent = new();
    private readonly CheckBox _disableAllTelemetry = new();
    private readonly Label _telemetryStatus = CreateSummaryLabel();

    internal SettingsForm(
        AppConfig config,
        WifiController wifiController,
        AccountSessionManager accountSessionManager,
        TelemetryClient telemetry,
        Action<IWin32Window?> openAccountCenter,
        Action<AppConfig> saveAction)
    {
        _initialConfig = config.Clone();
        _wifiController = wifiController;
        _accountSessionManager = accountSessionManager;
        _telemetry = telemetry;
        _openAccountCenter = openAccountCenter;
        _saveAction = saveAction;
        Text = $"{ProductInfo.ProductName} 设置";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(247, 249, 251);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        Icon = LoadApplicationIcon();
        MinimumSize = new Size(720, 560);
        ClientSize = new Size(1040, 900);
        _ssidTextBox.Text = _initialConfig.TargetSsid;
        _adapterTextBox.Text = _initialConfig.AdapterName;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Padding = new Padding(14, 10, 14, 14),
            ColumnCount = 1,
            RowCount = 6,
            AutoScroll = false
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 196));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 184));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var overview = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        overview.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        overview.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        overview.Controls.Add(new PictureBox
        {
            Image = LoadProductImage(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 12, 2)
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
            Margin = new Padding(8, 3, 0, 0),
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
        layout.Controls.Add(CreateTelemetryGroup(), 0, 4);

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
        saveButton.MinimumSize = new Size(120, 32);
        saveButton.FlatAppearance.BorderColor = Color.FromArgb(246, 169, 28);
        saveButton.Click += SaveButtonOnClick;
        buttonBar.Controls.Add(saveButton);
        buttonBar.Resize += (_, _) =>
        {
            saveButton.Left = buttonBar.ClientSize.Width - saveButton.Width;
            saveButton.Top = Math.Max(0, (buttonBar.ClientSize.Height - saveButton.Height) / 2);
        };
        layout.Controls.Add(buttonBar, 0, 5);

        Controls.Add(ResponsiveWindow.CreateScrollableViewport(layout, 868));
        AcceptButton = saveButton;
        Shown += (_, _) =>
        {
            ResponsiveWindow.FitToWorkingArea(this);
            _ssidTextBox.Focus();
            RefreshAuthorizationSummary();
        };
        _accountSessionManager.StatusChanged += AccountStateChanged;
        _telemetry.StatusChanged += TelemetryStateChanged;
        FormClosed += (_, _) =>
        {
            _accountSessionManager.StatusChanged -= AccountStateChanged;
            _telemetry.StatusChanged -= TelemetryStateChanged;
        };
    }

    private GroupBox CreateAccountGroup()
    {
        var group = new GroupBox
        {
            Text = "永久免费 · 可选登录",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(255, 252, 242),
            ForeColor = Color.FromArgb(23, 33, 43)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var centerButton = CreateSectionButton("打开账号中心", Color.FromArgb(255, 189, 74));
        centerButton.MinimumSize = new Size(140, 32);
        centerButton.Click += (_, _) => OpenAuthorizationCenter();
        table.Controls.Add(CreateSummaryPanel("XAOCEN Account 会话", _accountSummary, centerButton), 0, 0);
        table.Controls.Add(CreateSummaryPanel("免费使用说明", _authorizationSummary), 1, 0);
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
        var documentationButton = CreateSectionButton("产品介绍与使用帮助");
        documentationButton.Click += async (_, _) => await DocumentationRouter.OpenAsync();
        var supportButton = CreateSectionButton("报告问题");
        supportButton.Click += (_, _) =>
        {
            if (!FeedbackService.TryOpenSupport())
                MessageBox.Show(this, "无法打开问题反馈页面。", ProductInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        var logButton = CreateSectionButton("运行日志");
        logButton.Click += (_, _) => OpenRunLog();
        actions.Controls.Add(documentationButton);
        actions.Controls.Add(supportButton);
        actions.Controls.Add(logButton);
        var termsButton = CreateSectionButton("用户协议");
        termsButton.Click += async (_, _) => await DocumentationRouter.OpenLegalAsync("terms");
        var privacyButton = CreateSectionButton("隐私说明");
        privacyButton.Click += async (_, _) => await DocumentationRouter.OpenLegalAsync("privacy");
        actions.Controls.Add(termsButton);
        actions.Controls.Add(privacyButton);
        group.Controls.Add(actions);
        return group;
    }

    private GroupBox CreateTelemetryGroup()
    {
        var group = new GroupBox
        {
            Text = _telemetry.GetStatus().IsTestBuild ? "隐私与统计（测试）" : "隐私与统计",
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 218),
            Padding = new Padding(10),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(23, 33, 43)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 4,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var consent = _telemetry.Consent;
        var baseSummary = new Label
        {
            Text = "基础运行统计：默认启用（首次运行、启动、更新、激活）",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        table.Controls.Add(baseSummary, 0, 0);
        table.SetColumnSpan(baseSummary, 2);

        _analyticsConsent.Text = "增强匿名分析（可选）";
        _analyticsConsent.Checked = consent.Analytics;
        _analyticsConsent.AutoSize = true;
        _analyticsConsent.Anchor = AnchorStyles.Left;
        _analyticsConsent.Margin = new Padding(0, 4, 0, 4);
        _crashConsent.Text = "匿名崩溃报告（可选）";
        _crashConsent.Checked = consent.Crash;
        _crashConsent.AutoSize = true;
        _crashConsent.Anchor = AnchorStyles.Left;
        _crashConsent.Margin = new Padding(0, 4, 0, 4);
        _disableAllTelemetry.Text = "禁止所有统计上传";
        _disableAllTelemetry.Checked = consent.AllUploadsDisabled;
        _disableAllTelemetry.AutoSize = true;
        _disableAllTelemetry.Anchor = AnchorStyles.Left;
        _disableAllTelemetry.Margin = new Padding(0, 4, 0, 4);
        var telemetryAvailable = _telemetry.GetStatus().IsEnabled;
        _analyticsConsent.Enabled = telemetryAvailable && !consent.AllUploadsDisabled;
        _crashConsent.Enabled = telemetryAvailable && !consent.AllUploadsDisabled;
        _disableAllTelemetry.Enabled = telemetryAvailable;
        table.Controls.Add(_analyticsConsent, 0, 1);
        table.Controls.Add(_crashConsent, 1, 1);
        table.Controls.Add(_disableAllTelemetry, 0, 2);
        _disableAllTelemetry.CheckedChanged += (_, _) =>
        {
            var controlsEnabled = telemetryAvailable && !_disableAllTelemetry.Checked;
            _analyticsConsent.Enabled = controlsEnabled;
            _crashConsent.Enabled = controlsEnabled;
        };

        _telemetryStatus.Text = _telemetry.StatusText;
        _telemetryStatus.Font = new Font("Microsoft YaHei UI", 8.5F);
        _telemetryStatus.Margin = new Padding(6, 1, 0, 1);
        table.Controls.Add(_telemetryStatus, 2, 0);
        table.SetRowSpan(_telemetryStatus, 3);

        var resetButton = CreateSectionButton("重置匿名标识并删除待上传数据");
        resetButton.Height = 26;
        resetButton.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "这将删除本地待上传统计并生成新的匿名标识，是否继续？", ProductInfo.ProductName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            _telemetry.ResetInstanceId();
            _telemetryStatus.Text = _telemetry.StatusText;
        };
        table.Controls.Add(resetButton, 1, 2);

        var privacyNote = new Label
        {
            Text = "基础统计仅记录首次运行、启动、更新和核心激活；不包含系统、网络或设备详细信息。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            ForeColor = Color.FromArgb(80, 92, 104),
            Margin = new Padding(0, 4, 0, 0)
        };
        table.Controls.Add(privacyNote, 0, 3);
        table.SetColumnSpan(privacyNote, 3);
        group.Controls.Add(table);
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
            _telemetry.SetConsent(_analyticsConsent.Checked, _crashConsent.Checked, _disableAllTelemetry.Checked);
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
        _openAccountCenter(this);
        RefreshAuthorizationSummary();
    }

    private void RefreshAuthorizationSummary()
    {
        var session = _accountSessionManager.CurrentSession;
        if (session is null)
        {
            _accountSummary.Text = "状态：未登录\n登录可选，全部功能可正常使用。";
            _accountSummary.ForeColor = Color.FromArgb(80, 92, 104);
        }
        else
        {
            var accountName = !string.IsNullOrWhiteSpace(session.Value.Profile.Email)
                ? session.Value.Profile.Email
                : !string.IsNullOrWhiteSpace(session.Value.Profile.DisplayName)
                    ? session.Value.Profile.DisplayName
                    : "账号信息已验证";
            _accountSummary.Text = $"状态：已连接\n{accountName}\nReWiFi 永久免费";
            _accountSummary.ForeColor = Color.DarkGreen;
        }

        _authorizationSummary.Text = "全部网络恢复功能永久免费。\n无需会员、激活或离线授权文件。\n断网时仍可正常使用。\n登录可方便反馈与账号识别。";
        _authorizationSummary.ForeColor = Color.DarkGreen;
    }

    private void AccountStateChanged(string _) => RefreshAuthorizationSummaryOnUiThread();

    private void TelemetryStateChanged() => RefreshTelemetryStatusOnUiThread();

    private void RefreshTelemetryStatusOnUiThread()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(RefreshTelemetryStatusOnUiThread));
                return;
            }

            _telemetryStatus.Text = _telemetry.StatusText;
        }
        catch (InvalidOperationException)
        {
            // The settings form can close while a background telemetry flush completes.
        }
    }

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

    private static Control CreateSummaryPanel(string title, Label summary, Control? footer = null)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = footer is null ? 2 : 3,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 8, 4),
            Padding = new Padding(10, 6, 10, 6)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        if (footer is not null)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        }
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
        if (footer is not null)
        {
            footer.Anchor = AnchorStyles.Left;
            footer.Margin = new Padding(0, 4, 0, 0);
            panel.Controls.Add(footer, 0, 2);
        }
        return panel;
    }

    private static Button CreateSectionButton(string text, Color? backColor = null)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 30),
            Padding = new Padding(8, 2, 8, 2),
            Font = new Font("Microsoft YaHei UI", 9F),
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
