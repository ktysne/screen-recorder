using ScreenRecorderLib;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class ScreenRecorderLibRecordingEngine : IRecordingEngine
{
    private readonly DailyLog _log;
    private Recorder? _recorder;
    private string? _outputPath;
    private int _terminalEventRaised;
    private int _failureDispatchPending;
    private bool _disposed;

    public ScreenRecorderLibRecordingEngine(DailyLog log) => _log = log;

    public event EventHandler<RecordingEngineStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<RecordingEngineCompletedEventArgs>? RecordingCompleted;
    public event EventHandler<RecordingEngineFailedEventArgs>? RecordingFailed;

    public void Start(RecordingStartRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recorder is not null) throw new InvalidOperationException("録画エンジンはすでに開始しています。");

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
        options.AudioOptions.IsAudioEnabled = false;

        var recorder = Recorder.CreateRecorder(options);
        _recorder = recorder;
        recorder.OnStatusChanged += OnStatusChanged;
        recorder.OnRecordingComplete += OnRecordingComplete;
        recorder.OnRecordingFailed += OnRecordingFailed;
        recorder.Record(request.OutputPath);
    }

    public void Pause()
    {
        var recorder = _recorder ?? throw new InvalidOperationException("録画エンジンは開始していません。");
        if (recorder.Status == RecorderStatus.Recording) recorder.Pause();
    }

    public void Resume()
    {
        var recorder = _recorder ?? throw new InvalidOperationException("録画エンジンは開始していません。");
        if (recorder.Status == RecorderStatus.Paused) recorder.Resume();
    }

    public void Stop()
    {
        var recorder = _recorder ?? throw new InvalidOperationException("録画エンジンは開始していません。");
        if (recorder.Status is RecorderStatus.Recording or RecorderStatus.Paused) recorder.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var recorder = Interlocked.Exchange(ref _recorder, null);
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

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs eventArgs)
    {
        var status = eventArgs.Status switch
        {
            RecorderStatus.Recording => RecordingEngineStatus.Recording,
            RecorderStatus.Paused => RecordingEngineStatus.Paused,
            RecorderStatus.Finishing => RecordingEngineStatus.Saving,
            _ => (RecordingEngineStatus?)null
        };
        if (status is { } value) StatusChanged?.Invoke(this, new RecordingEngineStatusChangedEventArgs(value));
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs eventArgs)
    {
        if (Interlocked.CompareExchange(ref _terminalEventRaised, 1, 0) != 0) return;
        RecordingCompleted?.Invoke(this, new RecordingEngineCompletedEventArgs(eventArgs.FilePath ?? _outputPath ?? string.Empty));
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs eventArgs)
    {
        if (Interlocked.CompareExchange(ref _failureDispatchPending, 1, 0) != 0) return;
        var recorder = _recorder;
        if (recorder is not null && recorder.Status is RecorderStatus.Recording or RecorderStatus.Paused)
        {
            try { recorder.Stop(); }
            catch (Exception exception) { _log.Write($"Stopping failed recording failed: {exception}"); }
        }
        _ = RaiseFailureAfterStopAsync(recorder, eventArgs.FilePath ?? _outputPath ?? string.Empty, eventArgs.Error);
    }

    private async Task RaiseFailureAfterStopAsync(Recorder? recorder, string filePath, string error)
    {
        try
        {
            while (recorder?.Status == RecorderStatus.Finishing)
                await Task.Delay(100).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _terminalEventRaised, 1, 0) != 0) return;
        RecordingFailed?.Invoke(this, new RecordingEngineFailedEventArgs(filePath, error));
    }
}
