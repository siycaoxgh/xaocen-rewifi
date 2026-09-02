using System.Drawing;

namespace XAOCEN.ReWiFi;

internal sealed class AuthorizationForm : Form
{
    private readonly AccountSessionManager _accountSessionManager;
    private readonly OfflineAuthorizationManager _offlineAuthorizationManager;
    private readonly Label _accountStatus = CreateStatusLabel();
    private readonly Label _accountDetails = CreateDetailsLabel();
    private readonly Label _offlineStatus = CreateStatusLabel();
    private readonly Label _offlineDetails = CreateDetailsLabel();
    private readonly TextBox _licenseTextBox = new();
    private readonly System.Windows.Forms.Timer _authorizationProgressTimer = new() { Interval = 1000 };
    private CancellationTokenSource _closeCancellation = new();
    private DeviceAuthorizationProgress? _authorizationProgress;

    public AuthorizationForm(
        AccountSessionManager accountSessionManager,
        OfflineAuthorizationManager offlineAuthorizationManager)
    {
        _accountSessionManager = accountSessionManager;
        _offlineAuthorizationManager = offlineAuthorizationManager;

        Text = "XAOCEN ReWiFi 授权中心";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(247, 249, 251);
        ClientSize = new Size(900, 620);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        root.Controls.Add(CreateHeader(), 0, 0);

        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 8, 0, 8)
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.Controls.Add(CreateOnlineGroup(), 0, 0);
        columns.Controls.Add(CreateOfflineGroup(), 1, 0);
        root.Controls.Add(columns, 0, 1);

