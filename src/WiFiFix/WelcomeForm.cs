namespace XAOCEN.ReWiFi;

internal sealed class WelcomeForm : Form
{
    private readonly CheckBox _disable = new() { Text = "禁止所有统计上传（不影响网络恢复）", AutoSize = true };
    public bool DisableStatistics => _disable.Checked;
    public WelcomeForm(bool disabled)
    {
        Text = "欢迎使用 XAOCEN ReWiFi";
        Font = new Font("Microsoft YaHei UI", 10F);
        ClientSize = new Size(660, 420);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(20) };
        content.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(610, 0), Text = "XAOCEN ReWiFi · 永久免费的无线重连工具\n\n无需登录或激活。程序监测 Wi-Fi，恢复时会短暂断开网络；开机启动可在设置中关闭。\n\n基础统计默认开启，记录首次运行、启动、更新及核心启用规模；增强分析和崩溃报告分别可选，首次默认关闭，升级沿用原选择。下方可禁止全部统计上传。账号、文档和网络探测仍可能联网。\n\n首次启动说明请先阅读用户协议和隐私说明。继续使用后，将打开产品介绍与使用帮助。继续不代表同意可选分析或崩溃报告。" });
        var links = new FlowLayoutPanel { AutoSize = true };
        foreach (var item in new[] { ("用户协议", "terms"), ("隐私说明", "privacy") })
        {
            var link = new Button { Text = item.Item1, AutoSize = true };
            link.Click += async (_, _) => await DocumentationRouter.OpenLegalAsync(item.Item2);
            links.Controls.Add(link);
        }
        content.Controls.Add(links);
        _disable.Checked = disabled;
        content.Controls.Add(_disable);
        var continueButton = new Button { Text = "已阅读，继续使用", AutoSize = true, DialogResult = DialogResult.OK };
        content.Controls.Add(continueButton);
        Controls.Add(content);
    }
}
