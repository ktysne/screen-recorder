namespace ScreenRecorder.Core;

public static class DiagnosticLogTags
{
    public const string App = "app";
    public const string Hotkey = "hotkey";
    public const string Capture = "capture";
    public const string Record = "record";
    public const string Audio = "audio";
    public const string Convert = "convert";
    public const string Update = "update";
}

public static class DiagnosticLog
{
    private static readonly DiagnosticLogWriter Writer = new();

    public static string LogsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenRecorder", "logs");

    public static void Start(DiagnosticLogLevel level, string appVersion) => Writer.Start(LogsDirectory, level, appVersion);

    public static void Stop() => Writer.Stop();

    public static void SetLevel(DiagnosticLogLevel level) => Writer.SetLevel(level);

    public static bool Flush(int timeoutMilliseconds = DiagnosticLogWriter.FlushWaitMilliseconds) => Writer.Flush(timeoutMilliseconds);

    public static void Error(string tag, string message) => Writer.Write(DiagnosticLogLevel.Error, tag, message);

    public static void Warn(string tag, string message) => Writer.Write(DiagnosticLogLevel.Warn, tag, message);

    public static void Info(string tag, string message) => Writer.Write(DiagnosticLogLevel.Info, tag, message);

    public static void Debug(string tag, string message) => Writer.Write(DiagnosticLogLevel.Debug, tag, message);
}
