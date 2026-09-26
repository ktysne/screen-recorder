using System.Diagnostics;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class CaptureNotifier
{
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

    public void Show(int timeout, string title, string message, ToolTipIcon icon)
    {
        _pendingCapturePath = null;
        _pendingUpdateNotification = false;
        _tray.ShowBalloonTip(timeout, title, message, icon);
    }

    public void ShowForCapture(int timeout, string title, string message, ToolTipIcon icon, string? path)
    {
        _pendingCapturePath = path;
        _pendingUpdateNotification = false;
        _tray.ShowBalloonTip(timeout, title, message, icon);
    }

    public void ShowForUpdate(string message, ToolTipIcon icon, bool opensUpdateDialog)
    {
        Show(icon == ToolTipIcon.Info ? 4000 : 6000, UiLabels.AppName, message, icon);
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
        OpenPendingCaptureLocation();
    }

    private void OpenPendingCaptureLocation()
    {
        var path = _pendingCapturePath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true };
            using (Process.Start(start)) { }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.App, $"撮影したファイルの場所を開けませんでした: ファイル={path}; {exception}");
            Show(3000, UiLabels.AppName, string.Format(UiLabels.FolderOpenFailed, exception.Message), ToolTipIcon.Error);
        }
    }
}
