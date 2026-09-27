using System.Drawing;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal enum RecordingSourceKind
{
    Display,
    Region,
    Window
}

internal enum RecordingEngineStatus
{
    Recording,
    Paused,
    Saving
}

internal sealed record RecordingStartRequest(
    string OutputPath,
    RecordingSourceKind SourceKind,
    string DisplayDeviceName,
    Rectangle? SourceRect,
    IntPtr WindowHandle,
    Size SourceFrameSize,
    Size OutputFrameSize,
    int FrameRate,
    int BitrateMbps,
    bool CaptureCursor,
    bool HighlightClicks,
    bool HardwareEncodingEnabled,
    bool RequireCaptureBorder,
    bool CaptureSystemAudio,
    bool CaptureMicrophone,
    string? MicrophoneDeviceId,
    int AacBitrateKbps);

internal sealed class RecordingEngineStatusChangedEventArgs(RecordingEngineStatus status) : EventArgs
{
    public RecordingEngineStatus Status { get; } = status;
}

internal sealed class RecordingEngineCompletedEventArgs(string filePath) : EventArgs
{
    public string FilePath { get; } = filePath;
}

internal sealed class RecordingEngineFailedEventArgs(string filePath, string error, RecordingTerminationOutcome outcome, bool beforeRecordingStarted) : EventArgs
{
    public string FilePath { get; } = filePath;
    public string Error { get; } = error;
    public RecordingTerminationOutcome Outcome { get; } = outcome;
    public bool BeforeRecordingStarted { get; } = beforeRecordingStarted;
}

internal sealed class RecordingEngineWarningEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

internal sealed class RecordingEngineOperationFailedEventArgs(
    long operationId,
    RecordingWorkerOperationKind operation,
    string error) : EventArgs
{
    public long OperationId { get; } = operationId;
    public RecordingWorkerOperationKind Operation { get; } = operation;
    public string Error { get; } = error;
}

internal interface IRecordingEngine : IDisposable
{
    event EventHandler<RecordingEngineStatusChangedEventArgs>? StatusChanged;
    event EventHandler<RecordingEngineCompletedEventArgs>? RecordingCompleted;
    event EventHandler<RecordingEngineFailedEventArgs>? RecordingFailed;
    event EventHandler<RecordingEngineWarningEventArgs>? RecordingWarning;
    event EventHandler<RecordingEngineOperationFailedEventArgs>? OperationFailed;

    void Start(RecordingStartRequest request);
    void Pause(long operationId);
    void Resume(long operationId);
    void Stop();
    Task<RecordingTerminationOutcome> WaitForTerminationAsync(TimeSpan timeout);
}
