using System.Drawing;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class VideoRecordingRulesTests
{
    [Fact]
    public void CalculateDimensions_TruncatesOddSourceAndOutputDimensionsToEven()
    {
        var dimensions = VideoRecordingStateMachine.CalculateDimensions(1921, 1081, 100);

        Assert.Equal(new Size(1920, 1080), dimensions?.SourceSize);
        Assert.Equal(new Size(1920, 1080), dimensions?.OutputSize);
    }

    [Fact]
    public void CalculateDimensions_RejectsOnePixelSource()
    {
        Assert.Null(VideoRecordingStateMachine.CalculateDimensions(1, 100, 100));
        Assert.Null(VideoRecordingStateMachine.CalculateDimensions(100, 1, 100));
    }

    [Theory]
    [InlineData(1920, 1080, 75, 1440, 810)]
    [InlineData(1920, 1080, 50, 960, 540)]
    [InlineData(100, 100, 75, 74, 74)]
    public void CalculateDimensions_ScalesThenTruncatesOutputDimensions(int width, int height, int scalePercent, int expectedWidth, int expectedHeight)
    {
        var dimensions = VideoRecordingStateMachine.CalculateDimensions(width, height, scalePercent);

        Assert.Equal(new Size(expectedWidth, expectedHeight), dimensions?.OutputSize);
    }

    [Fact]
    public void CalculateDimensions_RejectsOutputThatRoundsDownToZero()
    {
        Assert.Null(VideoRecordingStateMachine.CalculateDimensions(2, 2, 50));
    }

    [Fact]
    public void GetTemporaryPath_UsesRecordingSuffixBeforeMp4Extension()
    {
        var path = VideoRecordingFileNaming.GetTemporaryPath(Path.Combine("videos", "2026-09", "ScreenRecorder_20260926.mp4"));

        Assert.Equal(Path.Combine("videos", "2026-09", "ScreenRecorder_20260926.recording.mp4"), path);
    }

    [Fact]
    public void GetAvailablePath_UsesTheScreenshotNameAndMonthlyFolderRules()
    {
        var capturedAt = new DateTime(2026, 9, 26, 8, 7, 6);
        var path = VideoRecordingFileNaming.GetAvailablePath(
            "videos", true, capturedAt, ScreenshotMode.Window, "Terminal", "{window}_{date}",
            candidate => candidate.EndsWith("Terminal_20260926.mp4", StringComparison.Ordinal)
                || candidate.EndsWith("Terminal_20260926_2.mp4", StringComparison.Ordinal));

        Assert.Equal(Path.Combine("videos", "2026-09", "Terminal_20260926_3.mp4"), path);
    }

    [Fact]
    public void HasMinimumFreeSpace_RequiresAtLeastOneGigabyte()
    {
        Assert.False(VideoRecordingStateMachine.HasMinimumFreeSpace(VideoRecordingStateMachine.MinimumFreeSpaceBytes - 1));
        Assert.True(VideoRecordingStateMachine.HasMinimumFreeSpace(VideoRecordingStateMachine.MinimumFreeSpaceBytes));
    }

    [Fact]
    public void TryCancelCountdown_ReturnsToIdleWithoutStartingRecording()
    {
        var machine = new VideoRecordingStateMachine();

        Assert.True(machine.TryBeginCountdown());
        Assert.True(machine.TryCancelCountdown());
        Assert.Equal(VideoRecordingState.Idle, machine.State);
        Assert.False(machine.TryStartRecording());
    }

    [Fact]
    public void TryStartRecording_EntersPreparingUntilEngineReportsRecording()
    {
        var machine = new VideoRecordingStateMachine();
        Assert.True(machine.TryBeginCountdown());

        Assert.True(machine.TryStartRecording());
        Assert.Equal(VideoRecordingState.Preparing, machine.State);
        Assert.Equal(RecordingEngineCommand.None, machine.OnEngineRecordingStarted());
        Assert.Equal(VideoRecordingState.Recording, machine.State);
    }

    [Fact]
    public void IsWaitingForEngineStart_IsTrueWhilePreparing()
    {
        var machine = CreatePreparingMachine();

        Assert.True(machine.IsWaitingForEngineStart);
    }

    [Fact]
    public void IsWaitingForEngineStart_RemainsTrueAfterStopIsDeferredDuringPreparing()
    {
        var machine = CreatePreparingMachine();

        Assert.Equal(RecordingEngineCommand.None, machine.RequestStop());

        Assert.Equal(VideoRecordingState.Saving, machine.State);
        Assert.True(machine.IsWaitingForEngineStart);
    }

    [Theory]
    [InlineData(VideoRecordingState.Recording)]
    [InlineData(VideoRecordingState.Paused)]
    [InlineData(VideoRecordingState.Saving)]
    public void IsWaitingForEngineStart_IsFalseAfterEngineStarts(VideoRecordingState state)
    {
        var machine = CreateRecordingMachine();
        if (state == VideoRecordingState.Paused) machine.RequestPause();
        if (state == VideoRecordingState.Saving) machine.RequestStop();

        Assert.Equal(state, machine.State);
        Assert.False(machine.IsWaitingForEngineStart);
    }

    [Theory]
    [InlineData(VideoRecordingState.Idle)]
    [InlineData(VideoRecordingState.Countdown)]
    public void IsWaitingForEngineStart_IsFalseBeforeRecordingStarts(VideoRecordingState state)
    {
        var machine = new VideoRecordingStateMachine();
        if (state == VideoRecordingState.Countdown) machine.TryBeginCountdown();

        Assert.Equal(state, machine.State);
        Assert.False(machine.IsWaitingForEngineStart);
    }

    [Fact]
    public void TryPauseAndResume_MoveBetweenRecordingStates()
    {
        var machine = CreateRecordingMachine();

        Assert.True(machine.TryPause());
        Assert.Equal(VideoRecordingState.Paused, machine.State);
        Assert.True(machine.TryResume());
        Assert.Equal(VideoRecordingState.Recording, machine.State);
    }

    [Fact]
    public void RequestStopBeforeEngineIsReady_StopsWhenRecordingStarts()
    {
        var machine = CreatePreparingMachine();

        Assert.Equal(RecordingEngineCommand.None, machine.RequestStop());
        Assert.Equal(VideoRecordingState.Saving, machine.State);
        Assert.Equal(RecordingEngineCommand.Stop, machine.OnEngineRecordingStarted());
    }

    [Fact]
    public void Preparing_DoesNotAcceptPauseOrResume()
    {
        var machine = CreatePreparingMachine();

        Assert.Equal(RecordingEngineCommand.None, machine.RequestPause());
        Assert.Equal(RecordingEngineCommand.None, machine.RequestResume());
        Assert.False(machine.TryPause());
        Assert.False(machine.TryResume());
        Assert.Equal(VideoRecordingState.Preparing, machine.State);
    }

    [Fact]
    public void PreparingFailure_CanMoveThroughSavingBackToIdle()
    {
        var machine = CreatePreparingMachine();

        Assert.True(machine.TryBeginSaving());
        Assert.Equal(VideoRecordingState.Saving, machine.State);
        Assert.True(machine.TryFail());
        Assert.Equal(VideoRecordingState.Idle, machine.State);
    }

    [Fact]
    public void EngineRecordingStartedAfterResume_DoesNotChangeRecordingState()
    {
        var machine = CreateRecordingMachine();
        Assert.True(machine.TryPause());
        Assert.True(machine.TryResume());

        Assert.Equal(RecordingEngineCommand.None, machine.OnEngineRecordingStarted());
        Assert.Equal(VideoRecordingState.Recording, machine.State);
    }

    [Fact]
    public void EngineRecordingStartedWhilePaused_ReturnsPause()
    {
        var machine = CreateRecordingMachine();
        Assert.True(machine.TryPause());

        Assert.Equal(RecordingEngineCommand.Pause, machine.OnEngineRecordingStarted());
        Assert.Equal(VideoRecordingState.Paused, machine.State);
    }

    [Theory]
    [InlineData(VideoRecordingState.Idle, false, false)]
    [InlineData(VideoRecordingState.Countdown, false, false)]
    [InlineData(VideoRecordingState.Preparing, true, false)]
    [InlineData(VideoRecordingState.Recording, true, true)]
    [InlineData(VideoRecordingState.Paused, true, true)]
    [InlineData(VideoRecordingState.Saving, false, false)]
    public void CanStopAndCanPause_ReflectEachState(VideoRecordingState state, bool canStop, bool canPause)
    {
        var machine = new VideoRecordingStateMachine();
        switch (state)
        {
            case VideoRecordingState.Countdown:
                machine.TryBeginCountdown();
                break;
            case VideoRecordingState.Preparing:
                machine.TryBeginCountdown();
                machine.TryStartRecording();
                break;
            case VideoRecordingState.Recording:
                CreateRecordingMachineState(machine);
                break;
            case VideoRecordingState.Paused:
                CreateRecordingMachineState(machine);
                machine.TryPause();
                break;
            case VideoRecordingState.Saving:
                CreateRecordingMachineState(machine);
                machine.TryBeginSaving();
                break;
        }

        Assert.Equal(canStop, machine.CanStop);
        Assert.Equal(canPause, machine.CanPause);
    }

    [Fact]
    public void RecordingCommandsAfterEngineIsReady_AreReturnedForImmediateExecution()
    {
        var machine = CreateRecordingMachine();
        Assert.Equal(RecordingEngineCommand.None, machine.OnEngineRecordingStarted());

        Assert.Equal(RecordingEngineCommand.Pause, machine.RequestPause());
        Assert.Equal(RecordingEngineCommand.Resume, machine.RequestResume());
        Assert.Equal(RecordingEngineCommand.Stop, machine.RequestStop());
    }

    [Theory]
    [InlineData(RecordingTerminationOutcome.Waiting, RecordingTerminationDecision.Wait)]
    [InlineData(RecordingTerminationOutcome.Completed, RecordingTerminationDecision.ContinueCompletedSave)]
    [InlineData(RecordingTerminationOutcome.Failed, RecordingTerminationDecision.NotifyIncompleteThenDispose)]
    [InlineData(RecordingTerminationOutcome.Idle, RecordingTerminationDecision.NotifyIncompleteThenDispose)]
    [InlineData(RecordingTerminationOutcome.TimedOut, RecordingTerminationDecision.NotifyIncompleteThenDispose)]
    public void RecordingTermination_ChoosesCleanupOnlyAfterAValidEndCondition(
        RecordingTerminationOutcome outcome,
        RecordingTerminationDecision expectedDecision)
    {
        Assert.Equal(expectedDecision, RecordingTerminationRules.Decide(outcome));
    }

    [Fact]
    public void SavingState_IgnoresRecordingOperations()
    {
        var machine = CreateRecordingMachine();
        Assert.True(machine.TryBeginSaving());

        Assert.False(machine.TryBeginCountdown());
        Assert.False(machine.TryStartRecording());
        Assert.False(machine.TryPause());
        Assert.False(machine.TryResume());
        Assert.False(machine.TryBeginSaving());
        Assert.Equal(VideoRecordingState.Saving, machine.State);
    }

    [Fact]
    public void TryFail_ReturnsAnyActiveStateToIdle()
    {
        var machine = CreateRecordingMachine();
        Assert.True(machine.TryBeginSaving());

        Assert.True(machine.TryFail());
        Assert.Equal(VideoRecordingState.Idle, machine.State);
        Assert.True(machine.TryBeginCountdown());
    }

    private static VideoRecordingStateMachine CreateRecordingMachine()
    {
        var machine = new VideoRecordingStateMachine();
        Assert.True(machine.TryBeginCountdown());
        Assert.True(machine.TryStartRecording());
        Assert.Equal(RecordingEngineCommand.None, machine.OnEngineRecordingStarted());
        return machine;
    }

    private static VideoRecordingStateMachine CreatePreparingMachine()
    {
        var machine = new VideoRecordingStateMachine();
        Assert.True(machine.TryBeginCountdown());
        Assert.True(machine.TryStartRecording());
        return machine;
    }

    private static void CreateRecordingMachineState(VideoRecordingStateMachine machine)
    {
        Assert.True(machine.TryBeginCountdown());
        Assert.True(machine.TryStartRecording());
        Assert.Equal(RecordingEngineCommand.None, machine.OnEngineRecordingStarted());
    }
}
