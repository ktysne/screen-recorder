using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal static class Program
{
    private static readonly TimeSpan RecordingEngineDisposalWaitOnExit = TimeSpan.FromMinutes(2);

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
        var settingsRepository = new SettingsRepository();
        var settings = settingsRepository.Load();
        DiagnosticLog.Start(settings.DiagnosticLogLevel, AppVersion.Current);
        Application.ThreadException += (_, eventArgs) => DiagnosticLog.Error(DiagnosticLogTags.App, $"UI スレッドで未処理の例外が発生しました: {eventArgs.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            DiagnosticLog.Error(DiagnosticLogTags.App, $"未処理の例外が発生しました: {eventArgs.ExceptionObject}");
            DiagnosticLog.Stop();
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            DiagnosticLog.Error(DiagnosticLogTags.App, $"未観測のタスク例外が発生しました: {eventArgs.Exception}");
        };
        DiagnosticLog.Info(DiagnosticLogTags.App, "アプリを起動しました。");
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var sync = new AutoStartSynchronizer(new RunRegistry(), exePath);
        try { sync.Apply(settings.StartWithWindows); }
        catch (Exception exception) { DiagnosticLog.Error(DiagnosticLogTags.App, $"自動起動の設定に失敗しました: {exception}"); }
        var applicationReturnedNormally = false;
        try
        {
            var context = new TrayApplicationContext(settings, settingsRepository, sync, exePath, startedAfterUpdate);
            Application.Run(context);
            applicationReturnedNormally = true;
            if (!context.RecordingEngineDisposal.Wait(RecordingEngineDisposalWaitOnExit))
                DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画エンジンの破棄が {RecordingEngineDisposalWaitOnExit.TotalMinutes:0} 分以内に終わらないまま終了します。");
        }
        finally
        {
            if (applicationReturnedNormally)
            {
                DiagnosticLog.Info(DiagnosticLogTags.App, "アプリを終了します。");
                DiagnosticLog.Stop();
            }
        }
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
