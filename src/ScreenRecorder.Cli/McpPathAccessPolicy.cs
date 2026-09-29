using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder.Cli;

internal sealed class McpPathAccessPolicy
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private readonly string[] _allowedDirectories;

    private McpPathAccessPolicy(string[] allowedDirectories) => _allowedDirectories = allowedDirectories;

    internal IReadOnlyList<string> AllowedDirectories => Array.AsReadOnly(_allowedDirectories);

    public static McpPathAccessPolicy Create(IEnumerable<string> allowedDirectories)
    {
        var resolved = allowedDirectories.Select(ResolveDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (resolved.Length == 0) throw new ArgumentException("許可するフォルダーを指定してください。", nameof(allowedDirectories));
        return new McpPathAccessPolicy(resolved);
    }

    public string? Validate(ParsedCliCommand command)
    {
        if (command.Options.TryGetValue("-o", out var output) && output is not null && !IsAllowedOutput(output))
            return "output は絶対パスで指定し、許可されたフォルダーの下に置いてください。";

        if (command.Definition.Name is "record" or "screenshot"
            && command.Options.TryGetValue("--settings", out var settings)
            && settings is not null
            && !IsAllowedSettingsFile(settings))
            return "settings は絶対パスで指定し、許可されたフォルダーの下に置いてください。";

        return null;
    }

    internal bool IsAllowedOutput(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        try
        {
            var fullPath = NormalizePath(Path.GetFullPath(path));
            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return false;
            if (TryGetAttributes(fullPath, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0) return false;
            return IsAllowedDirectory(ResolveDirectory(parent));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal bool IsAllowedSettingsFile(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        try
        {
            var fullPath = NormalizePath(Path.GetFullPath(path));
            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return false;
            if (!IsAllowedDirectory(ResolveDirectory(parent))) return false;
            if (!TryGetAttributes(fullPath, out _)) return true;
            return IsAllowedDirectory(ResolveExistingPath(fullPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool IsAllowedDirectory(string path) => _allowedDirectories.Any(root =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(EnsureSeparator(root), StringComparison.OrdinalIgnoreCase));

    private static string ResolveDirectory(string path)
    {
        var fullPath = NormalizePath(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"許可するフォルダーがありません: {path}");
        return ResolveExistingPath(fullPath);
    }

    private static string ResolveExistingPath(string path)
    {
        using var handle = CreateFile(path, 0, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"実体のパスを取得できません: {path}");

        var buffer = new StringBuilder(512);
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) throw new IOException($"実体のパスを取得できません: {path}");
            if (length < buffer.Capacity) return NormalizePath(RemoveDevicePrefix(buffer.ToString()));
            buffer.EnsureCapacity(checked((int)length + 1));
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }

    private static string EnsureSeparator(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static string RemoveDevicePrefix(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + path[8..];
        return path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder filePath, uint filePathSize, uint flags);
}
