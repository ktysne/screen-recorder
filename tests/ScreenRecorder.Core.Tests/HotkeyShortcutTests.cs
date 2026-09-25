using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class HotkeyShortcutTests
{
    [Fact]
    public void ParseAndDisplayRoundTripCanonicalShortcut()
    {
        Assert.True(HotkeyShortcut.TryParse("Ctrl+Shift+PrtSc", out var shortcut));
        Assert.Equal("Ctrl+Shift+PrtSc", shortcut!.ToDisplayString());
        Assert.True(HotkeyShortcut.TryParse(shortcut.ToDisplayString(), out var reparsed));
        Assert.Equal(shortcut, reparsed);
    }

    [Fact]
    public void NormalizeModifierOrderAndLetterCase()
    {
        Assert.True(HotkeyShortcut.TryParse("sHiFt + cTrL + printscreen", out var shortcut));
        Assert.Equal("Ctrl+Shift+PrtSc", shortcut!.ToDisplayString());
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl++PrtSc")]
    [InlineData("Ctrl+Ctrl+PrtSc")]
    [InlineData("PrtSc+F2")]
    [InlineData("NotAKey")]
    public void RejectMalformedShortcut(string notation)
    {
        Assert.False(HotkeyShortcut.TryParse(notation, out _));
    }

    [Fact]
    public void EmptyShortcutMeansUnassigned()
    {
        Assert.True(HotkeyShortcut.TryParse(string.Empty, out var shortcut));
        Assert.Null(shortcut);
    }

    [Fact]
    public void DetectDuplicateShortcutsAfterNormalization()
    {
        var settings = new Settings
        {
            ScreenshotRegionShortcut = "Alt+PrtSc",
            ScreenshotWindowShortcut = " alt + printscreen ",
            PauseRecordingShortcut = string.Empty
        };

        var duplicates = ShortcutSettingsValidator.Validate(settings)
            .Where(issue => issue.Kind == ShortcutValidationIssueKind.Duplicate)
            .Select(issue => issue.Action)
            .Order()
            .ToArray();

        Assert.Equal(new[] { RecorderAction.ScreenshotRegion, RecorderAction.ScreenshotWindow }, duplicates);
    }

    [Fact]
    public void SettingsValidationRejectsInvalidValuesAndShortcutNotation()
    {
        var settings = new Settings { VideoBitrateMbps = 101, ScreenshotRegionShortcut = "Ctrl+UnknownKey" };

        var issues = SettingsValidator.Validate(settings);

        Assert.Contains(issues, issue => issue.SettingName == nameof(Settings.VideoBitrateMbps) && issue.Kind == SettingsIssueKind.InvalidValue);
        Assert.Contains(issues, issue => issue.SettingName == nameof(Settings.ScreenshotRegionShortcut) && issue.Kind == SettingsIssueKind.InvalidShortcut);
    }

    [Theory]
    [InlineData(0xBA, "Semicolon")]
    [InlineData(0xBB, "Equals")]
    [InlineData(0xBF, "Slash")]
    [InlineData(0xDB, "LBracket")]
    public void VirtualKeyRoundTripsThroughDisplayName(int virtualKey, string keyName)
    {
        Assert.True(HotkeyShortcut.TryCreate(HotkeyModifiers.Control, virtualKey, out var shortcut));
        Assert.Equal($"Ctrl+{keyName}", shortcut!.ToDisplayString());
        Assert.True(HotkeyShortcut.TryParse(shortcut.ToDisplayString(), out var reparsed));
        Assert.Equal(shortcut, reparsed);
    }
}
