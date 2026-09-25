using System.Drawing;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class ScreenshotWindowGeometryTests
{
    [Fact]
    public void CalculateCrop_OffsetsExtendedFrameInsideWindowBounds()
    {
        var crop = ScreenshotWindowGeometry.CalculateCrop(
            new Rectangle(95, 75, 430, 330),
            new Rectangle(100, 80, 420, 320));

        Assert.Equal(new Rectangle(5, 5, 420, 320), crop?.BitmapBounds);
        Assert.Equal(new Rectangle(100, 80, 420, 320), crop?.ScreenBounds);
    }

    [Fact]
    public void CalculateCrop_ClipsFrameBoundsToWindowBounds()
    {
        var crop = ScreenshotWindowGeometry.CalculateCrop(
            new Rectangle(-100, -50, 200, 100),
            new Rectangle(-120, -60, 180, 90));

        Assert.Equal(new Rectangle(0, 0, 160, 80), crop?.BitmapBounds);
        Assert.Equal(new Rectangle(-100, -50, 160, 80), crop?.ScreenBounds);
    }

    [Fact]
    public void CalculateCrop_ReturnsNullWhenBoundsDoNotOverlap()
    {
        var crop = ScreenshotWindowGeometry.CalculateCrop(
            new Rectangle(0, 0, 100, 100),
            new Rectangle(100, 0, 100, 100));

        Assert.Null(crop);
    }
}
