using System.Drawing;

namespace ScreenRecorder.Core;

public readonly record struct ScreenshotWindowCrop(Rectangle BitmapBounds, Rectangle ScreenBounds);

public static class ScreenshotWindowGeometry
{
    public static ScreenshotWindowCrop? CalculateCrop(Rectangle windowBounds, Rectangle frameBounds)
    {
        if (windowBounds.Width <= 0 || windowBounds.Height <= 0 || frameBounds.Width <= 0 || frameBounds.Height <= 0) return null;

        var windowLeft = (long)windowBounds.X;
        var windowTop = (long)windowBounds.Y;
        var windowRight = windowLeft + windowBounds.Width;
        var windowBottom = windowTop + windowBounds.Height;
        var left = Math.Max(windowLeft, frameBounds.X);
        var top = Math.Max(windowTop, frameBounds.Y);
        var right = Math.Min(windowRight, (long)frameBounds.X + frameBounds.Width);
        var bottom = Math.Min(windowBottom, (long)frameBounds.Y + frameBounds.Height);
        if (right <= left || bottom <= top) return null;

        var cropWidth = (int)(right - left);
        var cropHeight = (int)(bottom - top);
        var screenBounds = new Rectangle((int)left, (int)top, cropWidth, cropHeight);
        var bitmapBounds = new Rectangle(
            (int)(left - windowLeft),
            (int)(top - windowTop),
            cropWidth,
            cropHeight);
        return new ScreenshotWindowCrop(bitmapBounds, screenBounds);
    }
}
