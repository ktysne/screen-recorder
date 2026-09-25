using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class ScreenshotCaptureResult(Bitmap image, string filePath) : IDisposable
{
    public Bitmap Image { get; } = image;
    public string FilePath { get; } = filePath;
    public void Dispose() => Image.Dispose();
}

internal sealed class ScreenshotCaptureService(DailyLog log)
{
    private const int WindowCaptureTimeoutMilliseconds = 5000;

    private sealed record WindowCapture(Bitmap Image, Rectangle Bounds);

    public async Task<ScreenshotCaptureResult?> CaptureAsync(ScreenshotMode mode, Settings settings)
    {
        var delayedRegion = mode == ScreenshotMode.Region && settings.CaptureDelaySeconds > 0;
        using var selection = mode == ScreenshotMode.Full
            ? null
            : await CaptureSelection.SelectAsync(mode, freezeDesktop: !delayedRegion);
        if (mode != ScreenshotMode.Full && selection is null) return null;

        var captureBounds = mode == ScreenshotMode.Full
            ? Screen.FromPoint(Cursor.Position).Bounds
            : selection!.Bounds;
        var displayBounds = mode == ScreenshotMode.Full ? captureBounds : Screen.FromRectangle(captureBounds).Bounds;
        if (settings.CaptureDelaySeconds > 0)
            await WaitWithCountdownAsync(settings.CaptureDelaySeconds, displayBounds);

        Bitmap? image = null;
        try
        {
            if (mode == ScreenshotMode.Window)
            {
                var windowCapture = await CaptureWindowAsync(selection!);
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
            var filePath = await Task.Run(() =>
            {
                DesktopCapture.MakeOpaque(image);
                return SaveImage(image, settings, mode, selection?.WindowTitle, capturedAt);
            });
            var result = new ScreenshotCaptureResult(image, filePath);
            image = null;
            return result;
        }
        finally
        {
            image?.Dispose();
        }
    }

    private async Task WaitWithCountdownAsync(int seconds, Rectangle displayBounds)
    {
        using var countdown = new CaptureCountdownForm(displayBounds);
        countdown.Show();
        if (!countdown.ExcludeFromCapture()) log.Write("SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed for screenshot countdown");
        for (var remaining = seconds; remaining > 0; remaining--)
        {
            countdown.SetRemainingSeconds(remaining);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        countdown.Close();
    }

    private static async Task<WindowCapture> CaptureWindowAsync(ScreenshotSelection selection)
    {
        var cancellation = new CancellationTokenSource();
        var captureTask = Task.Run(() => CaptureWindow(selection, cancellation.Token));
        var completed = await Task.WhenAny(captureTask, Task.Delay(WindowCaptureTimeoutMilliseconds));
        if (completed != captureTask)
        {
            cancellation.Cancel();
            _ = captureTask.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) task.Result.Image.Dispose();
                else _ = task.Exception;
                cancellation.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new TimeoutException("ウィンドウの応答を 5 秒以内に確認できませんでした。");
        }
        cancellation.Dispose();
        return await captureTask;
    }

    private static WindowCapture CaptureWindow(ScreenshotSelection selection, CancellationToken cancellationToken)
    {
        if (!NativeMethods.IsWindow(selection.Window)) throw new InvalidOperationException("選択したウィンドウはすでに閉じられています。");
        if (!TryGetWindowBounds(selection.Window, out var windowBounds)) throw new InvalidOperationException("ウィンドウの撮影範囲を取得できませんでした。");
        using (var printed = DesktopCapture.CaptureWindow(selection.Window, windowBounds, out var succeeded))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (succeeded && !DesktopCapture.IsEntirelyBlack(printed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameBounds = TryGetExtendedFrameBounds(selection.Window, out var currentBounds) ? currentBounds : windowBounds;
                var crop = ScreenshotWindowGeometry.CalculateCrop(windowBounds, frameBounds)
                    ?? throw new InvalidOperationException("ウィンドウの撮影範囲を取得できませんでした。");
                return new WindowCapture(printed.Clone(crop.BitmapBounds, PixelFormat.Format32bppArgb), crop.ScreenBounds);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.IsWindow(selection.Window)) throw new InvalidOperationException("選択したウィンドウはすでに閉じられています。");
        cancellationToken.ThrowIfCancellationRequested();
        if (NativeMethods.IsIconic(selection.Window)) NativeMethods.ShowWindow(selection.Window, NativeMethods.SwRestore);
        cancellationToken.ThrowIfCancellationRequested();
        NativeMethods.SetForegroundWindow(selection.Window);
        Thread.Sleep(120);
        cancellationToken.ThrowIfCancellationRequested();
        var fallbackBounds = TryGetExtendedFrameBounds(selection.Window, out var fallbackFrameBounds)
            ? fallbackFrameBounds
            : TryGetWindowBounds(selection.Window, out var currentWindowBounds) ? currentWindowBounds : windowBounds;
        return new WindowCapture(DesktopCapture.Capture(fallbackBounds), fallbackBounds);
    }

    private static bool TryGetWindowBounds(IntPtr window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!NativeMethods.GetWindowRect(window, out var rectangle)) return false;
        bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static bool TryGetExtendedFrameBounds(IntPtr window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DwmaExtendedFrameBounds, out NativeMethods.NativeRect rectangle, Marshal.SizeOf<NativeMethods.NativeRect>()) != 0) return false;
        bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static string SaveImage(Bitmap image, Settings settings, ScreenshotMode mode, string? windowTitle, DateTime capturedAt)
    {
        var directory = settings.StillImageDirectory;
        Directory.CreateDirectory(directory);
        var extension = settings.ImageFormat == StillImageFormat.Png ? ".png" : ".jpg";
        while (true)
        {
            var path = ScreenshotFileNaming.GetAvailablePath(
                directory,
                settings.OrganizeByMonth,
                capturedAt,
                mode,
                windowTitle,
                settings.FileNameTemplate,
                extension,
                File.Exists);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var retryWithAvailableFinalPath = false;
            for (var attempt = 1; ; attempt++)
            {
                var temporaryPath = ScreenshotFileNaming.GetTemporaryPath(path, attempt);
                var temporaryCreated = false;
                try
                {
                    using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    temporaryCreated = true;
                    if (settings.ImageFormat == StillImageFormat.Png) WritePng(stream, image, settings.PngCompression);
                    else WriteJpeg(stream, image, settings.JpegQuality);
                    stream.Flush(flushToDisk: true);
                }
                catch (IOException exception) when (!temporaryCreated && IsAlreadyExists(exception))
                {
                    continue;
                }
                catch
                {
                    TryDeleteTemporaryFile(temporaryPath);
                    throw;
                }

                try
                {
                    File.Move(temporaryPath, path);
                    return path;
                }
                catch (IOException exception) when (IsAlreadyExists(exception))
                {
                    TryDeleteTemporaryFile(temporaryPath);
                    retryWithAvailableFinalPath = true;
                    break;
                }
                catch
                {
                    TryDeleteTemporaryFile(temporaryPath);
                    throw;
                }
            }

            if (retryWithAvailableFinalPath) continue;
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool IsAlreadyExists(IOException exception)
    {
        var errorCode = exception.HResult & 0xffff;
        return errorCode is 80 or 183;
    }

    private static void WritePng(Stream stream, Bitmap image, PngCompression compression)
    {
        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(image.Width * 4);
            var pixels = new byte[checked(rowBytes * image.Height)];
            for (var row = 0; row < image.Height; row++) Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * rowBytes, rowBytes);
            PngWriter.Write(stream, image.Width, image.Height, pixels, compression);
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    private static void WriteJpeg(Stream stream, Bitmap image, int quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(candidate => candidate.FormatID == ImageFormat.Jpeg.Guid)
            ?? throw new InvalidOperationException("JPEG のエンコーダーが見つかりません。");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        image.Save(stream, codec, parameters);
    }
}
