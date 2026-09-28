using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using ScreenRecorder.Cli;
using Xunit;

namespace ScreenRecorder.Cli.Tests;

public sealed class McpPathAccessPolicyTests
{
    [Fact]
    public void OutputMustBeAbsoluteAndStayUnderTheResolvedAllowedDirectory()
    {
        using var directories = new TestDirectories();
        var policy = McpPathAccessPolicy.Create([directories.Allowed]);

        Assert.True(policy.IsAllowedOutput(Path.Combine(directories.Allowed, "capture.mp4")));
        Assert.False(policy.IsAllowedOutput(Path.Combine(directories.Outside, "capture.mp4")));
        Assert.False(policy.IsAllowedOutput("relative\\capture.mp4"));
        Assert.False(policy.IsAllowedOutput(Path.Combine(directories.Allowed, "..", "outside", "capture.mp4")));
    }

    [DirectoryJunctionFact]
    public void OutputParentIsResolvedThroughDirectoryReparsePoints()
    {
        using var directories = new TestDirectories();
        var policy = McpPathAccessPolicy.Create([directories.Allowed]);
        var link = Path.Combine(directories.Allowed, "outside-link");
        Assert.True(DirectoryJunction.TryCreate(link, directories.Outside));

        try
        {
            Assert.False(policy.IsAllowedOutput(Path.Combine(link, "capture.mp4")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [FileReparsePointFact]
    public void ExistingOutputCannotBeAReparsePoint()
    {
        using var directories = new TestDirectories();
        var policy = McpPathAccessPolicy.Create([directories.Allowed]);
        var target = Path.Combine(directories.Outside, "target.mp4");
        var link = Path.Combine(directories.Allowed, "capture.mp4");
        File.WriteAllText(target, "target");
        File.CreateSymbolicLink(link, target);

        try
        {
            Assert.False(policy.IsAllowedOutput(link));
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void RecordingAndScreenshotSettingsMustResolveInsideAllowedDirectories()
    {
        using var directories = new TestDirectories();
        var policy = McpPathAccessPolicy.Create([directories.Allowed]);
        var allowedSettings = Path.Combine(directories.Allowed, "settings.json");
        var outsideSettings = Path.Combine(directories.Outside, "settings.json");
        File.WriteAllText(allowedSettings, "{}");
        File.WriteAllText(outsideSettings, "{}");

        Assert.True(policy.IsAllowedSettingsFile(allowedSettings));
        Assert.False(policy.IsAllowedSettingsFile(outsideSettings));
        Assert.False(policy.IsAllowedSettingsFile("settings.json"));
    }

    private sealed class TestDirectories : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"screenrecorder-mcp-{Guid.NewGuid():N}");

        public TestDirectories()
        {
            Allowed = Path.Combine(_root, "allowed");
            Outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(Allowed);
            Directory.CreateDirectory(Outside);
        }

        public string Allowed { get; }
        public string Outside { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class DirectoryJunctionFactAttribute : FactAttribute
{
    public DirectoryJunctionFactAttribute()
    {
        if (!CanCreateDirectoryJunction()) Skip = "この環境ではジャンクションを作れません。";
    }

    private static bool CanCreateDirectoryJunction()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screenrecorder-link-check-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        try
        {
            Directory.CreateDirectory(target);
            return DirectoryJunction.TryCreate(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

internal static class DirectoryJunction
{
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ShareAll = 0x00000001 | 0x00000002 | 0x00000004;

    public static bool TryCreate(string junctionPath, string targetPath)
    {
        var created = false;
        try
        {
            Directory.CreateDirectory(junctionPath);
            var target = Path.GetFullPath(targetPath);
            var substituteName = target.StartsWith("\\\\", StringComparison.Ordinal)
                ? "\\??\\UNC\\" + target[2..]
                : "\\??\\" + target;
            var substituteBytes = System.Text.Encoding.Unicode.GetBytes(substituteName);
            var printBytes = System.Text.Encoding.Unicode.GetBytes(target);
            var pathBufferLength = substituteBytes.Length + sizeof(ushort) + printBytes.Length + sizeof(ushort);
            var reparseDataLength = 8 + pathBufferLength;
            var buffer = new byte[8 + reparseDataLength];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), IoReparseTagMountPoint);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)reparseDataLength));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substituteBytes.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substituteBytes.Length + sizeof(ushort))));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)printBytes.Length));
            substituteBytes.CopyTo(buffer, 16);
            printBytes.CopyTo(buffer, 16 + substituteBytes.Length + sizeof(ushort));

            using var handle = CreateFile(junctionPath, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
            created = !handle.IsInvalid
                && DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length,
                    IntPtr.Zero, 0, out _, IntPtr.Zero);
            return created;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or OverflowException)
        {
            return false;
        }
        finally
        {
            if (!created && Directory.Exists(junctionPath)) Directory.Delete(junctionPath);
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}

public sealed class FileReparsePointFactAttribute : FactAttribute
{
    public FileReparsePointFactAttribute()
    {
        if (!CanCreateFileLink()) Skip = "この環境ではファイルの再解析ポイントを作れません。";
    }

    private static bool CanCreateFileLink()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screenrecorder-link-check-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        File.WriteAllText(target, "target");
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
