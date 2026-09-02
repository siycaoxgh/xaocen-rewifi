using System.Drawing;

namespace XAOCEN.ReWiFi;

internal sealed class QrCodeForm : Form
{
    private readonly PictureBox _pictureBox;

    public QrCodeForm(string title, string instruction, string payload)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(700, 780);
        BackColor = Color.White;

        _pictureBox = new PictureBox
        {
            Image = QrCodeService.CreateBitmap(payload),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(640, 640),
            Location = new Point(30, 20),
            BackColor = Color.White
        };

        var instructionLabel = new Label
        {
            Text = instruction,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(25, 675),
            Size = new Size(650, 42),
            ForeColor = Color.FromArgb(80, 92, 104)
        };

        var closeButton = new Button
        {
            Text = "关闭",
            AutoSize = true,
            Height = 30,
            DialogResult = DialogResult.Cancel,
            Location = new Point(315, 730),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, 189, 74),
            ForeColor = Color.FromArgb(23, 33, 43)
        };
        closeButton.FlatAppearance.BorderColor = Color.FromArgb(246, 169, 28);

        Controls.Add(_pictureBox);
        Controls.Add(instructionLabel);
        Controls.Add(closeButton);
        AcceptButton = closeButton;
        CancelButton = closeButton;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pictureBox.Image?.Dispose();
            _pictureBox.Image = null;
        }

        base.Dispose(disposing);
    }
}
