using Microsoft.Win32;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int StartupNotificationDelayMilliseconds = 600;
    private readonly SettingsRepository _settingsRepository;
    private readonly AutoStartSynchronizer _autoStartSynchronizer;
    private readonly NotifyIcon _tray;
    private readonly TrayMenu _menu;
    private readonly HotkeyManager _hotkeyManager;
    private readonly ScreenshotCaptureService _screenshotCaptureService;
    private readonly UiDispatcher _uiDispatcher;
    private readonly TrayIcons _trayIcons;
    private readonly CaptureNotifier _captureNotifier;
    private readonly VideoRecordingController _recording;
    private Settings _settings;
    private SettingsForm? _settingsForm;
    private SaveDirectoryDialog? _saveDirectoryDialog;
    private bool _screenshotCaptureInProgress;
    private bool _exitRequested;
    private System.Windows.Forms.Timer? _startupNotificationTimer;
    private System.Windows.Forms.Timer? _leftoverRecordingNotificationTimer;
    private readonly UpdateController _updateController;
    private System.Windows.Forms.Timer? _updateCompletedNotificationTimer;

    private bool IsCaptureOrRecordingSelectionInProgress => _screenshotCaptureInProgress || _recording.SelectionInProgress;


    public TrayApplicationContext(
        Settings settings,
        SettingsRepository settingsRepository,
        AutoStartSynchronizer autoStartSynchronizer,
        string executablePath,
        bool startedAfterUpdate)
    {
        _uiDispatcher = new UiDispatcher();
        _settings = settings.Clone();
        _settingsRepository = settingsRepository;
        _autoStartSynchronizer = autoStartSynchronizer;
        _screenshotCaptureService = new ScreenshotCaptureService();
        _menu = new TrayMenu(PerformAction, PerformMenuCommand);
        _trayIcons = new TrayIcons();
        _tray = new NotifyIcon { Text = UiLabels.AppName, Icon = _trayIcons.Idle, ContextMenuStrip = _menu.Strip, Visible = true };
        _captureNotifier = new CaptureNotifier(_tray, OpenNotifiedUpdate);
        _recording = new VideoRecordingController(
            () => _settings,
            () => _exitRequested,
            RefreshRecordingUi,
            () => _updateController?.RefreshBusyState(),
            TryExitAfterPendingWork,
            _captureNotifier,
            _uiDispatcher,
            path => OpenFolderAsync(path, notifyFailure: false),
            ConfirmSaveDirectoryAsync,
            RememberConfirmedDirectory);
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => _captureNotifier.HandleBalloonClicked();
        _hotkeyManager = new HotkeyManager(PerformHotkeyAction);
        var failures = _hotkeyManager.Replace(_settings);
        _menu.ApplyShortcutAssignments(_settings);
        ReportHotkeyFailures(failures, startup: true);
        ReportIncompleteRecordings();
        SystemEvents.PowerModeChanged += HandlePowerModeChanged;

        var installDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        _updateController = new UpdateController(
            installDirectory,
            () => _settings,
            SaveSkippedUpdateVersion,
            GetUpdateBlockedReason,
            _captureNotifier.ShowForUpdate,
            RequestExit);
        _ = UpdateCleanup.RunAsync(installDirectory);
        if (startedAfterUpdate) ScheduleUpdateCompletedNotification();
        _updateController.Start();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= HandlePowerModeChanged;
        _updateController.Dispose();
        _updateCompletedNotificationTimer?.Stop();
        _updateCompletedNotificationTimer?.Dispose();
        _settingsForm?.Close();
        _startupNotificationTimer?.Stop();
        _startupNotificationTimer?.Dispose();
        _leftoverRecordingNotificationTimer?.Stop();
        _leftoverRecordingNotificationTimer?.Dispose();
        _recording.Dispose();
        _hotkeyManager.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcons.Dispose();
        _menu.Dispose();
        _uiDispatcher.Dispose();
        base.ExitThreadCore();
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

        var form = new SettingsForm(_settings, _hotkeyManager.Failures, SaveAndApplySettings, () => _recording.State != VideoRecordingState.Idle);
        _settingsForm = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_settingsForm, form)) _settingsForm = null;
        };
        form.Show();
        form.BringToFront();
        form.Activate();
    }

    private bool SaveAndApplySettings(Settings settings, bool stillDirectoryChanged, bool videoDirectoryChanged, bool defaultsRestored)
    {
        // 設定画面は開いた時点の値を持つため、開いている間に選ばれたスキップの版で上書きさせない。
        settings.SkippedUpdateVersion = _settings.SkippedUpdateVersion;
        if (!stillDirectoryChanged && !defaultsRestored)
        {
            settings.StillImageDirectory = _settings.StillImageDirectory;
            settings.ConfirmedStillImageDirectory = _settings.ConfirmedStillImageDirectory;
        }
        else
        {
            settings.ConfirmedStillImageDirectory = stillDirectoryChanged
                && !(defaultsRestored && string.Equals(settings.StillImageDirectory, new Settings().StillImageDirectory, StringComparison.Ordinal))
                    ? settings.StillImageDirectory
                    : null;
        }
        if (!videoDirectoryChanged && !defaultsRestored)
        {
            settings.VideoDirectory = _settings.VideoDirectory;
            settings.ConfirmedVideoDirectory = _settings.ConfirmedVideoDirectory;
        }
        else
        {
            settings.ConfirmedVideoDirectory = videoDirectoryChanged
                && !(defaultsRestored && string.Equals(settings.VideoDirectory, new Settings().VideoDirectory, StringComparison.Ordinal))
                    ? settings.VideoDirectory
                    : null;
        }
        _settingsRepository.Save(settings);
        _settings = settings.Clone();
        DiagnosticLog.Info(DiagnosticLogTags.App, "設定を保存しました。");
        DiagnosticLog.SetLevel(_settings.DiagnosticLogLevel);
        try
        {
            _autoStartSynchronizer.Apply(_settings.StartWithWindows);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.App, $"自動起動の設定に失敗しました: {exception}");
            ShowNotification(NotificationDuration.Brief, UiLabels.AppName, UiLabels.SettingsApplyFailed, ToolTipIcon.Error);
        }

        var failures = _hotkeyManager.Replace(_settings);
        _menu.ApplyShortcutAssignments(_settings);
        ReportHotkeyFailures(failures, startup: false);
        return true;
    }

    private void PerformHotkeyAction(RecorderAction action)
    {
        var assignment = ShortcutSettingsValidator.GetAssignments(_settings).FirstOrDefault(item => item.Action == action);
        if (assignment is null || !assignment.Enabled) return;
        PerformAction(action);
    }

    private void PerformMenuCommand(TrayMenuCommand command)
    {
        switch (command)
        {
            case TrayMenuCommand.OpenImageFolder:
                _ = OpenFolderAsync(_settings.StillImageDirectory);
                break;
            case TrayMenuCommand.OpenVideoFolder:
                _ = OpenFolderAsync(_settings.VideoDirectory);
                break;
            case TrayMenuCommand.Settings:
                ShowSettings();
                break;
            case TrayMenuCommand.Manual:
                OpenManual();
                break;
            case TrayMenuCommand.CheckForUpdates:
                _updateController.CheckManually();
                break;
            case TrayMenuCommand.Exit:
                RequestExit();
                break;
        }
    }

    private void OpenNotifiedUpdate() => _updateController.OpenNotifiedUpdate();

    private void ReportHotkeyFailures(IReadOnlyList<HotkeyFailure> failures, bool startup)
    {
        foreach (var failure in failures)
            DiagnosticLog.Warn(DiagnosticLogTags.Hotkey, $"ショートカットを登録できませんでした: 操作={UiLabels.ShortcutActionName(failure.Action)}、キー={failure.Notation}、理由={HotkeyFailureReasonName(failure.Reason)}、エラーコード={failure.ErrorCode}");
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
        ShowNotification(NotificationDuration.Standard, UiLabels.StartupHotkeyFailureTitle, body, ToolTipIcon.Warning);
    }

    private async void PerformAction(RecorderAction action)
    {
        DiagnosticLog.Info(DiagnosticLogTags.App, $"操作を受け付けました: {UiLabels.ShortcutActionName(action)}。");
        var recordingMode = action switch
        {
            RecorderAction.RecordingFullScreen => ScreenshotMode.Full,
            RecorderAction.RecordingRegion => ScreenshotMode.Region,
            RecorderAction.RecordingWindow => ScreenshotMode.Window,
            _ => (ScreenshotMode?)null
        };
        if (recordingMode is { } requestedRecordingMode)
        {
            if (_recording.State == VideoRecordingState.Idle && IsCaptureOrRecordingSelectionInProgress) return;
            await _recording.HandleRecordActionAsync(requestedRecordingMode);
            return;
        }

        if (action == RecorderAction.StopRecording)
        {
            _recording.HandleStopAction();
            return;
        }
        if (action == RecorderAction.PauseResume)
        {
            _recording.TogglePauseResume();
            return;
        }

        var mode = action switch
        {
            RecorderAction.ScreenshotFullScreen => ScreenshotMode.Full,
            RecorderAction.ScreenshotRegion => ScreenshotMode.Region,
            RecorderAction.ScreenshotWindow => ScreenshotMode.Window,
            _ => (ScreenshotMode?)null
        };
        if (mode is { } screenshotMode)
        {
            if (_recording.State == VideoRecordingState.Saving || IsCaptureOrRecordingSelectionInProgress) return;
            _screenshotCaptureInProgress = true;
            _updateController.RefreshBusyState();
            _captureNotifier.ClearPendingCapture();
            var captureSettings = _settings.Clone();
            try
            {
                using var result = await _screenshotCaptureService.CaptureAsync(screenshotMode, captureSettings);
                if (result is null) return;
                if (!SaveDirectoryRules.IsConfirmed(captureSettings.ConfirmedStillImageDirectory, captureSettings.StillImageDirectory))
                {
                    var selectedDirectory = await ConfirmSaveDirectoryAsync(SaveDirectoryKind.StillImage, captureSettings.StillImageDirectory);
                    if (selectedDirectory is null)
                    {
                        var clipboardResult = await CopyScreenshotToClipboardAsync(result.Image, captureSettings.CopyImageToClipboard);
                        ShowNotification(
                            NotificationDuration.Standard,
                            UiLabels.AppName,
                            clipboardResult.Copied
                                ? UiLabels.ScreenshotNotSavedClipboardNotification
                                : clipboardResult.Warning is not null
                                    ? UiLabels.ScreenshotNotSavedClipboardFailedNotification
                                    : UiLabels.ScreenshotNotSavedNotification,
                            clipboardResult.Warning is not null ? ToolTipIcon.Warning : ToolTipIcon.Info);
                        return;
                    }
                    RememberConfirmedDirectory(SaveDirectoryKind.StillImage, selectedDirectory);
                    captureSettings.StillImageDirectory = selectedDirectory;
                    captureSettings.ConfirmedStillImageDirectory = selectedDirectory;
                }

                string savedPath;
                while (true)
                {
                    try
                    {
                        savedPath = await _screenshotCaptureService.SaveImageAsync(result, captureSettings);
                        break;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        DiagnosticLog.Error(DiagnosticLogTags.Capture, $"静止画を保存できませんでした: 方法={CaptureText.CaptureMethodName(screenshotMode)}; {exception}");
                        var selectedDirectory = await ConfirmSaveDirectoryAsync(SaveDirectoryKind.StillImage, captureSettings.StillImageDirectory, exception);
                        if (selectedDirectory is null)
                        {
                            await CopyScreenshotToClipboardAsync(result.Image, captureSettings.CopyImageToClipboard);
                            var reason = CaptureText.ErrorDetail(exception.Message);
                            ShowNotification(NotificationDuration.Standard, UiLabels.AppName, string.Format(UiLabels.ScreenshotCaptureFailed, reason), ToolTipIcon.Error);
                            return;
                        }
                        RememberConfirmedDirectory(SaveDirectoryKind.StillImage, selectedDirectory);
                        captureSettings.StillImageDirectory = selectedDirectory;
                        captureSettings.ConfirmedStillImageDirectory = selectedDirectory;
                    }
                }
                await CompleteScreenshotAsync(result, screenshotMode, captureSettings, savedPath);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error(DiagnosticLogTags.Capture, $"静止画の撮影に失敗しました: 方法={CaptureText.CaptureMethodName(screenshotMode)}; {exception}");
                var reason = CaptureText.ErrorDetail(exception.Message);
                ShowNotification(NotificationDuration.Standard, UiLabels.AppName, string.Format(UiLabels.ScreenshotCaptureFailed, reason), ToolTipIcon.Error);
            }
            finally
            {
                _screenshotCaptureInProgress = false;
                _updateController.RefreshBusyState();
                TryExitAfterPendingWork();
            }
            return;
        }
    }

    private void RequestExit()
    {
        if (_recording.State == VideoRecordingState.Countdown)
        {
            _recording.CancelRecordingCountdown();
            _exitRequested = true;
            TryExitAfterPendingWork();
            return;
        }
        if (_recording.CanStop)
        {
            _exitRequested = true;
            ShowNotification(NotificationDuration.Brief, UiLabels.AppName, UiLabels.RecordingExitWaiting, ToolTipIcon.Info);
            _recording.StopRecording();
            return;
        }
        if (_recording.State == VideoRecordingState.Saving)
        {
            _exitRequested = true;
            ShowNotification(NotificationDuration.Brief, UiLabels.AppName, UiLabels.RecordingExitWaiting, ToolTipIcon.Info);
            return;
        }
        if (_screenshotCaptureInProgress || _recording.SelectionInProgress)
        {
            _exitRequested = true;
            _saveDirectoryDialog?.CancelFromExit();
            if (_screenshotCaptureInProgress)
                ShowNotification(NotificationDuration.Brief, UiLabels.AppName, UiLabels.ScreenshotExitWaiting, ToolTipIcon.Info);
            return;
        }
        ExitThread();
    }



    private void ShowNotification(NotificationDuration duration, string title, string message, ToolTipIcon icon) =>
        _captureNotifier.Show(duration, title, message, icon);

    private void ShowCaptureNotification(NotificationDuration duration, string title, string message, ToolTipIcon icon, string? path) =>
        _captureNotifier.ShowForCapture(duration, title, message, icon, path);

    private string? GetUpdateBlockedReason()
    {
        if (_recording.SelectionInProgress || _recording.State != VideoRecordingState.Idle) return UiLabels.UpdateBlockedByRecording;
        return _screenshotCaptureInProgress ? UiLabels.UpdateBlockedByCapture : null;
    }

    private bool SaveSkippedUpdateVersion(string version)
    {
        var updated = _settings.Clone();
        updated.SkippedUpdateVersion = version;
        try
        {
            _settingsRepository.Save(updated);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新をスキップする設定を保存できませんでした: {exception}");
            return false;
        }
        _settings = updated;
        return true;
    }

    private void ScheduleUpdateCompletedNotification()
    {
        var timer = new System.Windows.Forms.Timer { Interval = StartupNotificationDelayMilliseconds * 3 };
        _updateCompletedNotificationTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _updateCompletedNotificationTimer = null;
            DiagnosticLog.Info(DiagnosticLogTags.Update, $"更新後の版を起動しました: 版={AppVersion.Current}。");
            ShowNotification(NotificationDuration.Standard, UiLabels.AppName, string.Format(UiLabels.UpdateCompleted, AppVersion.Current), ToolTipIcon.Info);
        };
        timer.Start();
    }



    private void RefreshRecordingUi()
    {
        var state = _recording.State;
        _menu.ApplyRecordingState(state);
        _tray.Icon = _trayIcons.For(state);
        _tray.Text = UiLabels.TrayRecordingStatus(state);
        _settingsForm?.RefreshRecordingState();
        _updateController?.RefreshBusyState();
    }

    private void HandlePowerModeChanged(object? sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode != PowerModes.Suspend) return;
        _recording.HandleSystemSuspend();
    }

    private void DispatchToUi(Action action) => _uiDispatcher.Post(action);

    private void ReportIncompleteRecordings()
    {
        var videoDirectory = _settings.VideoDirectory;
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(videoDirectory)) return (Count: 0, Folder: (string?)null, Error: (Exception?)null);
                var count = 0;
                string? firstFolder = null;
                foreach (var path in Directory.EnumerateFiles(videoDirectory, VideoRecordingFileNaming.TemporaryFileSearchPattern, SearchOption.AllDirectories))
                {
                    count++;
                    firstFolder ??= Path.GetDirectoryName(path) ?? videoDirectory;
                }
                return (Count: count, Folder: firstFolder, Error: (Exception?)null);
            }
            catch (Exception exception)
            {
                return (Count: 0, Folder: (string?)null, Error: exception);
            }
        }).ContinueWith(task => DispatchToUi(() =>
        {
            var result = task.GetAwaiter().GetResult();
            if (result.Error is { } exception)
            {
                DiagnosticLog.Warn(DiagnosticLogTags.Record, $"未完了の録画を検索できませんでした: フォルダー={videoDirectory}; {exception}");
                return;
            }
            if (result.Count == 0 || result.Folder is null) return;
            var message = string.Format(UiLabels.IncompleteRecordingsFound, result.Count, CaptureText.PathDetail(result.Folder));
            _leftoverRecordingNotificationTimer = new System.Windows.Forms.Timer { Interval = StartupNotificationDelayMilliseconds * 2 };
            _leftoverRecordingNotificationTimer.Tick += (_, _) =>
            {
                _leftoverRecordingNotificationTimer.Stop();
                _leftoverRecordingNotificationTimer.Dispose();
                _leftoverRecordingNotificationTimer = null;
                ShowNotification(NotificationDuration.Long, UiLabels.AppName, message, ToolTipIcon.Warning);
            };
            _leftoverRecordingNotificationTimer.Start();
        }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public Task RecordingEngineDisposal => _recording.EngineDisposal;

    private void TryExitAfterPendingWork()
    {
        if (_exitRequested && !_screenshotCaptureInProgress && !_recording.SelectionInProgress && _recording.State == VideoRecordingState.Idle)
            ExitThread();
    }




    private static string HotkeyFailureReasonName(HotkeyFailureReason reason) => reason switch
    {
        HotkeyFailureReason.InvalidNotation => "キーの指定が正しくありません",
        HotkeyFailureReason.Duplicate => "同じキーが重複しています",
        _ => "Windows へ登録できませんでした"
    };


    private async Task CompleteScreenshotAsync(ScreenshotCaptureResult result, ScreenshotMode mode, Settings settings, string filePath)
    {
        var clipboardResult = await CopyScreenshotToClipboardAsync(result.Image, settings.CopyImageToClipboard);

        await CaptureCompletion.ExecuteAsync(
            CaptureCompletionKind.Screenshot,
            settings,
            filePath,
            settings.StillImageDirectory,
            $"方法={CaptureText.CaptureMethodName(mode)}、動作={settings.AfterCaptureAction}、ファイル={filePath}",
            clipboardResult.Warning,
            path => OpenFolderAsync(path, notifyFailure: false),
            ShowCaptureNotification);
    }

    private async Task<(string? Warning, bool Copied)> CopyScreenshotToClipboardAsync(Bitmap image, bool enabled)
    {
        if (!enabled) return (null, false);
        try
        {
            await ClipboardImageWriter.SetImageAsync(image);
            return (null, true);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Capture, $"静止画をクリップボードへコピーできませんでした: {exception}");
            return (UiLabels.ScreenshotClipboardFailed, false);
        }
    }

    private Task<string?> ConfirmSaveDirectoryAsync(SaveDirectoryKind kind, string directory, Exception? failure = null)
    {
        using var dialog = new SaveDirectoryDialog(kind, directory, failure);
        _saveDirectoryDialog = dialog;
        try
        {
            if (_settingsForm is { IsDisposed: false, Visible: true } owner) dialog.ShowDialog(owner);
            else dialog.ShowDialog();
            return Task.FromResult(dialog.DialogResult == DialogResult.OK ? dialog.SelectedDirectory : null);
        }
        finally
        {
            if (ReferenceEquals(_saveDirectoryDialog, dialog)) _saveDirectoryDialog = null;
        }
    }

    private void RememberConfirmedDirectory(SaveDirectoryKind kind, string directory)
    {
        var updated = _settings.Clone();
        if (kind == SaveDirectoryKind.StillImage)
        {
            updated.StillImageDirectory = directory;
            updated.ConfirmedStillImageDirectory = directory;
        }
        else
        {
            updated.VideoDirectory = directory;
            updated.ConfirmedVideoDirectory = directory;
        }

        _settings = updated;
        try
        {
            _settingsRepository.Save(updated);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.App, $"確認した保存先を設定へ保存できませんでした: 種別={kind}; フォルダー={directory}; {exception}");
        }
    }

    private void OpenManual()
    {
        try { BundledDocument.Open(BundledDocument.ManualFileName); }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.App, $"マニュアルを開けませんでした: {exception}");
            ShowNotification(NotificationDuration.Brief, UiLabels.AppName, UiLabels.ManualOpenFailed, ToolTipIcon.Error);
        }
    }

    private async Task<bool> OpenFolderAsync(string path, bool notifyFailure = true)
    {
        try
        {
            await ShellLauncher.OpenFolderAsync(path);
            return true;
        }
        catch (Exception exception)
        {
            if (notifyFailure)
                DiagnosticLog.Error(DiagnosticLogTags.App, $"フォルダーを開けませんでした: フォルダー={path}; {exception}");
            else
                DiagnosticLog.Warn(DiagnosticLogTags.App, $"保存後にフォルダーを開けませんでした: フォルダー={path}; {exception}");
            if (notifyFailure)
            {
                try { ShowNotification(NotificationDuration.Brief, UiLabels.AppName, string.Format(UiLabels.FolderOpenFailed, CaptureText.ErrorDetail(exception.Message)), ToolTipIcon.Error); }
                catch (Exception notificationException) { DiagnosticLog.Warn(DiagnosticLogTags.App, $"フォルダーを開けなかったことを通知できませんでした: {notificationException}"); }
            }
            return false;
        }
    }

}
