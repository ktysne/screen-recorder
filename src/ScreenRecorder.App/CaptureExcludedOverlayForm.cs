using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal abstract class CaptureExcludedOverlayForm : Form
{
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
            return parameters;
        }
    }

    public void ExcludeFromCapture(string logTag, string overlayName)
    {
        if (!NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WindowDisplayAffinityExcludeFromCapture))
            DiagnosticLog.Warn(logTag, $"{overlayName}を撮影対象から除外できませんでした。");
    }
}
