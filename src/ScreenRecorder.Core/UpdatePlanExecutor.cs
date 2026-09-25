namespace ScreenRecorder.Core;

/// <summary>更新の適用で使うファイル操作。テストで失敗を差し込めるように分けてある。</summary>
public interface IUpdateFileOperations
{
    bool FileExists(string path);
    void CreateDirectory(string path);
    /// <summary>移動先に同名のファイルがあれば失敗する。</summary>
    void MoveFile(string source, string destination);
    /// <summary>コピー先に同名のファイルがあれば失敗する。</summary>
    void CopyFile(string source, string destination);
    /// <summary>ファイルが無ければ何もしない。</summary>
    void DeleteFile(string path);
    /// <summary>ほかのプロセスと共有せずに開けるか。ファイルが無いときは true。</summary>
    bool CanOpenExclusively(string path);
}

public sealed class FileSystemUpdateOperations : IUpdateFileOperations
{
    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void MoveFile(string source, string destination) => File.Move(source, destination, overwrite: false);

    public void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: false);

    public void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    public bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <param name="FailedPath">失敗した操作の対象。成功したときは null。</param>
/// <param name="RollbackFailures">戻せなかったファイルと理由。空なら元の状態に戻っている。</param>
public sealed record UpdateApplyOutcome(bool Succeeded, string? FailedPath, Exception? Error, IReadOnlyList<string> RollbackFailures)
{
    public bool RolledBackCompletely => !Succeeded && RollbackFailures.Count == 0;
}

public static class UpdatePlanExecutor
{
    /// <summary>計画どおりに退避とコピーを行い、途中で失敗したら進んだ分を戻す。</summary>
    public static UpdateApplyOutcome Apply(UpdateApplyPlan plan, string sourceDirectory, string installDirectory, IUpdateFileOperations files)
    {
        var progress = Enumerable.Repeat(UpdateStepProgress.NotStarted, plan.Steps.Count).ToArray();
        string? currentPath = null;
        try
        {
            for (var index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                var target = Path.Combine(installDirectory, step.RelativePath);
                var backup = Path.Combine(installDirectory, step.BackupRelativePath);
                if (step.RemovesStaleBackup)
                {
                    currentPath = backup;
                    files.DeleteFile(backup);
                }
                if (step.ReplacesExistingFile)
                {
                    currentPath = target;
                    files.MoveFile(target, backup);
                    progress[index] = progress[index] with { BackedUp = true };
                }
                currentPath = target;
                files.CreateDirectory(Path.GetDirectoryName(target)!);
                progress[index] = progress[index] with { CopyStarted = true };
                files.CopyFile(Path.Combine(sourceDirectory, step.RelativePath), target);
            }
            return new UpdateApplyOutcome(true, null, null, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            var failures = Rollback(UpdateApplyPlanner.CreateRollbackPlan(plan, progress), installDirectory, files);
            return new UpdateApplyOutcome(false, currentPath, exception, failures);
        }
    }

    /// <summary>1 つ失敗しても残りを続け、戻せなかったものを返す。</summary>
    public static IReadOnlyList<string> Rollback(IReadOnlyList<UpdateRollbackAction> actions, string installDirectory, IUpdateFileOperations files)
    {
        var failures = new List<string>();
        foreach (var action in actions)
        {
            var target = Path.Combine(installDirectory, action.RelativePath);
            try
            {
                switch (action.Kind)
                {
                    case UpdateRollbackActionKind.DeleteInstalledFile:
                        files.DeleteFile(target);
                        break;
                    case UpdateRollbackActionKind.RestoreBackup:
                        files.MoveFile(target + UpdateApplyPlanner.BackupSuffix, target);
                        break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                failures.Add($"{target}: {exception.Message}");
            }
        }
        return failures;
    }
}
