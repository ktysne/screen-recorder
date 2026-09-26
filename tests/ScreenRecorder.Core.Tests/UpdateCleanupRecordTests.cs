using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class UpdateCleanupRecordTests
{
    private readonly string _install = Path.Combine(Path.GetTempPath(), "ScreenRecorder.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RecordForAnotherInstallationOrVersionIsPreservedWithoutDeletingFiles()
    {
        var record = new UpdateCleanupRecord(_install, "0.2.0", ["ScreenRecorder.exe.a1.old"]);

        var result = record.KeepUndeletedBackups(_install + "-other", "0.2.0", _ => throw new InvalidOperationException());

        Assert.Same(record, result);
        Assert.Same(record, record.KeepUndeletedBackups(_install, "0.2.1", _ => throw new InvalidOperationException()));
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

        Assert.Null(record.KeepUndeletedBackups(_install, "0.2.0", _ => true));
    }
}
