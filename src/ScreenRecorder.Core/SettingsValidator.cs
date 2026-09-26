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
        Require(settings.JpegQuality is >= 1 and <= 100, nameof(settings.JpegQuality));
        Require(Enum.IsDefined(settings.PngCompression), nameof(settings.PngCompression));
        Require(settings.CaptureDelaySeconds is 0 or 3 or 5 or 10, nameof(settings.CaptureDelaySeconds));
        Require(Enum.IsDefined(settings.AfterCaptureAction), nameof(settings.AfterCaptureAction));
        Require(settings.FrameRate is 15 or 24 or 30 or 60, nameof(settings.FrameRate));
        Require(settings.VideoBitrateMbps is >= 1 and <= 100, nameof(settings.VideoBitrateMbps));
        Require(settings.CountdownSeconds is 0 or 3 or 5, nameof(settings.CountdownSeconds));
        Require(settings.OutputScalePercent is 50 or 75 or 100, nameof(settings.OutputScalePercent));
        Require(Enum.IsDefined(settings.Encoder), nameof(settings.Encoder));
        Require(Enum.IsDefined(settings.AudioFormat), nameof(settings.AudioFormat));
        Require(settings.AacBitrateKbps is 96 or 128 or 160 or 192, nameof(settings.AacBitrateKbps));
        Require(settings.Mp3BitrateKbps is 128 or 192 or 256 or 320, nameof(settings.Mp3BitrateKbps));

        foreach (var issue in ShortcutSettingsValidator.Validate(settings))
        {
            issues.Add(new SettingsIssue(
                ShortcutSettingsValidator.GetSettingName(issue.Action),
                issue.Kind == ShortcutValidationIssueKind.Duplicate ? SettingsIssueKind.DuplicateShortcut : SettingsIssueKind.InvalidShortcut));
        }
        return issues;
    }
}
