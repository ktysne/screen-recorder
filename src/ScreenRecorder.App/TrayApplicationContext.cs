using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int StartupNotificationDelayMilliseconds = 600;
    private static readonly TimeSpan RecordingTerminationTimeout = TimeSpan.FromSeconds(30);
    private readonly SettingsRepository _settingsRepository;
    private readonly AutoStartSynchronizer _autoStartSynchronizer;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly HotkeyManager _hotkeyManager;
    private readonly ScreenshotCaptureService _screenshotCaptureService;
    private readonly Icon _idleTrayIcon;
    private readonly Icon _recordingTrayIcon;
    private readonly Icon _pausedTrayIcon;
    private readonly Icon _savingTrayIcon;
    private readonly Control _uiDispatcher;
    private readonly VideoRecordingStateMachine _recordingState = new();
    private readonly IVideoRecordingPostProcessor _videoPostProcessor;
    private readonly Dictionary<RecorderAction, ToolStripMenuItem> _shortcutMenuItems = [];
    private readonly Dictionary<RecorderAction, ToolStripMenuItem> _captureMenuParents = [];
    private Settings _settings;
    private SettingsForm? _settingsForm;
    private bool _screenshotCaptureInProgress;
    private bool _recordingSelectionInProgress;
    private bool _exitRequested;
    private bool _recordingFailureFinalizationStarted;
    private bool _systemSleepInhibited;
    private string? _pendingCapturePath;
    private System.Windows.Forms.Timer? _startupNotificationTimer;
    private System.Windows.Forms.Timer? _leftoverRecordingNotificationTimer;
    private System.Windows.Forms.Timer? _recordingTimer;
    private System.Windows.Forms.Timer? _windowMonitorTimer;
    private CancellationTokenSource? _recordingCountdownCancellation;
    private CaptureCountdownForm? _recordingCountdownForm;
    private RecordingToolbarForm? _recordingToolbar;
    private RecordingRegionFrameForm? _recordingRegionFrame;
    private IRecordingEngine? _recordingEngine;
    private ActiveRecording? _activeRecording;
    private Stopwatch? _recordingStopwatch;
    private ToolStripMenuItem? _pauseResumeItem;
    private ToolStripMenuItem? _stopRecordingItem;
    private readonly UpdateController _updateController;
    private bool _pendingUpdateNotification;
    private System.Windows.Forms.Timer? _updateCompletedNotificationTimer;

    private sealed record ActiveRecording(
        string FinalPath,
        string TemporaryPath,
        Settings Settings,
        DateTime CapturedAt,
        ScreenshotMode Mode,
        string? WindowTitle,
        RecordingSourceKind SourceKind,
        IntPtr WindowHandle,
        Rectangle TargetBounds,
        Rectangle DisplayBounds);

    public TrayApplicationContext(
        Settings settings,
        SettingsRepository settingsRepository,
        AutoStartSynchronizer autoStartSynchronizer,
        string executablePath,
        bool startedAfterUpdate)
    {
        _settings = settings.Clone();
        _videoPostProcessor = new FfmpegVideoRecordingPostProcessor();
        _settingsRepository = settingsRepository;
        _autoStartSynchronizer = autoStartSynchronizer;
        _uiDispatcher = new Control();
        _ = _uiDispatcher.Handle;
        _screenshotCaptureService = new ScreenshotCaptureService();
        _menu = CreateMenu();
        _idleTrayIcon = CreateIcon(TrayIconState.Idle);
        _recordingTrayIcon = CreateIcon(TrayIconState.Recording);
        _pausedTrayIcon = CreateIcon(TrayIconState.Paused);
        _savingTrayIcon = CreateIcon(TrayIconState.Saving);
        _tray = new NotifyIcon { Text = UiLabels.AppName, Icon = _idleTrayIcon, ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => HandleBalloonClicked();
        _hotkeyManager = new HotkeyManager(PerformHotkeyAction);
        var failures = _hotkeyManager.Replace(_settings);
        UpdateShortcutMenuLabels();
        ReportHotkeyFailures(failures, startup: true);
        ReportIncompleteRecordings();
        SystemEvents.PowerModeChanged += HandlePowerModeChanged;

        var installDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        _updateController = new UpdateController(
            installDirectory,
            () => _settings,
            SaveSkippedUpdateVersion,
            GetUpdateBlockedReason,
            ShowUpdateNotification,
            RequestExit);
        _ = UpdateCleanup.RunAsync(installDirectory);
        if (startedAfterUpdate) ScheduleUpdateCompletedNotification();
        _updateController.Start();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= HandlePowerModeChanged;
        SetSystemSleepInhibition(false);
        _updateController.Dispose();
        _updateCompletedNotificationTimer?.Stop();
        _updateCompletedNotificationTimer?.Dispose();
        _settingsForm?.Close();
        _startupNotificationTimer?.Stop();
        _startupNotificationTimer?.Dispose();
        _leftoverRecordingNotificationTimer?.Stop();
        _leftoverRecordingNotificationTimer?.Dispose();
        _recordingTimer?.Stop();
        _recordingTimer?.Dispose();
        _windowMonitorTimer?.Stop();
        _windowMonitorTimer?.Dispose();
        _recordingCountdownCancellation?.Cancel();
        _recordingCountdownForm?.Close();
        _recordingToolbar?.Close();
        _recordingRegionFrame?.Close();
        _recordingEngine?.Dispose();
        _hotkeyManager.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _idleTrayIcon.Dispose();
        _recordingTrayIcon.Dispose();
        _pausedTrayIcon.Dispose();
        _savingTrayIcon.Dispose();
        _uiDispatcher.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip { ShowItemToolTips = true };
        menu.Items.Add(CreateCaptureMenu(UiLabels.Screenshot,
            RecorderAction.ScreenshotFullScreen, RecorderAction.ScreenshotRegion, RecorderAction.ScreenshotWindow));
        menu.Items.Add(CreateCaptureMenu(UiLabels.Record,
            RecorderAction.RecordingFullScreen, RecorderAction.RecordingRegion, RecorderAction.RecordingWindow));
        _pauseResumeItem = CreateActionItem(UiLabels.PauseResume, RecorderAction.PauseResume);
        _pauseResumeItem.Enabled = false;
        menu.Items.Add(_pauseResumeItem);
        _stopRecordingItem = CreateActionItem(UiLabels.StopRecording, RecorderAction.StopRecording);
        _stopRecordingItem.Enabled = false;
        menu.Items.Add(_stopRecordingItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiLabels.OpenImageFolder, null, (_, _) => OpenFolder(_settings.StillImageDirectory)));
        menu.Items.Add(new ToolStripMenuItem(UiLabels.OpenVideoFolder, null, (_, _) => OpenFolder(_settings.VideoDirectory)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiLabels.Settings, null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem(UiLabels.Manual, null, (_, _) => OpenManual()));
        menu.Items.Add(new ToolStripMenuItem(UiLabels.CheckForUpdates, null, (_, _) => _updateController.CheckManually()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiLabels.Exit, null, (_, _) => RequestExit()));
        return menu;
    }

    private ToolStripMenuItem CreateCaptureMenu(string label, RecorderAction full, RecorderAction region, RecorderAction window)
    {
        var item = new ToolStripMenuItem(label);
        foreach (var action in new[] { full, region, window }) _captureMenuParents[action] = item;
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

        var form = new SettingsForm(_settings, _hotkeyManager.Failures, SaveAndApplySettings, () => _recordingState.State != VideoRecordingState.Idle);
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
        // 設定画面は開いた時点の値を持つため、開いている間に選ばれたスキップの版で上書きさせない。
        settings.SkippedUpdateVersion = _settings.SkippedUpdateVersion;
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
            ShowNotification(3000, UiLabels.AppName, UiLabels.SettingsApplyFailed, ToolTipIcon.Error);
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
            if (assignment.Action != RecorderAction.PauseResume)
            {
                item.Enabled = assignment.Enabled;
                item.ToolTipText = assignment.Enabled ? string.Empty : UiLabels.ShortcutDisabledToolTip;
            }
            item.ShortcutKeyDisplayString = HotkeyShortcut.TryParse(assignment.Notation, out var shortcut) && shortcut is not null
                ? shortcut.ToDisplayString()
                : string.Empty;
        }
        foreach (var parent in _captureMenuParents.Values.Distinct())
            parent.Enabled = parent.DropDownItems.Cast<ToolStripMenuItem>().Any(item => item.Enabled);
    }

    private void PerformHotkeyAction(RecorderAction action)
    {
        var assignment = ShortcutSettingsValidator.GetAssignments(_settings).FirstOrDefault(item => item.Action == action);
        if (assignment is null || !assignment.Enabled) return;
        PerformAction(action);
    }

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
        ShowNotification(3500, UiLabels.StartupHotkeyFailureTitle, body, ToolTipIcon.Warning);
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
            if (_recordingState.State == VideoRecordingState.Countdown)
            {
                CancelRecordingCountdown();
                return;
            }
            if (_recordingState.State is VideoRecordingState.Recording or VideoRecordingState.Paused)
            {
                StopRecording();
                return;
            }
            if (_recordingState.State != VideoRecordingState.Idle || _recordingSelectionInProgress || _exitRequested) return;
            await StartRecordingAsync(requestedRecordingMode);
            return;
        }

        if (action == RecorderAction.StopRecording)
        {
            if (_recordingState.State == VideoRecordingState.Countdown) CancelRecordingCountdown();
            else if (_recordingState.State is VideoRecordingState.Recording or VideoRecordingState.Paused) StopRecording();
            return;
        }
        if (action == RecorderAction.PauseResume)
        {
            TogglePauseResume();
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
            if (_recordingState.State == VideoRecordingState.Saving) return;
            if (_screenshotCaptureInProgress) return;
            _screenshotCaptureInProgress = true;
            _updateController.RefreshBusyState();
            _pendingCapturePath = null;
            var captureSettings = _settings.Clone();
            try
            {
                using var result = await _screenshotCaptureService.CaptureAsync(screenshotMode, captureSettings);
                if (result is null) return;
                CompleteScreenshot(result, screenshotMode, captureSettings);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error(DiagnosticLogTags.Capture, $"静止画の撮影に失敗しました: 方法={CaptureMethodName(screenshotMode)}; {exception}");
                var reason = exception.Message.Length > 180 ? exception.Message[..180] : exception.Message;
                ShowNotification(4000, UiLabels.AppName, string.Format(UiLabels.ScreenshotCaptureFailed, reason), ToolTipIcon.Error);
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
        if (_recordingState.State == VideoRecordingState.Countdown)
        {
            CancelRecordingCountdown();
            _exitRequested = true;
            TryExitAfterPendingWork();
            return;
        }
        if (_recordingState.State is VideoRecordingState.Recording or VideoRecordingState.Paused)
        {
            _exitRequested = true;
            ShowNotification(3000, UiLabels.AppName, UiLabels.RecordingExitWaiting, ToolTipIcon.Info);
            StopRecording();
            return;
        }
        if (_recordingState.State == VideoRecordingState.Saving)
        {
            _exitRequested = true;
            ShowNotification(3000, UiLabels.AppName, UiLabels.RecordingExitWaiting, ToolTipIcon.Info);
            return;
        }
        if (_screenshotCaptureInProgress || _recordingSelectionInProgress)
        {
            _exitRequested = true;
            if (_screenshotCaptureInProgress)
                ShowNotification(3000, UiLabels.AppName, UiLabels.ScreenshotExitWaiting, ToolTipIcon.Info);
            return;
        }
        ExitThread();
    }

    private async Task StartRecordingAsync(ScreenshotMode mode)
    {
        _recordingSelectionInProgress = true;
        _updateController.RefreshBusyState();
        var captureSettings = _settings.Clone();
        var engineStarted = false;
        try
        {
            if (!CheckRecordingSpace(captureSettings.VideoDirectory)) return;

            var fullDisplay = mode == ScreenshotMode.Full ? Screen.FromPoint(Cursor.Position) : null;
            using var selection = mode == ScreenshotMode.Full
                ? null
                : await CaptureSelection.SelectAsync(mode, freezeDesktop: false);
            if (mode != ScreenshotMode.Full && selection is null) return;
            if (_exitRequested) return;

            var targetBounds = mode == ScreenshotMode.Full ? fullDisplay!.Bounds : selection!.Bounds;
            var display = mode == ScreenshotMode.Full ? fullDisplay! : Screen.FromRectangle(targetBounds);
            var dimensions = VideoRecordingStateMachine.CalculateDimensions(
                targetBounds.Width,
                targetBounds.Height,
                captureSettings.OutputScalePercent);
            if (dimensions is null)
            {
                var message = targetBounds.Width < 2 || targetBounds.Height < 2
                    ? UiLabels.RecordingRegionTooSmall
                    : UiLabels.RecordingOutputTooSmall;
                ShowNotification(4000, UiLabels.AppName, message, ToolTipIcon.Warning);
                return;
            }

            var sourceKind = mode switch
            {
                ScreenshotMode.Full => RecordingSourceKind.Display,
                ScreenshotMode.Region => RecordingSourceKind.Region,
                ScreenshotMode.Window => RecordingSourceKind.Window,
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            var sourceRect = mode switch
            {
                ScreenshotMode.Full => new Rectangle(0, 0, dimensions.Value.SourceSize.Width, dimensions.Value.SourceSize.Height),
                ScreenshotMode.Region => new Rectangle(
                    targetBounds.X - display.Bounds.X,
                    targetBounds.Y - display.Bounds.Y,
                    dimensions.Value.SourceSize.Width,
                    dimensions.Value.SourceSize.Height),
                _ => (Rectangle?)null
            };
            var capturedAt = DateTime.Now;
            var finalPath = VideoRecordingFileNaming.GetAvailablePath(
                captureSettings.VideoDirectory,
                captureSettings.OrganizeByMonth,
                capturedAt,
                mode,
                selection?.WindowTitle,
                captureSettings.FileNameTemplate,
                candidate => File.Exists(candidate) || File.Exists(VideoRecordingFileNaming.GetTemporaryPath(candidate)));
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (!CheckRecordingSpace(captureSettings.VideoDirectory)) return;
            if (!_recordingState.TryBeginCountdown()) return;

            var temporaryPath = VideoRecordingFileNaming.GetTemporaryPath(finalPath);
            _recordingFailureFinalizationStarted = false;
            SetSystemSleepInhibition(true);
            _activeRecording = new ActiveRecording(
                finalPath,
                temporaryPath,
                captureSettings,
                capturedAt,
                mode,
                selection?.WindowTitle,
                sourceKind,
                selection?.Window ?? IntPtr.Zero,
                targetBounds,
                display.Bounds);
            UpdateRecordingUi();

            var countdownCancellation = new CancellationTokenSource();
            _recordingCountdownCancellation = countdownCancellation;
            try
            {
                if (captureSettings.CountdownSeconds > 0)
                    await WaitForRecordingCountdownAsync(captureSettings.CountdownSeconds, display.Bounds, countdownCancellation.Token);
                countdownCancellation.Token.ThrowIfCancellationRequested();
                if (_recordingState.State != VideoRecordingState.Countdown || _exitRequested) return;
                if (!CheckRecordingSpace(captureSettings.VideoDirectory))
                {
                    _recordingState.TryCancelCountdown();
                    UpdateRecordingUi();
                    return;
                }
            }
            finally
            {
                if (ReferenceEquals(_recordingCountdownCancellation, countdownCancellation)) _recordingCountdownCancellation = null;
                countdownCancellation.Dispose();
            }

            var request = new RecordingStartRequest(
                temporaryPath,
                sourceKind,
                display.DeviceName,
                sourceRect,
                selection?.Window ?? IntPtr.Zero,
                dimensions.Value.SourceSize,
                dimensions.Value.OutputSize,
                captureSettings.FrameRate,
                captureSettings.VideoBitrateMbps,
                captureSettings.CaptureVideoCursor,
                captureSettings.HighlightClicks,
                captureSettings.Encoder == EncoderMode.Automatic,
                !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
                captureSettings.CaptureSystemAudio,
                captureSettings.CaptureMicrophone,
                captureSettings.MicrophoneDeviceId,
                captureSettings.AudioFormat == AudioFormat.Mp3 ? 192 : captureSettings.AacBitrateKbps);
            var engine = new ScreenRecorderLibRecordingEngine();
            engine.StatusChanged += (_, eventArgs) => DispatchToUi(() => HandleRecordingStatus(engine, eventArgs));
            engine.RecordingCompleted += (_, eventArgs) => DispatchToUi(() => HandleRecordingCompleted(engine, eventArgs));
            engine.RecordingFailed += (_, eventArgs) => DispatchToUi(() => HandleRecordingFailed(engine, eventArgs));
            engine.RecordingWarning += (_, eventArgs) => DispatchToUi(() =>
            {
                ShowNotification(5000, UiLabels.AppName, eventArgs.Message, ToolTipIcon.Warning);
            });
            _recordingEngine = engine;
            engine.Start(request);
            engineStarted = true;
            if (!_recordingState.TryStartRecording())
            {
                engine.Stop();
                return;
            }

            var audioFormat = !captureSettings.CaptureSystemAudio && !captureSettings.CaptureMicrophone
                ? "なし"
                : $"{(captureSettings.AudioFormat == AudioFormat.Mp3 ? "MP3" : "AAC")} ({(captureSettings.AudioFormat == AudioFormat.Mp3 ? captureSettings.Mp3BitrateKbps : captureSettings.AacBitrateKbps)} kbps)";
            DiagnosticLog.Info(DiagnosticLogTags.Record,
                $"録画を開始しました: 方法={CaptureMethodName(mode)}、範囲=({targetBounds.X},{targetBounds.Y}) {targetBounds.Width}x{targetBounds.Height}、フレームレート={captureSettings.FrameRate} fps、ビットレート={captureSettings.VideoBitrateMbps} Mbps、音声形式={audioFormat}。");
            _recordingStopwatch = Stopwatch.StartNew();
            ShowRecordingOverlays();
            StartRecordingTimers(_activeRecording);
            UpdateRecordingUi();
        }
        catch (OperationCanceledException) when (_recordingState.State != VideoRecordingState.Recording)
        {
            if (_recordingState.State == VideoRecordingState.Countdown) _recordingState.TryCancelCountdown();
            UpdateRecordingUi();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画を開始できませんでした: 方法={CaptureMethodName(mode)}; {exception}");
            if (engineStarted)
            {
                StopRecording();
            }
            else
            {
                _recordingState.TryFail();
                CleanupRecordingSession();
                UpdateRecordingUi();
                ShowNotification(4000, UiLabels.AppName, string.Format(UiLabels.RecordingStartFailed, ShortError(exception.Message)), ToolTipIcon.Error);
            }
        }
        finally
        {
            _recordingSelectionInProgress = false;
            _updateController.RefreshBusyState();
            if (_recordingState.State == VideoRecordingState.Idle && _recordingEngine is null)
                CleanupRecordingSession();
            TryExitAfterPendingWork();
        }
    }

    private async Task WaitForRecordingCountdownAsync(int seconds, Rectangle displayBounds, CancellationToken cancellationToken)
    {
        using var countdown = new CaptureCountdownForm(displayBounds, forRecording: true);
        _recordingCountdownForm = countdown;
        countdown.Show();
        if (!countdown.ExcludeFromCapture()) DiagnosticLog.Warn(DiagnosticLogTags.Record, "録画カウントダウンを撮影対象から除外できませんでした。");
        try
        {
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                countdown.SetRemainingSeconds(remaining);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        finally
        {
            countdown.Close();
            if (ReferenceEquals(_recordingCountdownForm, countdown)) _recordingCountdownForm = null;
        }
    }

    private void CancelRecordingCountdown()
    {
        if (!_recordingState.TryCancelCountdown()) return;
        _recordingCountdownCancellation?.Cancel();
        _recordingCountdownForm?.Close();
        SetSystemSleepInhibition(false);
        UpdateRecordingUi();
    }

    private void TogglePauseResume()
    {
        if (_recordingState.State is not (VideoRecordingState.Recording or VideoRecordingState.Paused)) return;
        var wasRecording = _recordingState.State == VideoRecordingState.Recording;
        var command = wasRecording ? _recordingState.RequestPause() : _recordingState.RequestResume();
        try
        {
            ExecuteRecordingCommand(command);
            DiagnosticLog.Info(DiagnosticLogTags.Record, wasRecording ? "録画を一時停止しました。" : "録画を再開しました。");
            if (wasRecording) _recordingStopwatch?.Stop();
            else _recordingStopwatch?.Start();
            UpdateRecordingUi();
        }
        catch (Exception exception)
        {
            if (wasRecording) _recordingState.RequestResume();
            else _recordingState.RequestPause();
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画の一時停止または再開に失敗しました: {exception}");
            ShowNotification(3500, UiLabels.AppName, string.Format(UiLabels.RecordingFailed, ShortError(exception.Message)), ToolTipIcon.Warning);
            UpdateRecordingUi();
        }
    }

    private void StopRecording()
    {
        if (_recordingState.State == VideoRecordingState.Countdown)
        {
            CancelRecordingCountdown();
            return;
        }
        var command = _recordingState.RequestStop();
        if (_recordingState.State != VideoRecordingState.Saving) return;
        ShowSavingState();
        try
        {
            ExecuteRecordingCommand(command);
            DiagnosticLog.Info(DiagnosticLogTags.Record, "録画の停止を開始しました。");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画を停止できませんでした: {exception}");
            if (_recordingEngine is { } engine)
                _ = FinishRecordingFailureAfterTerminationAsync(engine, exception.Message, _activeRecording?.TemporaryPath);
            else
                FinishRecordingFailure(exception.Message, _activeRecording?.TemporaryPath, finalizationConfirmed: false);
        }
    }

    private void ExecuteRecordingCommand(RecordingEngineCommand command)
    {
        switch (command)
        {
            case RecordingEngineCommand.Pause:
                _recordingEngine?.Pause();
                break;
            case RecordingEngineCommand.Resume:
                _recordingEngine?.Resume();
                break;
            case RecordingEngineCommand.Stop:
                _recordingEngine?.Stop();
                break;
        }
    }

    private void HandleRecordingStatus(IRecordingEngine engine, RecordingEngineStatusChangedEventArgs eventArgs)
    {
        if (!ReferenceEquals(engine, _recordingEngine)) return;
        if (eventArgs.Status == RecordingEngineStatus.Recording)
        {
            var command = _recordingState.OnEngineRecordingStarted();
            try { ExecuteRecordingCommand(command); }
            catch (Exception exception)
            {
                DiagnosticLog.Error(DiagnosticLogTags.Record, $"保留中の録画操作に失敗しました: {exception}");
                if (command == RecordingEngineCommand.Stop)
                    _ = FinishRecordingFailureAfterTerminationAsync(engine, exception.Message, _activeRecording?.TemporaryPath);
                else
                {
                    _recordingState.RequestResume();
                    StopRecording();
                }
            }
            UpdateRecordingUi();
            return;
        }
        if (eventArgs.Status == RecordingEngineStatus.Saving && _recordingState.State is VideoRecordingState.Recording or VideoRecordingState.Paused)
        {
            if (_recordingState.TryBeginSaving()) ShowSavingState();
        }
    }

    private async void HandleRecordingCompleted(IRecordingEngine engine, RecordingEngineCompletedEventArgs eventArgs)
    {
        if (!ReferenceEquals(engine, _recordingEngine) || _activeRecording is not { } active) return;
        if (_recordingState.State is VideoRecordingState.Recording or VideoRecordingState.Paused) _recordingState.TryBeginSaving();
        ShowSavingState();
        string? processedPath = null;
        try
        {
            var completedPath = string.IsNullOrWhiteSpace(eventArgs.FilePath) ? active.TemporaryPath : eventArgs.FilePath;
            var processResult = await _videoPostProcessor.ProcessAsync(completedPath, active.Settings, CancellationToken.None);
            processedPath = processResult.FilePath;
            if (!File.Exists(processedPath)) throw new FileNotFoundException("録画ライブラリが完了を通知しましたが、一時ファイルが見つかりません。", processedPath);
            var pathToMove = processedPath;
            var finalPath = await Task.Run(() => MoveRecordingToFinalPath(active, pathToMove));
            DiagnosticLog.Info(DiagnosticLogTags.Record, $"録画を保存しました: {finalPath}");
            if (processResult.SupersededPath is { } supersededPath) DeleteSupersededRecording(supersededPath);
            CompleteRecordingSave(finalPath, active.Settings);
            if (processResult.Warning is not null)
                ShowCaptureNotification(5000, UiLabels.AppName, processResult.Warning, ToolTipIcon.Warning, finalPath);
            _recordingState.TryCompleteSaving();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画を保存できませんでした: 一時ファイル={active.TemporaryPath}; {exception}");
            var retainedPath = processedPath is not null && File.Exists(processedPath) ? processedPath : _activeRecording?.TemporaryPath ?? active.TemporaryPath;
            ShowRecordingFailure(exception.Message, retainedPath, finalizationConfirmed: true);
            _recordingState.TryFail();
        }
        finally
        {
            CleanupRecordingSession();
            UpdateRecordingUi();
            TryExitAfterPendingWork();
        }
    }

    private void HandleRecordingFailed(IRecordingEngine engine, RecordingEngineFailedEventArgs eventArgs)
    {
        if (!ReferenceEquals(engine, _recordingEngine)) return;
        if (_recordingState.State is VideoRecordingState.Recording or VideoRecordingState.Paused) _recordingState.TryBeginSaving();
        ShowSavingState();
        var decision = RecordingTerminationRules.Decide(eventArgs.Outcome);
        if (decision is RecordingTerminationDecision.Wait or RecordingTerminationDecision.ContinueCompletedSave) return;
        FinishRecordingFailure(
            eventArgs.Error,
            eventArgs.FilePath,
            finalizationConfirmed: false);
    }

    private async Task FinishRecordingFailureAfterTerminationAsync(IRecordingEngine engine, string error, string? temporaryPath)
    {
        var termination = await engine.WaitForTerminationAsync(RecordingTerminationTimeout);
        var decision = RecordingTerminationRules.Decide(termination);
        if (!ReferenceEquals(engine, _recordingEngine) || decision is RecordingTerminationDecision.Wait or RecordingTerminationDecision.ContinueCompletedSave)
            return;
        FinishRecordingFailure(error, temporaryPath, finalizationConfirmed: false);
    }

    private void FinishRecordingFailure(string error, string? temporaryPath, bool finalizationConfirmed)
    {
        if (_recordingFailureFinalizationStarted) return;
        _recordingFailureFinalizationStarted = true;
        var activePath = string.IsNullOrWhiteSpace(temporaryPath) ? _activeRecording?.TemporaryPath : temporaryPath;
        var retainedPath = !string.IsNullOrWhiteSpace(activePath) && File.Exists(activePath) ? activePath : null;
        DiagnosticLog.Error(DiagnosticLogTags.Record, retainedPath is null
            ? $"録画に失敗しました: {error}"
            : $"録画に失敗し、未完了のファイルを保持しました: {retainedPath}; {error}");
        ShowRecordingFailure(error, activePath, finalizationConfirmed);
        _recordingState.TryFail();
        CleanupRecordingSession();
        UpdateRecordingUi();
        TryExitAfterPendingWork();
    }

    private void ShowRecordingFailure(string error, string? temporaryPath, bool finalizationConfirmed)
    {
        var retainedPath = !string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath) ? temporaryPath : null;
        var message = retainedPath is null
            ? string.Format(UiLabels.RecordingFailed, ShortError(error))
            : string.Format(
                finalizationConfirmed ? UiLabels.RecordingTemporaryFileRetained : UiLabels.RecordingTemporaryFileIncomplete,
                ShortPath(retainedPath, 150));
        ShowCaptureNotification(5000, UiLabels.AppName, message, ToolTipIcon.Error, retainedPath);
    }

    private void ShowNotification(int timeout, string title, string message, ToolTipIcon icon)
    {
        _pendingCapturePath = null;
        _pendingUpdateNotification = false;
        _tray.ShowBalloonTip(timeout, title, message, icon);
    }

    private void ShowCaptureNotification(int timeout, string title, string message, ToolTipIcon icon, string? path)
    {
        _pendingCapturePath = path;
        _pendingUpdateNotification = false;
        _tray.ShowBalloonTip(timeout, title, message, icon);
    }

    private void ShowUpdateNotification(string message, ToolTipIcon icon, bool opensUpdateDialog)
    {
        ShowNotification(icon == ToolTipIcon.Info ? 4000 : 6000, UiLabels.AppName, message, icon);
        _pendingUpdateNotification = opensUpdateDialog;
    }

    private void HandleBalloonClicked()
    {
        if (_pendingUpdateNotification)
        {
            _pendingUpdateNotification = false;
            _updateController.OpenNotifiedUpdate();
            return;
        }
        OpenPendingCaptureLocation();
    }

    private string? GetUpdateBlockedReason()
    {
        if (_recordingSelectionInProgress || _recordingState.State != VideoRecordingState.Idle) return UiLabels.UpdateBlockedByRecording;
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
            ShowNotification(4000, UiLabels.AppName, string.Format(UiLabels.UpdateCompleted, AppVersion.Current), ToolTipIcon.Info);
        };
        timer.Start();
    }

    private void SetSystemSleepInhibition(bool inhibit)
    {
        if (_systemSleepInhibited == inhibit) return;
        var executionState = NativeMethods.ExecutionStateContinuous;
        if (inhibit) executionState |= NativeMethods.ExecutionStateSystemRequired;
        if (NativeMethods.SetThreadExecutionState(executionState) == 0)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Record, $"スリープを抑止できませんでした: 抑止={inhibit}; Windows エラー={Marshal.GetLastWin32Error()}");
            return;
        }
        _systemSleepInhibited = inhibit;
    }

    private void CompleteRecordingSave(string finalPath, Settings settings)
    {
        string? warning = null;
        if (settings.PlayCaptureSound)
        {
            try { SystemSounds.Asterisk.Play(); }
            catch (Exception exception) { DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画完了時の効果音を再生できませんでした: {exception}"); }
        }
        try
        {
            switch (settings.AfterCaptureAction)
            {
                case CaptureAfterAction.OpenFile:
                    using (Process.Start(new ProcessStartInfo(finalPath) { UseShellExecute = true })) { }
                    break;
                case CaptureAfterAction.OpenFolder:
                    if (!OpenFolder(Path.GetDirectoryName(finalPath) ?? settings.VideoDirectory, notifyFailure: false))
                        warning = UiLabels.RecordingAfterActionFailed;
                    break;
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画後の動作に失敗しました: 動作={settings.AfterCaptureAction}、ファイル={finalPath}; {exception}");
            warning = UiLabels.RecordingAfterActionFailed;
        }

        if (warning is not null) ShowCaptureNotification(4000, UiLabels.AppName, warning, ToolTipIcon.Warning, finalPath);
        else if (settings.NotifyWhenSaved) ShowCaptureNotification(4000, UiLabels.AppName, UiLabels.RecordingSavedNotification, ToolTipIcon.Info, finalPath);
    }

    private void DeleteSupersededRecording(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) { DiagnosticLog.Warn(DiagnosticLogTags.Convert, $"変換前の録画ファイルを削除できませんでした: {path}; {exception}"); }
    }

    private static string MoveRecordingToFinalPath(ActiveRecording active, string completedPath)
    {
        var finalPath = active.FinalPath;
        while (true)
        {
            try
            {
                File.Move(completedPath, finalPath);
                return finalPath;
            }
            catch (IOException exception) when (IsAlreadyExists(exception))
            {
                finalPath = VideoRecordingFileNaming.GetAvailablePath(
                    active.Settings.VideoDirectory,
                    active.Settings.OrganizeByMonth,
                    active.CapturedAt,
                    active.Mode,
                    active.WindowTitle,
                    active.Settings.FileNameTemplate,
                    candidate => File.Exists(candidate) || File.Exists(VideoRecordingFileNaming.GetTemporaryPath(candidate)));
            }
        }
    }

    private void ShowSavingState()
    {
        _recordingTimer?.Stop();
        _windowMonitorTimer?.Stop();
        _recordingStopwatch?.Stop();
        CloseRegionFrame();
        _recordingToolbar?.UpdateStatus(VideoRecordingState.Saving, _recordingStopwatch?.Elapsed ?? TimeSpan.Zero);
        UpdateRecordingUi();
    }

    private void ShowRecordingOverlays()
    {
        if (_activeRecording is not { } active) return;
        var toolbarSize = new Size(356, 48);
        var location = RecordingToolbarPlacement.FindLocation(active.TargetBounds, active.DisplayBounds, toolbarSize);
        try
        {
            var toolbar = new RecordingToolbarForm(location);
            toolbar.PauseResumeRequested += (_, _) => TogglePauseResume();
            toolbar.StopRequested += (_, _) => StopRecording();
            _recordingToolbar = toolbar;
            toolbar.Show();
            if (!toolbar.ExcludeFromCapture()) DiagnosticLog.Warn(DiagnosticLogTags.Record, "録画操作バーを撮影対象から除外できませんでした。");
            toolbar.UpdateStatus(VideoRecordingState.Recording, _recordingStopwatch?.Elapsed ?? TimeSpan.Zero);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画操作バーを表示できませんでした: {exception}");
        }

        if (active.SourceKind != RecordingSourceKind.Region) return;
        try
        {
            var frame = new RecordingRegionFrameForm(active.TargetBounds);
            _recordingRegionFrame = frame;
            frame.Show();
            if (!frame.ExcludeFromCapture()) DiagnosticLog.Warn(DiagnosticLogTags.Record, "範囲枠を撮影対象から除外できませんでした。");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画範囲の枠を表示できませんでした: {exception}");
        }
    }

    private void StartRecordingTimers(ActiveRecording? active)
    {
        _recordingTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _recordingTimer.Tick += (_, _) => _recordingToolbar?.UpdateStatus(_recordingState.State, _recordingStopwatch?.Elapsed ?? TimeSpan.Zero);
        _recordingTimer.Start();
        if (active is not { SourceKind: RecordingSourceKind.Window } windowRecording) return;
        _windowMonitorTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _windowMonitorTimer.Tick += (_, _) =>
        {
            if (_recordingState.State is not (VideoRecordingState.Recording or VideoRecordingState.Paused)) return;
            if (NativeMethods.IsWindow(windowRecording.WindowHandle)) return;
            DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画対象のウィンドウが閉じられました: hwnd={windowRecording.WindowHandle}");
            StopRecording();
        };
        _windowMonitorTimer.Start();
    }

    private void UpdateRecordingUi()
    {
        var state = _recordingState.State;
        if (_pauseResumeItem is not null)
        {
            _pauseResumeItem.Text = state == VideoRecordingState.Paused ? UiLabels.ResumeRecording : UiLabels.PauseRecording;
            _pauseResumeItem.Enabled = state is VideoRecordingState.Recording or VideoRecordingState.Paused;
        }
        if (_stopRecordingItem is not null)
            _stopRecordingItem.Enabled = state is VideoRecordingState.Countdown or VideoRecordingState.Recording or VideoRecordingState.Paused;

        _tray.Icon = state switch
        {
            VideoRecordingState.Recording => _recordingTrayIcon,
            VideoRecordingState.Paused => _pausedTrayIcon,
            VideoRecordingState.Saving => _savingTrayIcon,
            _ => _idleTrayIcon
        };
        _tray.Text = state switch
        {
            VideoRecordingState.Recording => "ScreenRecorder (録画中)",
            VideoRecordingState.Paused => "ScreenRecorder (一時停止中)",
            VideoRecordingState.Saving => "ScreenRecorder (保存中)",
            VideoRecordingState.Countdown => "ScreenRecorder (録画開始前)",
            _ => UiLabels.AppName
        };
        _recordingToolbar?.UpdateStatus(state, _recordingStopwatch?.Elapsed ?? TimeSpan.Zero);
        _settingsForm?.RefreshRecordingState();
        _updateController?.RefreshBusyState();
    }

    private void HandlePowerModeChanged(object? sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode != PowerModes.Suspend) return;
        if (_recordingEngine is { } engine)
        {
            try { engine.Stop(); }
            catch (Exception exception) { DiagnosticLog.Error(DiagnosticLogTags.Record, $"スリープに伴う録画の停止に失敗しました: {exception}"); }
        }
        DispatchToUi(() =>
        {
            if (_recordingState.State is VideoRecordingState.Recording or VideoRecordingState.Paused)
            {
                DiagnosticLog.Info(DiagnosticLogTags.Record, "スリープに入るため録画を停止します。");
                StopRecording();
            }
            else if (_recordingState.State == VideoRecordingState.Countdown && _recordingEngine is null)
            {
                CancelRecordingCountdown();
            }
        });
    }

    private void DispatchToUi(Action action)
    {
        if (_uiDispatcher.IsDisposed || !_uiDispatcher.IsHandleCreated) return;
        try { _uiDispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private bool CheckRecordingSpace(string videoDirectory)
    {
        try
        {
            var fullPath = Path.GetFullPath(videoDirectory);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root)) throw new IOException("保存先のドライブを特定できません。");
            var availableBytes = new DriveInfo(root).AvailableFreeSpace;
            if (VideoRecordingStateMachine.HasMinimumFreeSpace(availableBytes)) return true;
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画先の空き容量が不足しています: {videoDirectory}");
            ShowNotification(4000, UiLabels.AppName, UiLabels.RecordingSpaceInsufficient, ToolTipIcon.Warning);
            return false;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画先の空き容量を確認できませんでした: フォルダー={videoDirectory}; {exception}");
            ShowNotification(4000, UiLabels.AppName, string.Format(UiLabels.RecordingSpaceCheckFailed, ShortError(exception.Message)), ToolTipIcon.Error);
            return false;
        }
    }

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
                foreach (var path in Directory.EnumerateFiles(videoDirectory, "*.recording.mp4", SearchOption.AllDirectories))
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
            var message = string.Format(UiLabels.IncompleteRecordingsFound, result.Count, ShortPath(result.Folder, 190));
            _leftoverRecordingNotificationTimer = new System.Windows.Forms.Timer { Interval = StartupNotificationDelayMilliseconds * 2 };
            _leftoverRecordingNotificationTimer.Tick += (_, _) =>
            {
                _leftoverRecordingNotificationTimer.Stop();
                _leftoverRecordingNotificationTimer.Dispose();
                _leftoverRecordingNotificationTimer = null;
                ShowNotification(6000, UiLabels.AppName, message, ToolTipIcon.Warning);
            };
            _leftoverRecordingNotificationTimer.Start();
        }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void TryExitAfterPendingWork()
    {
        if (_exitRequested && !_screenshotCaptureInProgress && !_recordingSelectionInProgress && _recordingState.State == VideoRecordingState.Idle)
            ExitThread();
    }

    private void CleanupRecordingSession()
    {
        SetSystemSleepInhibition(false);
        _recordingTimer?.Stop();
        _recordingTimer?.Dispose();
        _recordingTimer = null;
        _windowMonitorTimer?.Stop();
        _windowMonitorTimer?.Dispose();
        _windowMonitorTimer = null;
        _recordingStopwatch?.Stop();
        _recordingStopwatch = null;
        CloseRegionFrame();
        _recordingToolbar?.Close();
        _recordingToolbar?.Dispose();
        _recordingToolbar = null;
        _recordingCountdownForm?.Close();
        _recordingCountdownForm = null;
        _recordingCountdownCancellation?.Dispose();
        _recordingCountdownCancellation = null;
        _recordingEngine?.Dispose();
        _recordingEngine = null;
        _activeRecording = null;
    }

    private void CloseRegionFrame()
    {
        _recordingRegionFrame?.Close();
        _recordingRegionFrame?.Dispose();
        _recordingRegionFrame = null;
    }

    private static string ShortError(string value) => value.Length > 180 ? value[..180] : value;

    private static string CaptureMethodName(ScreenshotMode mode) => mode switch
    {
        ScreenshotMode.Full => "ディスプレイ全体",
        ScreenshotMode.Region => "範囲指定",
        ScreenshotMode.Window => "ウィンドウ指定",
        _ => mode.ToString()
    };

    private static string HotkeyFailureReasonName(HotkeyFailureReason reason) => reason switch
    {
        HotkeyFailureReason.InvalidNotation => "キーの指定が正しくありません",
        HotkeyFailureReason.Duplicate => "同じキーが重複しています",
        _ => "Windows へ登録できませんでした"
    };

    private static string ShortPath(string value, int maximumLength) => value.Length > maximumLength
        ? $"…{value[^maximumLength..]}"
        : value;

    private static bool IsAlreadyExists(IOException exception) => (exception.HResult & 0xffff) is 80 or 183;

    private void CompleteScreenshot(ScreenshotCaptureResult result, ScreenshotMode mode, Settings settings)
    {
        string? warning = null;
        if (settings.CopyImageToClipboard)
        {
            try { Clipboard.SetImage(result.Image); }
            catch (Exception exception)
            {
                DiagnosticLog.Warn(DiagnosticLogTags.Capture, $"静止画をクリップボードへコピーできませんでした: {exception}");
                warning = UiLabels.ScreenshotClipboardFailed;
            }
        }

        if (settings.PlayCaptureSound)
        {
            try { SystemSounds.Asterisk.Play(); }
            catch (Exception exception) { DiagnosticLog.Warn(DiagnosticLogTags.Capture, $"撮影時の効果音を再生できませんでした: {exception}"); }
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
            DiagnosticLog.Warn(DiagnosticLogTags.Capture, $"撮影後の動作に失敗しました: 方法={CaptureMethodName(mode)}、動作={settings.AfterCaptureAction}、ファイル={result.FilePath}; {exception}");
            warning = UiLabels.ScreenshotAfterActionFailed;
        }

        if (warning is not null) ShowCaptureNotification(4000, UiLabels.AppName, warning, ToolTipIcon.Warning, result.FilePath);
        else if (settings.NotifyWhenSaved) ShowCaptureNotification(4000, UiLabels.AppName, UiLabels.ScreenshotSavedNotification, ToolTipIcon.Info, result.FilePath);
    }

    private void OpenPendingCaptureLocation()
    {
        var path = _pendingCapturePath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true };
            using (Process.Start(start)) { }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.App, $"撮影したファイルの場所を開けませんでした: ファイル={path}; {exception}");
            ShowNotification(3000, UiLabels.AppName, string.Format(UiLabels.FolderOpenFailed, exception.Message), ToolTipIcon.Error);
        }
    }


    private void OpenManual()
    {
        try { BundledDocument.Open(BundledDocument.ManualFileName); }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.App, $"マニュアルを開けませんでした: {exception}");
            ShowNotification(3000, UiLabels.AppName, UiLabels.ManualOpenFailed, ToolTipIcon.Error);
        }
    }

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
            if (notifyFailure)
                DiagnosticLog.Error(DiagnosticLogTags.App, $"フォルダーを開けませんでした: フォルダー={path}; {exception}");
            else
                DiagnosticLog.Warn(DiagnosticLogTags.App, $"保存後にフォルダーを開けませんでした: フォルダー={path}; {exception}");
            if (notifyFailure) ShowNotification(3000, UiLabels.AppName, string.Format(UiLabels.FolderOpenFailed, exception.Message), ToolTipIcon.Error);
            return false;
        }
    }

    private enum TrayIconState
    {
        Idle,
        Recording,
        Paused,
        Saving
    }

    private static Icon CreateIcon(TrayIconState state)
    {
        // 待機中だけアプリの図柄にする。図柄にも赤い丸があるため、録画中などに印を重ねると小さいトレイでは見分けにくい。
        if (state == TrayIconState.Idle) return AppIcon.Create(SystemInformation.SmallIconSize);
        var color = state switch
        {
            TrayIconState.Recording => Color.Firebrick,
            TrayIconState.Paused => Color.DarkOrange,
            TrayIconState.Saving => Color.DimGray,
            _ => Color.FromArgb(35, 110, 190)
        };
        using var bitmap = new Bitmap(32, 32);
        using var indicator = new Pen(Color.White, 2);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        using (var border = new Pen(Color.White, 2))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.FillRoundedRectangle(brush, new Rectangle(2, 2, 28, 28), 6);
            graphics.DrawRoundedRectangle(border, new Rectangle(2, 2, 28, 28), 6);
            switch (state)
            {
                case TrayIconState.Recording:
                    graphics.FillEllipse(Brushes.White, 11, 11, 10, 10);
                    break;
                case TrayIconState.Paused:
                    graphics.FillRectangle(Brushes.White, 9, 9, 5, 14);
                    graphics.FillRectangle(Brushes.White, 18, 9, 5, 14);
                    break;
                case TrayIconState.Saving:
                    graphics.DrawRectangle(indicator, 9, 9, 14, 14);
                    graphics.DrawLine(Pens.White, 16, 12, 16, 16);
                    graphics.DrawLine(Pens.White, 16, 16, 20, 18);
                    break;
                default:
                    graphics.DrawRectangle(Pens.White, 9, 9, 14, 14);
                    break;
            }
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
