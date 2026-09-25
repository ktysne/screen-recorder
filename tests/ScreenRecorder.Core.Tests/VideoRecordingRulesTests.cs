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
    public void TryPauseAndResume_MoveBetweenRecordingStates()
    {
        var machine = CreateRecordingMachine();

        Assert.True(machine.TryPause());
        Assert.Equal(VideoRecordingState.Paused, machine.State);
        Assert.True(machine.TryResume());
        Assert.Equal(VideoRecordingState.Recording, machine.State);
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
        return machine;
    }
}
