namespace ScreenRecorder.Core;

public static class CaptureText
{
    public static string ShortError(string value) => value.Length > 180 ? value[..180] : value;

    public static string CaptureMethodName(ScreenshotMode mode) => mode switch
    {
        ScreenshotMode.Full => "ディスプレイ全体",
        ScreenshotMode.Region => "範囲指定",
        ScreenshotMode.Window => "ウィンドウ指定",
        _ => mode.ToString()
    };

    public static string ShortPath(string value, int maximumLength) => value.Length > maximumLength
        ? $"…{value[^maximumLength..]}"
        : value;

    public static bool IsAlreadyExists(IOException exception) => (exception.HResult & 0xffff) is 80 or 183;
}
