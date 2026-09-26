namespace ScreenRecorder.Core;

public static class CaptureText
{
    private const int ErrorDetailLength = 180;
    private const int PathDetailLength = 150;

    public static string ErrorDetail(string message) => message.Length > ErrorDetailLength ? message[..ErrorDetailLength] : message;

    public static string CaptureMethodName(ScreenshotMode mode) => mode switch
    {
        ScreenshotMode.Full => "ディスプレイ全体",
        ScreenshotMode.Region => "範囲指定",
        ScreenshotMode.Window => "ウィンドウ指定",
        _ => mode.ToString()
    };

    public static string PathDetail(string path) => path.Length > PathDetailLength
        ? $"…{path[^PathDetailLength..]}"
        : path;

    public static bool IsAlreadyExists(IOException exception) => (exception.HResult & 0xffff) is 80 or 183;
}
