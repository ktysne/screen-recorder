using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenRecorder.Core;

namespace ScreenRecorder.Cli;

internal sealed record CliMonitor(
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    int WorkAreaX,
    int WorkAreaY,
    int WorkAreaWidth,
    int WorkAreaHeight,
    int? DpiX,
    int? DpiY,
    bool IsPrimary);

internal sealed record CliEnvironment(
    string SettingsDirectory,
    string LogDirectory,
    Func<IReadOnlyList<CliMonitor>> GetMonitors,
    Func<DateTimeOffset> Now,
    string CliVersion)
{
    public Func<string?, (string? Path, IReadOnlyList<string> Searched)> FindApp { get; init; } = FindApplication;

    public static CliEnvironment Create() => new(
        StoragePaths.GetSettingsDirectory(),
        StoragePaths.GetLogsDirectory(),
        EnumerateMonitors,
        () => DateTimeOffset.Now,
        typeof(CliEnvironment).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    private static (string? Path, IReadOnlyList<string> Searched) FindApplication(string? explicitPath)
    {
        var searched = new List<string>();
        if (explicitPath is not null)
        {
            var path = Path.GetFullPath(explicitPath);
            searched.Add(path);
            return (File.Exists(path) ? path : null, searched);
        }
        var sibling = Path.Combine(AppContext.BaseDirectory, "ScreenRecorder.exe");
        searched.Add(sibling);
        if (File.Exists(sibling)) return (sibling, searched);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScreenRecorder.slnx")))
            directory = directory.Parent;
        if (directory is null) return (null, searched);
        var bin = Path.Combine(directory.FullName, "src", "ScreenRecorder.App", "bin");
        foreach (var configuration in new[] { "Debug", "Release" })
        foreach (var layout in new[] { Path.Combine("x64", configuration), configuration })
        {
            var root = Path.Combine(bin, layout);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "ScreenRecorder.exe", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                searched.Add(path);
                if (File.Exists(path)) return (path, searched);
            }
        }
        return (null, searched);
    }

    private static IReadOnlyList<CliMonitor> EnumerateMonitors() => Screen.AllScreens.Select(screen =>
    {
        var bounds = screen.Bounds;
        var workArea = screen.WorkingArea;
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.NativePoint(bounds.Left, bounds.Top), 2);
        int? dpiX = null;
        int? dpiY = null;
        if (monitor != nint.Zero
            && NativeMethods.GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var x, out var y) == 0)
        {
            dpiX = checked((int)x);
            dpiY = checked((int)y);
        }

        return new CliMonitor(
            screen.DeviceName,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            workArea.X,
            workArea.Y,
            workArea.Width,
            workArea.Height,
            dpiX,
            dpiY,
            screen.Primary);
    }).ToArray();

    internal enum MonitorDpiType
    {
        Effective
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct NativePoint(int x, int y)
        {
            public readonly int X = x;
            public readonly int Y = y;
        }

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(nint monitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);
    }
}
