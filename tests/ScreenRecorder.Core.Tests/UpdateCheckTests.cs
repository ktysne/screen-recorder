using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class UpdateCheckTests
{
    private static string Manifest(string version) =>
        $$"""{ "schema": 1, "latest": { "version": "{{version}}", "url": "https://ktysne.info/screen-recorder/archives/a.zip", "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" } }""";

    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.2.3", "1.3.0", -1)]
    [InlineData("1.9.9", "2.0.0", -1)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("01.2.3", "1.2.3", 0)]
    public void VersionsCompareNumerically(string left, string right, int expectedSign)
    {
        Assert.True(UpdateVersion.TryParse(left, out var l));
        Assert.True(UpdateVersion.TryParse(right, out var r));
        Assert.Equal(expectedSign, Math.Sign(l.CompareTo(r)));
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.1.0+abcdef", true)]
    [InlineData(" 0.1.0 ", true)]
    [InlineData("0.1", false)]
    [InlineData("0.1.0-beta", false)]
    [InlineData(null, false)]
    public void ApplicationVersionIgnoresBuildMetadataOnly(string? text, bool expected)
    {
        Assert.Equal(expected, UpdateVersion.TryParseApplicationVersion(text, out _));
    }

    [Fact]
    public void NewerVersionIsAvailable()
    {
        var result = UpdateCheckEvaluator.Evaluate("0.1.0", Manifest("0.2.0"), null, UpdateCheckTrigger.Automatic);
        Assert.Equal(UpdateCheckKind.Available, result.Kind);
        Assert.Equal(new UpdateVersion(0, 2, 0), result.Manifest!.Version);
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.1.9")]
    public void SameOrOlderVersionIsUpToDate(string latest)
    {
        Assert.Equal(UpdateCheckKind.UpToDate, UpdateCheckEvaluator.Evaluate("0.2.0", Manifest(latest), null, UpdateCheckTrigger.Manual).Kind);
    }

    [Fact]
    public void SkippedVersionIsSilencedOnlyForAutomaticCheck()
    {
        Assert.Equal(UpdateCheckKind.Skipped, UpdateCheckEvaluator.Evaluate("0.1.0", Manifest("0.2.0"), "0.2.0", UpdateCheckTrigger.Automatic).Kind);
        Assert.Equal(UpdateCheckKind.Available, UpdateCheckEvaluator.Evaluate("0.1.0", Manifest("0.2.0"), "0.2.0", UpdateCheckTrigger.Manual).Kind);
    }

    [Fact]
    public void SkippingOneVersionDoesNotSilenceNewerVersion()
    {
        Assert.Equal(UpdateCheckKind.Available, UpdateCheckEvaluator.Evaluate("0.1.0", Manifest("0.3.0"), "0.2.0", UpdateCheckTrigger.Automatic).Kind);
    }

    [Fact]
    public void UnreadableSkippedVersionIsIgnored()
    {
        Assert.Equal(UpdateCheckKind.Available, UpdateCheckEvaluator.Evaluate("0.1.0", Manifest("0.2.0"), "garbage", UpdateCheckTrigger.Automatic).Kind);
    }

    [Fact]
    public void InvalidManifestFailsWithReason()
    {
        var result = UpdateCheckEvaluator.Evaluate("0.1.0", "{}", null, UpdateCheckTrigger.Manual);
        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
        Assert.Null(result.Manifest);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData(UpdateCheckTrigger.Manual, "手動", DiagnosticLogLevel.Error)]
    [InlineData(UpdateCheckTrigger.Automatic, "自動", DiagnosticLogLevel.Warn)]
    public void UpdateCheckLogUsesTriggerSpecificNameAndFailureLevel(
        UpdateCheckTrigger trigger,
        string expectedName,
        DiagnosticLogLevel expectedLevel)
    {
        Assert.Equal(expectedName, UpdateCheckLog.TriggerName(trigger));
        Assert.Equal(expectedLevel, UpdateCheckLog.FailureLevel(trigger));
    }

    [Theory]
    [InlineData(UpdateCheckTrigger.Manual, "詳細", "更新の確認に失敗しました: きっかけ=手動; 詳細")]
    [InlineData(UpdateCheckTrigger.Automatic, "詳細", "更新の確認に失敗しました: きっかけ=自動; 詳細")]
    public void UpdateCheckLogFormatsFailureMessage(UpdateCheckTrigger trigger, string detail, string expected)
    {
        Assert.Equal(expected, UpdateCheckLog.FailureMessage(trigger, detail));
    }

    [Theory]
    [InlineData(UpdateCheckKind.Available, "あり")]
    [InlineData(UpdateCheckKind.Skipped, "あり")]
    [InlineData(UpdateCheckKind.UpToDate, "なし")]
    public void UpdateCheckLogFormatsCompletedMessage(UpdateCheckKind kind, string expectedAvailability)
    {
        var result = new UpdateCheckResult(kind, new UpdateManifest(new UpdateVersion(0, 2, 0), "https://ktysne.info/screen-recorder/a.zip", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null), null);

        Assert.Equal(
            $"更新の確認が完了しました: きっかけ=自動、新しい版={expectedAvailability}、現在の版=0.1.0、最新の版=0.2.0。",
            UpdateCheckLog.CompletedMessage(UpdateCheckTrigger.Automatic, result, "0.1.0"));
    }

    [Fact]
    public void FailedResultKeepsDiagnosticDetailSeparateFromUserError()
    {
        var result = UpdateCheckResult.Failed("利用者向けの説明", "例外の詳細");

        Assert.Equal("利用者向けの説明", result.Error);
        Assert.Equal("例外の詳細", result.DiagnosticDetail);
    }

    [Fact]
    public void FailedResultDiagnosticDetailIsOptional()
    {
        var result = UpdateCheckResult.Failed("利用者向けの説明");

        Assert.Equal("利用者向けの説明", result.Error);
        Assert.Null(result.DiagnosticDetail);
    }

    [Fact]
    public void UnknownCurrentVersionFails()
    {
        Assert.Equal(UpdateCheckKind.Failed, UpdateCheckEvaluator.Evaluate("unknown", Manifest("0.2.0"), null, UpdateCheckTrigger.Manual).Kind);
    }

    [Theory]
    [InlineData(UpdateCheckKind.Available, UpdateCheckTrigger.Automatic, true)]
    [InlineData(UpdateCheckKind.UpToDate, UpdateCheckTrigger.Automatic, false)]
    [InlineData(UpdateCheckKind.Skipped, UpdateCheckTrigger.Automatic, false)]
    [InlineData(UpdateCheckKind.Failed, UpdateCheckTrigger.Automatic, false)]
    [InlineData(UpdateCheckKind.Available, UpdateCheckTrigger.Manual, true)]
    [InlineData(UpdateCheckKind.Skipped, UpdateCheckTrigger.Manual, true)]
    [InlineData(UpdateCheckKind.UpToDate, UpdateCheckTrigger.Manual, true)]
    [InlineData(UpdateCheckKind.Failed, UpdateCheckTrigger.Manual, true)]
    public void AutomaticCheckNotifiesOnlyAvailableVersion(UpdateCheckKind kind, UpdateCheckTrigger trigger, bool expected)
    {
        Assert.Equal(expected, UpdateCheckEvaluator.ShouldNotify(new UpdateCheckResult(kind, null, null), trigger));
    }

    [Theory]
    [InlineData(29, null, false)]
    [InlineData(30, null, true)]
    [InlineData(600, null, true)]
    public void FirstAutomaticCheckIsThirtySecondsAfterStart(int elapsedSeconds, int? lastCheckSeconds, bool expected)
    {
        Assert.Equal(expected, UpdateCheckSchedule.IsAutomaticCheckDue(
            true,
            TimeSpan.FromSeconds(elapsedSeconds),
            lastCheckSeconds is { } last ? TimeSpan.FromSeconds(last) : null,
            false));
    }

    [Fact]
    public void LaterAutomaticChecksAreTwentyFourHoursApart()
    {
        var last = TimeSpan.FromSeconds(30);
        Assert.False(UpdateCheckSchedule.IsAutomaticCheckDue(true, last + TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1), last, false));
        Assert.True(UpdateCheckSchedule.IsAutomaticCheckDue(true, last + TimeSpan.FromHours(24), last, false));
    }

    [Fact]
    public void AutomaticCheckThatFailedIsRetriedOneHourLater()
    {
        var last = TimeSpan.FromSeconds(30);
        Assert.False(UpdateCheckSchedule.IsAutomaticCheckDue(true, last + TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1), last, true));
        Assert.True(UpdateCheckSchedule.IsAutomaticCheckDue(true, last + TimeSpan.FromHours(1), last, true));
    }

    [Fact]
    public void RetryAfterFailureStillRespectsTheDisabledSetting()
    {
        Assert.False(UpdateCheckSchedule.IsAutomaticCheckDue(false, TimeSpan.FromDays(3), TimeSpan.FromSeconds(30), true));
    }

    [Fact]
    public void DisabledSettingStopsAutomaticChecks()
    {
        Assert.False(UpdateCheckSchedule.IsAutomaticCheckDue(false, TimeSpan.FromDays(3), null, false));
        Assert.False(UpdateCheckSchedule.IsAutomaticCheckDue(false, TimeSpan.FromDays(3), TimeSpan.FromSeconds(30), false));
    }
}
