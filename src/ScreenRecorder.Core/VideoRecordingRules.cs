using System.Drawing;

namespace ScreenRecorder.Core;

public readonly record struct RecordingDimensions(Size SourceSize, Size OutputSize);

public enum VideoRecordingState
{
    Idle,
    Countdown,
    Preparing,
    Recording,
    Paused,
    Saving
}

public enum RecordingEngineCommand
{
    None,
    Pause,
    Resume,
    Stop
}

public enum RecordingTerminationOutcome
{
    Waiting,
    Completed,
    Failed,
    Idle,
    TimedOut
}

public enum RecordingTerminationDecision
{
    Wait,
    ContinueCompletedSave,
    NotifyIncompleteThenDispose
}

public static class RecordingTerminationRules
{
    public static RecordingTerminationDecision Decide(RecordingTerminationOutcome outcome) => outcome switch
    {
        RecordingTerminationOutcome.Waiting => RecordingTerminationDecision.Wait,
        RecordingTerminationOutcome.Completed => RecordingTerminationDecision.ContinueCompletedSave,
        RecordingTerminationOutcome.Failed or RecordingTerminationOutcome.Idle or RecordingTerminationOutcome.TimedOut => RecordingTerminationDecision.NotifyIncompleteThenDispose,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };
}

public sealed class VideoRecordingStateMachine
{
    private bool _engineReady;
    private bool _stopRequested;
    public VideoRecordingState State { get; private set; }

    public bool CanStop => State is VideoRecordingState.Preparing or VideoRecordingState.Recording or VideoRecordingState.Paused;

    public bool CanPause => State is VideoRecordingState.Recording or VideoRecordingState.Paused;

    public bool IsWaitingForEngineStart => !_engineReady
        && (State == VideoRecordingState.Preparing || (_stopRequested && State == VideoRecordingState.Saving));

    public bool TryBeginCountdown() => Move(VideoRecordingState.Idle, VideoRecordingState.Countdown);

    public bool TryCancelCountdown() => Move(VideoRecordingState.Countdown, VideoRecordingState.Idle);

    public bool TryStartRecording()
    {
        if (!Move(VideoRecordingState.Countdown, VideoRecordingState.Preparing)) return false;
        _engineReady = false;
        _stopRequested = false;
        return true;
    }

    public bool TryPause()
    {
        if (State != VideoRecordingState.Recording) return false;
        RequestPause();
        return true;
    }

    public bool TryResume()
    {
        if (State != VideoRecordingState.Paused) return false;
        RequestResume();
        return true;
    }

    public RecordingEngineCommand RequestPause()
    {
        if (!Move(VideoRecordingState.Recording, VideoRecordingState.Paused)) return RecordingEngineCommand.None;
        return _engineReady ? RecordingEngineCommand.Pause : RecordingEngineCommand.None;
    }

    public RecordingEngineCommand RequestResume()
    {
        if (!Move(VideoRecordingState.Paused, VideoRecordingState.Recording)) return RecordingEngineCommand.None;
        return _engineReady ? RecordingEngineCommand.Resume : RecordingEngineCommand.None;
    }

    public RecordingEngineCommand RequestStop()
    {
        if (!CanStop) return RecordingEngineCommand.None;
        State = VideoRecordingState.Saving;
        if (!_engineReady)
        {
            _stopRequested = true;
            return RecordingEngineCommand.None;
        }
        return RecordingEngineCommand.Stop;
    }

    public RecordingEngineCommand OnEngineRecordingStarted()
    {
        if (State is VideoRecordingState.Idle or VideoRecordingState.Countdown) return RecordingEngineCommand.None;
        if (State == VideoRecordingState.Preparing)
        {
            State = VideoRecordingState.Recording;
            _engineReady = true;
            return RecordingEngineCommand.None;
        }
        _engineReady = true;
        if (_stopRequested || State == VideoRecordingState.Saving)
        {
            _stopRequested = false;
            return RecordingEngineCommand.Stop;
        }
        if (State == VideoRecordingState.Paused) return RecordingEngineCommand.Pause;
        return RecordingEngineCommand.None;
    }

    public bool TryBeginSaving()
    {
        if (!CanStop) return false;
        State = VideoRecordingState.Saving;
        return true;
    }

    public bool TryCompleteSaving()
    {
        if (!Move(VideoRecordingState.Saving, VideoRecordingState.Idle)) return false;
        _engineReady = false;
        _stopRequested = false;
        return true;
    }

    public bool TryFail()
    {
        if (State == VideoRecordingState.Idle) return false;
        State = VideoRecordingState.Idle;
        _engineReady = false;
        _stopRequested = false;
        return true;
    }

    public static RecordingDimensions? CalculateDimensions(int width, int height, int scalePercent)
    {
        if (scalePercent is not (50 or 75 or 100)) throw new ArgumentOutOfRangeException(nameof(scalePercent));

        var sourceWidth = RoundDownToEven(width);
        var sourceHeight = RoundDownToEven(height);
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        var outputWidth = RoundDownToEven((long)sourceWidth * scalePercent / 100);
        var outputHeight = RoundDownToEven((long)sourceHeight * scalePercent / 100);
        if (outputWidth <= 0 || outputHeight <= 0) return null;

        return new RecordingDimensions(
            new Size(sourceWidth, sourceHeight),
            new Size(outputWidth, outputHeight));
    }

    public static bool HasMinimumFreeSpace(long availableBytes) => availableBytes >= MinimumFreeSpaceBytes;

    public const long MinimumFreeSpaceBytes = 1_000_000_000;

    private bool Move(VideoRecordingState expected, VideoRecordingState next)
    {
        if (State != expected) return false;
        State = next;
        return true;
    }

    private static int RoundDownToEven(long value) => value <= 0 ? 0 : (int)(value & ~1L);
}

public static class VideoRecordingFileNaming
{
    public static string GetAvailablePath(
        string baseDirectory,
        bool organizeByMonth,
        DateTime capturedAt,
        ScreenshotMode mode,
        string? windowTitle,
        string? fileNameTemplate,
        Func<string, bool> fileExists) =>
        ScreenshotFileNaming.GetAvailablePath(
            baseDirectory,
            organizeByMonth,
            capturedAt,
            mode,
            windowTitle,
            fileNameTemplate,
            ".mp4",
            fileExists);

    public static string GetTemporaryPath(string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        if (!Path.GetExtension(finalPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("録画ファイルの拡張子は .mp4 である必要があります。", nameof(finalPath));

        var directory = Path.GetDirectoryName(finalPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(finalPath);
        return Path.Combine(directory, $"{baseName}.recording.mp4");
    }
}
