using System.Text.Json;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class SettingsFileReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void CorruptedJsonIsReportedWithoutCreatingBackup()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{ broken");

        var result = SettingsFileReader.Read(path);

        Assert.Contains(result.Issues, issue => issue.Code == "invalidJson");
        Assert.Equal("{ broken", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void InvalidValuesUnknownPropertiesAndShortcutConflictsAreReported()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """
            {
              "jpegQuality": 0,
              "imageFormat": "bmp",
              "futureSetting": true,
              "screenshotRegionShortcut": "Ctrl+A",
              "screenshotFullScreenShortcut": "Ctrl+A",
              "screenshotWindowShortcut": "not-a-shortcut"
            }
            """);

        var result = SettingsFileReader.Read(path);

        Assert.Contains(result.Issues, issue => issue.Code == "valueOutOfRange" && issue.Property == nameof(Settings.JpegQuality));
        Assert.Contains(result.Issues, issue => issue.Code == "invalidValue" && issue.Property == nameof(Settings.ImageFormat));
        Assert.Contains(result.Issues, issue => issue.Code == "unknownProperty" && issue.Property == "futureSetting");
        Assert.Contains(result.Issues, issue => issue.Code == "duplicateShortcut");
        Assert.Contains(result.Issues, issue => issue.Code == "invalidShortcut");
        Assert.Equal(SettingsSchema.JpegQuality.Default, result.Settings.JpegQuality);
    }

    [Fact]
    public void MissingPropertiesAreListedWhenDefaultsAreApplied()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"playCaptureSound\":true}");

        var result = SettingsFileReader.Read(path);

        Assert.True(result.Settings.PlayCaptureSound);
        Assert.Contains(nameof(Settings.JpegQuality), result.DefaultedProperties);
        Assert.DoesNotContain(nameof(Settings.PlayCaptureSound), result.DefaultedProperties);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "playCaptureSound": false, "jpegQuality": 5000, "imageFormat": "jpeg", "diagnosticLogLevel": "debug" }""")]
    [InlineData("""{ "videoDirectory": "  ", "fileNameTemplate": "", "microphoneDeviceId": 3, "encoder": "nope", "screenshotRegionShortcut": "" }""")]
    [InlineData("""{ "diagnosticLogLevel": "verbose", "confirmedVideoDirectory": null, "startWithWindows": "yes" }""")]
    public void ReadSettingsMatchWhatTheAppLoads(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), json);

        var read = SettingsFileReader.Read(Path.Combine(_directory, "settings.json")).Settings;
        var loaded = new SettingsRepository(_directory).Load();

        Assert.Equal(JsonSerializer.Serialize(loaded), JsonSerializer.Serialize(read));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
