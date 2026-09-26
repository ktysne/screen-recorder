using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class DiagnosticLogTests
{
    [Theory]
    [InlineData(DiagnosticLogLevel.Silent, "silent")]
    [InlineData(DiagnosticLogLevel.Error, "error")]
    [InlineData(DiagnosticLogLevel.Warn, "warn")]
    [InlineData(DiagnosticLogLevel.Info, "info")]
    [InlineData(DiagnosticLogLevel.Debug, "debug")]
    public void LevelSettingNamesRoundTrip(DiagnosticLogLevel level, string settingName)
    {
        Assert.Equal(settingName, level.ToSettingName());
        Assert.Equal(level, DiagnosticLogLevels.FromSettingName(settingName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("INFO")]
    public void UnknownAndEmptySettingNamesUseInfo(string? name) =>
        Assert.Equal(DiagnosticLogLevel.Info, DiagnosticLogLevels.FromSettingName(name));

    [Theory]
    [InlineData(DiagnosticLogLevel.Error, DiagnosticLogLevel.Error, true)]
    [InlineData(DiagnosticLogLevel.Error, DiagnosticLogLevel.Warn, false)]
    [InlineData(DiagnosticLogLevel.Warn, DiagnosticLogLevel.Error, true)]
    [InlineData(DiagnosticLogLevel.Info, DiagnosticLogLevel.Debug, false)]
    [InlineData(DiagnosticLogLevel.Debug, DiagnosticLogLevel.Debug, true)]
    [InlineData(DiagnosticLogLevel.Debug, DiagnosticLogLevel.Silent, false)]
    [InlineData(DiagnosticLogLevel.Silent, DiagnosticLogLevel.Error, false)]
    public void RecordingThresholdIncludesOnlyConfiguredLevels(DiagnosticLogLevel threshold, DiagnosticLogLevel entry, bool expected) =>
        Assert.Equal(expected, threshold.ShouldRecord(entry));

    [Fact]
    public void LineUsesFixedTimestampAndPaddedLevel()
    {
        var timestamp = new DateTime(2026, 9, 26, 14, 53, 1, 123, DateTimeKind.Local);

        Assert.Equal("2026-09-26 14:53:01.123 INFO  [record] 録画を保存しました。", DiagnosticLogFormatting.FormatLine(timestamp, DiagnosticLogLevel.Info, "record", "録画を保存しました。"));
        Assert.Equal("2026-09-26 14:53:01.123 WARN  [audio] 入力元がありません。", DiagnosticLogFormatting.FormatLine(timestamp, DiagnosticLogLevel.Warn, "audio", "入力元がありません。"));
    }

    [Fact]
    public void ControlCharactersBecomeSpacesAndJapaneseRemainsIntact()
    {
        Assert.Equal("撮影に失敗しました  理由: 権限 なし", DiagnosticLogFormatting.Sanitize("撮影に失敗しました\r\n理由: 権限\tなし\0"));
    }

    [Fact]
    public void LogFileNameUsesFixedWidthLocalTimestamp()
    {
        var timestamp = new DateTime(2026, 9, 26, 14, 53, 1, 7);

        Assert.Equal("screen-recorder-20260926-145301-007.log", DiagnosticLogFormatting.MakeFileName(timestamp));
        Assert.True(DiagnosticLogFormatting.IsLogFileName(DiagnosticLogFormatting.MakeFileName(timestamp)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("memo.txt")]
    [InlineData("screen-recorder.log")]
    [InlineData("screen-recorder-20260926-145301.log")]
    [InlineData("screen-recorder-2026o926-145301-007.log")]
    [InlineData("screen-recorder-20260926-145301-007.log.bak")]
    [InlineData("Screen-Recorder-20260926-145301-007.log")]
    public void UnrelatedOrMalformedNamesAreNotLogFiles(string name) =>
        Assert.False(DiagnosticLogFormatting.IsLogFileName(name));

    [Fact]
    public void FileNamesSortByStartupTimestamp()
    {
        var earlier = DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 26, 14, 53, 1, 7));
        var later = DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 26, 14, 53, 1, 8));
        var nextDay = DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 27, 0, 0, 0));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.True(string.CompareOrdinal(later, nextDay) < 0);
    }

    [Fact]
    public void RotationDeletesOldestMatchingNamesAndIgnoresOtherFiles()
    {
        var names = new[]
        {
            "memo.txt",
            "screen-recorder-20260926-145301-003.log",
            "screen-recorder-20260926-145301-001.log",
            "ScreenRecorder.settings",
            "screen-recorder-20260926-145301-002.log"
        };

        Assert.Equal(["screen-recorder-20260926-145301-001.log", "screen-recorder-20260926-145301-002.log"], DiagnosticLogFormatting.SelectFilesToDelete(names, 1));
        Assert.Empty(DiagnosticLogFormatting.SelectFilesToDelete(names, 3));
    }

    [Fact]
    public void RotationLeavesRoomForTheNextStartupLog()
    {
        var names = Enumerable.Range(1, DiagnosticLogFormatting.MaximumFiles)
            .Select(milliseconds => DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 26, 14, 53, 1, milliseconds)))
            .ToArray();

        Assert.Equal([names[0]], DiagnosticLogFormatting.SelectFilesToDelete(names, DiagnosticLogFormatting.MaximumFiles - 1));
    }

    [Fact]
    public void NewLogNameUsesTheCurrentTimeWhenItIsLaterThanExistingLogs()
    {
        var existing = new[] { "screen-recorder-20260926-145301-007.log", "memo.txt" };

        Assert.Equal("screen-recorder-20260926-150000-000.log", DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 26, 15, 0, 0), existing));
    }

    [Fact]
    public void NewLogNameFollowsTheLatestLogWhenTheClockWentBack()
    {
        var existing = new[] { "screen-recorder-20260926-145301-007.log", "screen-recorder-20260926-145959-999.log" };

        var name = DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 26, 13, 0, 0), existing);

        Assert.Equal("screen-recorder-20260926-150000-000.log", name);
        Assert.DoesNotContain(name, DiagnosticLogFormatting.SelectFilesToDelete(existing.Append(name), 1));
    }

    [Fact]
    public void NewLogNameFallsBackToTheCurrentTimeWhenTheLatestNameIsNotADate()
    {
        // 数字だけで構成されていれば名前の形には一致するが、13 月は日付として読めない。
        var existing = new[] { "screen-recorder-20261399-000000-000.log" };

        Assert.Equal("screen-recorder-20260926-150000-000.log", DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 26, 15, 0, 0), existing));
    }

    [Fact]
    public void HeaderStatesVersionLevelPrivacyAndExcludedContent()
    {
        var header = DiagnosticLogFormatting.CreateHeader("1.2.3", DiagnosticLogLevel.Warn);

        Assert.Contains("1.2.3", header[1]);
        Assert.Contains("warn", header[2]);
        Assert.Contains("外部へ送信されません", header[3]);
        Assert.Contains("クリップボード", header[4]);
    }
}
