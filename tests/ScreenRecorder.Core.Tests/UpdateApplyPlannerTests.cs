using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class UpdateApplyPlannerTests
{
    private static readonly string[] PackageFiles =
    [
        "ScreenRecorder.exe",
        "ScreenRecorderLib.dll",
        "ffmpeg/ffmpeg.exe",
        "manual.html"
    ];

    private static UpdateApplyPlan CreatePlan(IEnumerable<string> package, params string[] installed)
    {
        var existing = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        var result = UpdateApplyPlanner.CreatePlan(package, existing.Contains);
        Assert.Null(result.Error);
        return result.Plan!;
    }

    [Fact]
    public void ExistingFilesAreBackedUpAndNewFilesAreOnlyCopied()
    {
        var plan = CreatePlan(PackageFiles, "ScreenRecorder.exe", "ScreenRecorderLib.dll", "manual.html");
        Assert.Equal(["ffmpeg\\ffmpeg.exe", "manual.html", "ScreenRecorder.exe", "ScreenRecorderLib.dll"], plan.Steps.Select(step => step.RelativePath));
        Assert.False(plan.Steps.Single(step => step.RelativePath == "ffmpeg\\ffmpeg.exe").ReplacesExistingFile);
        Assert.True(plan.Steps.Single(step => step.RelativePath == "ScreenRecorder.exe").ReplacesExistingFile);
        Assert.Equal(["manual.html.old", "ScreenRecorder.exe.old", "ScreenRecorderLib.dll.old"], plan.BackupPaths);
    }

    [Fact]
    public void OnlyExistingExecutablesAndLibrariesNeedExclusiveAccess()
    {
        var plan = CreatePlan(PackageFiles, "ScreenRecorder.exe", "ScreenRecorderLib.dll", "manual.html");
        Assert.Equal(["ScreenRecorder.exe", "ScreenRecorderLib.dll"], plan.ExclusiveAccessPaths);
    }

    [Fact]
    public void LeftoverBackupIsRemovedBeforeBackingUp()
    {
        var plan = CreatePlan(PackageFiles, "ScreenRecorder.exe", "ScreenRecorder.exe.old");
        var step = plan.Steps.Single(step => step.RelativePath == "ScreenRecorder.exe");
        Assert.True(step.RemovesStaleBackup);
        Assert.True(step.ReplacesExistingFile);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("ffmpeg/../../evil.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("/evil.dll")]
    [InlineData("\\\\server\\share\\evil.dll")]
    [InlineData("a:stream")]
    [InlineData("dir//evil.dll")]
    [InlineData("evil.dll.")]
    [InlineData("evil.dll ")]
    [InlineData("NUL.txt")]
    [InlineData("ScreenRecorder.exe.old")]
    public void UnsafeOrReservedNamesRejectPlan(string name)
    {
        var result = UpdateApplyPlanner.CreatePlan(["ScreenRecorder.exe", name], _ => false);
        Assert.Null(result.Plan);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void DuplicateNamesDifferingOnlyByCaseRejectPlan()
    {
        Assert.Null(UpdateApplyPlanner.CreatePlan(["ScreenRecorder.exe", "a.dll", "A.DLL"], _ => false).Plan);
    }

    [Fact]
    public void PackageWithoutExecutableRejectsPlan()
    {
        Assert.Null(UpdateApplyPlanner.CreatePlan(["ffmpeg/ScreenRecorder.exe", "ScreenRecorderLib.dll"], _ => false).Plan);
    }

    [Fact]
    public void RollbackUndoesStartedStepsInReverseOrder()
    {
        var plan = CreatePlan(PackageFiles, "ScreenRecorder.exe", "manual.html");
        UpdateStepProgress[] progress =
        [
            new(BackedUp: false, CopyStarted: true),
            new(BackedUp: true, CopyStarted: true),
            new(BackedUp: true, CopyStarted: false),
            UpdateStepProgress.NotStarted
        ];
        var rollback = UpdateApplyPlanner.CreateRollbackPlan(plan, progress);
        Assert.Equal(
        [
            new UpdateRollbackAction(UpdateRollbackActionKind.RestoreBackup, "ScreenRecorder.exe"),
            new UpdateRollbackAction(UpdateRollbackActionKind.DeleteInstalledFile, "manual.html"),
            new UpdateRollbackAction(UpdateRollbackActionKind.RestoreBackup, "manual.html"),
            new UpdateRollbackAction(UpdateRollbackActionKind.DeleteInstalledFile, "ffmpeg\\ffmpeg.exe")
        ], rollback);
    }

    [Fact]
    public void FullRollbackDeletesAddedFilesAndRestoresEveryBackup()
    {
        var plan = CreatePlan(["ScreenRecorder.exe", "new.dll"], "ScreenRecorder.exe");
        Assert.Equal(
        [
            new UpdateRollbackAction(UpdateRollbackActionKind.DeleteInstalledFile, "ScreenRecorder.exe"),
            new UpdateRollbackAction(UpdateRollbackActionKind.RestoreBackup, "ScreenRecorder.exe"),
            new UpdateRollbackAction(UpdateRollbackActionKind.DeleteInstalledFile, "new.dll")
        ], UpdateApplyPlanner.CreateFullRollbackPlan(plan));
    }

    [Fact]
    public void RollbackRequiresProgressForEveryStep()
    {
        var plan = CreatePlan(PackageFiles);
        Assert.Throws<ArgumentException>(() => UpdateApplyPlanner.CreateRollbackPlan(plan, [UpdateStepProgress.NotStarted]));
    }
}
