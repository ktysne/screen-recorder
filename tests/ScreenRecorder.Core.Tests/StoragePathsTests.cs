using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class StoragePathsTests
{
    [Theory]
    [InlineData(@"C:\videos", @"C:\")]
    [InlineData(@"\\server\share\videos", @"\\server\share\")]
    [InlineData(@"\\server\share", @"\\server\share\")]
    public void GetVolumeRoot_ReturnsRootWithTrailingSeparator(string fullPath, string expected)
    {
        Assert.Equal(expected, StoragePaths.GetVolumeRoot(fullPath));
    }
}
