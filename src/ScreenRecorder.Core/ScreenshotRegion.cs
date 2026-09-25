using System.Drawing;

namespace ScreenRecorder.Core;

public static class ScreenshotRegion
{
    public static Rectangle? Normalize(Point start, Point end, Rectangle displayBounds)
    {
        if (displayBounds.Width <= 0 || displayBounds.Height <= 0) return null;

        var displayLeft = (long)displayBounds.X;
        var displayTop = (long)displayBounds.Y;
        var displayRight = displayLeft + displayBounds.Width;
        var displayBottom = displayTop + displayBounds.Height;
        var left = Math.Max(Math.Min(start.X, end.X), displayLeft);
        var top = Math.Max(Math.Min(start.Y, end.Y), displayTop);
        var right = Math.Min(Math.Max(start.X, end.X), displayRight);
        var bottom = Math.Min(Math.Max(start.Y, end.Y), displayBottom);
        if (right <= left || bottom <= top) return null;

        return Rectangle.FromLTRB((int)left, (int)top, (int)right, (int)bottom);
    }
}
