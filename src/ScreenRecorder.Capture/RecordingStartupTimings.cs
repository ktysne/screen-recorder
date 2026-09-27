namespace ScreenRecorder.Capture;

public sealed record RecordingStartupTimings(TimeSpan? ProcessStartToReady, TimeSpan? ReadyToRecording);
