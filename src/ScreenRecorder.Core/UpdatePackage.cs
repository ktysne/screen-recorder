using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenRecorder.Core;

/// <summary>利用者に理由として見せられる、更新の準備と適用の失敗。</summary>
public sealed class UpdatePackageException(string message, Exception? innerException = null) : Exception(message, innerException);

public static class UpdatePackage
{
    public static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static bool MatchesSha256(string filePath, string expectedSha256) =>
        string.Equals(ComputeSha256(filePath), expectedSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// すべての項目の名前を確かめてから書き出す。1 つでもフォルダーの外を指す名前があれば何も書かない。
    /// 戻り値は書き出したファイルの相対パス。
    /// </summary>
    public static IReadOnlyList<string> Extract(string zipPath, string destinationDirectory)
    {
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory)) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = new List<(ZipArchiveEntry Entry, string RelativePath)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                if (entry.Length != 0 || !UpdatePackagePaths.TryNormalizeRelativePath(entry.FullName.TrimEnd('/', '\\'), out _))
                    throw new UpdatePackageException($"更新ファイルに使えない名前が含まれています: {entry.FullName}");
                continue;
            }
            if (!UpdatePackagePaths.TryNormalizeRelativePath(entry.FullName, out var relative))
                throw new UpdatePackageException($"更新ファイルに使えない名前が含まれています: {entry.FullName}");
            var fullPath = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!fullPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new UpdatePackageException($"更新ファイルに展開先の外を指す名前が含まれています: {entry.FullName}");
            if (!seen.Add(relative))
                throw new UpdatePackageException($"更新ファイルに同じ名前が重複しています: {entry.FullName}");
            entries.Add((entry, relative));
        }
        if (!seen.Contains(UpdateApplyPlanner.ExecutableName))
            throw new UpdatePackageException($"更新ファイルに {UpdateApplyPlanner.ExecutableName} がありません。");

        Directory.CreateDirectory(destinationRoot);
        foreach (var (entry, relative) in entries)
        {
            var target = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        return entries.Select(item => item.RelativePath).ToArray();
    }

    /// <summary>展開先のファイルを相対パスで列挙する。</summary>
    public static IReadOnlyList<string> ListFiles(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();
    }
}

/// <summary>
/// 適用を終えた後で消す <c>.old</c> の記録。新しい版が起動し、適用した側が終わってから消す。
/// 適用した側が 15 秒後の生存確認で旧版へ戻すときに <c>.old</c> が要るため。
/// </summary>
public sealed record UpdateCleanupRecord(string InstallDirectory, string Version, IReadOnlyList<string> BackupFiles)
{
    public const string FileName = "cleanup.json";

    public string Serialize() => JsonSerializer.Serialize(this);

    public static UpdateCleanupRecord? TryDeserialize(string json)
    {
        try
        {
            var record = JsonSerializer.Deserialize<UpdateCleanupRecord>(json);
            return record is { InstallDirectory: not null, Version: not null, BackupFiles: not null } ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>記録がこのインストール先のこの版のものなら、<c>.old</c> を消してよい。</summary>
    public bool AppliesTo(string installDirectory, string version) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory)),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(Version, version, StringComparison.Ordinal);

    /// <summary>消す対象を、記録の中でも <c>.old</c> で終わる安全な相対パスに限る。</summary>
    public IEnumerable<string> GetBackupFilePaths() => BackupFiles
        .Select(TryNormalizeBackupPath)
        .Where(path => path is not null)
        .Select(path => Path.Combine(InstallDirectory, path!));

    /// <summary>一致する記録のうち、削除できなかったバックアップだけを残す。</summary>
    /// <param name="installDirectory">起動した側のインストール先。</param>
    /// <param name="version">起動した側の版。</param>
    /// <param name="tryDeleteBackup">削除に成功したときに true を返す。</param>
    public UpdateCleanupRecord? KeepUndeletedBackups(string installDirectory, string version, Func<string, bool> tryDeleteBackup)
    {
        ArgumentNullException.ThrowIfNull(tryDeleteBackup);
        if (!AppliesTo(installDirectory, version)) return this;

        var remaining = new List<string>();
        foreach (var backup in BackupFiles)
        {
            var normalized = TryNormalizeBackupPath(backup);
            if (normalized is null) continue;
            if (!tryDeleteBackup(Path.Combine(InstallDirectory, normalized))) remaining.Add(normalized);
        }
        return remaining.Count == 0 ? null : this with { BackupFiles = remaining };
    }

    private static string? TryNormalizeBackupPath(string? path) =>
        UpdatePackagePaths.TryNormalizeRelativePath(path, out var normalized)
        && normalized.EndsWith(UpdateApplyPlanner.BackupSuffix, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : null;
}
