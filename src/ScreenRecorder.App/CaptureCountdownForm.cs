using System.Runtime.InteropServices;

namespace ScreenRecorder.App;

internal sealed class CaptureCountdownForm : Form
{
    private readonly Font _messageFont = new("Yu Gothic UI", 13, FontStyle.Bold);
    private readonly string _countdownLabel;
    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        BackColor = Color.FromArgb(35, 35, 35),
        AccessibleName = "撮影までの残り時間"
    };

    public CaptureCountdownForm(Rectangle displayBounds, bool forRecording = false)
    {
        _countdownLabel = forRecording ? UiLabels.RecordingCountdownPrefix : UiLabels.ScreenshotCountdownPrefix;
        _message.Font = _messageFont;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(180, 52);
        Location = forRecording
            ? new Point(displayBounds.Left + (displayBounds.Width - Width) / 2, displayBounds.Top + (displayBounds.Height - Height) / 2)
            : new Point(displayBounds.Right - Width - 20, displayBounds.Top + 20);
        BackColor = Color.FromArgb(35, 35, 35);
        TopMost = true;
        ShowInTaskbar = false;
        Controls.Add(_message);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x00000080;
            return parameters;
        }
    }

    public bool ExcludeFromCapture() => NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WindowDisplayAffinityExcludeFromCapture);

    public void SetRemainingSeconds(int seconds) => _message.Text = $"{_countdownLabel} {seconds} 秒";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _messageFont.Dispose();
        base.Dispose(disposing);
    }
}

internal static class CursorOverlay
{
    public static void Draw(Bitmap bitmap, Rectangle captureBounds)
    {
        var cursor = new NativeMethods.CursorInfo { Size = Marshal.SizeOf<NativeMethods.CursorInfo>() };
        if (!NativeMethods.GetCursorInfo(ref cursor) || (cursor.Flags & NativeMethods.CursorShowing) == 0 || cursor.Cursor == IntPtr.Zero) return;
        if (!NativeMethods.GetIconInfo(cursor.Cursor, out var iconInfo)) return;

        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var deviceContext = graphics.GetHdc();
            try
            {
                var x = cursor.ScreenPosition.X - captureBounds.X - (int)iconInfo.HotspotX;
                var y = cursor.ScreenPosition.Y - captureBounds.Y - (int)iconInfo.HotspotY;
                NativeMethods.DrawIconEx(deviceContext, x, y, cursor.Cursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DrawIconNormal);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }
        finally
        {
            if (iconInfo.MaskBitmap != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.MaskBitmap);
            if (iconInfo.ColorBitmap != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.ColorBitmap);
        }
    }
}
