using System.Drawing;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace XAOCEN.ReWiFi;

internal sealed class QrScannerForm : Form
{
    private readonly PictureBox _preview = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _frameTimer = new() { Interval = 120 };
    private readonly QRCodeDetector _detector = new();
    private VideoCapture? _capture;
    private bool _completed;
    private bool _cameraErrorShown;

    public QrScannerForm()
    {
        Text = "扫描离线授权二维码";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new DrawingSize(620, 560);
        BackColor = Color.White;

        _preview.Location = new DrawingPoint(20, 20);
        _preview.Size = new DrawingSize(580, 405);
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(236, 241, 245);

        _status.Text = "正在打开摄像头……";
        _status.AutoSize = false;
        _status.Location = new DrawingPoint(20, 438);
        _status.Size = new DrawingSize(580, 42);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.ForeColor = Color.FromArgb(80, 92, 104);

        var cancelButton = new Button
        {
            Text = "取消",
            AutoSize = true,
            Height = 30,
            DialogResult = DialogResult.Cancel,
            Location = new DrawingPoint(275, 500),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(245, 247, 249)
        };
        cancelButton.FlatAppearance.BorderColor = Color.FromArgb(176, 190, 200);

        Controls.Add(_preview);
        Controls.Add(_status);
        Controls.Add(cancelButton);
        CancelButton = cancelButton;

        Shown += (_, _) => StartCamera();
        _frameTimer.Tick += (_, _) => ReadFrame();
    }

    public string? ResultText { get; private set; }

    private void StartCamera()
    {
        foreach (var api in new[] { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.ANY })
        {
            try
            {
                var capture = new VideoCapture(0, api);
                if (capture.IsOpened())
                {
                    capture.Set(VideoCaptureProperties.FrameWidth, 1280);
                    capture.Set(VideoCaptureProperties.FrameHeight, 720);
                    _capture = capture;
                    _status.Text = "请将 Account 返回的离线授权二维码放入取景框。";
                    _frameTimer.Start();
                    return;
                }

                capture.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"打开二维码摄像头失败：{ex.GetType().Name}");
            }
        }

        _status.Text = "无法打开摄像头，请检查摄像头权限，或使用粘贴/文件导入。";
    }

    private void ReadFrame()
    {
        if (_capture is null || _completed)
        {
            return;
        }

        try
        {
            using var frame = new Mat();
            if (!_capture.Read(frame) || frame.Empty())
            {
                return;
            }

            using var bitmap = BitmapConverter.ToBitmap(frame);
            var nextImage = new Bitmap(bitmap);
            var previousImage = _preview.Image;
            _preview.Image = nextImage;
            previousImage?.Dispose();

            var decoded = TryDecode(frame);
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                Complete(decoded.Trim());
            }
        }
        catch (Exception ex)
        {
            if (!_cameraErrorShown)
            {
                _cameraErrorShown = true;
                _frameTimer.Stop();
                _status.Text = "二维码读取失败，请调整距离或改用粘贴/文件导入。";
                AppLogger.Warning($"读取授权二维码失败：{ex.GetType().Name}");
            }
        }
    }

    private string? TryDecode(Mat frame)
    {
        var decoded = Decode(frame);
        if (!string.IsNullOrWhiteSpace(decoded))
        {
            return decoded;
        }

        using var grayscale = new Mat();
        Cv2.CvtColor(frame, grayscale, ColorConversionCodes.BGR2GRAY);
        decoded = Decode(grayscale);
        if (!string.IsNullOrWhiteSpace(decoded))
        {
            return decoded;
        }

        using var binary = new Mat();
        Cv2.Threshold(grayscale, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        decoded = Decode(binary);
        if (!string.IsNullOrWhiteSpace(decoded))
        {
            return decoded;
        }

        using var enlarged = new Mat();
        Cv2.Resize(grayscale, enlarged, new OpenCvSharp.Size(), 2, 2, InterpolationFlags.Cubic);
        return Decode(enlarged);
    }

    private string? Decode(Mat image)
    {
        using var straightQrCode = new Mat();
        var decoded = _detector.DetectAndDecode(image, out _, straightQrCode);
        return string.IsNullOrWhiteSpace(decoded) ? null : decoded.Trim();
    }

    private void Complete(string value)
    {
        _completed = true;
        ResultText = value;
        _status.Text = "二维码读取成功，正在进行本地授权校验……";
        DialogResult = DialogResult.OK;
        Close();
    }

    private void StopCamera()
    {
        _frameTimer.Stop();
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
        var image = _preview.Image;
        _preview.Image = null;
        image?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopCamera();
        _detector.Dispose();
        _frameTimer.Dispose();
        base.OnFormClosed(e);
    }
}
