using System.Diagnostics;
using System.Globalization;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

/// <summary>
/// 展開した新しい exe を <c>--apply-update</c> で起動したときの処理。トレイを出さずに適用だけを行う。
/// 手順の正本は docs/design.md「更新の手順」。
/// </summary>
internal static class UpdateApplier
{
    public const string ApplyUpdateArgument = "--apply-update";
    public const string UpdatedArgument = "--updated";
    private static readonly TimeSpan OldProcessExitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ExclusiveAccessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SingletonReleaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NewVersionSurvivalTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    public static void Run(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        var log = new DailyLog();
        log.Write($"Update applier started: version={AppVersion.Current}, args={string.Join(" | ", args)}");
        // 更新後の起動が .old を消す前に、この処理の終了(15 秒後の生存確認を含む)を待てるようにする。
        using var applierMutex = new Mutex(true, UpdatePaths.ApplierMutexName, out var created);
        if (!created)
        {
            log.Write("Update applier aborted: another applier is running");
            ShowError(UiLabels.UpdateApplyAlreadyRunning);
            return;
        }
        try
        {
            Apply(args, log);
        }
        catch (Exception exception)
        {
            log.Write($"Update applier crashed: {exception}");
            ShowError(string.Format(UiLabels.UpdateApplyFailedBroken, AppVersion.Current, "予期しないエラーが発生しました。", UpdateManifestParser.DistributionPageUrl, TryGetInstallDirectory(args) ?? string.Empty));
        }
        finally
        {
            applierMutex.ReleaseMutex();
            log.Write("Update applier finished");
        }
    }

    private static void Apply(string[] args, DailyLog log)
    {
        if (!TryParseArguments(args, out var oldProcessId, out var sourceDirectory, out var installDirectory))
        {
            log.Write("Update applier aborted: invalid arguments");
            ShowError(UiLabels.UpdateApplyInvalidArguments);
            return;
        }
        var version = AppVersion.Current;
        var installedExecutable = Path.Combine(installDirectory, UpdateApplyPlanner.ExecutableName);

        if (!WaitForOldProcessExit(oldProcessId))
        {
            log.Write($"Update applier aborted: old process {oldProcessId} did not exit");
            ShowError(UiLabels.UpdateApplyOldProcessRunning);
            return;
        }

        var files = new FileSystemUpdateOperations();
        var planResult = UpdateApplyPlanner.CreatePlan(UpdatePackage.ListFiles(sourceDirectory), path => File.Exists(Path.Combine(installDirectory, path)));
        if (planResult.Plan is not { } plan)
        {
            FailWithoutChanges(log, version, installedExecutable, planResult.Error ?? "更新ファイルの一覧を作れませんでした。");
            return;
        }
        var locked = WaitForExclusiveAccess(plan, installDirectory, files);
        if (locked.Count > 0)
        {
            log.Write($"Update files locked: {string.Join(", ", locked)}");
            FailWithoutChanges(log, version, installedExecutable, UiLabels.UpdateApplyFilesLocked);
            return;
        }

        var outcome = UpdatePlanExecutor.Apply(plan, sourceDirectory, installDirectory, files);
        if (!outcome.Succeeded)
        {
            log.Write($"Update apply failed: path={outcome.FailedPath}; {outcome.Error}; rollbackFailures={string.Join(" / ", outcome.RollbackFailures)}");
            var reason = $"{Path.GetFileName(outcome.FailedPath)} を置き換えられませんでした({outcome.Error?.Message})";
            ReportFailure(log, version, installDirectory, installedExecutable, reason, outcome.RollbackFailures);
            return;
        }
        log.Write($"Update files replaced: version={version}, install={installDirectory}, files={plan.Steps.Count}");
        TryWriteCleanupRecord(log, new UpdateCleanupRecord(installDirectory, version, plan.BackupPaths));

        if (!WaitForSingletonRelease())
        {
            log.Write("Update applied but another instance holds the singleton mutex");
            ShowError(string.Format(UiLabels.UpdateApplyAnotherInstance, version));
            return;
        }

        Process newProcess;
        try
        {
            newProcess = StartProcess(installedExecutable, UpdatedArgument);
        }
        catch (Exception exception)
        {
            log.Write($"Starting updated version failed: {exception}");
            RollBackAppliedUpdate(log, version, plan, installDirectory, installedExecutable, files, "新しい版を起動できませんでした。");
            return;
        }
        using (newProcess)
        {
            if (!newProcess.WaitForExit(NewVersionSurvivalTime))
            {
                log.Write($"Updated version is running: pid={newProcess.Id}, version={version}");
                return;
            }
            log.Write($"Updated version exited early: exitCode={newProcess.ExitCode}");
        }
        RollBackAppliedUpdate(log, version, plan, installDirectory, installedExecutable, files, UiLabels.UpdateApplyNewVersionExited);
    }

