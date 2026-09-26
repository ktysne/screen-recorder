using System.Diagnostics;

namespace ScreenRecorder.App;

internal static class ShellLauncher
{
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        var start = new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true };
        using (Process.Start(start)) { }
    }
}
