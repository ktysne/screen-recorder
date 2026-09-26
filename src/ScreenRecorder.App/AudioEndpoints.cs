using System.Runtime.InteropServices;
using ScreenRecorderLib;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

// ScreenRecorderLib の列挙は既定のデバイスが無いとアクセス違反で落ちるため、先に Core Audio で有無を確かめる。
internal static class AudioEndpoints
{
    private static bool HasDefaultPlaybackDevice() => HasDefaultEndpoint(AudioDataFlow.Render);

    private static bool HasDefaultMicrophone() => HasDefaultEndpoint(AudioDataFlow.Capture);

    public static List<RecordableAudioLoopbackDevice> GetLoopbackDevices() =>
        HasDefaultPlaybackDevice() ? Recorder.GetSystemAudioLoopbackDevices() : [];

    public static List<RecordableAudioCaptureDevice> GetCaptureDevices() =>
        HasDefaultMicrophone() ? Recorder.GetSystemAudioCaptureDevices() : [];

    private static bool HasDefaultEndpoint(AudioDataFlow flow)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? endpoint = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var result = enumerator.GetDefaultAudioEndpoint(flow, AudioRole.Console, out endpoint);
            return result >= 0 && endpoint is not null;
        }
        catch (Exception exception)
        {
            var deviceName = flow == AudioDataFlow.Render ? "PC の音声デバイス" : "マイク";
            DiagnosticLog.Warn(DiagnosticLogTags.Audio, $"{deviceName}の有無を確認できませんでした: {exception}");
            return false;
        }
        finally
        {
            if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private enum AudioDataFlow
    {
        Render = 0,
        Capture = 1
    }

    private enum AudioRole
    {
        Console = 0
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(AudioDataFlow dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(AudioDataFlow dataFlow, AudioRole role, out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice { }
}
