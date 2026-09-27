using System.Drawing;
using System.Runtime.InteropServices;

namespace ScreenRecorder.Capture;

public static class WindowRecordingTarget
{
    public static bool TryDescribe(IntPtr window, out Rectangle bounds, out string title, out string reason)
    {
        if (!TryGetWindowInfo(window, out bounds, out title, out reason)) return false;
        var className = new System.Text.StringBuilder(256);
        // 選択画面ではデスクトップも撮れるが、前面がデスクトップなら対象ウィンドウを選ばせる。
        if (CaptureNativeMethods.GetClassName(window, className, className.Capacity) != 0
            && className.ToString() is "Progman" or "WorkerW")
        {
            reason = "前面がデスクトップです";
            return false;
        }
        return true;
    }

    public static bool TryGetWindowInfo(IntPtr window, out Rectangle bounds, out string title, out string reason)
    {
        bounds = Rectangle.Empty;
        title = string.Empty;
        if (window == IntPtr.Zero || !CaptureNativeMethods.IsWindow(window))
        {
            reason = "前面のウィンドウがありません";
            return false;
        }
        if (!CaptureNativeMethods.IsWindowVisible(window))
        {
            reason = "ウィンドウが表示されていません";
            return false;
        }
        if (CaptureNativeMethods.IsIconic(window))
        {
            reason = "ウィンドウが最小化されています";
            return false;
        }
        if (CaptureNativeMethods.DwmGetWindowAttribute(window, CaptureNativeMethods.DwmaCloaked, out int cloaked, sizeof(int)) != 0 || cloaked != 0)
        {
            reason = "ウィンドウを撮影できません";
            return false;
        }

        var hasFrameBounds = CaptureNativeMethods.DwmGetWindowAttribute(window, CaptureNativeMethods.DwmaExtendedFrameBounds, out CaptureNativeMethods.NativeRect frame, Marshal.SizeOf<CaptureNativeMethods.NativeRect>()) == 0
            && frame.Right > frame.Left && frame.Bottom > frame.Top;
        var rectangle = hasFrameBounds ? frame : default;
        if (!hasFrameBounds && !CaptureNativeMethods.GetWindowRect(window, out rectangle))
        {
            reason = "ウィンドウの撮影範囲を取得できません";
            return false;
        }
        bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            reason = "ウィンドウの撮影範囲が空です";
            return false;
        }

        var length = CaptureNativeMethods.GetWindowTextLength(window);
        var titleBuffer = new System.Text.StringBuilder(Math.Max(1, length + 1));
        CaptureNativeMethods.GetWindowText(window, titleBuffer, titleBuffer.Capacity);
        title = titleBuffer.ToString();
        reason = string.Empty;
        return true;
    }
}
