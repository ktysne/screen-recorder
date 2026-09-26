using System.Runtime.InteropServices;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed record AudioEndpoint(string Id, string FriendlyName, bool IsDefault);

internal static class AudioEndpoints
{
    private const uint DeviceStateActive = 1;
    private const uint StorageModeRead = 0;
    private const ushort VariantTypeString = 31;
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static IReadOnlyList<AudioEndpoint> GetMicrophones() => EnumerateEndpoints(AudioDataFlow.Capture);

    public static bool HasDefaultPlaybackDevice() => GetDefaultEndpointId(AudioDataFlow.Render) is not null;

    private static IReadOnlyList<AudioEndpoint> EnumerateEndpoints(AudioDataFlow flow)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out devices));
            Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
            var defaultId = GetDefaultEndpointId(enumerator, flow);
            var endpoints = new List<AudioEndpoint>(checked((int)count));
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    Marshal.ThrowExceptionForHR(devices.Item(index, out device));
                    if (device is null) continue;
                    Marshal.ThrowExceptionForHR(device.GetId(out var id));
                    var friendlyName = GetFriendlyName(device, id);
                    endpoints.Add(new AudioEndpoint(id, friendlyName, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception exception)
                {
                    DiagnosticLog.Warn(DiagnosticLogTags.Audio, $"音声デバイスの情報を取得できなかったため、一覧から除きます: {exception.Message}");
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }
            return endpoints;
        }
        finally
        {
            ReleaseComObject(devices);
            ReleaseComObject(enumerator);
        }
    }

    private static string GetFriendlyName(IMMDevice device, string id)
    {
        IPropertyStore? propertyStore = null;
        try
        {
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageModeRead, out propertyStore));
            var value = default(PropVariant);
            try
            {
                var key = FriendlyNameKey;
                Marshal.ThrowExceptionForHR(propertyStore.GetValue(ref key, out value));
                return value.VariantType == VariantTypeString
                    ? Marshal.PtrToStringUni(value.StringValue) ?? id
                    : id;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch (Exception)
        {
            return id;
        }
        finally
        {
            ReleaseComObject(propertyStore);
        }
    }

    private static string? GetDefaultEndpointId(AudioDataFlow flow)
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            return GetDefaultEndpointId(enumerator, flow);
        }
        finally
        {
            ReleaseComObject(enumerator);
        }
    }

    private static string? GetDefaultEndpointId(IMMDeviceEnumerator enumerator, AudioDataFlow flow)
    {
        IMMDevice? endpoint = null;
        try
        {
            var result = enumerator.GetDefaultAudioEndpoint(flow, AudioRole.Console, out endpoint);
            if (result < 0 || endpoint is null) return null;
            return endpoint.GetId(out var id) >= 0 ? id : null;
        }
        finally
        {
            ReleaseComObject(endpoint);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
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
        int EnumAudioEndpoints(AudioDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

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
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, out IntPtr instance);

        [PreserveSig]
        int OpenPropertyStore(uint storageMode, out IPropertyStore propertyStore);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    // x64 の PROPVARIANT は 24 バイトで、GetValue はその全体を書き込む。
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr StringValue;
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropVariant value);
}
