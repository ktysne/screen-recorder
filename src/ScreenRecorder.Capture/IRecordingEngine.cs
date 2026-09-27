using System.Drawing;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

public enum RecordingSourceKind
{
    Display,
    Region,
    Window
}

public enum RecordingEngineStatus
{
    Recording,
    Paused,
    Saving
}

public sealed record RecordingStartRequest(
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
    int AacBitrateKbps)
{
    public RecordingWorkerStartData ToWorkerStartData() => new(
        OutputPath,
        SourceKind switch
        {
            RecordingSourceKind.Display => RecordingWorkerSourceKind.Display,
            RecordingSourceKind.Region => RecordingWorkerSourceKind.Region,
            RecordingSourceKind.Window => RecordingWorkerSourceKind.Window,
            _ => throw new ArgumentOutOfRangeException(nameof(SourceKind))
        },
        DisplayDeviceName,
        SourceRect is { } sourceRect
            ? new RecordingWorkerRectangle(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height)
            : null,
        WindowHandle.ToInt64(),
        new RecordingWorkerSize(SourceFrameSize.Width, SourceFrameSize.Height),
        new RecordingWorkerSize(OutputFrameSize.Width, OutputFrameSize.Height),
        FrameRate, BitrateMbps, CaptureCursor, HighlightClicks, HardwareEncodingEnabled,
        RequireCaptureBorder, CaptureSystemAudio, CaptureMicrophone, MicrophoneDeviceId, AacBitrateKbps);

    public static RecordingStartRequest FromWorkerStartData(RecordingWorkerStartData request) => new(
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
        request.FrameRate, request.BitrateMbps, request.CaptureCursor, request.HighlightClicks,
        request.HardwareEncodingEnabled, request.RequireCaptureBorder, request.CaptureSystemAudio,
        request.CaptureMicrophone, request.MicrophoneDeviceId, request.AacBitrateKbps);
}

public sealed class RecordingEngineStatusChangedEventArgs(RecordingEngineStatus status) : EventArgs
{
    public RecordingEngineStatus Status { get; } = status;
}

public sealed class RecordingEngineCompletedEventArgs(string filePath) : EventArgs
{
    public string FilePath { get; } = filePath;
}

public sealed class RecordingEngineFailedEventArgs(string filePath, string error, RecordingTerminationOutcome outcome, bool beforeRecordingStarted) : EventArgs
{
    public string FilePath { get; } = filePath;
    public string Error { get; } = error;
    public RecordingTerminationOutcome Outcome { get; } = outcome;
    public bool BeforeRecordingStarted { get; } = beforeRecordingStarted;
}

public sealed class RecordingEngineWarningEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class RecordingEngineOperationFailedEventArgs(
    long operationId,
    RecordingWorkerOperationKind operation,
    string error) : EventArgs
{
    public long OperationId { get; } = operationId;
    public RecordingWorkerOperationKind Operation { get; } = operation;
    public string Error { get; } = error;
}

public interface IRecordingEngine : IDisposable
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
