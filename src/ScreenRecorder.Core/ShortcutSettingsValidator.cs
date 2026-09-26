namespace ScreenRecorder.Core;

public sealed record ShortcutAssignment(RecorderAction Action, string Notation, bool Enabled);

public enum ShortcutValidationIssueKind
{
    InvalidNotation,
    Duplicate
}

public sealed record ShortcutValidationIssue(RecorderAction Action, ShortcutValidationIssueKind Kind, string Notation);

public static class ShortcutSettingsValidator
{
    public static string GetSettingName(RecorderAction action) => action switch
    {
        RecorderAction.ScreenshotRegion => nameof(Settings.ScreenshotRegionShortcut),
        RecorderAction.ScreenshotFullScreen => nameof(Settings.ScreenshotFullScreenShortcut),
        RecorderAction.ScreenshotWindow => nameof(Settings.ScreenshotWindowShortcut),
        RecorderAction.RecordingRegion => nameof(Settings.RecordingRegionShortcut),
        RecorderAction.RecordingFullScreen => nameof(Settings.RecordingFullScreenShortcut),
        RecorderAction.RecordingWindow => nameof(Settings.RecordingWindowShortcut),
        RecorderAction.PauseResume => nameof(Settings.PauseRecordingShortcut),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public static IReadOnlyList<ShortcutAssignment> GetAssignments(Settings settings) =>
    [
        new(RecorderAction.ScreenshotRegion, settings.ScreenshotRegionShortcut, settings.ScreenshotRegionEnabled),
        new(RecorderAction.ScreenshotFullScreen, settings.ScreenshotFullScreenShortcut, settings.ScreenshotFullScreenEnabled),
        new(RecorderAction.ScreenshotWindow, settings.ScreenshotWindowShortcut, settings.ScreenshotWindowEnabled),
        new(RecorderAction.RecordingRegion, settings.RecordingRegionShortcut, settings.RecordingRegionEnabled),
        new(RecorderAction.RecordingFullScreen, settings.RecordingFullScreenShortcut, settings.RecordingFullScreenEnabled),
        new(RecorderAction.RecordingWindow, settings.RecordingWindowShortcut, settings.RecordingWindowEnabled),
        new(RecorderAction.PauseResume, settings.PauseRecordingShortcut, settings.PauseRecordingEnabled)
    ];

    public static IReadOnlyList<ShortcutValidationIssue> Validate(Settings settings)
    {
        var assignments = GetAssignments(settings);
        var parsed = new List<(ShortcutAssignment Assignment, HotkeyShortcut Shortcut)>();
        var issues = new List<ShortcutValidationIssue>();
        foreach (var assignment in assignments)
        {
            if (!assignment.Enabled) continue;
            if (!HotkeyShortcut.TryParse(assignment.Notation, out var shortcut))
            {
                issues.Add(new ShortcutValidationIssue(assignment.Action, ShortcutValidationIssueKind.InvalidNotation, assignment.Notation));
                continue;
            }
            if (shortcut is not null) parsed.Add((assignment, shortcut));
        }

        foreach (var group in parsed.GroupBy(item => item.Shortcut).Where(group => group.Count() > 1))
        {
            foreach (var item in group)
                issues.Add(new ShortcutValidationIssue(item.Assignment.Action, ShortcutValidationIssueKind.Duplicate, item.Assignment.Notation));
        }
        return issues;
    }
}
