using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultsMatchDesign()
    {
        var settings = new Settings();
        Assert.True(settings.StartWithWindows);
        Assert.True(settings.CheckForUpdatesAutomatically);
        Assert.True(settings.NotifyWhenSaved);
        Assert.False(settings.PlayCaptureSound);
        Assert.Equal("ScreenRecorder_{date}_{time}", settings.FileNameTemplate);
        Assert.False(settings.OrganizeByMonth);
        Assert.Equal(StillImageFormat.Jpeg, settings.ImageFormat);
        Assert.Equal(98, settings.JpegQuality);
        Assert.Equal(PngCompression.Standard, settings.PngCompression);
        Assert.True(settings.CopyImageToClipboard);
        Assert.False(settings.CaptureImageCursor);
        Assert.Equal(0, settings.CaptureDelaySeconds);
        Assert.Equal(CaptureAfterAction.None, settings.AfterCaptureAction);
        Assert.Equal(30, settings.FrameRate);
        Assert.Equal(12, settings.VideoBitrateMbps);
        Assert.True(settings.CaptureVideoCursor);
        Assert.Equal(3, settings.CountdownSeconds);
        Assert.Equal(100, settings.OutputScalePercent);
        Assert.False(settings.HighlightClicks);
        Assert.Equal(EncoderMode.Automatic, settings.Encoder);
        Assert.True(settings.CaptureSystemAudio);
        Assert.False(settings.CaptureMicrophone);
        Assert.Equal(AudioFormat.Aac, settings.AudioFormat);
        Assert.Equal(192, settings.AacBitrateKbps);
        Assert.Equal(192, settings.Mp3BitrateKbps);
        Assert.Equal(new[] { "PrtSc", "Ctrl+PrtSc", "Alt+PrtSc", "Shift+PrtSc", "Ctrl+Shift+PrtSc", "Alt+Shift+PrtSc", "" }, new[]
        {
            settings.ScreenshotRegionShortcut, settings.ScreenshotFullScreenShortcut, settings.ScreenshotWindowShortcut,
            settings.RecordingRegionShortcut, settings.RecordingFullScreenShortcut, settings.RecordingWindowShortcut, settings.PauseRecordingShortcut,
        });
        Assert.Null(settings.SkippedUpdateVersion);
        Assert.EndsWith("ScreenRecorder", settings.StillImageDirectory);
        Assert.EndsWith("ScreenRecorder", settings.VideoDirectory);
    }

    [Fact]
    public void CorruptedJsonIsBackedUpAndDefaultsAreReturned()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ broken");
        var settings = new SettingsRepository(_directory).Load();
        Assert.Equal(98, settings.JpegQuality);
        Assert.Equal("{ broken", File.ReadAllText(Path.Combine(_directory, "settings.json.bak")));
    }

    [Theory]
    [InlineData("{\"jpegQuality\":0}", 98)]
    [InlineData("{\"jpegQuality\":101}", 98)]
    [InlineData("{\"aacBitrateKbps\":200}", 192)]
    [InlineData("{\"frameRate\":45}", 30)]
    public void OutOfRangeValueFallsBackOnlyForThatField(string json, int expected)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), json.Replace("}", ",\"playCaptureSound\":true}"));
        var settings = new SettingsRepository(_directory).Load();
        Assert.Equal(expected, json.Contains("jpegQuality") ? settings.JpegQuality : json.Contains("aacBitrate") ? settings.AacBitrateKbps : settings.FrameRate);
        Assert.True(settings.PlayCaptureSound);
    }

    [Fact]
    public void MissingAndMalformedPropertiesUseTheirDefaultsIndividually()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{\"jpegQuality\":\"bad\",\"playCaptureSound\":true}");
        var settings = new SettingsRepository(_directory).Load();
        Assert.Equal(98, settings.JpegQuality);
        Assert.True(settings.PlayCaptureSound);
        Assert.Equal(30, settings.FrameRate);
    }

    [Fact]
    public void SavedSettingsCanBeReadBack()
    {
        var repository = new SettingsRepository(_directory);
        repository.Save(new Settings { ImageFormat = StillImageFormat.Png, JpegQuality = 73 });
        var loaded = repository.Load();
        Assert.Equal(StillImageFormat.Png, loaded.ImageFormat);
        Assert.Equal(73, loaded.JpegQuality);
    }

    [Fact]
    public void FailedSaveKeepsPreviousSettings()
    {
        var repository = new SettingsRepository(_directory);
        repository.Save(new Settings { JpegQuality = 55 });
        // 一時ファイルのパスをフォルダで塞いで、書き込みを失敗させる。
        Directory.CreateDirectory(Path.Combine(_directory, "settings.json.tmp"));

        Assert.ThrowsAny<Exception>(() => repository.Save(new Settings { JpegQuality = 77 }));
        Assert.Equal(55, repository.Load().JpegQuality);
    }

    [Theory]
    [InlineData(nameof(Settings.CaptureDelaySeconds), new[] { 0, 3, 5, 10 })]
    [InlineData(nameof(Settings.FrameRate), new[] { 15, 24, 30, 60 })]
    [InlineData(nameof(Settings.CountdownSeconds), new[] { 0, 3, 5 })]
    [InlineData(nameof(Settings.OutputScalePercent), new[] { 100, 75, 50 })]
    [InlineData(nameof(Settings.AacBitrateKbps), new[] { 96, 128, 160, 192 })]
    [InlineData(nameof(Settings.Mp3BitrateKbps), new[] { 128, 192, 256, 320 })]
    public void EveryAllowedChoiceSurvivesSaveAndLoad(string propertyName, int[] allowedValues)
    {
        var property = typeof(Settings).GetProperty(propertyName)!;
        var repository = new SettingsRepository(_directory);
        foreach (var value in allowedValues)
        {
            var settings = new Settings();
            property.SetValue(settings, value);
            repository.Save(settings);
            Assert.Equal(value, property.GetValue(repository.Load()));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
