using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScreenRecorder.Capture;

public static class NamedPipeProcessIdentity
{
    public static int GetServerProcessId(NamedPipeClientStream pipe) => GetProcessId(pipe.SafePipeHandle, GetNamedPipeServerProcessId);

    public static int GetClientProcessId(NamedPipeServerStream pipe) => GetProcessId(pipe.SafePipeHandle, GetNamedPipeClientProcessId);

    public static string? GetExecutablePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static int GetProcessId(SafePipeHandle pipe, PipeProcessIdReader getProcessId)
    {
        if (!getProcessId(pipe, out var processId)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return checked((int)processId);
    }

    private delegate bool PipeProcessIdReader(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeServerProcessId", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
