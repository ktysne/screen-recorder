using System.Drawing;

namespace ScreenRecorder.Core;

public readonly record struct RecordingDimensions(Size SourceSize, Size OutputSize);

public enum VideoRecordingState
{
    Idle,
    Countdown,
    Recording,
    Paused,
    Saving
}

public sealed class VideoRecordingStateMachine
{
    public VideoRecordingState State { get; private set; }

    public bool TryBeginCountdown() => Move(VideoRecordingState.Idle, VideoRecordingState.Countdown);

    public bool TryCancelCountdown() => Move(VideoRecordingState.Countdown, VideoRecordingState.Idle);

    public bool TryStartRecording() => Move(VideoRecordingState.Countdown, VideoRecordingState.Recording);

    public bool TryPause() => Move(VideoRecordingState.Recording, VideoRecordingState.Paused);

    public bool TryResume() => Move(VideoRecordingState.Paused, VideoRecordingState.Recording);

    public bool TryBeginSaving()
    {
        if (State is not (VideoRecordingState.Recording or VideoRecordingState.Paused)) return false;
        State = VideoRecordingState.Saving;
        return true;
    }

    public bool TryCompleteSaving() => Move(VideoRecordingState.Saving, VideoRecordingState.Idle);

    public bool TryFail()
    {
        if (State == VideoRecordingState.Idle) return false;
        State = VideoRecordingState.Idle;
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
