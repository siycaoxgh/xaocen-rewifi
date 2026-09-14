using System.Drawing;

namespace XAOCEN.ReWiFi;

internal static class ResponsiveWindow
{
    private const int ScreenMargin = 24;

    public static void FitToWorkingArea(Form form)
    {
        var workingArea = Screen.FromControl(form).WorkingArea;
        var maximumWidth = Math.Max(720, workingArea.Width - ScreenMargin * 2);
        var maximumHeight = Math.Max(560, workingArea.Height - ScreenMargin * 2);
        var width = Math.Min(form.Width, maximumWidth);
        var height = Math.Min(form.Height, maximumHeight);

        if (width != form.Width || height != form.Height)
        {
            form.Size = new Size(width, height);
        }

        form.Left = workingArea.Left + Math.Max(ScreenMargin, (workingArea.Width - form.Width) / 2);
        form.Top = workingArea.Top + Math.Max(ScreenMargin, (workingArea.Height - form.Height) / 2);
    }
}
