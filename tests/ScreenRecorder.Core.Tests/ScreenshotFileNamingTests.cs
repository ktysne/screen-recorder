using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class ScreenshotFileNamingTests
{
    private static readonly DateTime CapturedAt = new(2026, 9, 26, 8, 7, 6);

    [Fact]
    public void ExpandTemplate_ReplacesAllSupportedTokens()
    {
        var value = ScreenshotFileNaming.ExpandTemplate("{date}_{time}_{mode}_{window}", CapturedAt, ScreenshotMode.Window, "Report: Q3? draft...");

        Assert.Equal("20260926_080706_window_Report_ Q3_ draft", value);
    }

    [Fact]
    public void ExpandTemplate_ReplacesForbiddenWindowCharacters()
    {
        var value = ScreenshotFileNaming.ExpandTemplate("{window}", CapturedAt, ScreenshotMode.Window, "A/B:C?D*E");

        Assert.Equal("A_B_C_D_E", value);
    }

    [Fact]
    public void ExpandTemplate_LimitsTitleToFortyTextElements()
    {
        var title = new string('界', 41);

        var value = ScreenshotFileNaming.ExpandTemplate("{window}", CapturedAt, ScreenshotMode.Window, title);

        Assert.Equal(new string('界', 40), value);
    }

    [Fact]
    public void ExpandTemplate_UsesDefaultWhenExpansionIsEmpty()
    {
        var value = ScreenshotFileNaming.ExpandTemplate("{window}", CapturedAt, ScreenshotMode.Window);

        Assert.Equal("ScreenRecorder_20260926_080706", value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/name")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    public void ExpandTemplate_UsesDefaultForEmptyOrUnusableNames(string template)
    {
        var value = ScreenshotFileNaming.ExpandTemplate(template, CapturedAt, ScreenshotMode.Full);

        Assert.Equal("ScreenRecorder_20260926_080706", value);
    }

    [Theory]
    [InlineData("report. ", "report")]
    [InlineData(" report", " report")]
    public void ExpandTemplate_RemovesTrailingDotsAndSpaces(string template, string expected)
    {
        Assert.Equal(expected, ScreenshotFileNaming.ExpandTemplate(template, CapturedAt, ScreenshotMode.Full));
    }

    [Fact]
    public void GetAvailablePath_AddsMonthFolderAndSkipsExistingNames()
    {
        var path = ScreenshotFileNaming.GetAvailablePath(
            "images", true, CapturedAt, ScreenshotMode.Region, null, "Shot_{date}", ".png",
            candidate => candidate is "images\\2026-09\\Shot_20260926.png" or "images\\2026-09\\Shot_20260926_2.png");

        Assert.Equal(Path.Combine("images", "2026-09", "Shot_20260926_3.png"), path);
    }
}
