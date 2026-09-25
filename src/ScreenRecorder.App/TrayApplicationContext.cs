using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Media;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int StartupNotificationDelayMilliseconds = 600;
    private readonly DailyLog _log;
    private readonly SettingsRepository _settingsRepository;
    private readonly AutoStartSynchronizer _autoStartSynchronizer;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly HotkeyManager _hotkeyManager;
    private readonly ScreenshotCaptureService _screenshotCaptureService;
    private readonly Icon _trayIcon;
    private readonly Dictionary<RecorderAction, ToolStripMenuItem> _shortcutMenuItems = [];
    private Settings _settings;
    private SettingsForm? _settingsForm;
    private bool _isRecording;
    private bool _screenshotCaptureInProgress;
    private string? _pendingScreenshotPath;
    private System.Windows.Forms.Timer? _startupNotificationTimer;

    public TrayApplicationContext(Settings settings, DailyLog log, SettingsRepository settingsRepository, AutoStartSynchronizer autoStartSynchronizer)
    {
        _settings = settings.Clone();
        _log = log;
        _settingsRepository = settingsRepository;
        _autoStartSynchronizer = autoStartSynchronizer;
        _screenshotCaptureService = new ScreenshotCaptureService(log);
        _menu = CreateMenu();
        _trayIcon = CreateIcon(false);
        _tray = new NotifyIcon { Text = UiLabels.AppName, Icon = _trayIcon, ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => OpenPendingScreenshotLocation();
        _hotkeyManager = new HotkeyManager(PerformAction);
        var failures = _hotkeyManager.Replace(_settings);
        UpdateShortcutMenuLabels();
        ReportHotkeyFailures(failures, startup: true);
    }

    protected override void ExitThreadCore()
    {
        _settingsForm?.Close();
        _startupNotificationTimer?.Stop();
        _startupNotificationTimer?.Dispose();
        _hotkeyManager.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }

    internal void SetRecordingState(bool isRecording)
    {
        _isRecording = isRecording;
        _settingsForm?.RefreshRecordingState();
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(CreateCaptureMenu(UiLabels.Screenshot,
            RecorderAction.ScreenshotFullScreen, RecorderAction.ScreenshotRegion, RecorderAction.ScreenshotWindow));
        menu.Items.Add(CreateCaptureMenu(UiLabels.Record,
            RecorderAction.RecordingFullScreen, RecorderAction.RecordingRegion, RecorderAction.RecordingWindow));
        var pauseResume = CreateActionItem(UiLabels.PauseResume, RecorderAction.PauseResume);
        pauseResume.Enabled = false;
        menu.Items.Add(pauseResume);
        var stopRecording = CreateActionItem(UiLabels.StopRecording, RecorderAction.StopRecording);
        stopRecording.Enabled = false;
        menu.Items.Add(stopRecording);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiLabels.OpenImageFolder, null, (_, _) => OpenFolder(_settings.StillImageDirectory)));
        menu.Items.Add(new ToolStripMenuItem(UiLabels.OpenVideoFolder, null, (_, _) => OpenFolder(_settings.VideoDirectory)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiLabels.Settings, null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem(UiLabels.Manual, null, (_, _) => NotifyNotImplemented()));
        menu.Items.Add(new ToolStripMenuItem(UiLabels.CheckForUpdates, null, (_, _) => NotifyNotImplemented()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiLabels.Exit, null, (_, _) => ExitThread()));
        return menu;
    }

    private ToolStripMenuItem CreateCaptureMenu(string label, RecorderAction full, RecorderAction region, RecorderAction window)
    {
        var item = new ToolStripMenuItem(label);
        item.DropDownItems.Add(CreateActionItem(UiLabels.FullDisplay, full));
        item.DropDownItems.Add(CreateActionItem(UiLabels.SelectRegion, region));
        item.DropDownItems.Add(CreateActionItem(UiLabels.SelectWindow, window));
        return item;
    }

    private ToolStripMenuItem CreateActionItem(string label, RecorderAction action)
    {
        var item = new ToolStripMenuItem(label, null, (_, _) => PerformAction(action));
        _shortcutMenuItems[action] = item;
        return item;
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false } existing)
        {
            if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
            existing.BringToFront();
            existing.Activate();
            return;
        }

        var form = new SettingsForm(_settings, _hotkeyManager.Failures, _log, SaveAndApplySettings, () => _isRecording);
        _settingsForm = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_settingsForm, form)) _settingsForm = null;
        };
        form.Show();
        form.BringToFront();
        form.Activate();
    }

    private bool SaveAndApplySettings(Settings settings)
    {
        _settingsRepository.Save(settings);
        _settings = settings.Clone();
        try
        {
            _autoStartSynchronizer.Apply(_settings.StartWithWindows);
        }
        catch (Exception exception)
        {
            _log.Write($"Auto-start update failed: {exception}");
            _tray.ShowBalloonTip(3000, UiLabels.AppName, UiLabels.SettingsApplyFailed, ToolTipIcon.Error);
        }

        var failures = _hotkeyManager.Replace(_settings);
        UpdateShortcutMenuLabels();
        ReportHotkeyFailures(failures, startup: false);
        return true;
    }

    private void UpdateShortcutMenuLabels()
    {
        foreach (var assignment in ShortcutSettingsValidator.GetAssignments(_settings))
        {
            if (!_shortcutMenuItems.TryGetValue(assignment.Action, out var item)) continue;
            item.ShortcutKeyDisplayString = HotkeyShortcut.TryParse(assignment.Notation, out var shortcut) && shortcut is not null
                ? shortcut.ToDisplayString()
                : string.Empty;
        }
    }

    private void ReportHotkeyFailures(IReadOnlyList<HotkeyFailure> failures, bool startup)
    {
        foreach (var failure in failures)
            _log.Write($"Hotkey registration failed: action={failure.Action}, shortcut={failure.Notation}, reason={failure.Reason}, error={failure.ErrorCode}");
        if (failures.Count == 0) return;
        if (startup)
        {
            var timer = new System.Windows.Forms.Timer { Interval = StartupNotificationDelayMilliseconds };
            _startupNotificationTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                _startupNotificationTimer = null;
                ShowHotkeyFailureTip(failures, startup);
            };
            timer.Start();
            return;
        }
        ShowHotkeyFailureTip(failures, startup);
    }

    private void ShowHotkeyFailureTip(IReadOnlyList<HotkeyFailure> failures, bool startup)
    {
        var printScreenFailure = failures.FirstOrDefault(failure => failure.PrintScreenSettingsEnabled);
        var body = printScreenFailure is null
            ? startup ? UiLabels.StartupHotkeyFailureBody : UiLabels.SettingsApplyHotkeyFailed
            : UiLabels.PrintScreenSnippingHint;
        _tray.ShowBalloonTip(3500, UiLabels.StartupHotkeyFailureTitle, body, ToolTipIcon.Warning);
    }

    private async void PerformAction(RecorderAction action)
    {
        _log.Write($"Action requested: {action}");
        var mode = action switch
        {
            RecorderAction.ScreenshotFullScreen => ScreenshotMode.Full,
            RecorderAction.ScreenshotRegion => ScreenshotMode.Region,
            RecorderAction.ScreenshotWindow => ScreenshotMode.Window,
            _ => (ScreenshotMode?)null
        };
        if (mode is { } screenshotMode)
        {
            if (_screenshotCaptureInProgress) return;
            _screenshotCaptureInProgress = true;
            _pendingScreenshotPath = null;
            var captureSettings = _settings.Clone();
            try
            {
                using var result = await _screenshotCaptureService.CaptureAsync(screenshotMode, captureSettings);
                if (result is null) return;
                CompleteScreenshot(result, screenshotMode, captureSettings);
            }
            catch (Exception exception)
            {
                _log.Write($"Screenshot failed: mode={screenshotMode}; {exception}");
                var reason = exception.Message.Length > 180 ? exception.Message[..180] : exception.Message;
                _tray.ShowBalloonTip(4000, UiLabels.AppName, string.Format(UiLabels.ScreenshotCaptureFailed, reason), ToolTipIcon.Error);
            }
            finally
            {
                _screenshotCaptureInProgress = false;
            }
            return;
        }
        _tray.ShowBalloonTip(2500, UiLabels.AppName, UiLabels.NotImplemented, ToolTipIcon.Info);
    }

    private void CompleteScreenshot(ScreenshotCaptureResult result, ScreenshotMode mode, Settings settings)
    {
        string? warning = null;
        if (settings.CopyImageToClipboard)
        {
            try { Clipboard.SetImage(result.Image); }
            catch (Exception exception)
            {
                _log.Write($"Screenshot clipboard copy failed: {exception}");
                warning = UiLabels.ScreenshotClipboardFailed;
            }
        }

        if (settings.PlayCaptureSound)
        {
            try { SystemSounds.Asterisk.Play(); }
            catch (Exception exception) { _log.Write($"Screenshot sound failed: {exception}"); }
        }
        try
        {
            switch (settings.AfterCaptureAction)
            {
                case CaptureAfterAction.OpenFile:
                    using (Process.Start(new ProcessStartInfo(result.FilePath) { UseShellExecute = true })) { }
                    break;
                case CaptureAfterAction.OpenFolder:
                    if (!OpenFolder(Path.GetDirectoryName(result.FilePath) ?? settings.StillImageDirectory, notifyFailure: false))
                        warning = UiLabels.ScreenshotAfterActionFailed;
                    break;
            }
        }
        catch (Exception exception)
        {
            _log.Write($"Screenshot after-action failed: mode={mode}, action={settings.AfterCaptureAction}, file={result.FilePath}; {exception}");
            warning = UiLabels.ScreenshotAfterActionFailed;
        }

        if (settings.NotifyWhenSaved)
        {
            _pendingScreenshotPath = result.FilePath;
        }
        if (warning is not null) _tray.ShowBalloonTip(4000, UiLabels.AppName, warning, ToolTipIcon.Warning);
        else if (settings.NotifyWhenSaved) _tray.ShowBalloonTip(4000, UiLabels.AppName, UiLabels.ScreenshotSavedNotification, ToolTipIcon.Info);
    }

    private void OpenPendingScreenshotLocation()
    {
        var path = _pendingScreenshotPath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true };
            using (Process.Start(start)) { }
        }
        catch (Exception exception)
        {
            _log.Write($"Open screenshot location failed: file={path}; {exception}");
            _tray.ShowBalloonTip(3000, UiLabels.AppName, string.Format(UiLabels.FolderOpenFailed, exception.Message), ToolTipIcon.Error);
        }
    }

    private void NotifyNotImplemented() => _tray.ShowBalloonTip(2500, UiLabels.AppName, UiLabels.NotImplemented, ToolTipIcon.Info);

    private bool OpenFolder(string path, bool notifyFailure = true)
    {
        try
        {
            Directory.CreateDirectory(path);
            var start = new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true };
            using (Process.Start(start)) { }
            return true;
        }
        catch (Exception exception)
        {
            _log.Write($"Open folder failed: {exception}");
            if (notifyFailure) _tray.ShowBalloonTip(3000, UiLabels.AppName, string.Format(UiLabels.FolderOpenFailed, exception.Message), ToolTipIcon.Error);
            return false;
        }
    }

    private static Icon CreateIcon(bool recording)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(recording ? Color.Firebrick : Color.FromArgb(35, 110, 190)))
        using (var border = new Pen(Color.White, 2))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.FillRoundedRectangle(brush, new Rectangle(2, 2, 28, 28), 6);
            graphics.DrawRoundedRectangle(border, new Rectangle(2, 2, 28, 28), 6);
            if (recording) graphics.FillEllipse(Brushes.White, 11, 11, 10, 10);
            else graphics.DrawRectangle(Pens.White, 9, 9, 14, 14);
        }
        var iconHandle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(iconHandle);
        }
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = RoundedRectangle(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = RoundedRectangle(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
