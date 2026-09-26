using System.IO.Compression;
using System.Text;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class UpdatePlanExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ScreenRecorder.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _source;

    public UpdatePlanExecutorTests()
    {
        _install = Path.Combine(_root, "install");
        _source = Path.Combine(_root, "source");
        Write(_install, "ScreenRecorder.exe", "old exe");
        Write(_install, "ScreenRecorder.exe.old", "user backup");
        Write(_install, "ScreenRecorderLib.dll", "old lib");
        Write(_install, "manual.html", "old manual");
        Write(_install, "user-notes.txt", "user file");
        Write(_source, "ScreenRecorder.exe", "new exe");
        Write(_source, "ScreenRecorderLib.dll", "new lib");
        Write(_source, "manual.html", "new manual");
        Write(_source, "ffmpeg/ffmpeg.exe", "new ffmpeg");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ApplyReplacesPackageFilesAndKeepsBackups()
    {
        var plan = CreatePlan();
        var outcome = UpdatePlanExecutor.Apply(plan, _source, _install, new FileSystemUpdateOperations());

        Assert.True(outcome.Succeeded);
        Assert.Equal("new exe", Read("ScreenRecorder.exe"));
        Assert.Equal("new ffmpeg", Read("ffmpeg/ffmpeg.exe"));
        Assert.Equal("old exe", Read(plan.Steps.Single(step => step.RelativePath == "ScreenRecorder.exe").BackupRelativePath));
        Assert.Equal("old lib", Read(plan.Steps.Single(step => step.RelativePath == "ScreenRecorderLib.dll").BackupRelativePath));
        Assert.Equal("user backup", Read("ScreenRecorder.exe.old"));
        Assert.Equal("user file", Read("user-notes.txt"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FailedCopyRestoresOriginalInstallation(int failingCopy)
    {
        var before = Snapshot();
        var files = new FailingOperations { FailOnCopyNumber = failingCopy };

        var outcome = UpdatePlanExecutor.Apply(CreatePlan(), _source, _install, files);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.RolledBackCompletely);
        Assert.NotNull(outcome.FailedPath);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void FailedBackupRestoresOriginalInstallation()
    {
        var before = Snapshot();
        var files = new FailingOperations { FailOnMoveNumber = 2 };

        var outcome = UpdatePlanExecutor.Apply(CreatePlan(), _source, _install, files);

        Assert.True(outcome.RolledBackCompletely);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void FullRollbackAfterSuccessfulApplyRestoresPreviousVersion()
    {
        var before = Snapshot();
        var plan = CreatePlan();
        var files = new FileSystemUpdateOperations();
        Assert.True(UpdatePlanExecutor.Apply(plan, _source, _install, files).Succeeded);

        var failures = UpdatePlanExecutor.Rollback(UpdateApplyPlanner.CreateFullRollbackPlan(plan), _install, files);

        Assert.Empty(failures);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void ExistingUserBackupIsUntouched()
    {
        var plan = CreatePlan();
        var outcome = UpdatePlanExecutor.Apply(plan, _source, _install, new FileSystemUpdateOperations());
        Assert.True(outcome.Succeeded);
        Assert.Equal("user backup", Read("ScreenRecorder.exe.old"));
        Assert.Equal("old exe", Read(plan.Steps.Single(step => step.RelativePath == "ScreenRecorder.exe").BackupRelativePath));
    }

    [Fact]
    public void ApplyAbortsWithoutChangingFilesWhenPlannedBackupAppears()
    {
        var plan = CreatePlan();
        var backupPath = plan.Steps.Single(step => step.RelativePath == "ScreenRecorder.exe").BackupRelativePath;
        Write(_install, backupPath, "existing backup");
        var before = Snapshot();

        var outcome = UpdatePlanExecutor.Apply(plan, _source, _install, new FileSystemUpdateOperations());

        Assert.False(outcome.Succeeded);
        Assert.Equal(Path.Combine(_install, backupPath), outcome.FailedPath);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void LockedFileIsReportedAsNotExclusive()
    {
        var path = Path.Combine(_install, "ScreenRecorderLib.dll");
        var files = new FileSystemUpdateOperations();
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(files.CanOpenExclusively(path));
        Assert.True(files.CanOpenExclusively(path));
        Assert.True(files.CanOpenExclusively(Path.Combine(_install, "missing.dll")));
    }

    [Fact]
    public void ExtractWritesPackageFilesUnderDestination()
    {
        var zip = CreateZip(("ScreenRecorder.exe", "exe"), ("ffmpeg/ffmpeg.exe", "ffmpeg"));
        var destination = Path.Combine(_root, "extracted");

        var files = UpdatePackage.Extract(zip, destination);

        Assert.Equal(["ScreenRecorder.exe", "ffmpeg\\ffmpeg.exe"], files);
        Assert.Equal("ffmpeg", File.ReadAllText(Path.Combine(destination, "ffmpeg", "ffmpeg.exe")));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("ffmpeg/../../evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("/evil.dll")]
    public void ExtractRejectsEntriesOutsideDestinationWithoutWritingAnything(string name)
    {
        var zip = CreateZip(("ScreenRecorder.exe", "exe"), (name, "evil"));
        var destination = Path.Combine(_root, "extracted");

        Assert.Throws<UpdatePackageException>(() => UpdatePackage.Extract(zip, destination));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(_root, "evil.dll")));
    }

    [Fact]
    public void ExtractRequiresExecutableAtRoot()
    {
        var zip = CreateZip(("ScreenRecorder/ScreenRecorder.exe", "exe"));
        Assert.Throws<UpdatePackageException>(() => UpdatePackage.Extract(zip, Path.Combine(_root, "extracted")));
    }

    [Fact]
    public void Sha256IsComparedWithoutCase()
    {
        var path = Path.Combine(_root, "data.bin");
        File.WriteAllText(path, "abc", new UTF8Encoding(false));
        const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.Equal(expected, UpdatePackage.ComputeSha256(path));
        Assert.True(UpdatePackage.MatchesSha256(path, expected.ToUpperInvariant()));
        Assert.False(UpdatePackage.MatchesSha256(path, new string('0', 64)));
    }

    [Fact]
    public void CleanupRecordRoundTripsAndAppliesToSameInstallationAndOlderVersions()
    {
        var record = new UpdateCleanupRecord(_install, "0.2.0", ["ScreenRecorder.exe.old", "../outside.old", "manual.html"]);
        var restored = UpdateCleanupRecord.TryDeserialize(record.Serialize())!;

        Assert.True(restored.CanBeCleanedBy(_install + Path.DirectorySeparatorChar, "0.2.0"));
        Assert.True(restored.CanBeCleanedBy(_install, "0.3.0"));
        Assert.False(restored.CanBeCleanedBy(_install, "0.1.0"));
        Assert.False(restored.CanBeCleanedBy(_source, "0.2.0"));
        Assert.Equal([Path.Combine(_install, "ScreenRecorder.exe.old")], restored.GetBackupFilePaths());
        Assert.Null(UpdateCleanupRecord.TryDeserialize("{"));
        Assert.Null(UpdateCleanupRecord.TryDeserialize("{}"));
    }

    private UpdateApplyPlan CreatePlan()
    {
        var result = UpdateApplyPlanner.CreatePlan(UpdatePackage.ListFiles(_source), path => File.Exists(Path.Combine(_install, path)));
        Assert.Null(result.Error);
        return result.Plan!;
    }

    private Dictionary<string, string> Snapshot() => UpdatePackage.ListFiles(_install)
        .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(_install, path)));

    private string Read(string relativePath) => File.ReadAllText(Path.Combine(_install, relativePath));

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string CreateZip(params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    private sealed class FailingOperations : IUpdateFileOperations
    {
        private readonly FileSystemUpdateOperations _inner = new();
        private int _copies;
        private int _moves;
        public int FailOnCopyNumber { get; init; }
        public int FailOnMoveNumber { get; init; }

        public bool FileExists(string path) => _inner.FileExists(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public bool CanOpenExclusively(string path) => _inner.CanOpenExclusively(path);

        public void MoveFile(string source, string destination)
        {
            if (++_moves == FailOnMoveNumber) throw new IOException("injected move failure");
            _inner.MoveFile(source, destination);
        }

        // 書きかけのファイルが残る失敗を再現するため、失敗する前に中身の一部を書く。
        public void CopyFile(string source, string destination)
        {
            if (++_copies == FailOnCopyNumber)
            {
                File.WriteAllText(destination, "partial");
                throw new IOException("injected copy failure");
            }
            _inner.CopyFile(source, destination);
        }
    }
}
