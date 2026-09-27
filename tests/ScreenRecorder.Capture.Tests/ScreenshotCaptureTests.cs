using System.Drawing;
using System.Text.Json;
using ScreenRecorder.Capture;
using ScreenRecorder.Cli;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Capture.Tests;

public sealed class ScreenshotCaptureTests
{
    [DesktopFact]
    public void SaveImageToPathReplacesAnExistingFileAfterWritingTheTemporaryImage()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "screenshot.png");
        File.WriteAllText(path, "existing");
        try
        {
            using var image = new Bitmap(7, 5);
            ScreenshotImageStorage.SaveImageToPath(image, path, StillImageFormat.Png, PngCompression.Fast, 98, overwrite: true);

            using var saved = Image.FromFile(path);
            Assert.Equal(new Size(7, 5), saved.Size);
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory)));
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [DesktopFact]
    public void CliScreenshotSavesDisplayAsPngWithDisplayDimensions()
    {
        var display = Screen.AllScreens[0];
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "display.png");
        try
        {
            var invocation = Run(CliEnvironment.Create(), "screenshot", "--display", "0", "--defaults", "-o", path);

            Assert.True(invocation.Code == 0, invocation.Output);
            using var document = JsonDocument.Parse(invocation.Output);
            var result = document.RootElement.GetProperty("result");
            Assert.Equal("png", result.GetProperty("format").GetString());
            Assert.Equal(display.Bounds.Width, result.GetProperty("width").GetInt32());
            Assert.Equal(display.Bounds.Height, result.GetProperty("height").GetInt32());
            using var saved = Image.FromFile(path);
            Assert.Equal(display.Bounds.Size, saved.Size);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [DesktopFact]
    public void CliScreenshotSavesRectangleAsJpegWithRequestedDimensions()
    {
        var display = Screen.AllScreens[0];
        var bounds = new Rectangle(display.Bounds.X, display.Bounds.Y, Math.Min(320, display.Bounds.Width), Math.Min(240, display.Bounds.Height));
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "region.jpeg");
        try
        {
            var invocation = Run(CliEnvironment.Create(), "screenshot", "--rect", $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}", "--defaults", "-o", path);

            Assert.True(invocation.Code == 0, invocation.Output);
            using var document = JsonDocument.Parse(invocation.Output);
            var result = document.RootElement.GetProperty("result");
            Assert.Equal("jpeg", result.GetProperty("format").GetString());
            Assert.Equal(bounds.Width, result.GetProperty("width").GetInt32());
            Assert.Equal(bounds.Height, result.GetProperty("height").GetInt32());
            using var saved = Image.FromFile(path);
            Assert.Equal(bounds.Size, saved.Size);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static (int Code, string Output) Run(CliEnvironment environment, params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return (CliApplication.Run(arguments, output, error, environment), output.ToString());
    }
}
