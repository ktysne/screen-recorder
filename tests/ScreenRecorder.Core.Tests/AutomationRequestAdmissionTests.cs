using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class AutomationRequestAdmissionTests
{
    private static readonly AutomationRequestState Ready = new(false, false, null, false);

    [Theory]
    [InlineData("hello")]
    [InlineData("status")]
    [InlineData("waitFor")]
    [InlineData("exit")]
    [InlineData("perform")]
    [InlineData("selection")]
    public void ReadyStateAcceptsEveryMethodExceptSelectionWithoutAnOpenScreen(string method)
    {
        var decision = AutomationRequestAdmission.Decide(Ready, method, RecorderAction.ScreenshotFullScreen);

        Assert.Equal(method != "selection", decision.Accepted);
        Assert.Equal(method == "selection" ? "selectionNotOpen" : null, decision.ErrorDataCode);
    }

    [Theory]
    [InlineData("perform")]
    [InlineData("selection")]
    public void ModalStateRejectsInteractiveMethods(string method)
    {
        var decision = AutomationRequestAdmission.Decide(Ready with { ModalDialogOpen = true, SelectionScreenOpen = true }, method);

        Assert.False(decision.Accepted);
        Assert.Equal("rejectedWhileModal", decision.ErrorDataCode);
    }

    [Fact]
    public void SelectionStateAcceptsSelectionAndRejectsPerform()
    {
        var state = Ready with { SelectionScreenOpen = true };

        Assert.True(AutomationRequestAdmission.Decide(state, "selection").Accepted);
        Assert.Equal("rejectedWhileSelection", AutomationRequestAdmission.Decide(state, "perform").ErrorDataCode);
    }

    [Theory]
    [InlineData(AutomationCountdownKind.Recording, RecorderAction.StopRecording, true)]
    [InlineData(AutomationCountdownKind.Recording, RecorderAction.PauseResume, false)]
    [InlineData(AutomationCountdownKind.Recording, RecorderAction.ScreenshotFullScreen, false)]
    [InlineData(AutomationCountdownKind.Screenshot, RecorderAction.StopRecording, false)]
    [InlineData(AutomationCountdownKind.Screenshot, RecorderAction.ScreenshotFullScreen, false)]
    public void CountdownStateOnlyAcceptsTheStopActionThatCancelsARecordingCountdown(AutomationCountdownKind countdown, RecorderAction action, bool expected)
    {
        var decision = AutomationRequestAdmission.Decide(Ready with { Countdown = countdown }, "perform", action);

        Assert.Equal(expected, decision.Accepted);
        Assert.Equal(expected ? null : "rejectedDuringCountdown", decision.ErrorDataCode);
    }

    [Fact]
    public void ScreenshotCountdownAcceptsStopWhileARecordingCanBeStopped()
    {
        var state = Ready with { Countdown = AutomationCountdownKind.Screenshot, RecordingStoppable = true };

        Assert.True(AutomationRequestAdmission.Decide(state, "perform", RecorderAction.StopRecording).Accepted);
        Assert.Equal("rejectedDuringCountdown", AutomationRequestAdmission.Decide(state, "perform", RecorderAction.PauseResume).ErrorDataCode);
    }

    [Fact]
    public void UpdateStateAcceptsOnlyUnconditionallyAllowedMethods()
    {
        var state = Ready with { UpdateDownloadOrPreparationInProgress = true };

        Assert.True(AutomationRequestAdmission.Decide(state, "exit").Accepted);
        Assert.Equal("rejectedDuringUpdate", AutomationRequestAdmission.Decide(state, "perform", RecorderAction.StopRecording).ErrorDataCode);
    }
}
