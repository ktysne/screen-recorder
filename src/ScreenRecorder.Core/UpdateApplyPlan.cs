namespace ScreenRecorder.Core;

/// <summary>配布物の 1 ファイルをインストール先へ置く手順。</summary>
/// <param name="RelativePath">インストール先からの相対パス。区切りは <c>\</c> にそろえてある。</param>
/// <param name="ReplacesExistingFile">インストール先に同名のファイルがあり、<c>.old</c> へ退避してから置く。</param>
/// <param name="RemovesStaleBackup">前回の更新で残った <c>.old</c> があり、退避の前に消す。</param>
public sealed record UpdateFileStep(string RelativePath, bool ReplacesExistingFile, bool RemovesStaleBackup)
{
    public string BackupRelativePath => RelativePath + UpdateApplyPlanner.BackupSuffix;
}

public sealed record UpdateApplyPlan(IReadOnlyList<UpdateFileStep> Steps)
{
    /// <summary>旧プロセスや常駐の検査ソフトが開いている間は置き換えられない、既存の exe と DLL。</summary>
    public IReadOnlyList<string> ExclusiveAccessPaths => Steps
        .Where(step => step.ReplacesExistingFile && IsBinary(step.RelativePath))
        .Select(step => step.RelativePath)
        .ToArray();

    public IReadOnlyList<string> BackupPaths => Steps
        .Where(step => step.ReplacesExistingFile)
        .Select(step => step.BackupRelativePath)
        .ToArray();

    private static bool IsBinary(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
}

public sealed record UpdateApplyPlanResult(UpdateApplyPlan? Plan, string? Error);

/// <summary>1 つの手順がどこまで進んだか。ロールバックで戻す範囲を決める。</summary>
/// <param name="CopyStarted">コピーを始めた。途中で失敗しても書きかけのファイルが残りうる。</param>
public sealed record UpdateStepProgress(bool BackedUp, bool CopyStarted)
{
    public static readonly UpdateStepProgress NotStarted = new(false, false);
}

public enum UpdateRollbackActionKind { DeleteInstalledFile, RestoreBackup }

public sealed record UpdateRollbackAction(UpdateRollbackActionKind Kind, string RelativePath);

/// <summary>更新の適用とロールバックの計画を立てる。手順の正本は docs/design.md「更新の手順」。</summary>
public static class UpdateApplyPlanner
{
    public const string BackupSuffix = ".old";
    public const string ExecutableName = "ScreenRecorder.exe";

    /// <param name="packageFiles">展開先にあるファイルの相対パス。</param>
    /// <param name="installedFileExists">インストール先に、その相対パスのファイルがあるか。</param>
    public static UpdateApplyPlanResult CreatePlan(IEnumerable<string> packageFiles, Func<string, bool> installedFileExists)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in packageFiles)
        {
            if (!UpdatePackagePaths.TryNormalizeRelativePath(file, out var relative))
                return new UpdateApplyPlanResult(null, $"更新ファイルに使えない名前が含まれています: {file}");
            if (relative.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
                return new UpdateApplyPlanResult(null, $"更新ファイルに {BackupSuffix} で終わる名前が含まれています: {file}");
            if (!seen.Add(relative))
                return new UpdateApplyPlanResult(null, $"更新ファイルに同じ名前が重複しています: {file}");
            normalized.Add(relative);
        }
        if (!seen.Contains(ExecutableName))
            return new UpdateApplyPlanResult(null, $"更新ファイルに {ExecutableName} がありません。");

        var steps = normalized
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new UpdateFileStep(path, installedFileExists(path), installedFileExists(path + BackupSuffix)))
            .ToArray();
        return new UpdateApplyPlanResult(new UpdateApplyPlan(steps), null);
    }

    /// <summary>進んだ手順を逆順に戻す。置いたファイル(書きかけを含む)を消してから、<c>.old</c> を元の名前へ戻す。</summary>
    public static IReadOnlyList<UpdateRollbackAction> CreateRollbackPlan(UpdateApplyPlan plan, IReadOnlyList<UpdateStepProgress> progress)
    {
        if (progress.Count != plan.Steps.Count) throw new ArgumentException("進み具合の数が手順の数と一致しません。", nameof(progress));
        var actions = new List<UpdateRollbackAction>();
        for (var index = plan.Steps.Count - 1; index >= 0; index--)
        {
            var step = plan.Steps[index];
            if (progress[index].CopyStarted) actions.Add(new UpdateRollbackAction(UpdateRollbackActionKind.DeleteInstalledFile, step.RelativePath));
            if (progress[index].BackedUp) actions.Add(new UpdateRollbackAction(UpdateRollbackActionKind.RestoreBackup, step.RelativePath));
        }
        return actions;
    }

    /// <summary>すべての手順を終えた後で、新しい版が起動できなかったときに旧版へ戻す計画。</summary>
    public static IReadOnlyList<UpdateRollbackAction> CreateFullRollbackPlan(UpdateApplyPlan plan) =>
        CreateRollbackPlan(plan, plan.Steps.Select(step => new UpdateStepProgress(step.ReplacesExistingFile, true)).ToArray());
}

public static class UpdatePackagePaths
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// フォルダーの外を指しうるパス(絶対パス、ドライブ指定、<c>..</c>)と、Windows が名前を読み替える書き方を拒む。
    /// 末尾の点や空白は Windows が取り除くため、別のファイルを指すことがある。
    /// </summary>
    public static bool TryNormalizeRelativePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(path)) return false;
        var candidate = path.Replace('/', '\\');
        if (candidate.StartsWith('\\') || candidate.Contains(':') || Path.IsPathRooted(candidate)) return false;
        var segments = candidate.Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..") return false;
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (ReservedDeviceNames.Contains(Path.GetFileNameWithoutExtension(segment))) return false;
        }
        normalized = string.Join('\\', segments);
        return true;
    }
}
