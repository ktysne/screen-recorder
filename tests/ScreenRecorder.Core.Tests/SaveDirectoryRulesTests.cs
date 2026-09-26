using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class SaveDirectoryRulesTests
{
    [Fact]
    public void ConfirmedDirectoryMatchesNormalizedPathIgnoringCase()
    {
        var current = Path.Combine("Media", "Capture");
        var confirmed = Path.Combine("Media", "Temp", "..", "Capture").ToUpperInvariant();

        Assert.True(SaveDirectoryRules.IsConfirmed(confirmed, current));
    }

    [Theory]
    [InlineData(null, "Pictures")]
    [InlineData("Pictures", "Videos")]
    [InlineData("\0", "Pictures")]
    public void MissingOrInvalidConfirmedPathIsNotConfirmed(string? confirmed, string current)
    {
        Assert.False(SaveDirectoryRules.IsConfirmed(confirmed, current));
    }

    [Theory]
    [InlineData(SaveDirectoryKind.StillImage, "Pictures")]
    [InlineData(SaveDirectoryKind.Video, "Videos")]
    public void FallbackDirectoriesUseProfileThenLocalAppData(SaveDirectoryKind kind, string folder)
    {
        var candidates = SaveDirectoryRules.GetFallbackDirectories(kind, "profile", "local");

        Assert.Equal(new[]
        {
            Path.Combine("profile", "ScreenRecorder", folder),
            Path.Combine("local", "ScreenRecorder", folder)
        }, candidates);
    }

    [Fact]
    public void EmptyEnvironmentPathsAreOmitted()
    {
        Assert.Equal(
            new[] { Path.Combine("local", "ScreenRecorder", "Videos") },
            SaveDirectoryRules.GetFallbackDirectories(SaveDirectoryKind.Video, "", "local"));
        Assert.Empty(SaveDirectoryRules.GetFallbackDirectories(SaveDirectoryKind.StillImage, null, " "));
    }
}
