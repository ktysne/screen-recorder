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
        Assert.Equal(DiagnosticLogLevel.Info, settings.DiagnosticLogLevel);
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
        Assert.Equal(WindowScreenshotShortcutTarget.ActiveWindow, settings.WindowScreenshotShortcutTarget);
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
        Assert.All(new[]
        {
            settings.ScreenshotRegionEnabled, settings.ScreenshotFullScreenEnabled, settings.ScreenshotWindowEnabled,
            settings.RecordingRegionEnabled, settings.RecordingFullScreenEnabled, settings.RecordingWindowEnabled,
            settings.PauseRecordingEnabled
        }, Assert.True);
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
    public void MissingAndMalformedShortcutEnabledPropertiesDefaultToTrue()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{\"screenshotRegionEnabled\":false,\"screenshotFullScreenEnabled\":\"bad\",\"screenshotWindowEnabled\":1,\"recordingRegionEnabled\":null}");

        var settings = new SettingsRepository(_directory).Load();

        Assert.False(settings.ScreenshotRegionEnabled);
        Assert.All(new[]
        {
            settings.ScreenshotFullScreenEnabled, settings.ScreenshotWindowEnabled, settings.RecordingRegionEnabled,
            settings.RecordingFullScreenEnabled, settings.RecordingWindowEnabled, settings.PauseRecordingEnabled
        }, Assert.True);
    }

    [Fact]
    public void DisabledShortcutSettingsSurviveSaveLoadAndClone()
    {
        var settings = new Settings
        {
            ScreenshotRegionEnabled = false,
            ScreenshotFullScreenEnabled = false,
            ScreenshotWindowEnabled = false,
            RecordingRegionEnabled = false,
            RecordingFullScreenEnabled = false,
            RecordingWindowEnabled = false,
            PauseRecordingEnabled = false
        };
        var repository = new SettingsRepository(_directory);
        repository.Save(settings);

        var loaded = repository.Load();
        Assert.All(new[]
        {
            loaded.ScreenshotRegionEnabled, loaded.ScreenshotFullScreenEnabled, loaded.ScreenshotWindowEnabled,
            loaded.RecordingRegionEnabled, loaded.RecordingFullScreenEnabled, loaded.RecordingWindowEnabled,
            loaded.PauseRecordingEnabled
        }, Assert.False);
        var clone = loaded.Clone();
        Assert.All(new[]
        {
            clone.ScreenshotRegionEnabled, clone.ScreenshotFullScreenEnabled, clone.ScreenshotWindowEnabled,
            clone.RecordingRegionEnabled, clone.RecordingFullScreenEnabled, clone.RecordingWindowEnabled,
            clone.PauseRecordingEnabled
        }, Assert.False);
    }

    [Theory]
    [InlineData("{\"diagnosticLogLevel\":\"unknown\"}")]
    [InlineData("{\"diagnosticLogLevel\":\"\"}")]
    [InlineData("{\"diagnosticLogLevel\":4}")]
    public void InvalidDiagnosticLogLevelUsesDefault(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), json);

        Assert.Equal(DiagnosticLogLevel.Info, new SettingsRepository(_directory).Load().DiagnosticLogLevel);
    }

    [Theory]
    [InlineData(DiagnosticLogLevel.Silent, "silent")]
    [InlineData(DiagnosticLogLevel.Error, "error")]
    [InlineData(DiagnosticLogLevel.Warn, "warn")]
    [InlineData(DiagnosticLogLevel.Info, "info")]
    [InlineData(DiagnosticLogLevel.Debug, "debug")]
    public void DiagnosticLogLevelIsSavedAsLowercaseAndSurvivesReload(DiagnosticLogLevel level, string settingName)
    {
        var repository = new SettingsRepository(_directory);
        repository.Save(new Settings { DiagnosticLogLevel = level });

        var json = File.ReadAllText(Path.Combine(_directory, "settings.json"));
        Assert.Contains($"\"diagnosticLogLevel\": \"{settingName}\"", json);
        Assert.Equal(level, repository.Load().DiagnosticLogLevel);
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
    public void WindowScreenshotShortcutTargetSurvivesSaveLoadAndClone()
    {
        var repository = new SettingsRepository(_directory);
        repository.Save(new Settings { WindowScreenshotShortcutTarget = WindowScreenshotShortcutTarget.SelectWindow });

        var loaded = repository.Load();

        Assert.Equal(WindowScreenshotShortcutTarget.SelectWindow, loaded.WindowScreenshotShortcutTarget);
        Assert.Equal(WindowScreenshotShortcutTarget.SelectWindow, loaded.Clone().WindowScreenshotShortcutTarget);
    }

    [Fact]
    public void EmptyFilenameTemplateCanBeSavedForCaptureFallback()
    {
        var repository = new SettingsRepository(_directory);
        repository.Save(new Settings { FileNameTemplate = string.Empty });

        Assert.Equal(string.Empty, repository.Load().FileNameTemplate);
    }

    [Fact]
    public void UnassignedShortcutStaysUnassignedAfterReload()
    {
        var repository = new SettingsRepository(_directory);
        repository.Save(new Settings { ScreenshotRegionShortcut = "" });
        Assert.Equal("", repository.Load().ScreenshotRegionShortcut);
    }

    [Fact]
    public void MissingShortcutFallsBackToDefault()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{\"screenshotRegionShortcut\":5}");
        Assert.Equal("PrtSc", new SettingsRepository(_directory).Load().ScreenshotRegionShortcut);
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

    [Fact]
    public void IntSettingDefaultsAreValidAndUsedBySettings()
    {
        var settings = new Settings();
        foreach (var setting in SettingsSchema.IntSettings)
        {
            Assert.True(setting.IsValid(setting.Default), setting.Name);
            Assert.Equal(setting.Default, setting.Get(settings));
        }
    }

    [Theory]
    [InlineData(nameof(Settings.CaptureDelaySeconds), new[] { 0, 3, 5, 10 })]
    [InlineData(nameof(Settings.FrameRate), new[] { 15, 24, 30, 60 })]
    [InlineData(nameof(Settings.CountdownSeconds), new[] { 0, 3, 5 })]
    [InlineData(nameof(Settings.OutputScalePercent), new[] { 100, 75, 50 })]
    [InlineData(nameof(Settings.Mp3BitrateKbps), new[] { 128, 192, 256, 320 })]
    public void ChoicesMatchDesign(string settingName, int[] expected)
    {
        var setting = SettingsSchema.IntSettings.OfType<IntChoiceSetting>().Single(item => item.Name == settingName);
        Assert.Equal(expected, setting.Choices);
    }

    [Theory]
    [InlineData(nameof(Settings.JpegQuality), 1, 100)]
    [InlineData(nameof(Settings.VideoBitrateMbps), 1, 100)]
    public void RangesMatchDesign(string settingName, int min, int max)
    {
        var setting = SettingsSchema.IntSettings.OfType<IntRangeSetting>().Single(item => item.Name == settingName);
        Assert.Equal((min, max), (setting.Min, setting.Max));
    }

    [Fact]
    public void AacBitrateChoicesMatchRecordingLibraryValues()
    {
        // 録画ライブラリの音声ビットレート列挙はこの 4 値だけを持つ。
        Assert.Equal(new[] { 96, 128, 160, 192 }, SettingsSchema.AacBitrateKbps.Choices);
    }

    [Fact]
    public void EveryAllowedIntSettingValueSurvivesSaveAndLoad()
    {
        var repository = new SettingsRepository(_directory);
        foreach (var setting in SettingsSchema.IntSettings)
        {
            var values = setting switch
            {
                IntChoiceSetting choice => choice.Choices,
                IntRangeSetting range => Enumerable.Range(range.Min, range.Max - range.Min + 1),
                _ => throw new InvalidOperationException($"未知の整数設定です: {setting.Name}")
            };
            foreach (var value in values)
            {
                var settings = new Settings();
                setting.Set(settings, value);
                repository.Save(settings);
                Assert.Equal(value, setting.Get(repository.Load()));
            }
        }
    }

    [Fact]
    public void InvalidIntegerSettingDoesNotChangeExistingFileOrLeaveTemporaryFile()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        var temporaryPath = settingsPath + ".tmp";
        var repository = new SettingsRepository(_directory);
        repository.Save(new Settings { FrameRate = SettingsSchema.FrameRate.Default });
        var originalContents = File.ReadAllText(settingsPath);
        var invalidSettings = new Settings { OutputScalePercent = 60 };

        var exception = Assert.Throws<ArgumentException>(() => repository.Save(invalidSettings));

        Assert.Contains(nameof(Settings.OutputScalePercent), exception.Message);
        Assert.Equal(originalContents, File.ReadAllText(settingsPath));
        Assert.False(File.Exists(temporaryPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
