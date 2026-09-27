using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenRecorder.Capture;

public static class DesktopCapture
{
    public static Bitmap Capture(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        var screenDc = CaptureNativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("画面のデバイスコンテキストを取得できませんでした。");

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmapHandle = IntPtr.Zero;
        IntPtr previousObject = IntPtr.Zero;
        try
        {
            memoryDc = CaptureNativeMethods.CreateCompatibleDC(screenDc);
            bitmapHandle = CaptureNativeMethods.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (memoryDc == IntPtr.Zero || bitmapHandle == IntPtr.Zero) throw new InvalidOperationException("画面の撮影領域を確保できませんでした。");
            previousObject = CaptureNativeMethods.SelectObject(memoryDc, bitmapHandle);
            if (previousObject == IntPtr.Zero) throw new InvalidOperationException("撮影先のビットマップを選択できませんでした。");
            if (!CaptureNativeMethods.BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, CaptureNativeMethods.SourceCopy | CaptureNativeMethods.CaptureLayeredWindows))
                throw new InvalidOperationException("画面を撮影できませんでした。");
            return Image.FromHbitmap(bitmapHandle);
        }
        finally
        {
            if (previousObject != IntPtr.Zero) CaptureNativeMethods.SelectObject(memoryDc, previousObject);
            if (bitmapHandle != IntPtr.Zero) CaptureNativeMethods.DeleteObject(bitmapHandle);
            if (memoryDc != IntPtr.Zero) CaptureNativeMethods.DeleteDC(memoryDc);
            CaptureNativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static Bitmap CaptureWindow(IntPtr window, Rectangle bounds, out bool printSucceeded)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var deviceContext = graphics.GetHdc();
                try { printSucceeded = CaptureNativeMethods.PrintWindow(window, deviceContext, CaptureNativeMethods.PrintWindowRenderFullContent); }
                finally { graphics.ReleaseHdc(deviceContext); }
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public static bool IsEntirelyBlack(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[checked(bitmap.Width * 4)];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (var x = 0; x < row.Length; x += 4)
                    if (row[x] != 0 || row[x + 1] != 0 || row[x + 2] != 0) return false;
            }
            return true;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public static void MakeOpaque(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(bitmap.Width * 4);
            var row = new byte[rowBytes];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var address = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(address, row, 0, rowBytes);
                for (var alpha = 3; alpha < rowBytes; alpha += 4) row[alpha] = byte.MaxValue;
                Marshal.Copy(row, 0, address, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
