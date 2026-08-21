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
    private readonly Action<AppConfig> _saveAction;

    public SettingsForm(AppConfig config, WifiController wifiController, Action<AppConfig> saveAction)
    {
        _initialConfig = config.Clone();
        _wifiController = wifiController;
        _saveAction = saveAction;
        Text = $"{ProductInfo.ProductName} 设置";
        BackColor = Color.FromArgb(247, 249, 251);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(700, 520);
        _ssidTextBox.Text = _initialConfig.TargetSsid;
        _adapterTextBox.Text = _initialConfig.AdapterName;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
        var aboutButton = new Button
        {
            Text = "打开产品介绍",
            AutoSize = true,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, 189, 74),
            ForeColor = Color.FromArgb(23, 33, 43)
        };
        aboutButton.FlatAppearance.BorderColor = Color.FromArgb(246, 169, 28);
        aboutButton.Click += (_, _) =>
        {
            if (!AboutPage.TryOpen())
                MessageBox.Show(this, "无法打开本地产品介绍页面。", ProductInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        overviewActions.Controls.Add(aboutButton);
        overviewActions.Controls.Add(githubLink);
        overview.Controls.Add(overviewActions, 1, 1);
        layout.Controls.Add(overview, 0, 0);
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
        settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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
        layout.Controls.Add(settingsGroup, 0, 1);

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
        layout.Controls.Add(buttonBar, 0, 2);

        Controls.Add(layout);
        AcceptButton = saveButton;
        Shown += (_, _) => _ssidTextBox.Focus();
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

        var config = new AppConfig
        {
            TargetSsid = _ssidTextBox.Text.Trim(),
            AdapterName = _adapterTextBox.Text.Trim(),
            FailureDelaySeconds = (int)_failureDelay.Value,
            CooldownSeconds = (int)_cooldown.Value,
            AutoRecovery = _autoRecovery.Checked,
            AutoStart = _autoStart.Checked
        };

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
