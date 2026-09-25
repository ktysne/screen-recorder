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
    public async Task<ScreenshotCaptureResult?> CaptureAsync(ScreenshotMode mode, Settings settings)
    {
        using var selection = mode == ScreenshotMode.Full ? null : await CaptureSelection.SelectAsync(mode);
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
            image = mode switch
            {
                ScreenshotMode.Full => DesktopCapture.Capture(captureBounds),
                ScreenshotMode.Region => selection!.DetachFrozenImage(),
                ScreenshotMode.Window => await CaptureWindowAsync(selection!),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            if (mode == ScreenshotMode.Window && TryGetExtendedFrameBounds(selection!.Window, out var windowBounds)) captureBounds = windowBounds;
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

    private static async Task<Bitmap> CaptureWindowAsync(ScreenshotSelection selection)
    {
        if (!NativeMethods.IsWindow(selection.Window)) throw new InvalidOperationException("選択したウィンドウはすでに閉じられています。");
        var bounds = TryGetExtendedFrameBounds(selection.Window, out var currentBounds) ? currentBounds : selection.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("ウィンドウの撮影範囲を取得できませんでした。");
        var printed = DesktopCapture.CaptureWindow(selection.Window, bounds, out var succeeded);
        if (succeeded && !DesktopCapture.IsEntirelyBlack(printed)) return printed;
        printed.Dispose();

        if (!NativeMethods.IsWindow(selection.Window)) throw new InvalidOperationException("選択したウィンドウはすでに閉じられています。");
        if (NativeMethods.IsIconic(selection.Window)) NativeMethods.ShowWindow(selection.Window, NativeMethods.SwRestore);
        NativeMethods.SetForegroundWindow(selection.Window);
        await Task.Delay(120);
        if (TryGetExtendedFrameBounds(selection.Window, out currentBounds)) bounds = currentBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("ウィンドウの撮影範囲を取得できませんでした。");
        return DesktopCapture.Capture(bounds);
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
            var created = false;
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                if (settings.ImageFormat == StillImageFormat.Png) WritePng(stream, image, settings.PngCompression);
                else WriteJpeg(stream, image, settings.JpegQuality);
                return path;
            }
            catch (IOException exception) when (!created && IsAlreadyExists(exception))
            {
            }
            catch
            {
                if (created)
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
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
