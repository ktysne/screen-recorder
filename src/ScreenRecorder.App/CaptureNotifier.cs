using System.Diagnostics;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class CaptureNotifier
{
    private const int BriefDurationMilliseconds = 3000;
    private const int StandardDurationMilliseconds = 4000;
    private const int LongDurationMilliseconds = 6000;
    private const int BalloonMessageMaximumLength = 255;

    private readonly NotifyIcon _tray;
    private readonly Action _openNotifiedUpdate;
    private string? _pendingCapturePath;
    private bool _pendingUpdateNotification;

    public CaptureNotifier(NotifyIcon tray, Action openNotifiedUpdate)
    {
        _tray = tray;
        _openNotifiedUpdate = openNotifiedUpdate;
    }

    public void ClearPendingCapture() => _pendingCapturePath = null;

    public void Show(NotificationDuration duration, string title, string message, ToolTipIcon icon)
    {
        _pendingCapturePath = null;
        _pendingUpdateNotification = false;
        _tray.ShowBalloonTip(ToMilliseconds(duration), title, LimitMessageLength(message), icon);
    }

    public void ShowForCapture(NotificationDuration duration, string title, string message, ToolTipIcon icon, string? path)
    {
        _pendingCapturePath = path;
        _pendingUpdateNotification = false;
        _tray.ShowBalloonTip(ToMilliseconds(duration), title, LimitMessageLength(message), icon);
    }

    public void ShowForUpdate(string message, ToolTipIcon icon, bool opensUpdateDialog)
    {
        Show(icon == ToolTipIcon.Info ? NotificationDuration.Standard : NotificationDuration.Long, UiLabels.AppName, message, icon);
        _pendingUpdateNotification = opensUpdateDialog;
    }

    public void HandleBalloonClicked()
    {
        if (_pendingUpdateNotification)
        {
            _pendingUpdateNotification = false;
            _openNotifiedUpdate();
            return;
        }
        _ = OpenPendingCaptureLocationAsync();
    }

    private async Task OpenPendingCaptureLocationAsync()
    {
        var path = _pendingCapturePath;
        if (path is null) return;
        try
        {
            await Task.Run(() =>
            {
                if (!File.Exists(path)) return;
                var start = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true };
                using (Process.Start(start)) { }
            });
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.App, $"撮影したファイルの場所を開けませんでした: ファイル={path}; {exception}");
            Show(NotificationDuration.Brief, UiLabels.AppName, string.Format(UiLabels.FolderOpenFailed, CaptureText.ErrorDetail(exception.Message)), ToolTipIcon.Error);
        }
    }

    private static int ToMilliseconds(NotificationDuration duration)
    {
        // Windows 10 以降は OS の設定が表示時間を上書きすることがあるため、値は目安。
        return duration switch
        {
            NotificationDuration.Brief => BriefDurationMilliseconds,
            NotificationDuration.Standard => StandardDurationMilliseconds,
            NotificationDuration.Long => LongDurationMilliseconds,
            _ => throw new ArgumentOutOfRangeException(nameof(duration))
        };
    }

    private static string LimitMessageLength(string message) => message.Length > BalloonMessageMaximumLength
        ? $"{message[..(BalloonMessageMaximumLength - 1)]}…"
        : message;
}
