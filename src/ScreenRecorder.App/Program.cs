using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 適用中は旧プロセスがミューテックスを持っているため、多重起動の判定より前に分ける。
        if (args.Contains(UpdateApplier.ApplyUpdateArgument, StringComparer.OrdinalIgnoreCase))
        {
            UpdateApplier.Run(args);
            return;
        }

        using var mutex = new Mutex(true, UpdatePaths.SingletonMutexName, out var created);
        if (!created) return;
        Run(startedAfterUpdate: args.Contains(UpdateApplier.UpdatedArgument, StringComparer.OrdinalIgnoreCase));
    }

    private static void Run(bool startedAfterUpdate)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        var log = new DailyLog();
        log.Prune(DateTime.Now);
        log.Write("Application started");
        Application.ThreadException += (_, eventArgs) => log.Write($"UI exception: {eventArgs.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => log.Write($"Unhandled exception: {eventArgs.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => { log.Write($"Unobserved task exception: {eventArgs.Exception}"); eventArgs.SetObserved(); };

        var settingsRepository = new SettingsRepository();
        var settings = settingsRepository.Load();
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var sync = new AutoStartSynchronizer(new RunRegistry(), exePath);
        try { sync.Apply(settings.StartWithWindows); }
        catch (Exception exception) { log.Write($"Auto-start update failed: {exception}"); }
        Application.Run(new TrayApplicationContext(settings, log, settingsRepository, sync, exePath, startedAfterUpdate));
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
