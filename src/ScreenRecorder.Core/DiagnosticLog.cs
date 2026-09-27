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
    public const string RecordWorker = "record-worker";
}

public static class DiagnosticLog
{
    private static readonly DiagnosticLogWriter Writer = new();
    private static ForwardingDestination? _forwardingDestination;

    private sealed record ForwardingDestination(
        DiagnosticLogLevel MinimumLevel,
        Action<DiagnosticLogLevel, string, string> Write);

    public static string LogsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenRecorder", "logs");

    public static DiagnosticLogLevel Level => Writer.Level;

    public static void Start(DiagnosticLogLevel level, string appVersion) => Writer.Start(LogsDirectory, level, appVersion);

    public static void Stop() => Writer.Stop();

    public static void SetLevel(DiagnosticLogLevel level) => Writer.SetLevel(level);

    public static bool Flush(int timeoutMilliseconds = DiagnosticLogWriter.FlushWaitMilliseconds) => Writer.Flush(timeoutMilliseconds);

    public static void SetForwarder(DiagnosticLogLevel minimumLevel, Action<DiagnosticLogLevel, string, string> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (!Enum.IsDefined(minimumLevel)) throw new ArgumentOutOfRangeException(nameof(minimumLevel));
        Volatile.Write(ref _forwardingDestination, new ForwardingDestination(minimumLevel, write));
    }

    public static void ClearForwarder() => Volatile.Write(ref _forwardingDestination, null);

    public static void Error(string tag, string message) => Write(DiagnosticLogLevel.Error, tag, message);

    public static void Warn(string tag, string message) => Write(DiagnosticLogLevel.Warn, tag, message);

    public static void Info(string tag, string message) => Write(DiagnosticLogLevel.Info, tag, message);

    public static void Debug(string tag, string message) => Write(DiagnosticLogLevel.Debug, tag, message);

    private static void Write(DiagnosticLogLevel level, string tag, string message)
    {
        var forwarding = Volatile.Read(ref _forwardingDestination);
        if (forwarding is null)
        {
            Writer.Write(level, tag, message);
            return;
        }

        if (forwarding.MinimumLevel.ShouldRecord(level)) forwarding.Write(level, tag, message);
    }
}
