using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

public static class ScreenshotImageStorage
{
    public static string SaveImage(Bitmap image, Settings settings, ScreenshotMode mode, string? windowTitle, DateTime capturedAt)
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
            try
            {
                return SaveImageToPath(image, path, settings.ImageFormat, settings.PngCompression, settings.JpegQuality, overwrite: false);
            }
            catch (IOException exception) when (CaptureText.IsAlreadyExists(exception) && File.Exists(path))
            {
            }
        }
    }

    public static string SaveImageToPath(
        Bitmap image,
        string path,
        StillImageFormat format,
        PngCompression pngCompression,
        int jpegQuality,
        bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        for (var attempt = 1; ; attempt++)
        {
            var temporaryPath = ScreenshotFileNaming.GetTemporaryPath(path, attempt);
            var temporaryCreated = false;
            try
            {
                using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                temporaryCreated = true;
                WriteImage(stream, image, format, pngCompression, jpegQuality);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException exception) when (!temporaryCreated && CaptureText.IsAlreadyExists(exception))
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
                File.Move(temporaryPath, path, overwrite);
                return path;
            }
            catch
            {
                TryDeleteTemporaryFile(temporaryPath);
                throw;
            }
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void WriteImage(Stream stream, Bitmap image, StillImageFormat format, PngCompression pngCompression, int jpegQuality)
    {
        if (format == StillImageFormat.Png) WritePng(stream, image, pngCompression);
        else WriteJpeg(stream, image, jpegQuality);
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
