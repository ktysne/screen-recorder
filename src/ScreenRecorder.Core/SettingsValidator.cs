namespace ScreenRecorder.Core;

public enum SettingsIssueKind
{
    InvalidValue,
    InvalidShortcut,
    DuplicateShortcut
}

public sealed record SettingsIssue(string SettingName, SettingsIssueKind Kind);

public static class SettingsValidator
{
    public static IReadOnlyList<SettingsIssue> Validate(Settings settings)
    {
        var issues = new List<SettingsIssue>();
        void Require(bool condition, string name)
        {
            if (!condition) issues.Add(new SettingsIssue(name, SettingsIssueKind.InvalidValue));
        }

        Require(!string.IsNullOrWhiteSpace(settings.StillImageDirectory), nameof(settings.StillImageDirectory));
        Require(!string.IsNullOrWhiteSpace(settings.VideoDirectory), nameof(settings.VideoDirectory));
        Require(Enum.IsDefined(settings.ImageFormat), nameof(settings.ImageFormat));
        Require(Enum.IsDefined(settings.PngCompression), nameof(settings.PngCompression));
        Require(Enum.IsDefined(settings.AfterCaptureAction), nameof(settings.AfterCaptureAction));
        Require(Enum.IsDefined(settings.WindowScreenshotShortcutTarget), nameof(settings.WindowScreenshotShortcutTarget));
        Require(Enum.IsDefined(settings.Encoder), nameof(settings.Encoder));
        Require(Enum.IsDefined(settings.AudioFormat), nameof(settings.AudioFormat));
        foreach (var setting in SettingsSchema.IntSettings)
            Require(setting.IsValid(setting.Get(settings)), setting.Name);

        foreach (var issue in ShortcutSettingsValidator.Validate(settings))
        {
            issues.Add(new SettingsIssue(
                ShortcutSettingsValidator.GetSettingName(issue.Action),
                issue.Kind == ShortcutValidationIssueKind.Duplicate ? SettingsIssueKind.DuplicateShortcut : SettingsIssueKind.InvalidShortcut));
        }
        return issues;
    }
}
