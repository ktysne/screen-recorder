using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class UpdateCleanupRecordTests
{
    private readonly string _install = Path.Combine(Path.GetTempPath(), "ScreenRecorder.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CleanupRecordFileNameIncludesUpdateIdOutsideTheStagingPrefix()
    {
        var fileName = UpdateCleanupRecord.GetFileName("abc123");

        Assert.Equal("cleanup-abc123.json", fileName);
        Assert.False(fileName.StartsWith("ScreenRecorder-", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordForAnotherInstallationOrNewerVersionIsPreservedWithoutDeletingFiles()
    {
        var record = new UpdateCleanupRecord(_install, "0.2.0", ["ScreenRecorder.exe.a1.old"]);

        var result = record.KeepUndeletedBackups(_install + "-other", "0.2.0", _ => throw new InvalidOperationException());

        Assert.Same(record, result);
        Assert.Same(record, record.KeepUndeletedBackups(_install, "0.1.9", _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData("0.2.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.1.9", true)]
    [InlineData("0.2.0", "0.2.1", false)]
    [InlineData("invalid", "0.2.0", false)]
    [InlineData("0.2.0", "invalid", false)]
    public void CleanupEligibilityRequiresReadableVersionsAndDoesNotTargetNewerRecords(string currentVersion, string recordVersion, bool expected)
    {
        var record = new UpdateCleanupRecord(_install, recordVersion, []);

        Assert.Equal(expected, record.CanBeCleanedBy(_install, currentVersion));
    }

    [Fact]
    public void CleanupEligibilityRequiresTheSameInstallation()
    {
        var record = new UpdateCleanupRecord(_install, "0.1.0", []);

        Assert.False(record.CanBeCleanedBy(_install + "-other", "0.2.0"));
    }

    [Fact]
    public void OnlyBackupsThatCouldNotBeDeletedRemainInTheRecord()
    {
        var record = new UpdateCleanupRecord(_install, "0.2.0", ["ScreenRecorder.exe.a1.old", "manual.html.a1.old", "../outside.old"]);
        var deleted = new List<string>();

        var remaining = record.KeepUndeletedBackups(_install, "0.2.0", path =>
        {
            deleted.Add(path);
            return path.EndsWith("ScreenRecorder.exe.a1.old", StringComparison.Ordinal);
        });

        Assert.Equal(
        [
            Path.Combine(_install, "ScreenRecorder.exe.a1.old"),
            Path.Combine(_install, "manual.html.a1.old")
        ], deleted);
        Assert.Equal(["manual.html.a1.old"], remaining!.BackupFiles);
    }

    [Fact]
    public void FullyDeletedBackupsRemoveTheRecord()
    {
        var record = new UpdateCleanupRecord(_install, "0.2.0", ["ScreenRecorder.exe.a1.old", "manual.html.a1.old"]);

        Assert.Null(record.KeepUndeletedBackups(_install, "0.2.1", _ => true));
    }
}