    private static void RollBackAppliedUpdate(DailyLog log, string version, UpdateApplyPlan plan, string installDirectory, string installedExecutable, IUpdateFileOperations files, string reason)
    {
        TryDelete(log, UpdatePaths.CleanupRecordPath);
        WaitForExclusiveAccess(plan, installDirectory, files);
        var failures = UpdatePlanExecutor.Rollback(UpdateApplyPlanner.CreateFullRollbackPlan(plan), installDirectory, files);
        log.Write($"Update rolled back: reason={reason}; failures={string.Join(" / ", failures)}");
        ReportFailure(log, version, installDirectory, installedExecutable, reason, failures);
    }

    private static void ReportFailure(DailyLog log, string version, string installDirectory, string installedExecutable, string reason, IReadOnlyList<string> rollbackFailures)
    {
        if (rollbackFailures.Count > 0)
        {
            ShowError(string.Format(UiLabels.UpdateApplyFailedBroken, version, reason, UpdateManifestParser.DistributionPageUrl, installDirectory));
            return;
        }
        TryStartPreviousVersion(log, installedExecutable);
        ShowError(string.Format(UiLabels.UpdateApplyFailedRestored, version, reason, UpdateManifestParser.DistributionPageUrl));
    }

    private static void FailWithoutChanges(DailyLog log, string version, string installedExecutable, string reason)
    {
        log.Write($"Update aborted before changing files: {reason}");
        TryStartPreviousVersion(log, installedExecutable);
        ShowError(string.Format(UiLabels.UpdateApplyFailedRestored, version, reason, UpdateManifestParser.DistributionPageUrl));
    }

    private static void TryStartPreviousVersion(DailyLog log, string installedExecutable)
    {
        try
        {
            using var _ = StartProcess(installedExecutable, null);
            log.Write("Previous version started");
        }
        catch (Exception exception)
        {
            log.Write($"Starting previous version failed: {exception}");
        }
    }

