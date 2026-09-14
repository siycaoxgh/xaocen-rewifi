using System.Drawing;

namespace XAOCEN.ReWiFi;

internal sealed class AuthorizationForm : Form
{
    private readonly AccountSessionManager _accountSessionManager;
    private readonly Label _accountStatus = CreateStatusLabel();
    private readonly Label _accountDetails = CreateDetailsLabel();
    private readonly System.Windows.Forms.Timer _authorizationProgressTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource _closeCancellation = new();
    private CancellationTokenSource? _authorizationCancellation;
    private Button? _cancelAuthorizationButton;
    private DeviceAuthorizationProgress? _authorizationProgress;

    public AuthorizationForm(AccountSessionManager accountSessionManager)
    {
        _accountSessionManager = accountSessionManager;

        Text = "XAOCEN ReWiFi 账号中心";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = LoadApplicationIcon();
        BackColor = Color.FromArgb(247, 249, 251);
        MinimumSize = new Size(720, 560);
        ClientSize = new Size(1040, 900);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3,
            AutoScroll = false
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        root.Controls.Add(CreateHeader(), 0, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 8, 0, 8),
            Padding = new Padding(0),
            AutoScroll = true
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(CreateOnlineGroup(), 0, 0);
        content.Controls.Add(CreateAccountGuidanceGroup(), 0, 1);
        root.Controls.Add(content, 0, 1);

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

        Controls.Add(ResponsiveWindow.CreateScrollableViewport(root, 650));
        AcceptButton = closeButton;
        Shown += (_, _) =>
        {
            ResponsiveWindow.FitToWorkingArea(this);
            RefreshState();
        };
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
        FormClosing += (_, _) =>
        {
            _authorizationCancellation?.Cancel();
            _closeCancellation.Cancel();
        };
        FormClosed += (_, _) =>
        {
            _accountSessionManager.StatusChanged -= AccountSessionStatusChanged;
            _accountSessionManager.AuthorizationProgressChanged -= AccountAuthorizationProgressChanged;
            _authorizationProgressTimer.Stop();
            _authorizationProgressTimer.Dispose();
            _authorizationCancellation?.Dispose();
            _closeCancellation.Dispose();
        };
    }

