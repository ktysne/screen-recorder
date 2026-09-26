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
    public static string GetSettingName(RecorderAction action) => ShortcutBindings.For(action).SettingName;

    public static IReadOnlyList<ShortcutAssignment> GetAssignments(Settings settings) => ShortcutBindings.All
        .Select(binding => new ShortcutAssignment(binding.Action, binding.GetNotation(settings), binding.GetEnabled(settings)))
        .ToArray();

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
