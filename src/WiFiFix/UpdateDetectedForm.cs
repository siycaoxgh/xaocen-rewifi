using System.Diagnostics;
using System.Drawing;

namespace XAOCEN.ReWiFi;

internal sealed class UpdateDetectedForm : Form
{
    public UpdateDetectedForm(string currentVersion, string newVersion, string newExecutablePath, bool samePublicVersion)
    {
        Text = samePublicVersion ? $"{ProductInfo.ProductName} 新构建" : $"{ProductInfo.ProductName} 更新";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(247, 249, 251);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(640, 285);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 18, 22, 18),
            ColumnCount = 1,
            RowCount = 4
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        content.Controls.Add(new Label
        {
            Text = samePublicVersion ? "检测到当前版本的新构建" : "检测到新版 ReWiFi",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(23, 33, 43)
        }, 0, 0);

        content.Controls.Add(new Label
        {
            Text = samePublicVersion
                ? $"当前正在运行：v{currentVersion}\n准备启动：v{newVersion}\n\n这是同一公开版本的更新构建。退出当前版本后，新构建会自动继续启动。程序不会扫描或删除其他位置的旧文件。"
                : $"当前正在运行：v{currentVersion}\n准备启动：v{newVersion}\n\n退出当前版本后，新版会自动继续启动。程序不会扫描或删除其他位置的旧文件。",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(46, 70, 89)
        }, 0, 1);

        var pathBox = new TextBox
        {
            Text = newExecutablePath,
            ReadOnly = true,
            Dock = DockStyle.Top,
            BorderStyle = BorderStyle.FixedSingle
        };
        content.Controls.Add(pathBox, 0, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };
        var continueButton = new Button
        {
            Text = "退出旧版并运行新版",
            AutoSize = true,
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(255, 181, 55)
        };
        var cancelButton = new Button
        {
            Text = "稍后",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };
        var locationButton = new Button
        {
            Text = "打开新版位置",
            AutoSize = true
        };
        locationButton.Click += (_, _) => OpenLocation(newExecutablePath);
        actions.Controls.AddRange([continueButton, cancelButton, locationButton]);
        content.Controls.Add(actions, 0, 3);

        Controls.Add(content);
        AcceptButton = continueButton;
        CancelButton = cancelButton;
    }

    private static void OpenLocation(string executablePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{executablePath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"打开新版位置失败：{ex.Message}");
        }
    }
}
