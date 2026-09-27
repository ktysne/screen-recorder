namespace ScreenRecorder.Capture;

public static class RecordingTimeouts
{
    public static readonly TimeSpan Start = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Finalization = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Termination = TimeSpan.FromSeconds(30);
}
