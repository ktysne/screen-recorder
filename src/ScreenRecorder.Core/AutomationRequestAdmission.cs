namespace ScreenRecorder.Core;

public sealed record AutomationRequestState(
    bool ModalDialogOpen,
    bool SelectionScreenOpen,
    bool CountdownInProgress,
    bool UpdateDownloadOrPreparationInProgress);

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
        if (state.CountdownInProgress)
            return method == "perform" && action == RecorderAction.StopRecording
                ? AutomationRequestDecision.Accept
                : AutomationRequestDecision.Reject("rejectedDuringCountdown");
        if (state.UpdateDownloadOrPreparationInProgress)
            return AutomationRequestDecision.Reject("rejectedDuringUpdate");
        if (method == "selection") return AutomationRequestDecision.Reject("selectionNotOpen");

        return AutomationRequestDecision.Accept;
    }
}