        var closeButton = new Button
        {
            Text = "关闭",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, 189, 74),
            ForeColor = Color.FromArgb(23, 33, 43)
        };
        closeButton.FlatAppearance.BorderColor = Color.FromArgb(246, 169, 28);
        closeButton.Click += (_, _) => Close();
        var buttonPanel = new Panel { Dock = DockStyle.Fill };
        buttonPanel.Controls.Add(closeButton);
        buttonPanel.Resize += (_, _) => closeButton.Left = buttonPanel.ClientSize.Width - closeButton.Width;
        root.Controls.Add(buttonPanel, 0, 2);

        Controls.Add(root);
        AcceptButton = closeButton;
        Shown += (_, _) => RefreshState();
        _accountSessionManager.StatusChanged += AccountSessionStatusChanged;
        _accountSessionManager.AuthorizationProgressChanged += AccountAuthorizationProgressChanged;
        _authorizationProgressTimer.Tick += (_, _) =>
        {
            if (_authorizationProgress is { } progress && progress.Phase != DeviceAuthorizationPhase.Connected)
            {
                RenderAuthorizationProgress(progress);
            }
        };
        _authorizationProgressTimer.Start();
        FormClosed += (_, _) =>
        {
            _accountSessionManager.StatusChanged -= AccountSessionStatusChanged;
            _accountSessionManager.AuthorizationProgressChanged -= AccountAuthorizationProgressChanged;
            _authorizationProgressTimer.Stop();
            _authorizationProgressTimer.Dispose();
            _closeCancellation.Cancel();
            _closeCancellation.Dispose();
        };
    }

    private Control CreateHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label
        {
            Text = "XAOCEN Account 与 ReWiFi 离线授权",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(23, 33, 43),
            Location = new Point(0, 2)
        });
        header.Controls.Add(new Label
        {
            Text = "网络可用时优先在线检查，网络不可用时自动使用本地签名校验。",
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 92, 104),
            Location = new Point(2, 36)
        });
        return header;
    }

    private GroupBox CreateOnlineGroup()
    {
        var group = CreateGroup("在线账号会话");
        var table = CreateTable(2, 6);
        table.Controls.Add(CreateFieldLabel("当前状态："), 0, 0);
        table.Controls.Add(_accountStatus, 1, 0);
        table.Controls.Add(CreateFieldLabel("会话信息："), 0, 1);
        table.Controls.Add(_accountDetails, 1, 1);

        var authorizeButton = CreateButton("在线授权", Color.FromArgb(224, 239, 248));
        authorizeButton.Click += async (_, _) => await AuthorizeAsync(authorizeButton);
        var logoutButton = CreateButton("退出账号");
        logoutButton.Click += async (_, _) => await EndAccountAsync(logoutButton, revoke: false);
        var revokeButton = CreateButton("撤销此设备");
        revokeButton.Click += async (_, _) => await EndAccountAsync(revokeButton, revoke: true);
        var actions = CreateActions(authorizeButton, logoutButton, revokeButton);
        table.Controls.Add(actions, 0, 2);
        table.SetColumnSpan(actions, 2);

        var note = CreateNoteLabel("浏览器只负责登录和批准设备；Account 页面会自动刷新并显示等待、已批准、已完成、拒绝或过期状态。授权完成后可以关闭浏览器，再返回本窗口查看“已连接”状态。\n\n当前不涉及会员、订阅、付费或匿名遥测。登录始终由 XAOCEN Account 完成。", 4);
        table.Controls.Add(note, 0, 3);
        table.SetColumnSpan(note, 2);
        group.Controls.Add(table);
        return group;
    }

    private GroupBox CreateOfflineGroup()
    {
        var group = CreateGroup("离线授权");
        var table = CreateTable(2, 8);
        table.Controls.Add(CreateFieldLabel("当前状态："), 0, 0);
        table.Controls.Add(_offlineStatus, 1, 0);
        table.Controls.Add(CreateFieldLabel("校验信息："), 0, 1);
        table.Controls.Add(_offlineDetails, 1, 1);

        var copyKeyButton = CreateButton("复制设备公钥");
        copyKeyButton.Click += (_, _) => CopyDeviceKey();
        var showKeyQrButton = CreateButton("显示公钥二维码");
        showKeyQrButton.Click += (_, _) => ShowDeviceKeyQr();
        var checkButton = CreateButton("检查当前授权");
        checkButton.Click += async (_, _) => await CheckOfflineAsync(checkButton);
        var actions = CreateActions(copyKeyButton, showKeyQrButton, checkButton);
        table.Controls.Add(actions, 0, 2);
        table.SetColumnSpan(actions, 2);

        _licenseTextBox.Multiline = true;
        _licenseTextBox.ScrollBars = ScrollBars.Vertical;
        _licenseTextBox.Dock = DockStyle.Fill;
        _licenseTextBox.Height = 92;
        _licenseTextBox.PlaceholderText = "可粘贴 compactLicense 授权字符串";
        table.Controls.Add(_licenseTextBox, 0, 3);
        table.SetColumnSpan(_licenseTextBox, 2);

        var pasteButton = CreateButton("从剪贴板粘贴");
        pasteButton.Click += (_, _) => PasteLicense();
        var scanButton = CreateButton("扫描授权二维码");
        scanButton.Click += (_, _) => ScanLicenseQr();
        var importTextButton = CreateButton("验证并保存字符串");
        importTextButton.Click += (_, _) => ImportLicenseText();
        var importFileButton = CreateButton("导入授权文件");
        importFileButton.Click += (_, _) => ImportLicenseFile();
        var importActions = CreateActions(pasteButton, scanButton, importTextButton, importFileButton);
        table.Controls.Add(importActions, 0, 4);
        table.SetColumnSpan(importActions, 2);

        var keyNote = CreateNoteLabel("设备私钥只保存在 Windows Credential Manager；不会写入 config.json、日志或普通文件。当前离线授权文件位置：%LOCALAPPDATA%\\XAOCEN ReWiFi\\offline-license.xaocen-license。\n\n公钥二维码供联网手机或 Account 网页扫描；授权二维码由 Account 签发后供本机扫描。二维码内容仍需通过本地签名、产品、平台和设备校验。", 3);
        table.Controls.Add(keyNote, 0, 5);
        table.SetColumnSpan(keyNote, 2);
        group.Controls.Add(table);
        return group;
    }

    private async Task AuthorizeAsync(Button button)
    {
        await RunButtonOperationAsync(button, "授权中……", async () =>
        {
            try
            {
                await _accountSessionManager.AuthorizeAsync(_closeCancellation.Token);
                RefreshState();
            }
            catch (OperationCanceledException) when (!_closeCancellation.IsCancellationRequested)
            {
                _authorizationProgress = null;
                SetAccountStatus("授权已取消", "没有建立新的账号会话。", Color.DarkOrange);
            }
            catch (Exception ex)
            {
                _authorizationProgress = null;
                SetAccountStatus("授权失败", ex.Message, Color.Firebrick);
                MessageBox.Show(this, $"XAOCEN Account 授权失败：{ex.Message}", ProductInfo.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
    }

    private async Task EndAccountAsync(Button button, bool revoke)
    {
        await RunButtonOperationAsync(button, "处理中……", async () =>
        {
            try
            {
                if (revoke)
                {
                    await _accountSessionManager.RevokeAsync(_closeCancellation.Token);
                }
                else
                {
                    await _accountSessionManager.LogoutAsync(_closeCancellation.Token);
                }

                RefreshState();
            }
            catch (Exception ex)
            {
                SetAccountStatus("操作失败", ex.Message, Color.Firebrick);
                MessageBox.Show(this, $"账号操作失败：{ex.Message}", ProductInfo.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
    }

    private async Task CheckOfflineAsync(Button button)
    {
        await RunButtonOperationAsync(button, "检查中……", async () =>
        {
            try
            {
                var decision = await _offlineAuthorizationManager.EvaluateAsync(_closeCancellation.Token);
                RefreshState();
                RenderOfflineDecision(decision);
                MessageBox.Show(this, $"当前授权状态：{GetModeText(decision.Mode)}\n{decision.Message}", ProductInfo.ProductName,
                    MessageBoxButtons.OK,
                    decision.IsAllowed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                SetOfflineStatus("检查失败", ex.Message, Color.Firebrick);
                MessageBox.Show(this, $"离线授权检查失败：{ex.Message}", ProductInfo.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
    }

    private void CopyDeviceKey()
    {
        try
        {
            Clipboard.SetText(_offlineAuthorizationManager.GetDevicePublicKey());
            SetOfflineStatus("设备公钥已复制", "请交给已登录 XAOCEN Account 的联网设备申请离线授权。", Color.DarkGreen);
        }
        catch (Exception ex)
        {
            SetOfflineStatus("生成公钥失败", ex.Message, Color.Firebrick);
            MessageBox.Show(this, $"生成离线设备公钥失败：{ex.Message}", ProductInfo.ProductName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowDeviceKeyQr()
    {
        try
        {
            var publicKey = _offlineAuthorizationManager.GetDevicePublicKey();
            using var form = new QrCodeForm(
                "XAOCEN ReWiFi 设备公钥二维码",
                "请使用联网手机或 XAOCEN Account 网页扫描此二维码申请离线授权。",
                publicKey);
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            SetOfflineStatus("生成二维码失败", ex.Message, Color.Firebrick);
            MessageBox.Show(this, $"生成设备公钥二维码失败：{ex.Message}", ProductInfo.ProductName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PasteLicense()
    {
        if (Clipboard.ContainsText())
        {
            _licenseTextBox.Text = Clipboard.GetText().Trim();
            _licenseTextBox.SelectAll();
        }
    }

    private void ScanLicenseQr()
    {
        using var scanner = new QrScannerForm();
        if (scanner.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(scanner.ResultText))
        {
            return;
        }

        var scanned = scanner.ResultText.Trim();
        if (scanned.Contains("BEGIN PUBLIC KEY", StringComparison.OrdinalIgnoreCase))
        {
            SetOfflineStatus("二维码类型不匹配", "检测到设备公钥二维码，请使用 Account 网页扫描公钥后再签发离线授权。", Color.DarkOrange);
            return;
        }

        _licenseTextBox.Text = scanned;
        ImportLicenseText();
    }

    private void ImportLicenseText()
    {
        try
        {
            var result = _offlineAuthorizationManager.ImportLicense(_licenseTextBox.Text.Trim());
            SetOfflineStatus(result.IsValid ? "本地授权有效" : "本地授权无效", result.Message,
                result.IsValid ? Color.DarkGreen : Color.Firebrick);
            if (!result.IsValid)
            {
                MessageBox.Show(this, result.Message, ProductInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            RefreshState();
        }
        catch (Exception ex)
        {
            SetOfflineStatus("导入失败", ex.Message, Color.Firebrick);
        }
    }

    private void ImportLicenseFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入 XAOCEN ReWiFi 离线授权",
            Filter = "XAOCEN 离线授权 (*.xaocen-license;*.txt)|*.xaocen-license;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _licenseTextBox.Text = File.ReadAllText(dialog.FileName).Trim();
            ImportLicenseText();
        }
        catch (Exception ex)
        {
            SetOfflineStatus("读取文件失败", ex.Message, Color.Firebrick);
        }
    }

    private void RefreshState()
    {
        var session = _accountSessionManager.CurrentSession;
        if (session is null)
        {
            SetAccountStatus("未连接", "尚未建立 XAOCEN Account 登录会话。\n离线授权在线校验不会建立账号登录。", Color.FromArgb(80, 92, 104));
        }
        else
        {
            var displayName = string.IsNullOrWhiteSpace(session.Value.Profile.DisplayName)
                ? "账号已连接"
                : session.Value.Profile.DisplayName;
            var email = string.IsNullOrWhiteSpace(session.Value.Profile.Email) ? string.Empty : $" · {session.Value.Profile.Email}";
            SetAccountStatus("XAOCEN Account 已连接",
                $"{displayName}{email}\n访问令牌仅驻留内存。\n{GetEntitlementDetails()}", Color.DarkGreen);
        }

        var localResult = _offlineAuthorizationManager.ValidateStoredLicense();
        if (_offlineAuthorizationManager.LastDecision is { } lastDecision)
        {
            RenderOfflineDecision(lastDecision);
        }
        else if (localResult.IsValid && localResult.Payload is not null)
        {
            SetOfflineStatus("本地授权有效", BuildOfflineDetails(
                localResult,
                "本地签名和设备校验已通过；尚未执行本次联网检查。"), Color.DarkGreen);
        }
        else if (localResult.Message == "当前没有离线授权文件。")
        {
            SetOfflineStatus("未导入", "尚未配置离线授权文件。", Color.FromArgb(80, 92, 104));
        }
        else
        {
            SetOfflineStatus("本地授权无效", localResult.Message, Color.Firebrick);
        }
    }

    private void RenderOfflineDecision(OfflineAuthorizationDecision decision)
    {
        var (title, color, details) = decision.Mode switch
        {
            OfflineAuthorizationMode.Online => (
                "离线授权联网校验通过",
                Color.DarkGreen,
                BuildOfflineDetails(decision.LocalResult,
                    "Account 已验证当前离线授权和设备状态；此检查不会建立左侧账号会话。")),
            OfflineAuthorizationMode.Offline => (
                "本地校验通过",
                Color.DarkGreen,
                BuildOfflineDetails(decision.LocalResult, decision.Message)),
            OfflineAuthorizationMode.Rejected => (
                "授权被拒绝",
                Color.Firebrick,
                decision.Message),
            _ => (
                "未配置离线授权",
                Color.FromArgb(80, 92, 104),
                decision.Message)
        };
        SetOfflineStatus(title, details, color);
    }

    private static string BuildOfflineDetails(OfflineLicenseValidationResult result, string introduction)
    {
        if (!result.IsValid || result.Payload is null)
        {
            return string.IsNullOrWhiteSpace(introduction) ? result.Message : introduction;
        }

        var payload = result.Payload;
        var licenseType = payload.LicenseType switch
        {
            "perpetual" => "永久授权",
            "subscription" => "订阅授权",
            "trial" => "试用授权",
            "public_test" => "公开测试授权",
            _ => string.IsNullOrWhiteSpace(payload.LicenseType) ? "未知类型" : payload.LicenseType
        };
        var entitlementExpiry = payload.LicenseType?.Equals("perpetual", StringComparison.OrdinalIgnoreCase) == true
            ? "永久"
            : payload.EntitlementExpiresAt is null ? "未单独设置" : FormatProtocolDate(payload.EntitlementExpiresAt);
        return string.Join(Environment.NewLine,
            introduction,
            $"授权类型：{licenseType}",
            $"权益到期：{entitlementExpiry}",
            $"下次联网检查：{FormatProtocolDate(payload.NextOnlineCheckAt)}",
            $"最迟重新授权：{FormatProtocolDate(payload.HardReauthorizeAt)}");
    }

    private string GetEntitlementDetails()
    {
        var entitlements = _accountSessionManager.CurrentEntitlements;
        if (entitlements is null)
        {
            return "ReWiFi 权益：暂未获取（不影响账号连接）。";
        }

        var entitlement = entitlements.FirstOrDefault(item =>
            string.Equals(item.ProductId, ProductInfo.AccountProductId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(entitlement.ProductId))
        {
            return "ReWiFi 权益：未查询到产品权益。";
        }

        var status = entitlement.Status switch
        {
            "active" => "有效",
            "revoked" => "已撤销",
            "expired" => "已过期",
            _ => string.IsNullOrWhiteSpace(entitlement.Status) ? "未知" : entitlement.Status
        };
        var licenseType = entitlement.LicenseType switch
        {
            "perpetual" => "永久",
            "subscription" => "订阅",
            "trial" or "public_test" => "测试",
            _ => string.IsNullOrWhiteSpace(entitlement.LicenseType) ? "未知" : entitlement.LicenseType
        };
        var expiresAt = string.IsNullOrWhiteSpace(entitlement.ExpiresAt)
            ? "永久"
            : FormatProtocolDate(entitlement.ExpiresAt);
        var offline = entitlement.OfflineAuthorizationAvailable ? "可签发离线授权" : "暂不可签发离线授权";
        return $"ReWiFi 权益：{status} · {licenseType}\n有效至：{expiresAt} · 设备：{entitlement.ActiveDevices}/{entitlement.MaxDevices}\n{offline}";
    }

    private static string FormatProtocolDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : value ?? "未知";
    }

    private static string FormatProtocolDate(DateTimeOffset? value) => value is { } timestamp
        ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "未设置";

    private void AccountSessionStatusChanged(string status)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                var (title, color) = status switch
                {
                    var value when value.Contains("等待", StringComparison.Ordinal) => ("等待账号批准", Color.DarkOrange),
                    var value when value.Contains("已批准", StringComparison.Ordinal) => ("已批准", Color.DarkOrange),
                    var value when value.Contains("已连接", StringComparison.Ordinal) => ("XAOCEN Account 已连接", Color.DarkGreen),
                    var value when value.Contains("已拒绝", StringComparison.Ordinal) => ("授权已拒绝", Color.Firebrick),
                    var value when value.Contains("已过期", StringComparison.Ordinal) || value.Contains("超时", StringComparison.Ordinal) => ("授权已过期", Color.Firebrick),
                    var value when value.Contains("已取消", StringComparison.Ordinal) => ("授权已取消", Color.DarkOrange),
                    _ => ("账号操作", Color.DarkOrange)
                };
                SetAccountStatus(title, status, color);
            }));
        }
        catch (InvalidOperationException)
        {
            // The form can close while an authorization poll is completing.
        }
    }

    private void AccountAuthorizationProgressChanged(DeviceAuthorizationProgress progress)
    {
        _authorizationProgress = progress;
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() => RenderAuthorizationProgress(progress)));
        }
        catch (InvalidOperationException)
        {
            // The form can close while an authorization poll is completing.
        }
    }

    private void RenderAuthorizationProgress(DeviceAuthorizationProgress progress)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (progress.Phase == DeviceAuthorizationPhase.Connected)
        {
            RefreshState();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var remaining = progress.ExpiresAt - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var remainingText = $"{(int)remaining.TotalMinutes} 分 {remaining.Seconds:00} 秒";
        var lastPollText = progress.LastPollAt is { } lastPoll
            ? lastPoll.ToLocalTime().ToString("HH:mm:ss")
            : "尚未轮询";
        var nextPollText = progress.NextPollAt is { } nextPoll
            ? nextPoll.ToLocalTime().ToString("HH:mm:ss")
            : "无需继续轮询";
        var details = string.Join(Environment.NewLine,
            $"授权截止：{progress.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"上次轮询：{lastPollText}",
            $"下一次轮询：{nextPollText}",
            $"当前轮询：{progress.PollCount} 次",
            $"授权剩余：{remainingText}");

        var (title, color, prefix) = progress.Phase switch
        {
            DeviceAuthorizationPhase.Waiting => ("等待账号批准", Color.DarkOrange, "等待 XAOCEN Account 批准设备。"),
            DeviceAuthorizationPhase.Approved => ("已批准", Color.DarkGreen, "账号已批准，正在获取会话。"),
            DeviceAuthorizationPhase.Denied => ("授权已拒绝", Color.Firebrick, "XAOCEN Account 拒绝了本次设备授权。"),
            DeviceAuthorizationPhase.Consumed => ("授权码已使用", Color.Firebrick, "授权码已经使用，请重新发起授权。"),
            DeviceAuthorizationPhase.Expired => ("授权已过期", Color.Firebrick, "本次设备授权已超过有效时间。"),
            _ => ("账号操作", Color.DarkOrange, "正在处理账号授权。")
        };

        SetAccountStatus(title, $"{prefix}\n{details}", color);
    }

    private async Task RunButtonOperationAsync(Button button, string busyText, Func<Task> operation)
    {
        var originalText = button.Text;
        button.Text = busyText;
        button.Enabled = false;
        try
        {
            await operation();
        }
        finally
        {
            button.Text = originalText;
            button.Enabled = true;
        }
    }

    private static GroupBox CreateGroup(string title) => new()
    {
        Text = title,
        Dock = DockStyle.Fill,
        Padding = new Padding(12),
        Margin = new Padding(0, 0, 8, 0),
        BackColor = Color.White,
        ForeColor = Color.FromArgb(23, 33, 43)
    };

    private static TableLayoutPanel CreateTable(int columns, int rows) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = columns,
        RowCount = rows,
        Margin = new Padding(0)
    };

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Top,
        Margin = new Padding(0, 5, 8, 5)
    };

    private static Label CreateStatusLabel() => new()
    {
        Text = "未检查",
        AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Top,
        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 5, 0, 5)
    };

    private static Label CreateDetailsLabel() => new()
    {
        Text = "",
        AutoSize = true,
        MaximumSize = new Size(320, 0),
        Anchor = AnchorStyles.Left | AnchorStyles.Top,
        ForeColor = Color.FromArgb(80, 92, 104),
        Margin = new Padding(0, 5, 0, 5)
    };

    private static Label CreateNoteLabel(string text, int height) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        Height = height * 22,
        ForeColor = Color.FromArgb(80, 92, 104),
        Padding = new Padding(0, 8, 0, 0)
    };

    private static FlowLayoutPanel CreateActions(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(0, 3, 0, 3)
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static Button CreateButton(string text, Color? backColor = null)
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

    private void SetAccountStatus(string status, string details, Color color)
    {
        _accountStatus.Text = status;
        _accountStatus.ForeColor = color;
        _accountDetails.Text = details;
    }

    private void SetOfflineStatus(string status, string details, Color color)
    {
        _offlineStatus.Text = status;
        _offlineStatus.ForeColor = color;
        _offlineDetails.Text = details;
    }

    private static string GetModeText(OfflineAuthorizationMode mode) => mode switch
    {
        OfflineAuthorizationMode.Online => "离线授权在线校验通过",
        OfflineAuthorizationMode.Offline => "本地离线校验通过",
        OfflineAuthorizationMode.Rejected => "授权被拒绝",
        _ => "未配置离线授权"
    };
}
