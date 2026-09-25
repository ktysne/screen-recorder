using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class Mp3TranscodeRulesTests
{
    [Theory]
    [InlineData(128, "128k")]
    [InlineData(192, "192k")]
    [InlineData(256, "256k")]
    [InlineData(320, "320k")]
    public void BuildArguments_UsesConfiguredBitrate(int bitrate, string expectedBitrate)
    {
        var arguments = Mp3TranscodeRules.BuildArguments("C:\\Videos\\source file.mp4", "C:\\Videos\\converted file.mp4", bitrate);

        Assert.Equal(
            ["-y", "-i", "C:\\Videos\\source file.mp4", "-c:v", "copy", "-c:a", "libmp3lame", "-b:a", expectedBitrate, "C:\\Videos\\converted file.mp4"],
            arguments);
    }

    [Fact]
    public void DecideOutcome_UsesConvertedFileWhenProcessAndOutputSucceed() =>
        Assert.Equal(Mp3TranscodeOutcome.UseConvertedFile, Mp3TranscodeRules.DecideOutcome(true, 0, true));

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, null, true)]
    public void DecideOutcome_KeepsAacWhenConversionIsUnavailableOrFails(bool available, int? exitCode, bool outputExists) =>
        Assert.Equal(Mp3TranscodeOutcome.KeepAacFile, Mp3TranscodeRules.DecideOutcome(available, exitCode, outputExists));

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(101, 100, true)]
    [InlineData(99, 100, false)]
    [InlineData(100, -1, false)]
    public void HasEnoughFreeSpace_RequiresSpaceForAnotherFile(long freeBytes, long inputBytes, bool expected) =>
        Assert.Equal(expected, Mp3TranscodeRules.HasEnoughFreeSpace(freeBytes, inputBytes));
}
