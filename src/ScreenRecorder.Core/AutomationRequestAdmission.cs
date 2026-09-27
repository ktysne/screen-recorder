namespace ScreenRecorder.Core;

public enum AutomationCountdownKind { Recording, Screenshot }

public sealed record AutomationRequestState(
    bool ModalDialogOpen,
    bool SelectionScreenOpen,
    AutomationCountdownKind? Countdown,
    bool UpdateDownloadOrPreparationInProgress,
    bool RecordingStoppable = false);

public sealed record AutomationRequestDecision(bool Accepted, string? ErrorDataCode)
{
    public static AutomationRequestDecision Accept { get; } = new(true, null);

    public static AutomationRequestDecision Reject(string code) => new(false, code);
}

public static class AutomationRequestAdmission
{
    public static AutomationRequestDecision Decide(
        AutomationRequestState state,
        string method,
        RecorderAction? action = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        if (method is "hello" or "status" or "waitFor" or "exit") return AutomationRequestDecision.Accept;
        if (state.ModalDialogOpen) return AutomationRequestDecision.Reject("rejectedWhileModal");
        if (state.SelectionScreenOpen)
            return method == "selection"
                ? AutomationRequestDecision.Accept
                : AutomationRequestDecision.Reject("rejectedWhileSelection");
        // 静止画の撮影の遅延は停止で取り消せないので、その間の停止は進行中の録画があるときだけ受け付ける。
        if (state.Countdown is { } countdown)
            return method == "perform" && action == RecorderAction.StopRecording
                && (countdown == AutomationCountdownKind.Recording || state.RecordingStoppable)
                ? AutomationRequestDecision.Accept
                : AutomationRequestDecision.Reject("rejectedDuringCountdown");
        if (state.UpdateDownloadOrPreparationInProgress)
            return AutomationRequestDecision.Reject("rejectedDuringUpdate");
        if (method == "selection") return AutomationRequestDecision.Reject("selectionNotOpen");

        return AutomationRequestDecision.Accept;
    }
}
