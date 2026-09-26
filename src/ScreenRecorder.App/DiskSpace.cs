using System.ComponentModel;
using System.Runtime.InteropServices;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal static class DiskSpace
{
    public static long GetAvailableFreeBytes(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = StoragePaths.GetVolumeRoot(fullPath);
        if (!NativeMethods.GetDiskFreeSpaceExW(root, out var availableBytes, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return availableBytes;
    }
}
