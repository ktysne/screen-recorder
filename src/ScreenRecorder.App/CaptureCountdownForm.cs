using System.Runtime.InteropServices;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal enum CaptureCountdownKind
{
    Screenshot,
    Recording
}

internal sealed class CaptureCountdownForm : CaptureExcludedOverlayForm
{
    private readonly Font _messageFont = new("Yu Gothic UI", 13, FontStyle.Bold);
    private readonly string _countdownLabel;
    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        BackColor = Color.FromArgb(35, 35, 35)
    };

    private static readonly Size TextPaddingLogical = new(16, 8);
    private const int ScreenshotCountdownEdgeMarginLogical = 20;
    private readonly Rectangle _displayBounds;
    private readonly bool _forRecording;
    private readonly int _maxSeconds;

    public CaptureCountdownForm(Rectangle displayBounds, CaptureCountdownKind kind, int maxSeconds)
    {
        _forRecording = kind == CaptureCountdownKind.Recording;
        _displayBounds = displayBounds;
        _maxSeconds = maxSeconds;
        _countdownLabel = _forRecording ? UiLabels.RecordingCountdownPrefix : UiLabels.ScreenshotCountdownPrefix;
        _message.AccessibleName = _forRecording ? UiLabels.RecordingCountdownAccessibleName : UiLabels.ScreenshotCountdownAccessibleName;
        _message.Font = _messageFont;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(35, 35, 35);
        TopMost = true;
        ShowInTaskbar = false;
        Controls.Add(_message);
        FitToText();
    }

    // 窓の大きさを固定すると、表示倍率を上げたときに文字が折り返して切れるため、文字の幅から決める。
    private void FitToText()
    {
        var textSize = TextRenderer.MeasureText(FormatMessage(_maxSeconds), _message.Font);
        Size = new Size(
            textSize.Width + LogicalToDeviceUnits(TextPaddingLogical.Width) * 2,
            textSize.Height + LogicalToDeviceUnits(TextPaddingLogical.Height) * 2);
        var margin = LogicalToDeviceUnits(ScreenshotCountdownEdgeMarginLogical);
        Location = _forRecording
            ? new Point(_displayBounds.Left + (_displayBounds.Width - Width) / 2, _displayBounds.Top + (_displayBounds.Height - Height) / 2)
            : new Point(_displayBounds.Right - Width - margin, _displayBounds.Top + margin);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToText();
    }

    private string FormatMessage(int seconds) => $"{_countdownLabel} {seconds} 秒";

    public void SetRemainingSeconds(int seconds) => _message.Text = FormatMessage(seconds);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _messageFont.Dispose();
        base.Dispose(disposing);
    }
}

internal static class CaptureCountdown
{
    public static async Task RunAsync(
        CaptureCountdownKind kind,
        int seconds,
        Rectangle displayBounds,
        CancellationToken cancellationToken,
        Action<CaptureCountdownForm>? onShown = null)
    {
        using var countdown = new CaptureCountdownForm(displayBounds, kind, seconds);
        try
        {
            countdown.Show();
            onShown?.Invoke(countdown);
            countdown.ExcludeFromCapture(
                kind == CaptureCountdownKind.Recording ? DiagnosticLogTags.Record : DiagnosticLogTags.Capture,
                kind == CaptureCountdownKind.Recording ? "録画カウントダウン" : "撮影カウントダウン");
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                countdown.SetRemainingSeconds(remaining);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        finally
        {
            if (!countdown.IsDisposed) countdown.Close();
        }
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
