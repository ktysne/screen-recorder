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

internal sealed class RecordingEngineFailedEventArgs(string filePath, string error, RecordingTerminationOutcome outcome) : EventArgs
{
    public string FilePath { get; } = filePath;
    public string Error { get; } = error;
    public RecordingTerminationOutcome Outcome { get; } = outcome;
}

internal sealed class RecordingEngineWarningEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

internal interface IRecordingEngine : IDisposable
{
    event EventHandler<RecordingEngineStatusChangedEventArgs>? StatusChanged;
    event EventHandler<RecordingEngineCompletedEventArgs>? RecordingCompleted;
    event EventHandler<RecordingEngineFailedEventArgs>? RecordingFailed;
    event EventHandler<RecordingEngineWarningEventArgs>? RecordingWarning;

    void Start(RecordingStartRequest request);
    void Pause();
    void Resume();
    void Stop();
    Task<RecordingTerminationOutcome> WaitForTerminationAsync(TimeSpan timeout);
}
