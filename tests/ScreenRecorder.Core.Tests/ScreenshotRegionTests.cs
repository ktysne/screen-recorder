using System.Drawing;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class ScreenshotRegionTests
{
    [Fact]
    public void Normalize_SupportsReverseDrag()
    {
        var region = ScreenshotRegion.Normalize(new Point(80, 70), new Point(20, 30), new Rectangle(0, 0, 100, 100));

        Assert.Equal(new Rectangle(20, 30, 60, 40), region);
    }

    [Fact]
    public void Normalize_ClipsToTheDisplayWhereDragStarted()
    {
        var region = ScreenshotRegion.Normalize(new Point(90, 80), new Point(130, 120), new Rectangle(0, 0, 100, 100));

        Assert.Equal(new Rectangle(90, 80, 10, 20), region);
    }

    [Fact]
    public void Normalize_SupportsDisplaysWithNegativeCoordinates()
    {
        var region = ScreenshotRegion.Normalize(new Point(-2300, -100), new Point(-1800, 400), new Rectangle(-1920, 0, 1920, 1080));

        Assert.Equal(new Rectangle(-1920, 0, 120, 400), region);
    }

    [Fact]
    public void Normalize_ReturnsNullWhenWidthIsZero()
    {
        var region = ScreenshotRegion.Normalize(new Point(50, 30), new Point(50, 70), new Rectangle(0, 0, 100, 100));

        Assert.Null(region);
    }

    [Fact]
    public void Normalize_ReturnsNullWhenSelectionDoesNotIntersectDisplay()
    {
        var region = ScreenshotRegion.Normalize(new Point(-20, 10), new Point(-1, 30), new Rectangle(0, 0, 100, 100));

        Assert.Null(region);
    }
}
