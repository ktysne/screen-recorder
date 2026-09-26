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
        var settings = new SettingsRepository().Load();
        DiagnosticLog.Start(settings.DiagnosticLogLevel, AppVersion.Current);
        DiagnosticLog.Info(DiagnosticLogTags.Update, $"更新適用プロセスを開始しました: 版={AppVersion.Current}、引数={string.Join(" | ", args)}。");
        // 更新後の起動が .old を消す前に、この処理の終了(15 秒後の生存確認を含む)を待てるようにする。
        Mutex? applierMutex = null;
        var ownsMutex = false;
        try
        {
            applierMutex = new Mutex(true, UpdatePaths.ApplierMutexName, out ownsMutex);
            if (!ownsMutex)
            {
                DiagnosticLog.Error(DiagnosticLogTags.Update, "別の更新適用プロセスが動作中のため、処理を開始できませんでした。");
                ShowError(UiLabels.UpdateApplyAlreadyRunning);
                return;
            }

            Apply(args);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新の適用中に未処理の例外が発生しました: {exception}");
            ShowError(string.Format(UiLabels.UpdateApplyFailedBroken, AppVersion.Current, "予期しないエラーが発生しました。", UpdateManifestParser.DistributionPageUrl, TryGetInstallDirectory(args) ?? string.Empty));
        }
        finally
        {
            try
            {
                if (ownsMutex) applierMutex?.ReleaseMutex();
            }
            finally
            {
                try { applierMutex?.Dispose(); }
                finally
                {
                    DiagnosticLog.Info(DiagnosticLogTags.Update, "更新適用プロセスを終了します。");
                    DiagnosticLog.Stop();
                }
            }
        }
    }

    private static void Apply(string[] args)
    {
        if (!TryParseArguments(args, out var oldProcessId, out var sourceDirectory, out var installDirectory))
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, "更新適用プロセスの引数が正しくありません。");
            ShowError(UiLabels.UpdateApplyInvalidArguments);
            return;
        }
        var version = AppVersion.Current;
        var installedExecutable = Path.Combine(installDirectory, UpdateApplyPlanner.ExecutableName);

        if (!WaitForOldProcessExit(oldProcessId))
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新前のプロセスが終了しませんでした: PID={oldProcessId}");
            ShowError(UiLabels.UpdateApplyOldProcessRunning);
            return;
        }

        var files = new FileSystemUpdateOperations();
        var planResult = UpdateApplyPlanner.CreatePlan(UpdatePackage.ListFiles(sourceDirectory), path => File.Exists(Path.Combine(installDirectory, path)));
        if (planResult.Plan is not { } plan)
        {
            FailWithoutChanges(version, installedExecutable, planResult.Error ?? "更新ファイルの一覧を作れませんでした。");
            return;
        }
        var locked = WaitForExclusiveAccess(plan, installDirectory, files);
        if (locked.Count > 0)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新対象のファイルを開けませんでした: {string.Join(", ", locked)}");
            FailWithoutChanges(version, installedExecutable, UiLabels.UpdateApplyFilesLocked);
            return;
        }

        var outcome = UpdatePlanExecutor.Apply(plan, sourceDirectory, installDirectory, files);
        if (!outcome.Succeeded)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新ファイルを置き換えられませんでした: ファイル={outcome.FailedPath}; {outcome.Error}; 復元の失敗={string.Join(" / ", outcome.RollbackFailures)}");
            var reason = $"{Path.GetFileName(outcome.FailedPath)} を置き換えられませんでした({outcome.Error?.Message})";
            ReportFailure(version, installDirectory, installedExecutable, reason, outcome.RollbackFailures);
            return;
        }
        DiagnosticLog.Info(DiagnosticLogTags.Update, $"更新ファイルを置き換えました: 版={version}、インストール先={installDirectory}、ファイル数={plan.Steps.Count}。");
        UpdateCleanup.TryWriteCleanupRecord(new UpdateCleanupRecord(installDirectory, version, plan.BackupPaths), UpdatePaths.GetCleanupRecordPath(plan.UpdateId));

        if (!WaitForSingletonRelease())
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, "更新後に別の ScreenRecorder が起動しているため、新しい版を起動できませんでした。");
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
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新後の ScreenRecorder を起動できませんでした: {exception}");
            RollBackAppliedUpdate(version, plan, installDirectory, installedExecutable, files, "新しい版を起動できませんでした。");
            return;
        }
        using (newProcess)
        {
            if (!newProcess.WaitForExit(NewVersionSurvivalTime))
            {
                DiagnosticLog.Info(DiagnosticLogTags.Update, $"更新後の ScreenRecorder が起動しました: PID={newProcess.Id}、版={version}。");
                return;
            }
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新後の ScreenRecorder が早期に終了しました: 終了コード={newProcess.ExitCode}。");
        }
        RollBackAppliedUpdate(version, plan, installDirectory, installedExecutable, files, UiLabels.UpdateApplyNewVersionExited);
    }

    private static void RollBackAppliedUpdate(string version, UpdateApplyPlan plan, string installDirectory, string installedExecutable, IUpdateFileOperations files, string reason)
    {
        TryDelete(UpdatePaths.GetCleanupRecordPath(plan.UpdateId));
        WaitForExclusiveAccess(plan, installDirectory, files);
        var failures = UpdatePlanExecutor.Rollback(UpdateApplyPlanner.CreateFullRollbackPlan(plan), installDirectory, files);
        DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新を取り消して元の版へ戻しました: 理由={reason}、失敗={string.Join(" / ", failures)}。");
        ReportFailure(version, installDirectory, installedExecutable, reason, failures);
    }

    private static void ReportFailure(string version, string installDirectory, string installedExecutable, string reason, IReadOnlyList<string> rollbackFailures)
    {
        if (rollbackFailures.Count > 0)
        {
            ShowError(string.Format(UiLabels.UpdateApplyFailedBroken, version, reason, UpdateManifestParser.DistributionPageUrl, installDirectory));
            return;
        }
        TryStartPreviousVersion(installedExecutable);
        ShowError(string.Format(UiLabels.UpdateApplyFailedRestored, version, reason, UpdateManifestParser.DistributionPageUrl));
    }

    private static void FailWithoutChanges(string version, string installedExecutable, string reason)
    {
        DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新ファイルを変更する前に処理を中止しました: {reason}");
        TryStartPreviousVersion(installedExecutable);
        ShowError(string.Format(UiLabels.UpdateApplyFailedRestored, version, reason, UpdateManifestParser.DistributionPageUrl));
    }

    private static void TryStartPreviousVersion(string installedExecutable)
    {
        try
        {
            using var _ = StartProcess(installedExecutable, null);
            DiagnosticLog.Info(DiagnosticLogTags.Update, "元の版を起動しました。");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"元の版を起動できませんでした: {exception}");
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新用ファイルを削除できませんでした: ファイル={path}; {exception.Message}"); }
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

    internal static void TryWriteCleanupRecord(UpdateCleanupRecord record, string recordPath)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(UpdatePaths.UpdateDirectory);
            temporaryPath = Path.Combine(UpdatePaths.UpdateDirectory, $"ScreenRecorder-cleanup-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, record.Serialize());
            File.Move(temporaryPath, recordPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新の後始末情報を保存できませんでした: {exception}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { DiagnosticLog.Warn(DiagnosticLogTags.Update, $"一時的な後始末情報を削除できませんでした: {exception.Message}"); }
            }
        }
    }

    public static Task RunAsync(string installDirectory) => Task.Run(() => Run(installDirectory));

    private static void Run(string installDirectory)
    {
        try
        {
            if (!Directory.Exists(UpdatePaths.UpdateDirectory)) return;
            // 適用した側は 15 秒後に新しい版が落ちていれば .old から戻すため、その判断が済むまで消さない。
            var applierState = WaitForApplierToFinish();
            if (applierState == ApplierState.StillRunning)
            {
                DiagnosticLog.Warn(DiagnosticLogTags.Update, "更新適用プロセスが動作中のため、後始末を延期しました。");
                return;
            }
            CleanUpBackups(installDirectory);
            // ミューテックスを放した直後の適用側はまだ終了しておらず、展開先の exe を消せないため少し待つ。
            if (applierState == ApplierState.Finished) Thread.Sleep(ApplierExitGrace);
            CleanUpStaging();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新後の後始末に失敗しました: {exception}");
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

    private static void CleanUpBackups(string installDirectory)
    {
        var recordPaths = Directory.EnumerateFiles(UpdatePaths.UpdateDirectory, $"{UpdateCleanupRecord.FileNamePrefix}*.json").ToList();
        if (File.Exists(UpdatePaths.LegacyCleanupRecordPath)) recordPaths.Add(UpdatePaths.LegacyCleanupRecordPath);
        foreach (var recordPath in recordPaths)
        {
            try
            {
                var record = UpdateCleanupRecord.TryDeserialize(File.ReadAllText(recordPath));
                if (record is null || !record.CanBeCleanedBy(installDirectory, AppVersion.Current)) continue;

                var files = new FileSystemUpdateOperations();
                var remaining = record.KeepUndeletedBackups(installDirectory, AppVersion.Current, backup =>
                {
                    try
                    {
                        files.DeleteFile(backup);
                        return true;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新前のバックアップを削除できませんでした: {backup}; {exception.Message}");
                        return false;
                    }
                });
                if (remaining is null)
                {
                    File.Delete(recordPath);
                    DiagnosticLog.Info(DiagnosticLogTags.Update, $"更新前のバックアップを削除しました: 版={record.Version}、ファイル数={record.BackupFiles.Count}。");
                    continue;
                }
                TryWriteCleanupRecord(remaining, recordPath);
                DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新前のバックアップが残っています: 版={record.Version}、残り={remaining.BackupFiles.Count}。");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新の後始末情報を処理できませんでした: ファイル={recordPath}; {exception.Message}");
            }
        }
    }

    private static void CleanUpStaging()
    {
        foreach (var directory in Directory.EnumerateDirectories(UpdatePaths.UpdateDirectory, "ScreenRecorder-*"))
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新用フォルダーを削除できませんでした: フォルダー={directory}; {exception.Message}"); }
        }
        foreach (var file in Directory.EnumerateFiles(UpdatePaths.UpdateDirectory, "ScreenRecorder-*"))
        {
            try { File.Delete(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新用ファイルを削除できませんでした: ファイル={file}; {exception.Message}"); }
        }
    }
}