    private Control CreateHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label
        {
            Text = "XAOCEN Account · 可选登录",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(23, 33, 43),
            Location = new Point(0, 2)
        });
        header.Controls.Add(new Label
        {
            Text = "ReWiFi 永久免费，无需登录或激活即可使用全部网络恢复功能。",
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 92, 104),
            Location = new Point(2, 36)
        });
        return header;
    }

    private GroupBox CreateOnlineGroup()
    {
        var group = CreateGroup("XAOCEN Account 会话");
        group.Margin = new Padding(0, 0, 0, 10);
        var table = CreateTable(2, 3);
        table.Padding = new Padding(0, 0, 0, 10);
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(CreateFieldLabel("当前状态："), 0, 0);
        table.Controls.Add(_accountStatus, 1, 0);
        table.Controls.Add(CreateFieldLabel("会话信息："), 0, 1);
        table.Controls.Add(_accountDetails, 1, 1);

        var authorizeButton = CreateButton("登录账号", Color.FromArgb(224, 239, 248));
        authorizeButton.Click += async (_, _) => await AuthorizeAsync(authorizeButton);
        _cancelAuthorizationButton = CreateButton("取消登录");
        _cancelAuthorizationButton.Enabled = false;
        _cancelAuthorizationButton.Click += (_, _) => CancelAuthorization();
        var logoutButton = CreateButton("退出账号");
        logoutButton.Click += async (_, _) => await LogoutAsync(logoutButton);
        var actions = CreateActions(authorizeButton, _cancelAuthorizationButton, logoutButton);
        table.Controls.Add(actions, 0, 2);
        table.SetColumnSpan(actions, 2);
        group.Controls.Add(table);
        return group;
    }

    private GroupBox CreateAccountGuidanceGroup()
    {
        var group = CreateGroup("登录与设备说明");
        group.Margin = new Padding(0, 0, 0, 10);
        var note = CreateNoteLabel(
            "浏览器只负责登录和批准设备；授权完成后可以关闭浏览器，再返回本窗口查看“已连接”状态。\n" +
            "如需切换浏览器账号，请先点击“取消登录”，再重新发起登录。\n" +
            "“退出账号”会结束本机当前账号会话并清除本地刷新凭据；其他设备请在 XAOCEN Account 网页中管理。\n" +
            "登录用于账号识别；不登录也能使用全部网络恢复功能。统计偏好在设置中独立管理。",
            6);
        note.Padding = new Padding(4, 8, 4, 4);
        group.Controls.Add(note);
        return group;
    }

    private async Task AuthorizeAsync(Button button)
    {
        if (_authorizationCancellation is not null)
        {
            return;
        }

        var authorizationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_closeCancellation.Token);
        _authorizationCancellation = authorizationCancellation;
        var originalText = button.Text;
        button.Text = "验证中……";
        button.Enabled = false;
        if (_cancelAuthorizationButton is not null)
        {
            _cancelAuthorizationButton.Enabled = true;
        }

        try
        {
            await _accountSessionManager.AuthorizeAsync(authorizationCancellation.Token);
            RefreshState();
        }
        catch (OperationCanceledException)
        {
            _authorizationProgress = null;
            if (!IsDisposed && !_closeCancellation.IsCancellationRequested)
            {
                SetAccountStatus("登录已取消", "没有建立新的账号会话，可以重新点击“登录账号”。", Color.DarkOrange);
            }
        }
        catch (Exception ex)
        {
            _authorizationProgress = null;
            if (!IsDisposed)
            {
                SetAccountStatus("登录失败", ex.Message, Color.Firebrick);
                MessageBox.Show(this, $"XAOCEN Account 登录失败：{ex.Message}", ProductInfo.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (ReferenceEquals(_authorizationCancellation, authorizationCancellation))
            {
                _authorizationCancellation = null;
            }
            authorizationCancellation.Dispose();
            if (!IsDisposed)
            {
                button.Text = originalText;
                button.Enabled = true;
                if (_cancelAuthorizationButton is not null)
                {
                    _cancelAuthorizationButton.Enabled = false;
                }
            }
        }
    }

    private void CancelAuthorization()
    {
        if (_authorizationCancellation is null || _authorizationCancellation.IsCancellationRequested)
        {
            return;
        }

        SetAccountStatus("正在取消登录", "正在停止当前设备授权请求，请稍候……", Color.DarkOrange);
        _authorizationCancellation.Cancel();
    }

    private async Task LogoutAsync(Button button)
    {
        await RunButtonOperationAsync(button, "处理中……", async () =>
        {
            try
            {
                await _accountSessionManager.LogoutAsync(_closeCancellation.Token);

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

    private void RefreshState()
    {
        var session = _accountSessionManager.CurrentSession;
        if (session is null)
        {
            SetAccountStatus("未连接", "尚未登录。\nReWiFi 永久免费，全部功能可正常使用。", Color.FromArgb(80, 92, 104));
        }
        else
        {
            var displayName = string.IsNullOrWhiteSpace(session.Value.Profile.DisplayName)
                ? "账号已连接"
                : session.Value.Profile.DisplayName;
            var email = string.IsNullOrWhiteSpace(session.Value.Profile.Email) ? string.Empty : $" · {session.Value.Profile.Email}";
            SetAccountStatus("XAOCEN Account 已连接",
                $"{displayName}{email}\n访问令牌仅驻留内存。\n永久免费，全部功能可正常使用。", Color.DarkGreen);
        }

    }

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
                if (status.Contains("已连接", StringComparison.Ordinal) ||
                    status.Contains("会话已恢复", StringComparison.Ordinal) ||
                    status.Contains("已退出", StringComparison.Ordinal) ||
                    status.Contains("已失效", StringComparison.Ordinal) ||
                    status.Contains("已撤销", StringComparison.Ordinal))
                {
                    RefreshState();
                    return;
                }

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
        Margin = new Padding(0),
        BackColor = Color.White,
        ForeColor = Color.FromArgb(23, 33, 43)
    };

    private static TableLayoutPanel CreateTable(int columns, int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            RowCount = rows,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

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
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        Margin = new Padding(0, 5, 0, 5)
    };

    private static Label CreateDetailsLabel() => new()
    {
        Text = "",
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.TopLeft,
        Font = new Font("Microsoft YaHei UI", 9F),
        ForeColor = Color.FromArgb(80, 92, 104),
        Margin = new Padding(0, 5, 0, 5)
    };

    private static Label CreateNoteLabel(string text, int height) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        Height = height * 22,
        Font = new Font("Microsoft YaHei UI", 9F),
        ForeColor = Color.FromArgb(80, 92, 104),
        Padding = new Padding(0, 8, 0, 0)
    };

    private static FlowLayoutPanel CreateActions(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(0, 5, 0, 9)
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

    private void SetAccountStatus(string status, string details, Color color)
    {
        _accountStatus.Text = status;
        _accountStatus.ForeColor = color;
        _accountDetails.Text = details;
    }

}
