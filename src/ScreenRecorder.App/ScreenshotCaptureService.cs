using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class ScreenshotCaptureResult(Bitmap image, DateTime capturedAt, ScreenshotMode mode, string? windowTitle) : IDisposable
{
    public Bitmap Image { get; } = image;
    public DateTime CapturedAt { get; } = capturedAt;
    public ScreenshotMode Mode { get; } = mode;
    public string? WindowTitle { get; } = windowTitle;
    public void Dispose() => Image.Dispose();
}

internal sealed class ScreenshotCaptureService
{
    public async Task<ScreenshotCaptureResult?> CaptureAsync(ScreenshotMode mode, Settings settings, IntPtr? targetWindow = null)
    {
        var delayedRegion = mode == ScreenshotMode.Region && settings.CaptureDelaySeconds > 0;
        ScreenshotSelection? shortcutSelection = null;
        if (mode == ScreenshotMode.Window && targetWindow is { } requestedWindow)
        {
            if (CaptureSelection.TryCreateWindowSelection(requestedWindow, out shortcutSelection, out var reason))
            {
                DiagnosticLog.Info(DiagnosticLogTags.Capture, $"ショートカットの撮影対象に前面のウィンドウを使います: hwnd={requestedWindow}。");
            }
            else
            {
                DiagnosticLog.Info(DiagnosticLogTags.Capture, $"ショートカットの前面ウィンドウを撮影できないため、選択画面を表示します: 理由={reason}。");
            }
        }
        using var selection = mode == ScreenshotMode.Full
            ? null
            : shortcutSelection ?? await CaptureSelection.SelectAsync(mode, freezeDesktop: !delayedRegion);
        if (mode != ScreenshotMode.Full && selection is null) return null;

        var captureBounds = mode == ScreenshotMode.Full
            ? Screen.FromPoint(Cursor.Position).Bounds
            : selection!.Bounds;
        DiagnosticLog.Info(DiagnosticLogTags.Capture, $"静止画の撮影を開始しました: 方法={CaptureText.CaptureMethodName(mode)}、範囲=({captureBounds.X},{captureBounds.Y}) {captureBounds.Width}x{captureBounds.Height}。");
        var displayBounds = mode == ScreenshotMode.Full ? captureBounds : Screen.FromRectangle(captureBounds).Bounds;
        if (settings.CaptureDelaySeconds > 0)
            await WaitWithCountdownAsync(settings.CaptureDelaySeconds, displayBounds);

        Bitmap? image = null;
        try
        {
            if (mode == ScreenshotMode.Window)
            {
                var windowCapture = await ScreenshotWindowCapture.CaptureAsync(
                    selection!.Window,
                    message => DiagnosticLog.Warn(DiagnosticLogTags.Capture, message));
                image = windowCapture.Image;
                captureBounds = windowCapture.Bounds;
            }
            else
            {
                image = mode switch
                {
                    ScreenshotMode.Full => DesktopCapture.Capture(captureBounds),
                    ScreenshotMode.Region when delayedRegion => DesktopCapture.Capture(captureBounds),
                    ScreenshotMode.Region => selection!.DetachFrozenImage(),
                    _ => throw new ArgumentOutOfRangeException(nameof(mode))
                };
            }
            if (settings.CaptureImageCursor) CursorOverlay.Draw(image, captureBounds);

            var capturedAt = DateTime.Now;
            await Task.Run(() => DesktopCapture.MakeOpaque(image));
            var result = new ScreenshotCaptureResult(image, capturedAt, mode, selection?.WindowTitle);
            image = null;
            return result;
        }
        finally
        {
            image?.Dispose();
        }
    }

    public Task<string> SaveImageAsync(ScreenshotCaptureResult result, Settings settings) => Task.Run(() =>
    {
        var path = ScreenshotImageStorage.SaveImage(result.Image, settings, result.Mode, result.WindowTitle, result.CapturedAt);
        DiagnosticLog.Info(DiagnosticLogTags.Capture, $"静止画を保存しました: {path}");
        return path;
    });

    private async Task WaitWithCountdownAsync(int seconds, Rectangle displayBounds)
    {
        await CaptureCountdown.RunAsync(CaptureCountdownKind.Screenshot, seconds, displayBounds, CancellationToken.None);
    }
}
