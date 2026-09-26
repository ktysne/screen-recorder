namespace ScreenRecorder.Core;

public sealed record ShortcutBinding(
    RecorderAction Action,
    string SettingName,
    string EnabledSettingName,
    Func<Settings, string> GetNotation,
    Action<Settings, string> SetNotation,
    Func<Settings, bool> GetEnabled,
    Action<Settings, bool> SetEnabled);

public static class ShortcutBindings
{
    // この順序はホットキー登録 ID と設定画面の行順を決める。
    public static IReadOnlyList<ShortcutBinding> All { get; } = Array.AsReadOnly<ShortcutBinding>(
    [
        new(RecorderAction.ScreenshotRegion, nameof(Settings.ScreenshotRegionShortcut), nameof(Settings.ScreenshotRegionEnabled), settings => settings.ScreenshotRegionShortcut, (settings, value) => settings.ScreenshotRegionShortcut = value, settings => settings.ScreenshotRegionEnabled, (settings, value) => settings.ScreenshotRegionEnabled = value),
        new(RecorderAction.ScreenshotFullScreen, nameof(Settings.ScreenshotFullScreenShortcut), nameof(Settings.ScreenshotFullScreenEnabled), settings => settings.ScreenshotFullScreenShortcut, (settings, value) => settings.ScreenshotFullScreenShortcut = value, settings => settings.ScreenshotFullScreenEnabled, (settings, value) => settings.ScreenshotFullScreenEnabled = value),
        new(RecorderAction.ScreenshotWindow, nameof(Settings.ScreenshotWindowShortcut), nameof(Settings.ScreenshotWindowEnabled), settings => settings.ScreenshotWindowShortcut, (settings, value) => settings.ScreenshotWindowShortcut = value, settings => settings.ScreenshotWindowEnabled, (settings, value) => settings.ScreenshotWindowEnabled = value),
        new(RecorderAction.RecordingRegion, nameof(Settings.RecordingRegionShortcut), nameof(Settings.RecordingRegionEnabled), settings => settings.RecordingRegionShortcut, (settings, value) => settings.RecordingRegionShortcut = value, settings => settings.RecordingRegionEnabled, (settings, value) => settings.RecordingRegionEnabled = value),
        new(RecorderAction.RecordingFullScreen, nameof(Settings.RecordingFullScreenShortcut), nameof(Settings.RecordingFullScreenEnabled), settings => settings.RecordingFullScreenShortcut, (settings, value) => settings.RecordingFullScreenShortcut = value, settings => settings.RecordingFullScreenEnabled, (settings, value) => settings.RecordingFullScreenEnabled = value),
        new(RecorderAction.RecordingWindow, nameof(Settings.RecordingWindowShortcut), nameof(Settings.RecordingWindowEnabled), settings => settings.RecordingWindowShortcut, (settings, value) => settings.RecordingWindowShortcut = value, settings => settings.RecordingWindowEnabled, (settings, value) => settings.RecordingWindowEnabled = value),
        new(RecorderAction.PauseResume, nameof(Settings.PauseRecordingShortcut), nameof(Settings.PauseRecordingEnabled), settings => settings.PauseRecordingShortcut, (settings, value) => settings.PauseRecordingShortcut = value, settings => settings.PauseRecordingEnabled, (settings, value) => settings.PauseRecordingEnabled = value)
    ]);

    public static ShortcutBinding For(RecorderAction action) => All.FirstOrDefault(binding => binding.Action == action)
        ?? throw new ArgumentOutOfRangeException(nameof(action));
}
