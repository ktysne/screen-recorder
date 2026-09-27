using System.Drawing;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class RecordingWorkerLibraryHost : IDisposable
{
    private readonly RecordingWorkerStartData _workerRequest;
    private readonly Action<RecordingWorkerRecordingState> _statusChanged;
    private readonly Action<string> _completed;
    private readonly Action<string, string, bool> _failed;
    private readonly Action<string> _warning;
    private readonly ScreenRecorderLibRecordingEngine _engine = new();
    private int _hasObservedRecording;
    private int _hasCompleted;

    public RecordingWorkerLibraryHost(
        RecordingWorkerStartData workerRequest,
        Action<RecordingWorkerRecordingState> statusChanged,
        Action<string> completed,
        Action<string, string, bool> failed,
        Action<string> warning)
    {
        _workerRequest = workerRequest;
        _statusChanged = statusChanged;
        _completed = completed;
        _failed = failed;
        _warning = warning;
        _engine.StatusChanged += HandleStatusChanged;
        _engine.RecordingCompleted += HandleCompleted;
        _engine.RecordingFailed += HandleFailed;
        _engine.RecordingWarning += HandleWarning;
    }

    public bool HasObservedRecording => Volatile.Read(ref _hasObservedRecording) != 0;

    public bool HasCompleted => Volatile.Read(ref _hasCompleted) != 0;

    public void Start() => _engine.Start(ToRecordingRequest(_workerRequest));

    public void Pause() => _engine.Pause();

    public void Resume() => _engine.Resume();

    public void Stop() => _engine.Stop();

    public Task<RecordingTerminationOutcome> WaitForTerminationAsync(TimeSpan timeout) => _engine.WaitForTerminationAsync(timeout);

    public void Dispose()
    {
        _engine.StatusChanged -= HandleStatusChanged;
        _engine.RecordingCompleted -= HandleCompleted;
        _engine.RecordingFailed -= HandleFailed;
        _engine.RecordingWarning -= HandleWarning;
        _engine.Dispose();
    }

    private void HandleStatusChanged(object? sender, RecordingEngineStatusChangedEventArgs eventArgs)
    {
        var workerState = eventArgs.Status switch
        {
            RecordingEngineStatus.Recording => RecordingWorkerRecordingState.Recording,
            RecordingEngineStatus.Paused => RecordingWorkerRecordingState.Paused,
            RecordingEngineStatus.Saving => RecordingWorkerRecordingState.Saving,
            _ => (RecordingWorkerRecordingState?)null
        };
        if (workerState is not { } state) return;
        if (state == RecordingWorkerRecordingState.Recording)
            Interlocked.Exchange(ref _hasObservedRecording, 1);
        _statusChanged(state);
    }

    private void HandleCompleted(object? sender, RecordingEngineCompletedEventArgs eventArgs)
    {
        Interlocked.Exchange(ref _hasCompleted, 1);
        _completed(eventArgs.FilePath);
    }

    private void HandleFailed(object? sender, RecordingEngineFailedEventArgs eventArgs) =>
        _failed(eventArgs.FilePath, eventArgs.Error, !HasObservedRecording);

    private void HandleWarning(object? sender, RecordingEngineWarningEventArgs eventArgs) => _warning(eventArgs.Message);

    private static RecordingStartRequest ToRecordingRequest(RecordingWorkerStartData request) => new(
        request.OutputPath,
        request.SourceKind switch
        {
            RecordingWorkerSourceKind.Display => RecordingSourceKind.Display,
            RecordingWorkerSourceKind.Region => RecordingSourceKind.Region,
            RecordingWorkerSourceKind.Window => RecordingSourceKind.Window,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        },
        request.DisplayDeviceName,
        request.SourceRect is { } sourceRect
            ? new Rectangle(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height)
            : null,
        new IntPtr(request.WindowHandle),
        new Size(request.SourceFrameSize.Width, request.SourceFrameSize.Height),
        new Size(request.OutputFrameSize.Width, request.OutputFrameSize.Height),
        request.FrameRate,
        request.BitrateMbps,
        request.CaptureCursor,
        request.HighlightClicks,
        request.HardwareEncodingEnabled,
        request.RequireCaptureBorder,
        request.CaptureSystemAudio,
        request.CaptureMicrophone,
        request.MicrophoneDeviceId,
        request.AacBitrateKbps);
}

internal static class RecordingWorkerLibraryBridge
{
    public static object Start(
        RecordingWorkerStartData request,
        Action<RecordingWorkerRecordingState> statusChanged,
        Action<string> completed,
        Action<string, string, bool> failed,
        Action<string> warning)
    {
        var engine = new RecordingWorkerLibraryHost(request, statusChanged, completed, failed, warning);
        try
        {
            engine.Start();
            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    public static void Pause(object engine) => ((RecordingWorkerLibraryHost)engine).Pause();

    public static void Resume(object engine) => ((RecordingWorkerLibraryHost)engine).Resume();

    public static void Stop(object engine) => ((RecordingWorkerLibraryHost)engine).Stop();

    public static bool HasObservedRecording(object engine) => ((RecordingWorkerLibraryHost)engine).HasObservedRecording;

    public static bool HasCompleted(object engine) => ((RecordingWorkerLibraryHost)engine).HasCompleted;

    public static Task<RecordingTerminationOutcome> WaitForTerminationAsync(object engine, TimeSpan timeout) =>
        ((RecordingWorkerLibraryHost)engine).WaitForTerminationAsync(timeout);

    public static void Dispose(object engine) => ((RecordingWorkerLibraryHost)engine).Dispose();
}