    private static Process StartProcess(string executable, string? argument)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        if (argument is not null) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException($"{executable} を起動できませんでした。");
    }

    /// <summary>PID は再利用されうるため、ScreenRecorder 以外のプロセスに当たったら終了済みとみなす。</summary>
    private static bool WaitForOldProcessExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(UpdateApplyPlanner.ExecutableName), StringComparison.OrdinalIgnoreCase)) return true;
            return process.WaitForExit(OldProcessExitTimeout);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>旧プロセスの解放と、ウイルス対策ソフトによる一時的なロックの解除を待つ。戻り値は開けなかったファイル。</summary>
    private static IReadOnlyList<string> WaitForExclusiveAccess(UpdateApplyPlan plan, string installDirectory, IUpdateFileOperations files)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var locked = plan.ExclusiveAccessPaths.Where(path => !files.CanOpenExclusively(Path.Combine(installDirectory, path))).ToArray();
            if (locked.Length == 0 || deadline.Elapsed >= ExclusiveAccessTimeout) return locked;
            Thread.Sleep(RetryDelay);
        }
    }

    private static bool WaitForSingletonRelease()
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (!Mutex.TryOpenExisting(UpdatePaths.SingletonMutexName, out var mutex)) return true;
            mutex.Dispose();
            if (deadline.Elapsed >= SingletonReleaseTimeout) return false;
            Thread.Sleep(RetryDelay);
        }
    }

    private static void TryWriteCleanupRecord(DailyLog log, UpdateCleanupRecord record)
    {
        try
        {
            Directory.CreateDirectory(UpdatePaths.UpdateDirectory);
            File.WriteAllText(UpdatePaths.CleanupRecordPath, record.Serialize());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write($"Writing update cleanup record failed: {exception}");
        }
    }

    private static void TryDelete(DailyLog log, string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { log.Write($"Deleting {path} failed: {exception.Message}"); }
    }

    private static bool TryParseArguments(string[] args, out int oldProcessId, out string sourceDirectory, out string installDirectory)
    {
        oldProcessId = 0;
        sourceDirectory = installDirectory = string.Empty;
        var index = Array.FindIndex(args, arg => string.Equals(arg, ApplyUpdateArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 3 >= args.Length) return false;
        if (!int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out oldProcessId) || oldProcessId <= 0) return false;
        if (!TryGetExistingDirectory(args[index + 2], out sourceDirectory) || !TryGetExistingDirectory(args[index + 3], out installDirectory)) return false;
        if (string.Equals(sourceDirectory, installDirectory, StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(Path.Combine(sourceDirectory, UpdateApplyPlanner.ExecutableName))
            && File.Exists(Path.Combine(installDirectory, UpdateApplyPlanner.ExecutableName));
    }

    private static bool TryGetExistingDirectory(string value, out string directory)
    {
        directory = string.Empty;
        if (!Path.IsPathFullyQualified(value)) return false;
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        return Directory.Exists(directory);
    }

    private static string? TryGetInstallDirectory(string[] args)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, ApplyUpdateArgument, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 3 < args.Length ? args[index + 3] : null;
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, UiLabels.UpdateDialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
}

/// <summary>更新の後に残った <c>.old</c> と一時ファイルを片付ける。</summary>
internal static class UpdateCleanup
{
    private static readonly TimeSpan ApplierWaitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ApplierExitGrace = TimeSpan.FromSeconds(3);

    public static Task RunAsync(DailyLog log, string installDirectory) => Task.Run(() => Run(log, installDirectory));

    private static void Run(DailyLog log, string installDirectory)
    {
        try
        {
            if (!Directory.Exists(UpdatePaths.UpdateDirectory)) return;
            // 適用した側は 15 秒後に新しい版が落ちていれば .old から戻すため、その判断が済むまで消さない。
            var applierState = WaitForApplierToFinish();
            if (applierState == ApplierState.StillRunning)
            {
                log.Write("Update cleanup skipped: applier is still running");
                return;
            }
            CleanUpBackups(log, installDirectory);
            // ミューテックスを放した直後の適用側はまだ終了しておらず、展開先の exe を消せないため少し待つ。
            if (applierState == ApplierState.Finished) Thread.Sleep(ApplierExitGrace);
            CleanUpStaging(log);
        }
        catch (Exception exception)
        {
            log.Write($"Update cleanup failed: {exception}");
        }
    }

    private enum ApplierState { NotRunning, Finished, StillRunning }

    private static ApplierState WaitForApplierToFinish()
    {
        if (!Mutex.TryOpenExisting(UpdatePaths.ApplierMutexName, out var mutex)) return ApplierState.NotRunning;
        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(ApplierWaitTimeout)) return ApplierState.StillRunning;
            }
            catch (AbandonedMutexException)
            {
            }
            mutex.ReleaseMutex();
            return ApplierState.Finished;
        }
    }

    private static void CleanUpBackups(DailyLog log, string installDirectory)
    {
        var recordPath = UpdatePaths.CleanupRecordPath;
        if (!File.Exists(recordPath)) return;
        var record = UpdateCleanupRecord.TryDeserialize(File.ReadAllText(recordPath));
        if (record is not null && record.AppliesTo(installDirectory, AppVersion.Current))
        {
            var files = new FileSystemUpdateOperations();
            foreach (var backup in record.GetBackupFilePaths())
            {
                try { files.DeleteFile(backup); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    log.Write($"Deleting update backup failed: {backup}; {exception.Message}");
                }
            }
            log.Write($"Update backups removed: version={record.Version}, files={record.BackupFiles.Count}");
        }
        File.Delete(recordPath);
    }

    private static void CleanUpStaging(DailyLog log)
    {
        foreach (var directory in Directory.EnumerateDirectories(UpdatePaths.UpdateDirectory, "ScreenRecorder-*"))
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { log.Write($"Deleting update staging failed: {directory}; {exception.Message}"); }
        }
        foreach (var file in Directory.EnumerateFiles(UpdatePaths.UpdateDirectory, "ScreenRecorder-*"))
        {
            try { File.Delete(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { log.Write($"Deleting update staging failed: {file}; {exception.Message}"); }
        }
    }
}
