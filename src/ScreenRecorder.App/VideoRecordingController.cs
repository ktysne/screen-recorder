using System.Diagnostics;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class VideoRecordingController : IDisposable
{
    private static readonly TimeSpan RecordingEngineDisposeWarningThreshold = TimeSpan.FromSeconds(1);
    private readonly Func<Settings> _currentSettings;
    private readonly string _executablePath;
    private readonly Func<bool> _exitRequested;
    private readonly Action _recordingStateChanged;
    private readonly Action _busyStateChanged;
    private readonly Action _tryExitAfterPendingWork;
    private readonly CaptureNotifier _notifier;
    private readonly UiDispatcher _dispatcher;
    private readonly Func<string, Task<bool>> _openFolderQuietly;
    private readonly Func<SaveDirectoryKind, string, Exception?, Task<string?>> _confirmSaveDirectory;
    private readonly Action<SaveDirectoryKind, string> _rememberConfirmedDirectory;
    private readonly Action<string, string?, string?> _recordCapture;
    private readonly Action<string, string?, string> _recordFailure;
    private readonly VideoRecordingStateMachine _recordingState = new();
    private readonly IVideoRecordingPostProcessor _videoPostProcessor = new FfmpegVideoRecordingPostProcessor(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"));
    private readonly SystemSleepInhibitor _systemSleepInhibitor = new();
    private bool _recordingSelectionInProgress;
    private bool _recordingFailureFinalizationStarted;
    private bool _recordingFinalizationTimeoutStarted;
    private bool _recordingCompletionProcessing;
    private System.Windows.Forms.Timer? _recordingTimer;
    private System.Windows.Forms.Timer? _windowMonitorTimer;
    private CancellationTokenSource? _recordingCountdownCancellation;
    private CaptureCountdownForm? _recordingCountdownForm;
    private RecordingToolbarForm? _recordingToolbar;
    private RecordingRegionFrameForm? _recordingRegionFrame;
    private IRecordingEngine? _recordingEngine;
    private ActiveRecording? _activeRecording;
    private Stopwatch? _recordingStopwatch;
    private TimeSpan _recordingElapsedBase;
    private TimeSpan _pauseResumeElapsedBefore;
    private long _pauseResumeStartedAtTimestamp;
    private Task _engineDisposal = Task.CompletedTask;
    private long _pauseResumeOperationId;

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

    private sealed record RecordingSpaceAvailability(long? AvailableBytes, Exception? Error);

    public VideoRecordingController(
        string executablePath,
        Func<Settings> currentSettings,
        Func<bool> exitRequested,
        Action recordingStateChanged,
        Action busyStateChanged,
        Action tryExitAfterPendingWork,
        CaptureNotifier notifier,
        UiDispatcher dispatcher,
        Func<string, Task<bool>> openFolderQuietly,
        Func<SaveDirectoryKind, string, Exception?, Task<string?>> confirmSaveDirectory,
        Action<SaveDirectoryKind, string> rememberConfirmedDirectory,
        Action<string, string?, string?> recordCapture,
        Action<string, string?, string> recordFailure)
    {
        _executablePath = executablePath;
        _currentSettings = currentSettings;
        _exitRequested = exitRequested;
        _recordingStateChanged = recordingStateChanged;
        _busyStateChanged = busyStateChanged;
        _tryExitAfterPendingWork = tryExitAfterPendingWork;
        _notifier = notifier;
        _dispatcher = dispatcher;
        _openFolderQuietly = openFolderQuietly;
        _confirmSaveDirectory = confirmSaveDirectory;
        _rememberConfirmedDirectory = rememberConfirmedDirectory;
        _recordCapture = recordCapture;
        _recordFailure = recordFailure;
    }

    public VideoRecordingState State => _recordingState.State;
    public bool CanStop => _recordingState.CanStop;
    public bool CanPause => _recordingState.CanPause;
    public bool SelectionInProgress => _recordingSelectionInProgress;
    public int? CountdownRemainingSeconds => _recordingCountdownForm?.RemainingSeconds;
    public bool RecordingToolbarOpen => _recordingToolbar is { IsDisposed: false, Visible: true };

    // ライブラリの破棄は一時ファイルの書き終えを含むことがあるため、終了時にこの完了を待つ。
    public Task EngineDisposal => _engineDisposal;

    public void Dispose()
    {
        _systemSleepInhibitor.Release();
        _recordingTimer?.Stop();
        _recordingTimer?.Dispose();
        _windowMonitorTimer?.Stop();
        _windowMonitorTimer?.Dispose();
        _recordingCountdownCancellation?.Cancel();
        _recordingCountdownForm?.Close();
        _recordingToolbar?.Close();
        _recordingRegionFrame?.Close();
        var engine = _recordingEngine;
        _recordingEngine = null;
        if (engine is not null) QueueEngineDisposal(engine);
    }

    public async Task HandleRecordActionAsync(ScreenshotMode mode)
    {
        if (_recordingState.State == VideoRecordingState.Countdown)
        {
            CancelRecordingCountdown();
            return;
        }
        if (_recordingState.CanStop)
        {
            StopRecording();
            return;
        }
        if (_recordingState.State != VideoRecordingState.Idle || _recordingSelectionInProgress || _exitRequested()) return;
        await StartRecordingAsync(mode);
    }

    public void HandleStopAction()
    {
        if (_recordingState.State == VideoRecordingState.Countdown) CancelRecordingCountdown();
        else if (_recordingState.CanStop) StopRecording();
    }

    private async Task StartRecordingAsync(ScreenshotMode mode)
    {
        _recordingSelectionInProgress = true;
        _recordingStateChanged();
        _busyStateChanged();
        var captureSettings = _currentSettings().Clone();
        var engineStarted = false;
        try
        {
            string? selectedDirectory = null;
            if (!SaveDirectoryRules.IsConfirmed(captureSettings.ConfirmedVideoDirectory, captureSettings.VideoDirectory))
            {
                selectedDirectory = await _confirmSaveDirectory(SaveDirectoryKind.Video, captureSettings.VideoDirectory, null);
                if (selectedDirectory is null || _exitRequested()) return;
            }
            else
            {
                try
                {
                    await Task.Run(() => SaveDirectoryProbe.Check(captureSettings.VideoDirectory));
                }
                catch (Exception exception) when (SaveDirectoryProbe.IsFailure(exception))
                {
                    if (_exitRequested()) return;
                    selectedDirectory = await _confirmSaveDirectory(SaveDirectoryKind.Video, captureSettings.VideoDirectory, exception);
                    if (selectedDirectory is null || _exitRequested()) return;
                }
            }

            if (_exitRequested()) return;
            if (selectedDirectory is not null)
            {
                captureSettings.VideoDirectory = selectedDirectory;
                captureSettings.ConfirmedVideoDirectory = selectedDirectory;
                _rememberConfirmedDirectory(SaveDirectoryKind.Video, selectedDirectory);
            }

            var initialSpaceAvailability = await Task.Run(() => GetRecordingSpaceAvailability(captureSettings.VideoDirectory));
            if (_exitRequested()) return;
            if (!CheckRecordingSpace(captureSettings.VideoDirectory, initialSpaceAvailability)) return;

            var fullDisplay = mode == ScreenshotMode.Full ? Screen.FromPoint(Cursor.Position) : null;
            using var selection = mode == ScreenshotMode.Full
                ? null
                : await CaptureSelection.SelectAsync(mode, freezeDesktop: false);
            if (mode != ScreenshotMode.Full && selection is null) return;
            if (_exitRequested()) return;

            var targetBounds = mode == ScreenshotMode.Full ? fullDisplay!.Bounds : selection!.Bounds;
            var display = mode == ScreenshotMode.Full ? fullDisplay! : Screen.FromRectangle(targetBounds);
            var startPlanResult = RecordingStartPlanner.Plan(mode, new(display.DeviceName, display.Bounds),
                targetBounds, (selection?.Window ?? IntPtr.Zero).ToInt64(), string.Empty, captureSettings,
                !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
            if (startPlanResult.Error is { } planError)
            {
                var message = planError == RecordingStartPlanError.RegionTooSmall
                    ? UiLabels.RecordingRegionTooSmall
                    : UiLabels.RecordingOutputTooSmall;
                _recordFailure("recording", null, message);
                _notifier.Show(NotificationDuration.Standard, UiLabels.AppName, message, ToolTipIcon.Warning);
                return;
            }

            var plan = startPlanResult.Plan!;
            var sourceKind = plan.StartData.SourceKind switch
            {
                RecordingWorkerSourceKind.Display => RecordingSourceKind.Display,
                RecordingWorkerSourceKind.Region => RecordingSourceKind.Region,
                RecordingWorkerSourceKind.Window => RecordingSourceKind.Window,
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            var capturedAt = DateTime.Now;
            var preparation = await Task.Run(() =>
            {
                var finalPath = VideoRecordingFileNaming.GetAvailablePath(
                    captureSettings.VideoDirectory,
                    captureSettings.OrganizeByMonth,
                    capturedAt,
                    mode,
                    selection?.WindowTitle,
                    captureSettings.FileNameTemplate,
                    candidate => File.Exists(candidate) || File.Exists(VideoRecordingFileNaming.GetTemporaryPath(candidate)));
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                return (FinalPath: finalPath, SpaceAvailability: GetRecordingSpaceAvailability(captureSettings.VideoDirectory));
            });
            if (_exitRequested()) return;
            var finalPath = preparation.FinalPath;
            if (!CheckRecordingSpace(captureSettings.VideoDirectory, preparation.SpaceAvailability)) return;
            if (!_recordingState.TryBeginCountdown()) return;

            var temporaryPath = VideoRecordingFileNaming.GetTemporaryPath(finalPath);
            _recordingFailureFinalizationStarted = false;
            _recordingFinalizationTimeoutStarted = false;
            _recordingCompletionProcessing = false;
            _systemSleepInhibitor.Inhibit();
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
                if (_recordingState.State != VideoRecordingState.Countdown || _exitRequested()) return;
                var spaceAvailability = await Task.Run(() => GetRecordingSpaceAvailability(captureSettings.VideoDirectory));
                countdownCancellation.Token.ThrowIfCancellationRequested();
                if (_exitRequested()) return;
                if (!CheckRecordingSpace(captureSettings.VideoDirectory, spaceAvailability))
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

            var request = RecordingStartRequest.FromWorkerStartData(plan.StartData with { OutputPath = temporaryPath });
            var engine = new RecordingWorkerProcessEngine(_executablePath, DiagnosticLog.Level);
            engine.StatusChanged += (_, eventArgs) => DispatchToUi(() => HandleRecordingStatus(engine, eventArgs));
            engine.RecordingCompleted += (_, eventArgs) => DispatchToUi(() => HandleRecordingCompleted(engine, eventArgs));
            engine.RecordingFailed += (_, eventArgs) => DispatchToUi(() => HandleRecordingFailed(engine, eventArgs));
            engine.OperationFailed += (_, eventArgs) => DispatchToUi(() => HandlePauseResumeOperationFailed(engine, eventArgs));
            engine.RecordingWarning += (_, eventArgs) => DispatchToUi(() =>
            {
                if (!ReferenceEquals(engine, _recordingEngine)) return;
                var message = CaptureText.ErrorDetail(eventArgs.Message);
                _recordFailure("recording", _activeRecording?.TemporaryPath, message);
                _notifier.Show(NotificationDuration.Long, UiLabels.AppName, message, ToolTipIcon.Warning);
            });
            _recordingEngine = engine;
            var startStopwatch = Stopwatch.StartNew();
            engine.Start(request);
            startStopwatch.Stop();
            DiagnosticLog.Debug(DiagnosticLogTags.Record, $"録画エンジンの開始処理にかかった時間: {startStopwatch.ElapsedMilliseconds} ms");
            engineStarted = true;
            if (!_recordingState.TryStartRecording())
            {
                engine.Stop();
                return;
            }
            _ = FinishRecordingStartFailureAfterTimeoutAsync(engine);

            DiagnosticLog.Info(DiagnosticLogTags.Record, $"録画の開始を要求しました: 方法={CaptureText.CaptureMethodName(mode)}。");
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
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画を開始できませんでした: 方法={CaptureText.CaptureMethodName(mode)}; {exception}");
            if (engineStarted)
            {
                StopRecording();
            }
            else
            {
                var failurePath = _activeRecording?.TemporaryPath;
                _recordingState.TryFail();
                CleanupRecordingSession();
                UpdateRecordingUi();
                var message = string.Format(UiLabels.RecordingStartFailed, CaptureText.ErrorDetail(exception.Message));
                _recordFailure("recording", failurePath, message);
                _notifier.Show(NotificationDuration.Standard, UiLabels.AppName, message, ToolTipIcon.Error);
            }
        }
        finally
        {
            _recordingSelectionInProgress = false;
            _recordingStateChanged();
            _busyStateChanged();
            if (_recordingState.State == VideoRecordingState.Idle && _recordingEngine is null)
                CleanupRecordingSession();
            _tryExitAfterPendingWork();
        }
    }

    private async Task WaitForRecordingCountdownAsync(int seconds, Rectangle displayBounds, CancellationToken cancellationToken)
    {
        CaptureCountdownForm? countdown = null;
        try
        {
            await CaptureCountdown.RunAsync(
                CaptureCountdownKind.Recording,
                seconds,
                displayBounds,
                cancellationToken,
                shownCountdown =>
                {
                    countdown = shownCountdown;
                    _recordingCountdownForm = shownCountdown;
                });
        }
        finally
        {
            if (ReferenceEquals(_recordingCountdownForm, countdown)) _recordingCountdownForm = null;
        }
    }

    public void CancelRecordingCountdown()
    {
        if (!_recordingState.TryCancelCountdown()) return;
        _recordingCountdownCancellation?.Cancel();
        _recordingCountdownForm?.Close();
        _systemSleepInhibitor.Release();
        UpdateRecordingUi();
    }

    public void TogglePauseResume()
    {
        if (!_recordingState.CanPause) return;
        var wasRecording = _recordingState.State == VideoRecordingState.Recording;
        var command = wasRecording ? _recordingState.RequestPause() : _recordingState.RequestResume();
        var operationId = BeginPauseResumeOperation();
        try
        {
            ExecuteRecordingCommand(command, operationId);
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
            NotifyPauseResumeFailure(exception.Message);
            UpdateRecordingUi();
        }
    }

    public void StopRecording()
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
            var engine = _recordingEngine;
            ExecuteRecordingCommand(command);
            if (command == RecordingEngineCommand.Stop && engine is not null)
                StartRecordingFinalizationTimeout(engine);
            DiagnosticLog.Info(DiagnosticLogTags.Record, "録画の停止を開始しました。");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画を停止できませんでした: {exception}");
            if (_recordingEngine is { } engine)
                _ = FinishRecordingFailureAfterTerminationAsync(engine, exception.Message, _activeRecording?.TemporaryPath);
            else
                _ = FinishRecordingFailureAsync(exception.Message, _activeRecording?.TemporaryPath, finalizationConfirmed: false);
        }
    }

    private void ExecuteRecordingCommand(RecordingEngineCommand command, long operationId = 0)
    {
        switch (command)
        {
            case RecordingEngineCommand.Pause:
                _recordingEngine?.Pause(operationId > 0 ? operationId : BeginPauseResumeOperation());
                break;
            case RecordingEngineCommand.Resume:
                _recordingEngine?.Resume(operationId > 0 ? operationId : BeginPauseResumeOperation());
                break;
            case RecordingEngineCommand.Stop:
                _recordingEngine?.Stop();
                break;
        }
    }

    private void HandlePauseResumeOperationFailed(IRecordingEngine engine, RecordingEngineOperationFailedEventArgs eventArgs)
    {
        if (!ReferenceEquals(engine, _recordingEngine) || eventArgs.OperationId != _pauseResumeOperationId) return;

        var failedPause = eventArgs.Operation == RecordingWorkerOperationKind.Pause;
        var expectedState = failedPause ? VideoRecordingState.Paused : VideoRecordingState.Recording;
        if (_recordingState.State != expectedState) return;

        if (failedPause)
        {
            _recordingState.RequestResume();
        }
        else
        {
            _recordingState.RequestPause();
        }

        var elapsed = _pauseResumeElapsedBefore;
        if (failedPause) elapsed += Stopwatch.GetElapsedTime(_pauseResumeStartedAtTimestamp);
        RestoreRecordingElapsed(elapsed, isRunning: failedPause);
        NotifyPauseResumeFailure(eventArgs.Error);
        UpdateRecordingUi();
    }

    private long BeginPauseResumeOperation()
    {
        _pauseResumeStartedAtTimestamp = Stopwatch.GetTimestamp();
        _pauseResumeElapsedBefore = GetRecordingElapsed();
        return checked(++_pauseResumeOperationId);
    }

    private TimeSpan GetRecordingElapsed() =>
        _recordingElapsedBase + (_recordingStopwatch?.Elapsed ?? TimeSpan.Zero);

    private void RestoreRecordingElapsed(TimeSpan elapsed, bool isRunning)
    {
        _recordingElapsedBase = elapsed;
        _recordingStopwatch ??= new Stopwatch();
        _recordingStopwatch.Reset();
        if (isRunning) _recordingStopwatch.Start();
    }

    private void NotifyPauseResumeFailure(string error)
    {
        var message = string.Format(UiLabels.RecordingFailed, CaptureText.ErrorDetail(error));
        _recordFailure("recording", _activeRecording?.TemporaryPath, message);
        _notifier.Show(NotificationDuration.Standard, UiLabels.AppName, message, ToolTipIcon.Warning);
    }

    private void HandleRecordingStatus(IRecordingEngine engine, RecordingEngineStatusChangedEventArgs eventArgs)
    {
        if (!ReferenceEquals(engine, _recordingEngine)) return;
        if (eventArgs.Status == RecordingEngineStatus.Recording)
        {
            var wasPreparing = _recordingState.State == VideoRecordingState.Preparing;
            var command = _recordingState.OnEngineRecordingStarted();
            if (wasPreparing && _recordingState.State == VideoRecordingState.Recording)
            {
                _recordingElapsedBase = TimeSpan.Zero;
                _recordingStopwatch = Stopwatch.StartNew();
                if (_activeRecording is { } active)
                {
                    var captureSettings = active.Settings;
                    var audioFormat = !captureSettings.CaptureSystemAudio && !captureSettings.CaptureMicrophone
                        ? "なし"
                        : $"{(captureSettings.AudioFormat == AudioFormat.Mp3 ? "MP3" : "AAC")} ({(captureSettings.AudioFormat == AudioFormat.Mp3 ? captureSettings.Mp3BitrateKbps : captureSettings.AacBitrateKbps)} kbps)";
                    DiagnosticLog.Info(DiagnosticLogTags.Record,
                        $"録画を開始しました: 方法={CaptureText.CaptureMethodName(active.Mode)}、範囲=({active.TargetBounds.X},{active.TargetBounds.Y}) {active.TargetBounds.Width}x{active.TargetBounds.Height}、フレームレート={captureSettings.FrameRate} fps、ビットレート={captureSettings.VideoBitrateMbps} Mbps、音声形式={audioFormat}。");
                }
            }
            try
            {
                ExecuteRecordingCommand(command);
                if (command == RecordingEngineCommand.Stop)
                    StartRecordingFinalizationTimeout(engine);
            }
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
        if (eventArgs.Status == RecordingEngineStatus.Saving && _recordingState.CanStop)
        {
            if (_recordingState.TryBeginSaving()) ShowSavingState();
        }
    }

    private async void HandleRecordingCompleted(IRecordingEngine engine, RecordingEngineCompletedEventArgs eventArgs)
    {
        if (!ReferenceEquals(engine, _recordingEngine)
            || _recordingCompletionProcessing
            || _recordingFailureFinalizationStarted
            || _activeRecording is not { } active) return;
        _recordingCompletionProcessing = true;
        if (_recordingState.CanStop) _recordingState.TryBeginSaving();
        ShowSavingState();
        string? processedPath = null;
        try
        {
            var completedPath = string.IsNullOrWhiteSpace(eventArgs.FilePath) ? active.TemporaryPath : eventArgs.FilePath;
            var result = await RecordingFinalizer.FinalizeAsync(completedPath, active.Settings, active.FinalPath,
                () => VideoRecordingFileNaming.GetAvailablePath(
                    active.Settings.VideoDirectory, active.Settings.OrganizeByMonth, active.CapturedAt,
                    active.Mode, active.WindowTitle, active.Settings.FileNameTemplate,
                    candidate => File.Exists(candidate) || File.Exists(VideoRecordingFileNaming.GetTemporaryPath(candidate))),
                _videoPostProcessor, CancellationToken.None, active.TemporaryPath);
            processedPath = result.RetainedPath;
            if (result.Failure is { } failure) throw failure;
            var finalPath = result.FinalPath!;
            DiagnosticLog.Info(DiagnosticLogTags.Record, $"録画を保存しました: {finalPath}");
            if (result.Warning is { } warning)
                _recordFailure("recording", finalPath, warning);
            await CompleteRecordingSave(finalPath, active.Settings, result.Warning);
            if (result.Warning is not null)
                _notifier.ShowForCapture(NotificationDuration.Long, UiLabels.AppName, result.Warning, ToolTipIcon.Warning, finalPath);
            // 変換前の一時ファイルが残ると次の起動で未完了の録画と誤って知らせるため、削除を終えてから保存を完了する。
            if (result.SupersededPath is { } supersededPath) await RecordingFinalizer.DeleteSupersededAsync(supersededPath);
            _recordingState.TryCompleteSaving();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画を保存できませんでした: 一時ファイル={active.TemporaryPath}; {exception}");
            var temporaryPath = _activeRecording?.TemporaryPath ?? active.TemporaryPath;
            var retainedPath = await Task.Run(() =>
            {
                if (processedPath is not null && File.Exists(processedPath)) return processedPath;
                return !string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath) ? temporaryPath : null;
            });
            ShowRecordingFailure(exception.Message, retainedPath, finalizationConfirmed: true);
            _recordingState.TryFail();
        }
        finally
        {
            CleanupRecordingSession();
            UpdateRecordingUi();
            _tryExitAfterPendingWork();
        }
    }

    private void HandleRecordingFailed(IRecordingEngine engine, RecordingEngineFailedEventArgs eventArgs)
    {
        if (!ReferenceEquals(engine, _recordingEngine)) return;
        if (_recordingState.CanStop) _recordingState.TryBeginSaving();
        ShowSavingState();
        var decision = RecordingTerminationRules.Decide(eventArgs.Outcome);
        if (decision is RecordingTerminationDecision.Wait or RecordingTerminationDecision.ContinueCompletedSave) return;
        _ = FinishRecordingFailureAsync(
            eventArgs.Error,
            eventArgs.FilePath,
            finalizationConfirmed: false,
            recordingStartFailure: eventArgs.BeforeRecordingStarted);
    }

    private async Task FinishRecordingStartFailureAfterTimeoutAsync(IRecordingEngine engine)
    {
        await Task.Delay(RecordingTimeouts.Start);
        if (!ReferenceEquals(engine, _recordingEngine) || !_recordingState.IsWaitingForEngineStart) return;

        DiagnosticLog.Warn(
            DiagnosticLogTags.Record,
            $"録画エンジンが {RecordingTimeouts.Start.TotalSeconds:0} 秒以内に記録を始めなかったため、録画の開始を諦めます。");
        await FinishRecordingFailureAsync(
            UiLabels.RecordingStartTimedOut,
            _activeRecording?.TemporaryPath,
            finalizationConfirmed: false,
            recordingStartFailure: true);
    }

    private void StartRecordingFinalizationTimeout(IRecordingEngine engine)
    {
        if (_recordingFinalizationTimeoutStarted || !ReferenceEquals(engine, _recordingEngine)) return;
        _recordingFinalizationTimeoutStarted = true;
        _ = FinishRecordingFailureAfterSuccessfulStopAsync(engine);
    }

    private async Task FinishRecordingFailureAfterSuccessfulStopAsync(IRecordingEngine engine)
    {
        var termination = await engine.WaitForTerminationAsync(RecordingTimeouts.Finalization);
        var decision = RecordingTerminationRules.Decide(termination);
        if (!ReferenceEquals(engine, _recordingEngine)
            || _recordingCompletionProcessing
            || decision != RecordingTerminationDecision.NotifyIncompleteThenDispose)
            return;

        await FinishRecordingFailureAsync(
            UiLabels.RecordingFinalizationTimedOut,
            _activeRecording?.TemporaryPath,
            finalizationConfirmed: false);
    }

    private async Task FinishRecordingFailureAfterTerminationAsync(IRecordingEngine engine, string error, string? temporaryPath)
    {
        var termination = await engine.WaitForTerminationAsync(RecordingTimeouts.Termination);
        var decision = RecordingTerminationRules.Decide(termination);
        if (!ReferenceEquals(engine, _recordingEngine) || decision is RecordingTerminationDecision.Wait or RecordingTerminationDecision.ContinueCompletedSave)
            return;
        await FinishRecordingFailureAsync(error, temporaryPath, finalizationConfirmed: false);
    }

    private async Task FinishRecordingFailureAsync(
        string error,
        string? temporaryPath,
        bool finalizationConfirmed,
        bool recordingStartFailure = false)
    {
        if (_recordingFailureFinalizationStarted || _recordingCompletionProcessing) return;
        _recordingFailureFinalizationStarted = true;
        var activePath = string.IsNullOrWhiteSpace(temporaryPath) ? _activeRecording?.TemporaryPath : temporaryPath;
        var retainedPath = await Task.Run(() =>
            !string.IsNullOrWhiteSpace(activePath) && File.Exists(activePath) ? activePath : null);
        DiagnosticLog.Error(DiagnosticLogTags.Record, retainedPath is null
            ? $"録画に失敗しました: {error}"
            : $"録画に失敗し、未完了のファイルを保持しました: {retainedPath}; {error}");
        ShowRecordingFailure(error, retainedPath, finalizationConfirmed, recordingStartFailure);
        _recordingState.TryFail();
        CleanupRecordingSession();
        UpdateRecordingUi();
        _tryExitAfterPendingWork();
    }

    private void ShowRecordingFailure(
        string error,
        string? retainedPath,
        bool finalizationConfirmed,
        bool recordingStartFailure = false)
    {
        var message = recordingStartFailure
            ? retainedPath is null
                ? string.Format(UiLabels.RecordingStartFailed, CaptureText.ErrorDetail(error))
                : string.Format(UiLabels.RecordingStartTemporaryFileRetained, CaptureText.PathDetail(retainedPath))
            : retainedPath is null
                ? string.Format(UiLabels.RecordingFailed, CaptureText.ErrorDetail(error))
                : string.Format(
                    finalizationConfirmed ? UiLabels.RecordingTemporaryFileRetained : UiLabels.RecordingTemporaryFileIncomplete,
                    CaptureText.PathDetail(retainedPath));
        _recordFailure("recording", retainedPath, message);
        _notifier.ShowForCapture(NotificationDuration.Long, UiLabels.AppName, message, ToolTipIcon.Error, retainedPath);
    }

    private Task CompleteRecordingSave(string finalPath, Settings settings, string? finalizationWarning)
    {
        return CaptureCompletion.ExecuteAsync(
            CaptureCompletionKind.Recording,
            settings,
            finalPath,
            settings.VideoDirectory,
            $"動作={settings.AfterCaptureAction}、ファイル={finalPath}",
            warning: null,
            _openFolderQuietly,
            notification => _recordCapture("recording", finalPath, finalizationWarning ?? notification),
            message => _recordFailure("recording", finalPath, message),
            _notifier.ShowForCapture);
    }

    private void ShowSavingState()
    {
        _recordingTimer?.Stop();
        _windowMonitorTimer?.Stop();
        _recordingStopwatch?.Stop();
        CloseRegionFrame();
        _recordingToolbar?.UpdateStatus(VideoRecordingState.Saving, GetRecordingElapsed());
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
            toolbar.ExcludeFromCapture(DiagnosticLogTags.Record, "録画操作バー");
            toolbar.UpdateStatus(_recordingState.State, GetRecordingElapsed());
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
            frame.ExcludeFromCapture(DiagnosticLogTags.Record, "範囲枠");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画範囲の枠を表示できませんでした: {exception}");
        }
    }

    private void StartRecordingTimers(ActiveRecording? active)
    {
        _recordingTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _recordingTimer.Tick += (_, _) => _recordingToolbar?.UpdateStatus(_recordingState.State, GetRecordingElapsed());
        _recordingTimer.Start();
        if (active is not { SourceKind: RecordingSourceKind.Window } windowRecording) return;
        _windowMonitorTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _windowMonitorTimer.Tick += (_, _) =>
        {
            if (!_recordingState.CanStop) return;
            if (NativeMethods.IsWindow(windowRecording.WindowHandle)) return;
            DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画対象のウィンドウが閉じられました: hwnd={windowRecording.WindowHandle}");
            StopRecording();
        };
        _windowMonitorTimer.Start();
    }

    private void UpdateRecordingUi()
    {
        var state = _recordingState.State;
        _recordingToolbar?.UpdateStatus(state, GetRecordingElapsed());
        _recordingStateChanged();
    }

    public void HandleSystemSuspend()
    {
        if (_recordingEngine is { } engine)
        {
            try { engine.Stop(); }
            catch (Exception exception) { DiagnosticLog.Error(DiagnosticLogTags.Record, $"スリープに伴う録画の停止に失敗しました: {exception}"); }
        }
        _dispatcher.Post(() =>
        {
            if (_recordingState.CanStop)
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

    private static RecordingSpaceAvailability GetRecordingSpaceAvailability(string videoDirectory)
    {
        try
        {
            return new RecordingSpaceAvailability(DiskSpace.GetAvailableFreeBytes(videoDirectory), null);
        }
        catch (Exception exception)
        {
            return new RecordingSpaceAvailability(null, exception);
        }
    }

    private bool CheckRecordingSpace(string videoDirectory, RecordingSpaceAvailability availability)
    {
        if (availability.Error is { } exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画先の空き容量を確認できませんでした: フォルダー={videoDirectory}; {exception}");
            var message = string.Format(UiLabels.RecordingSpaceCheckFailed, CaptureText.ErrorDetail(exception.Message));
            _recordFailure("recording", _activeRecording?.TemporaryPath, message);
            _notifier.Show(NotificationDuration.Standard, UiLabels.AppName, message, ToolTipIcon.Error);
            return false;
        }
        if (VideoRecordingStateMachine.HasMinimumFreeSpace(availability.AvailableBytes!.Value)) return true;
        DiagnosticLog.Error(DiagnosticLogTags.Record, $"録画先の空き容量が不足しています: {videoDirectory}");
        _recordFailure("recording", _activeRecording?.TemporaryPath, UiLabels.RecordingSpaceInsufficient);
        _notifier.Show(NotificationDuration.Standard, UiLabels.AppName, UiLabels.RecordingSpaceInsufficient, ToolTipIcon.Warning);
        return false;
    }

    private void CleanupRecordingSession()
    {
        var engine = _recordingEngine;
        _recordingEngine = null;
        if (engine is not null) QueueEngineDisposal(engine);

        _systemSleepInhibitor.Release();
        _recordingTimer?.Stop();
        _recordingTimer?.Dispose();
        _recordingTimer = null;
        _windowMonitorTimer?.Stop();
        _windowMonitorTimer?.Dispose();
        _windowMonitorTimer = null;
        _recordingStopwatch?.Stop();
        _recordingStopwatch = null;
        _recordingElapsedBase = TimeSpan.Zero;
        CloseRegionFrame();
        _recordingToolbar?.Close();
        _recordingToolbar?.Dispose();
        _recordingToolbar = null;
        _recordingCountdownForm?.Close();
        _recordingCountdownForm = null;
        _recordingCountdownCancellation?.Dispose();
        _recordingCountdownCancellation = null;
        _recordingCompletionProcessing = false;
        _activeRecording = null;
    }

    private static void DisposeRecordingEngine(IRecordingEngine engine)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            engine.Dispose();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画エンジンの破棄に失敗しました: {exception}");
        }
        finally
        {
            stopwatch.Stop();
            if (stopwatch.Elapsed > RecordingEngineDisposeWarningThreshold)
                DiagnosticLog.Warn(DiagnosticLogTags.Record, $"録画エンジンの破棄に {stopwatch.ElapsedMilliseconds} ms かかりました。");
        }
    }

    private void QueueEngineDisposal(IRecordingEngine engine)
    {
        var previousDisposal = _engineDisposal;
        var disposal = Task.Run(() => DisposeRecordingEngine(engine));
        var combinedDisposal = Task.WhenAll(previousDisposal, disposal);
        _engineDisposal = combinedDisposal;
        _ = combinedDisposal.ContinueWith(
            _ => _dispatcher.Post(() =>
            {
                _busyStateChanged();
                _tryExitAfterPendingWork();
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void CloseRegionFrame()
    {
        _recordingRegionFrame?.Close();
        _recordingRegionFrame?.Dispose();
        _recordingRegionFrame = null;
    }

    private void DispatchToUi(Action action) => _dispatcher.Post(action);
}
