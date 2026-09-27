using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

public sealed class ScreenshotWindowCaptureResult(Bitmap image, Rectangle bounds)
{
    public Bitmap Image { get; } = image;
    public Rectangle Bounds { get; } = bounds;
}

public static class ScreenshotWindowCapture
{
    private const int WindowCaptureTimeoutMilliseconds = 5000;

    private sealed record WindowCapture(Bitmap Image, Rectangle Bounds);

    // 打ち切った PrintWindow は止められないので、戻るまで次のウィンドウ撮影を受け付けず、スレッドと画像を溜めない。
    private static Task? _abandonedWindowCapture;

    public static async Task<ScreenshotWindowCaptureResult> CaptureAsync(IntPtr window, Action<string>? fallbackWarning = null)
    {
        if (_abandonedWindowCapture is { IsCompleted: false })
            throw new InvalidOperationException("前に撮影しようとしたウィンドウがまだ応答していません。しばらく待ってから撮り直してください。");
        _abandonedWindowCapture = null;

        var cancellation = new CancellationTokenSource();
        var captureTask = Task.Run(() => CaptureWindow(window, cancellation.Token, fallbackWarning));
        var completed = await Task.WhenAny(captureTask, Task.Delay(WindowCaptureTimeoutMilliseconds));
        if (completed != captureTask)
        {
            cancellation.Cancel();
            _abandonedWindowCapture = captureTask.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) task.Result.Image.Dispose();
                else _ = task.Exception;
                cancellation.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new TimeoutException("ウィンドウの応答を 5 秒以内に確認できませんでした。");
        }
        cancellation.Dispose();
        var result = await captureTask;
        return new ScreenshotWindowCaptureResult(result.Image, result.Bounds);
    }

    private static WindowCapture CaptureWindow(IntPtr window, CancellationToken cancellationToken, Action<string>? fallbackWarning)
    {
        if (!CaptureNativeMethods.IsWindow(window)) throw new InvalidOperationException("撮影するウィンドウはすでに閉じられています。");
        if (!TryGetWindowBounds(window, out var windowBounds)) throw new InvalidOperationException("ウィンドウの撮影範囲を取得できませんでした。");
        return CaptureWindow(window, windowBounds, cancellationToken, fallbackWarning);
    }

    private static WindowCapture CaptureWindow(IntPtr window, Rectangle windowBounds, CancellationToken cancellationToken, Action<string>? fallbackWarning)
    {
        using (var printed = DesktopCapture.CaptureWindow(window, windowBounds, out var succeeded))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (succeeded && !DesktopCapture.IsEntirelyBlack(printed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameBounds = TryGetExtendedFrameBounds(window, out var currentBounds) ? currentBounds : windowBounds;
                var crop = ScreenshotWindowGeometry.CalculateCrop(windowBounds, frameBounds)
                    ?? throw new InvalidOperationException("ウィンドウの撮影範囲を取得できませんでした。");
                return new WindowCapture(printed.Clone(crop.BitmapBounds, PixelFormat.Format32bppArgb), crop.ScreenBounds);
            }
        }

        fallbackWarning?.Invoke($"ウィンドウ撮影で代替のキャプチャ方法を使用します: hwnd={window}。");
        cancellationToken.ThrowIfCancellationRequested();
        if (!CaptureNativeMethods.IsWindow(window)) throw new InvalidOperationException("撮影するウィンドウはすでに閉じられています。");
        cancellationToken.ThrowIfCancellationRequested();
        if (CaptureNativeMethods.IsIconic(window)) CaptureNativeMethods.ShowWindow(window, CaptureNativeMethods.SwRestore);
        cancellationToken.ThrowIfCancellationRequested();
        CaptureNativeMethods.SetForegroundWindow(window);
        Thread.Sleep(120);
        cancellationToken.ThrowIfCancellationRequested();
        var fallbackBounds = TryGetExtendedFrameBounds(window, out var fallbackFrameBounds)
            ? fallbackFrameBounds
            : TryGetWindowBounds(window, out var currentWindowBounds) ? currentWindowBounds : windowBounds;
        return new WindowCapture(DesktopCapture.Capture(fallbackBounds), fallbackBounds);
    }

    private static bool TryGetWindowBounds(IntPtr window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!CaptureNativeMethods.GetWindowRect(window, out var rectangle)) return false;
        bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static bool TryGetExtendedFrameBounds(IntPtr window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (CaptureNativeMethods.DwmGetWindowAttribute(window, CaptureNativeMethods.DwmaExtendedFrameBounds, out CaptureNativeMethods.NativeRect rectangle, Marshal.SizeOf<CaptureNativeMethods.NativeRect>()) != 0) return false;
        bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }
}
