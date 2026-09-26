using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class CaptureCompletionRulesTests
{
    [Theory]
    [InlineData(false, false, CaptureNotification.None)]
    [InlineData(false, true, CaptureNotification.Saved)]
    [InlineData(true, false, CaptureNotification.Warning)]
    [InlineData(true, true, CaptureNotification.Warning)]
    public void DecideNotification_PrioritizesWarning(bool hasWarning, bool notifyWhenSaved, CaptureNotification expected)
    {
        Assert.Equal(expected, CaptureCompletionRules.DecideNotification(hasWarning, notifyWhenSaved));
    }

    [Fact]
    public void ErrorDetail_KeepsExactly180Characters()
    {
        var value = new string('x', 180);

        Assert.Equal(value, CaptureText.ErrorDetail(value));
    }

    [Fact]
    public void ErrorDetail_TruncatesOneCharacterOver180Characters()
    {
        var value = new string('x', 181);

        Assert.Equal(new string('x', 180), CaptureText.ErrorDetail(value));
    }

    [Fact]
    public void PathDetail_KeepsExactly150Characters()
    {
        var value = new string('x', 150);

        Assert.Equal(value, CaptureText.PathDetail(value));
    }

    [Fact]
    public void PathDetail_PrefixesAnEllipsisAndKeepsTheEndWhenOneCharacterOver150Characters()
    {
        var value = new string('x', 151);

        Assert.Equal($"…{new string('x', 150)}", CaptureText.PathDetail(value));
    }

    [Theory]
    [InlineData(ScreenshotMode.Full, "ディスプレイ全体")]
    [InlineData(ScreenshotMode.Region, "範囲指定")]
    [InlineData(ScreenshotMode.Window, "ウィンドウ指定")]
    public void CaptureMethodName_ReturnsTheJapaneseMethodName(ScreenshotMode mode, string expected)
    {
        Assert.Equal(expected, CaptureText.CaptureMethodName(mode));
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(183, true)]
    [InlineData(5, false)]
    public void IsAlreadyExists_ChecksTheLow16BitsOfHResult(int errorCode, bool expected)
    {
        Assert.Equal(expected, CaptureText.IsAlreadyExists(new HResultIOException(errorCode)));
    }

    private sealed class HResultIOException : IOException
    {
        public HResultIOException(int errorCode) => HResult = unchecked((int)0x80070000) | errorCode;
    }
}
