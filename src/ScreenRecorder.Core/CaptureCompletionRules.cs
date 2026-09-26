namespace ScreenRecorder.Core;

public enum CaptureNotification
{
    None,
    Warning,
    Saved
}

public static class CaptureCompletionRules
{
    public static CaptureNotification DecideNotification(bool hasWarning, bool notifyWhenSaved) =>
        hasWarning ? CaptureNotification.Warning : notifyWhenSaved ? CaptureNotification.Saved : CaptureNotification.None;
}
