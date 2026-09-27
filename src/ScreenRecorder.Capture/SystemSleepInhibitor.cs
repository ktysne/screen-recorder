using System.Runtime.InteropServices;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

public sealed class SystemSleepInhibitor : IDisposable
{
    private bool _inhibited;

    public void Inhibit() => SetInhibition(true);
    public void Release() => SetInhibition(false);
    public void Dispose() => Release();

    private void SetInhibition(bool inhibit)
    {
        // 実行状態はスレッド単位なので、抑止と解除は同じ生存中のスレッドから呼ぶ。
        if (_inhibited == inhibit) return;
        var executionState = CaptureNativeMethods.ExecutionStateContinuous;
        if (inhibit) executionState |= CaptureNativeMethods.ExecutionStateSystemRequired;
        if (CaptureNativeMethods.SetThreadExecutionState(executionState) == 0)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Record, $"スリープを抑止できませんでした: 抑止={inhibit}; Windows エラー={Marshal.GetLastWin32Error()}");
            return;
        }
        _inhibited = inhibit;
    }
}
