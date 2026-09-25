using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!args.Contains("--apply-update", StringComparer.OrdinalIgnoreCase))
        {
            using var mutex = new Mutex(true, "Local\\ScreenRecorder.Singleton", out var created);
            if (!created) return;
            Run();
        }
        else
        {
            Run();
        }
    }

    private static void Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        var log = new DailyLog();
        log.Prune(DateTime.Now);
        log.Write("Application started");
        Application.ThreadException += (_, eventArgs) => log.Write($"UI exception: {eventArgs.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => log.Write($"Unhandled exception: {eventArgs.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => { log.Write($"Unobserved task exception: {eventArgs.Exception}"); eventArgs.SetObserved(); };

        var settings = new SettingsRepository().Load();
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var sync = new AutoStartSynchronizer(new RunRegistry(), exePath);
        try { sync.Apply(settings.StartWithWindows); }
        catch (Exception exception) { log.Write($"Auto-start update failed: {exception}"); }
        Application.Run(new TrayApplicationContext(settings, log));
        log.Write("Application stopped");
    }

    private sealed class RunRegistry : IAutoStartRegistry
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public void Set(string valueName, string executablePath)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(valueName, $"\"{executablePath}\"");
        }
        public void Remove(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
