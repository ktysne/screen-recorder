using System.Diagnostics;
using System.Media;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

// 静止画と録画で違う、保存後の処理のログと通知の文言。
internal sealed record CaptureCompletionKind(
    string LogTag,
    string SoundFailureMessage,
    string ActionFailureMessage,
    string ActionFailureNotification,
    string SavedNotification)
{
    public static CaptureCompletionKind Screenshot { get; } = new(
        DiagnosticLogTags.Capture,
        "撮影時の効果音を再生できませんでした",
        "撮影後の動作に失敗しました",
        UiLabels.ScreenshotAfterActionFailed,
        UiLabels.ScreenshotSavedNotification);

    public static CaptureCompletionKind Recording { get; } = new(
        DiagnosticLogTags.Record,
        "録画完了時の効果音を再生できませんでした",
        "録画後の動作に失敗しました",
        UiLabels.RecordingAfterActionFailed,
        UiLabels.RecordingSavedNotification);
}

internal static class CaptureCompletion
{
    // warning は、保存後の動作より前に起きた失敗の警告。保存後の動作が失敗すれば、そちらで上書きする。
    public static void Execute(
        CaptureCompletionKind kind,
        Settings settings,
        string filePath,
        string defaultDirectory,
        string actionFailureDetails,
        string? warning,
        Func<string, bool> openFolder,
        Action<int, string, string, ToolTipIcon, string?> showNotification)
    {
        if (settings.PlayCaptureSound)
        {
            try { SystemSounds.Asterisk.Play(); }
            catch (Exception exception) { DiagnosticLog.Warn(kind.LogTag, $"{kind.SoundFailureMessage}: {exception}"); }
        }

        try
        {
            switch (settings.AfterCaptureAction)
            {
                case CaptureAfterAction.OpenFile:
                    using (Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true })) { }
                    break;
                case CaptureAfterAction.OpenFolder:
                    if (!openFolder(Path.GetDirectoryName(filePath) ?? defaultDirectory))
                        warning = kind.ActionFailureNotification;
                    break;
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(kind.LogTag, $"{kind.ActionFailureMessage}: {actionFailureDetails}; {exception}");
            warning = kind.ActionFailureNotification;
        }

        var notification = CaptureCompletionRules.DecideNotification(warning is not null, settings.NotifyWhenSaved);
        if (notification == CaptureNotification.None) return;
        var message = notification == CaptureNotification.Warning ? warning! : kind.SavedNotification;
        var icon = notification == CaptureNotification.Warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
        try { showNotification(4000, UiLabels.AppName, message, icon, filePath); }
        catch (Exception exception) { DiagnosticLog.Warn(kind.LogTag, $"保存の通知を表示できませんでした: ファイル={filePath}; {exception}"); }
    }
}
