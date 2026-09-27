using System.Drawing;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class RecordingStartPlannerTests
{
    [Theory]
    [InlineData(-2880, 0)]
    [InlineData(0, -1620)]
    public void Plan_RegionUsesDisplayRelativeCoordinatesOnNegativeDisplays(int displayX, int displayY)
    {
        var display = new RecordingDisplayInfo("DISPLAY", new Rectangle(displayX, displayY, 2560, 1440));
        var target = new Rectangle(displayX + 101, displayY + 103, 401, 301);
        var result = RecordingStartPlanner.Plan(ScreenshotMode.Region, display, target, 0, "temp.mp4", new Settings(), false);

        Assert.Null(result.Error);
        Assert.Equal(new RecordingWorkerRectangle(101, 103, 400, 300), result.Plan!.StartData.SourceRect);
        Assert.Equal(new RecordingWorkerSize(400, 300), result.Plan.StartData.OutputFrameSize);
    }

    [Fact]
    public void Plan_ScalesOutputToFiftyPercentAfterEvenRounding()
    {
        var settings = new Settings { OutputScalePercent = 50 };
        var display = new RecordingDisplayInfo("DISPLAY", new Rectangle(0, 0, 1920, 1080));
        var result = RecordingStartPlanner.Plan(ScreenshotMode.Full, display, new Rectangle(0, 0, 1919, 1079), 0, "temp.mp4", settings, false);

        Assert.Equal(new RecordingWorkerSize(1918, 1078), result.Plan!.StartData.SourceFrameSize);
        Assert.Equal(new RecordingWorkerSize(958, 538), result.Plan.StartData.OutputFrameSize);
    }

    [Fact]
    public void FindContainingDisplay_ReturnsNullWhenRegionCrossesBoundary()
    {
        var displays = new[]
        {
            new RecordingDisplayInfo("LEFT", new Rectangle(-1920, 0, 1920, 1080)),
            new RecordingDisplayInfo("RIGHT", new Rectangle(0, 0, 1920, 1080))
        };

        Assert.Null(RecordingStartPlanner.FindContainingDisplay(displays, new Rectangle(-100, 50, 200, 100)));
    }

    [Fact]
    public void Plan_DistinguishesSourceAndScaledOutputTooSmall()
    {
        var display = new RecordingDisplayInfo("DISPLAY", new Rectangle(0, 0, 1920, 1080));
        var settings = new Settings { OutputScalePercent = 50 };

        Assert.Equal(RecordingStartPlanError.RegionTooSmall,
            RecordingStartPlanner.Plan(ScreenshotMode.Region, display, new Rectangle(0, 0, 1, 100),
                0, "temp.mp4", settings, false).Error);
        Assert.Equal(RecordingStartPlanError.OutputTooSmall,
            RecordingStartPlanner.Plan(ScreenshotMode.Region, display, new Rectangle(0, 0, 2, 2),
                0, "temp.mp4", settings, false).Error);
    }
}
