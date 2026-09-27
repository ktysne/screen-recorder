using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class AutomationWaitConditionsTests
{
    private static readonly DateTimeOffset CaptureAfter = new(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(9));

    [Theory]
    [InlineData("2026-09-28T10:00:00+09:00")]
    [InlineData("2026-09-28T01:00:00Z")]
    [InlineData("2026-09-28T10:00:00.1234567+09:00")]
    [InlineData("2026-09-28T10:00+09:00")]
    public void CaptureAfterAcceptsIso8601WithTimeAndOffset(string value)
    {
        Assert.True(AutomationWaitConditions.TryParseCaptureAfter(value, out var parsed));
        Assert.Equal(CaptureAfter.UtcDateTime.Date, parsed.UtcDateTime.Date);
    }

    [Theory]
    [InlineData("2026-09-28")]
    [InlineData("09/28/2026")]
    [InlineData("2026-09-28T10:00:00")]
    [InlineData("2026-09-28 10:00:00+09:00")]
    [InlineData("")]
    public void CaptureAfterRejectsDateOnlyLocalAndNonIsoForms(string value)
    {
        Assert.False(AutomationWaitConditions.TryParseCaptureAfter(value, out _));
    }

    [Fact]
    public void WaitWithoutCaptureAfterMatchesOnTheStateAlone()
    {
        var wait = new AutomationWaitForParams("idle", null, 1000);

        Assert.True(AutomationWaitConditions.Matches("idle", null, wait));
        Assert.False(AutomationWaitConditions.Matches("recording", null, wait));
    }

    [Fact]
    public void CaptureAfterNeedsACaptureStrictlyLaterThanTheGivenTime()
    {
        var wait = new AutomationWaitForParams("idle", CaptureAfter, 1000);

        Assert.False(AutomationWaitConditions.Matches("idle", null, wait));
        Assert.False(AutomationWaitConditions.Matches("idle", CaptureAfter.AddSeconds(-1), wait));
        Assert.False(AutomationWaitConditions.Matches("idle", CaptureAfter, wait));
        Assert.True(AutomationWaitConditions.Matches("idle", CaptureAfter.AddTicks(1), wait));
        Assert.False(AutomationWaitConditions.Matches("saving", CaptureAfter.AddSeconds(1), wait));
    }
}
