using System.Drawing;

namespace ScreenRecorder.Core;

public sealed record RecordingDisplayInfo(string DeviceName, Rectangle Bounds);
public enum RecordingStartPlanError { RegionTooSmall, OutputTooSmall }
public sealed record RecordingStartPlan(RecordingWorkerStartData StartData, Rectangle TargetBounds, Rectangle DisplayBounds);
public sealed record RecordingStartPlanResult(RecordingStartPlan? Plan, RecordingStartPlanError? Error);

public static class RecordingStartPlanner
{
    public static RecordingStartPlanResult Plan(
        ScreenshotMode mode, RecordingDisplayInfo display, Rectangle targetBounds, long windowHandle,
        string outputPath, Settings settings, bool requireCaptureBorder)
    {
        var dimensions = VideoRecordingStateMachine.CalculateDimensions(
            targetBounds.Width, targetBounds.Height, settings.OutputScalePercent);
        if (dimensions is null)
            return new(null, targetBounds.Width < 2 || targetBounds.Height < 2
                ? RecordingStartPlanError.RegionTooSmall : RecordingStartPlanError.OutputTooSmall);

        var sourceKind = mode switch
        {
            ScreenshotMode.Full => RecordingWorkerSourceKind.Display,
            ScreenshotMode.Region => RecordingWorkerSourceKind.Region,
            ScreenshotMode.Window => RecordingWorkerSourceKind.Window,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var sourceRect = mode switch
        {
            ScreenshotMode.Full => new Rectangle(0, 0, dimensions.Value.SourceSize.Width, dimensions.Value.SourceSize.Height),
            ScreenshotMode.Region => new Rectangle(targetBounds.X - display.Bounds.X, targetBounds.Y - display.Bounds.Y,
                dimensions.Value.SourceSize.Width, dimensions.Value.SourceSize.Height),
            _ => (Rectangle?)null
        };
        var startData = new RecordingWorkerStartData(
            outputPath, sourceKind, display.DeviceName,
            sourceRect is { } rect ? new RecordingWorkerRectangle(rect.X, rect.Y, rect.Width, rect.Height) : null,
            windowHandle,
            new RecordingWorkerSize(dimensions.Value.SourceSize.Width, dimensions.Value.SourceSize.Height),
            new RecordingWorkerSize(dimensions.Value.OutputSize.Width, dimensions.Value.OutputSize.Height),
            settings.FrameRate, settings.VideoBitrateMbps, settings.CaptureVideoCursor, settings.HighlightClicks,
            settings.Encoder == EncoderMode.Automatic, requireCaptureBorder, settings.CaptureSystemAudio,
            settings.CaptureMicrophone, settings.MicrophoneDeviceId,
            settings.AudioFormat == AudioFormat.Mp3 ? 192 : settings.AacBitrateKbps);
        return new(new(startData, targetBounds, display.Bounds), null);
    }

    public static RecordingDisplayInfo? FindContainingDisplay(IReadOnlyList<RecordingDisplayInfo> displays, Rectangle targetBounds) =>
        displays.FirstOrDefault(display => display.Bounds.Contains(targetBounds));
}
