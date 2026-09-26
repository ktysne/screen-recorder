namespace ScreenRecorder.Core;

public enum DiagnosticLogLevel
{
    Silent,
    Error,
    Warn,
    Info,
    Debug
}

public static class DiagnosticLogLevels
{
    public const DiagnosticLogLevel Default = DiagnosticLogLevel.Info;

    public static string ToSettingName(this DiagnosticLogLevel level) => level switch
    {
        DiagnosticLogLevel.Silent => "silent",
        DiagnosticLogLevel.Error => "error",
        DiagnosticLogLevel.Warn => "warn",
        DiagnosticLogLevel.Debug => "debug",
        _ => "info"
    };

    public static DiagnosticLogLevel FromSettingName(string? value) => value switch
    {
        "silent" => DiagnosticLogLevel.Silent,
        "error" => DiagnosticLogLevel.Error,
        "warn" => DiagnosticLogLevel.Warn,
        "debug" => DiagnosticLogLevel.Debug,
        "info" => DiagnosticLogLevel.Info,
        _ => Default
    };

    public static bool ShouldRecord(this DiagnosticLogLevel threshold, DiagnosticLogLevel entry) =>
        threshold != DiagnosticLogLevel.Silent && entry != DiagnosticLogLevel.Silent && entry <= threshold;
}
