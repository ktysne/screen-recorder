using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal enum TrayMenuCommand
{
    OpenImageFolder,
    OpenVideoFolder,
    Settings,
    Manual,
    CheckForUpdates,
    Exit
}

internal sealed class TrayMenu : IDisposable
{
    private readonly Dictionary<RecorderAction, ToolStripMenuItem> _shortcutMenuItems = [];
    private readonly Dictionary<RecorderAction, ToolStripMenuItem> _captureMenuParents = [];
    private readonly ToolStripMenuItem _pauseResumeItem;
    private readonly ToolStripMenuItem _stopRecordingItem;

    public TrayMenu(Action<RecorderAction> performAction, Action<TrayMenuCommand> performCommand)
    {
        Strip = new ContextMenuStrip { ShowItemToolTips = true };
        Strip.Items.Add(CreateCaptureMenu(UiLabels.Screenshot,
            RecorderAction.ScreenshotFullScreen, RecorderAction.ScreenshotRegion, RecorderAction.ScreenshotWindow, performAction));
        Strip.Items.Add(CreateCaptureMenu(UiLabels.Record,
            RecorderAction.RecordingFullScreen, RecorderAction.RecordingRegion, RecorderAction.RecordingWindow, performAction));
        _pauseResumeItem = CreateActionItem(UiLabels.PauseResume, RecorderAction.PauseResume, performAction);
        _pauseResumeItem.Enabled = false;
        Strip.Items.Add(_pauseResumeItem);
        _stopRecordingItem = CreateActionItem(UiLabels.StopRecording, RecorderAction.StopRecording, performAction);
        _stopRecordingItem.Enabled = false;
        Strip.Items.Add(_stopRecordingItem);
        Strip.Items.Add(new ToolStripSeparator());
        Strip.Items.Add(new ToolStripMenuItem(UiLabels.OpenImageFolder, null, (_, _) => performCommand(TrayMenuCommand.OpenImageFolder)));
        Strip.Items.Add(new ToolStripMenuItem(UiLabels.OpenVideoFolder, null, (_, _) => performCommand(TrayMenuCommand.OpenVideoFolder)));
        Strip.Items.Add(new ToolStripSeparator());
        Strip.Items.Add(new ToolStripMenuItem(UiLabels.Settings, null, (_, _) => performCommand(TrayMenuCommand.Settings)));
        Strip.Items.Add(new ToolStripMenuItem(UiLabels.Manual, null, (_, _) => performCommand(TrayMenuCommand.Manual)));
        Strip.Items.Add(new ToolStripMenuItem(UiLabels.CheckForUpdates, null, (_, _) => performCommand(TrayMenuCommand.CheckForUpdates)));
        Strip.Items.Add(new ToolStripSeparator());
        Strip.Items.Add(new ToolStripMenuItem(UiLabels.Exit, null, (_, _) => performCommand(TrayMenuCommand.Exit)));
    }

    public ContextMenuStrip Strip { get; }

    public void ApplyShortcutAssignments(Settings settings)
    {
        foreach (var assignment in ShortcutSettingsValidator.GetAssignments(settings))
        {
            if (!_shortcutMenuItems.TryGetValue(assignment.Action, out var item)) continue;
            if (assignment.Action != RecorderAction.PauseResume)
            {
                item.Enabled = assignment.Enabled;
                item.ToolTipText = assignment.Enabled ? string.Empty : UiLabels.ShortcutDisabledToolTip;
            }
            item.ShortcutKeyDisplayString = HotkeyShortcut.ToDisplayNotation(assignment.Notation) ?? string.Empty;
        }
        foreach (var parent in _captureMenuParents.Values.Distinct())
            parent.Enabled = parent.DropDownItems.Cast<ToolStripMenuItem>().Any(item => item.Enabled);
    }

    public void ApplyRecordingState(VideoRecordingState state)
    {
        _pauseResumeItem.Text = state == VideoRecordingState.Paused ? UiLabels.ResumeRecording : UiLabels.PauseRecording;
        _pauseResumeItem.Enabled = state is VideoRecordingState.Recording or VideoRecordingState.Paused;
        _stopRecordingItem.Enabled = state is VideoRecordingState.Countdown or VideoRecordingState.Preparing or VideoRecordingState.Recording or VideoRecordingState.Paused;
    }

    public void Dispose() => Strip.Dispose();

    private ToolStripMenuItem CreateCaptureMenu(string label, RecorderAction full, RecorderAction region, RecorderAction window, Action<RecorderAction> performAction)
    {
        var item = new ToolStripMenuItem(label);
        foreach (var action in new[] { full, region, window }) _captureMenuParents[action] = item;
        item.DropDownItems.Add(CreateActionItem(UiLabels.FullDisplay, full, performAction));
        item.DropDownItems.Add(CreateActionItem(UiLabels.SelectRegion, region, performAction));
        item.DropDownItems.Add(CreateActionItem(UiLabels.SelectWindow, window, performAction));
        return item;
    }

    private ToolStripMenuItem CreateActionItem(string label, RecorderAction action, Action<RecorderAction> performAction)
    {
        var item = new ToolStripMenuItem(label, null, (_, _) => performAction(action));
        _shortcutMenuItems[action] = item;
        return item;
    }
}
