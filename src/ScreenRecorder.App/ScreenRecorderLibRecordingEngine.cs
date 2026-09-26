using ScreenRecorderLib;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class ScreenRecorderLibRecordingEngine : IRecordingEngine
{
    private static readonly TimeSpan FailureFinalizationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleCompletionGracePeriod = TimeSpan.FromMilliseconds(200);
    private readonly object _gate = new();
    private readonly TaskCompletionSource _terminationSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Recorder? _recorder;
    private string? _outputPath;
    private int _terminalEventRaised;
    private int _failureDispatchPending;
    private int _terminationKind;
    private bool _startRequested;
    private bool _stopRequested;
    private bool _pauseRequested;
    private bool _hasObservedRecording;
    private bool _disposed;

    public ScreenRecorderLibRecordingEngine() { }

    public event EventHandler<RecordingEngineStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<RecordingEngineCompletedEventArgs>? RecordingCompleted;
    public event EventHandler<RecordingEngineFailedEventArgs>? RecordingFailed;
    public event EventHandler<RecordingEngineWarningEventArgs>? RecordingWarning;

    public void Start(RecordingStartRequest request)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startRequested) throw new InvalidOperationException("録画エンジンはすでに開始しています。");
            _startRequested = true;
        }

        _outputPath = request.OutputPath;
        var options = RecorderOptions.Default;
        options.SourceOptions = new SourceOptions
        {
            RecordingSources = [CreateSource(request)]
        };
        options.OutputOptions.RecorderMode = RecorderMode.Video;
        options.OutputOptions.OutputFrameSize = new ScreenSize(request.OutputFrameSize.Width, request.OutputFrameSize.Height);
        options.VideoEncoderOptions.Encoder = new H264VideoEncoder
        {
            BitrateMode = H264BitrateControlMode.UnconstrainedVBR
        };
        options.VideoEncoderOptions.Bitrate = checked(request.BitrateMbps * 1_000_000);
        options.VideoEncoderOptions.Framerate = request.FrameRate;
        options.VideoEncoderOptions.IsFixedFramerate = true;
        options.VideoEncoderOptions.IsHardwareEncodingEnabled = request.HardwareEncodingEnabled;
        options.MouseOptions = new MouseOptions
        {
            IsMousePointerEnabled = request.CaptureCursor,
            IsMouseClicksDetected = request.HighlightClicks
        };
        var loopbackAvailable = false;
        if (request.CaptureSystemAudio)
        {
            try { loopbackAvailable = AudioEndpoints.GetLoopbackDevices().Any(device => device.IsDefaultDevice); }
            catch (Exception exception) { DiagnosticLog.Warn(DiagnosticLogTags.Audio, $"PC の音声デバイスを列挙できませんでした: {exception}"); }
        }
        var microphoneDevices = new List<RecordableAudioCaptureDevice>();
        if (request.CaptureMicrophone)
        {
            try { microphoneDevices = AudioEndpoints.GetCaptureDevices(); }
            catch (Exception exception) { DiagnosticLog.Warn(DiagnosticLogTags.Audio, $"マイクを列挙できませんでした: {exception}"); }
        }
        var microphoneAvailable = !request.CaptureMicrophone || (request.MicrophoneDeviceId is null
            ? microphoneDevices.Any(device => device.IsDefaultDevice)
            : microphoneDevices.Any(device => string.Equals(device.ID, request.MicrophoneDeviceId, StringComparison.Ordinal)));
        var captureSystemAudio = request.CaptureSystemAudio && loopbackAvailable;
        var captureMicrophone = request.CaptureMicrophone && microphoneAvailable;
        if (request.CaptureSystemAudio && !captureSystemAudio) DiagnosticLog.Warn(DiagnosticLogTags.Audio, "PC の音声入力元を使用できないため、音声を収録しません。");
        if (request.CaptureMicrophone && !captureMicrophone) DiagnosticLog.Warn(DiagnosticLogTags.Audio, "マイクを使用できないため、音声を収録しません。");
        options.AudioOptions.IsAudioEnabled = captureSystemAudio || captureMicrophone;
        // 録画中に設定を変える API(DynamicAudioOptions)は録画の開始前には効かないので、作る前の設定で音源を渡す。
        options.AudioOptions.AudioSources.Clear();
        if (captureSystemAudio || captureMicrophone)
        {
            options.AudioOptions.Bitrate = ToAudioBitrate(request.AacBitrateKbps);
            options.AudioOptions.Channels = AudioChannels.Stereo;
            if (captureSystemAudio) options.AudioOptions.AudioSources.Add(LoopbackAudioSource.Default);
            if (captureMicrophone)
                options.AudioOptions.AudioSources.Add(request.MicrophoneDeviceId is null
                    ? CaptureAudioSource.Default
                    : new CaptureAudioSource(request.MicrophoneDeviceId));
        }

        var recorder = Recorder.CreateRecorder(options);
        lock (_gate)
        {
            if (_disposed)
            {
                recorder.Dispose();
                throw new ObjectDisposedException(nameof(ScreenRecorderLibRecordingEngine));
            }
            _recorder = recorder;
        }
        recorder.OnStatusChanged += OnStatusChanged;
        recorder.OnRecordingComplete += OnRecordingComplete;
        recorder.OnRecordingFailed += OnRecordingFailed;
        recorder.Record(request.OutputPath);
        var missingSources = new List<string>();
        if (request.CaptureSystemAudio && !captureSystemAudio) missingSources.Add("PC の音声デバイス");
        if (request.CaptureMicrophone && !captureMicrophone) missingSources.Add("マイク");
        if (missingSources.Count > 0)
            RecordingWarning?.Invoke(this, new RecordingEngineWarningEventArgs($"{string.Join("、", missingSources)}が見つからなかったため、その音源を録音しません。"));
    }

    public void Pause()
    {
        Recorder? recorder;
        var applyImmediately = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_startRequested) throw new InvalidOperationException("録画エンジンは開始していません。");
            recorder = _recorder;
            if (!_hasObservedRecording)
            {
                _pauseRequested = true;
                return;
            }
            applyImmediately = recorder?.Status == RecorderStatus.Recording;
        }
        if (applyImmediately) recorder!.Pause();
    }

    public void Resume()
    {
        Recorder? recorder;
        var applyImmediately = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_startRequested) throw new InvalidOperationException("録画エンジンは開始していません。");
            recorder = _recorder;
            if (!_hasObservedRecording)
            {
                _pauseRequested = false;
                return;
            }
            applyImmediately = recorder?.Status == RecorderStatus.Paused;
        }
        if (applyImmediately) recorder!.Resume();
    }

    public void Stop()
    {
        Recorder? recorder;
        var applyImmediately = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_startRequested) throw new InvalidOperationException("録画エンジンは開始していません。");
            recorder = _recorder;
            if (!_hasObservedRecording)
            {
                _stopRequested = true;
                _pauseRequested = false;
                return;
            }
            applyImmediately = recorder?.Status is RecorderStatus.Recording or RecorderStatus.Paused;
        }
        if (applyImmediately) recorder!.Stop();
    }

    public async Task<RecordingTerminationOutcome> WaitForTerminationAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_terminationSignal.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != _terminationSignal.Task) return RecordingTerminationOutcome.TimedOut;
        if (Volatile.Read(ref _terminationKind) == (int)RecordingTerminationOutcome.Idle)
        {
            await Task.Delay(IdleCompletionGracePeriod).ConfigureAwait(false);
            if (Volatile.Read(ref _terminationKind) == (int)RecordingTerminationOutcome.Completed)
                return RecordingTerminationOutcome.Completed;
            return RecordingTerminationOutcome.Idle;
        }
        return RecordingTerminationOutcome.Completed;
    }

    public void Dispose()
    {
        Recorder? recorder;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            recorder = _recorder;
            _recorder = null;
        }
        if (recorder is null) return;
        recorder.OnStatusChanged -= OnStatusChanged;
        recorder.OnRecordingComplete -= OnRecordingComplete;
        recorder.OnRecordingFailed -= OnRecordingFailed;
        recorder.Dispose();
    }

    private static RecordingSourceBase CreateSource(RecordingStartRequest request)
    {
        if (request.SourceKind == RecordingSourceKind.Window)
        {
            return new WindowRecordingSource(request.WindowHandle)
            {
                IsCursorCaptureEnabled = request.CaptureCursor,
                IsBorderRequired = request.RequireCaptureBorder,
                OutputSize = new ScreenSize(request.SourceFrameSize.Width, request.SourceFrameSize.Height)
            };
        }

        var source = new DisplayRecordingSource(request.DisplayDeviceName)
        {
            RecorderApi = RecorderApi.DesktopDuplication,
            IsCursorCaptureEnabled = request.CaptureCursor
        };
        if (request.SourceRect is { } sourceRect)
        {
            source.SourceRect = new ScreenRect(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height);
        }
        return source;
    }

    private static AudioBitrate ToAudioBitrate(int bitrateKbps) => bitrateKbps switch
    {
        96 => AudioBitrate.bitrate_96kbps,
        128 => AudioBitrate.bitrate_128kbps,
        160 => AudioBitrate.bitrate_160kbps,
        192 => AudioBitrate.bitrate_192kbps,
        _ => throw new ArgumentOutOfRangeException(nameof(bitrateKbps))
    };

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs eventArgs)
    {
        if (eventArgs.Status == RecorderStatus.Idle)
        {
            Interlocked.CompareExchange(ref _terminationKind, (int)RecordingTerminationOutcome.Idle, (int)RecordingTerminationOutcome.Waiting);
            _terminationSignal.TrySetResult();
            return;
        }

        var status = eventArgs.Status switch
        {
            RecorderStatus.Recording => RecordingEngineStatus.Recording,
            RecorderStatus.Paused => RecordingEngineStatus.Paused,
            RecorderStatus.Finishing => RecordingEngineStatus.Saving,
            _ => (RecordingEngineStatus?)null
        };
        if (eventArgs.Status == RecorderStatus.Recording && sender is Recorder recorder)
            ApplyPendingCommand(recorder);
        if (status is { } value) StatusChanged?.Invoke(this, new RecordingEngineStatusChangedEventArgs(value));
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs eventArgs)
    {
        if (Interlocked.CompareExchange(ref _terminalEventRaised, 1, 0) != 0) return;
        Interlocked.Exchange(ref _terminationKind, (int)RecordingTerminationOutcome.Completed);
        _terminationSignal.TrySetResult();
        RecordingCompleted?.Invoke(this, new RecordingEngineCompletedEventArgs(eventArgs.FilePath ?? _outputPath ?? string.Empty));
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs eventArgs)
    {
        StartFailureFinalization(GetRecorder(), eventArgs.FilePath ?? _outputPath ?? string.Empty, eventArgs.Error, stopIfActive: true);
    }

    private Recorder? GetRecorder()
    {
        lock (_gate) return _recorder;
    }

    private void ApplyPendingCommand(Recorder recorder)
    {
        RecordingEngineCommand command;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_recorder, recorder)) return;
            _hasObservedRecording = true;
            command = _stopRequested
                ? RecordingEngineCommand.Stop
                : _pauseRequested
                    ? RecordingEngineCommand.Pause
                    : RecordingEngineCommand.None;
            _stopRequested = false;
            _pauseRequested = false;
        }

        try
        {
            switch (command)
            {
                case RecordingEngineCommand.Pause:
                    recorder.Pause();
                    break;
                case RecordingEngineCommand.Stop:
                    recorder.Stop();
                    break;
            }
        }
        catch (Exception exception)
        {
            StartFailureFinalization(recorder, _outputPath ?? string.Empty, exception.Message, stopIfActive: command != RecordingEngineCommand.Stop);
        }
    }

    private void StartFailureFinalization(Recorder? recorder, string filePath, string error, bool stopIfActive)
    {
        if (Interlocked.CompareExchange(ref _failureDispatchPending, 1, 0) != 0) return;
        if (stopIfActive && recorder is not null && recorder.Status is RecorderStatus.Recording or RecorderStatus.Paused)
        {
            try { recorder.Stop(); }
            catch (Exception exception) { DiagnosticLog.Error(DiagnosticLogTags.Record, $"失敗した録画を停止できませんでした: {exception}"); }
        }
        _ = RaiseFailureAfterStopAsync(filePath, error);
    }

    private async Task RaiseFailureAfterStopAsync(string filePath, string error)
    {
        var termination = await WaitForTerminationAsync(FailureFinalizationTimeout).ConfigureAwait(false);
        if (RecordingTerminationRules.Decide(termination) == RecordingTerminationDecision.ContinueCompletedSave) return;
        if (Interlocked.CompareExchange(ref _terminalEventRaised, 1, 0) != 0) return;
        RecordingFailed?.Invoke(this, new RecordingEngineFailedEventArgs(filePath, error, termination));
    }
}
