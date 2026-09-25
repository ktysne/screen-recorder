using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class SettingsValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSaveDirectoriesAreInvalid(string blank)
    {
        var issues = SettingsValidator.Validate(new Settings { StillImageDirectory = blank, VideoDirectory = blank });

        Assert.Contains(new SettingsIssue(nameof(Settings.StillImageDirectory), SettingsIssueKind.InvalidValue), issues);
        Assert.Contains(new SettingsIssue(nameof(Settings.VideoDirectory), SettingsIssueKind.InvalidValue), issues);
    }

    [Fact]
    public void DefaultSettingsHaveNoIssues()
    {
        Assert.Empty(SettingsValidator.Validate(new Settings()));
    }
}
