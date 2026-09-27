using System.Runtime.InteropServices;

namespace ScreenRecorder.Capture;

public static class CursorOverlay
{
    public static void Draw(Bitmap bitmap, Rectangle captureBounds)
    {
        var cursor = new CaptureNativeMethods.CursorInfo { Size = Marshal.SizeOf<CaptureNativeMethods.CursorInfo>() };
        if (!CaptureNativeMethods.GetCursorInfo(ref cursor) || (cursor.Flags & CaptureNativeMethods.CursorShowing) == 0 || cursor.Cursor == IntPtr.Zero) return;
        if (!CaptureNativeMethods.GetIconInfo(cursor.Cursor, out var iconInfo)) return;

        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var deviceContext = graphics.GetHdc();
            try
            {
                var x = cursor.ScreenPosition.X - captureBounds.X - (int)iconInfo.HotspotX;
                var y = cursor.ScreenPosition.Y - captureBounds.Y - (int)iconInfo.HotspotY;
                CaptureNativeMethods.DrawIconEx(deviceContext, x, y, cursor.Cursor, 0, 0, 0, IntPtr.Zero, CaptureNativeMethods.DrawIconNormal);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }
        finally
        {
            if (iconInfo.MaskBitmap != IntPtr.Zero) CaptureNativeMethods.DeleteObject(iconInfo.MaskBitmap);
            if (iconInfo.ColorBitmap != IntPtr.Zero) CaptureNativeMethods.DeleteObject(iconInfo.ColorBitmap);
        }
    }
}
