using System.Globalization;
using System.Text;

namespace ScreenRecorder.Core;

public static class DiagnosticLogFormatting
{
    public const int MaximumFiles = 10;
    private const string FilePrefix = "screen-recorder-";
    private const string FileSuffix = ".log";

    public static string FormatLine(DateTime timestamp, DiagnosticLogLevel level, string tag, string message)
    {
        var levelName = level switch
        {
            DiagnosticLogLevel.Error => "ERROR",
            DiagnosticLogLevel.Warn => "WARN ",
            DiagnosticLogLevel.Info => "INFO ",
            DiagnosticLogLevel.Debug => "DEBUG",
            _ => "-    "
        };

        return string.Create(CultureInfo.InvariantCulture, $"{timestamp:yyyy-MM-dd HH:mm:ss.fff} {levelName} [{Sanitize(tag)}] {Sanitize(message)}");
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var result = new StringBuilder(text.Length);
        foreach (var character in text)
            result.Append(char.IsControl(character) ? ' ' : character);

        return result.ToString().TrimEnd(' ');
    }

    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";

    public static string MakeFileName(DateTime timestamp) =>
        FilePrefix + timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture) + FileSuffix;

    /// <summary>既存のどのログよりも名前順で後になる、新しいログの名前を返す。</summary>
    /// <remarks>時計が戻ると現在時刻の名前が既存より前になり、次のローテーションで最新のログが消えるため、最新の名前の 1 ミリ秒後へ送る。</remarks>
    public static string MakeFileName(DateTime timestamp, IEnumerable<string> existingNames)
    {
        var candidate = MakeFileName(timestamp);
        var latest = existingNames.Where(IsLogFileName).Max(StringComparer.Ordinal);
        if (latest is null || StringComparer.Ordinal.Compare(candidate, latest) > 0) return candidate;

        var latestTimestamp = latest.Substring(FilePrefix.Length, TimestampFormat.Length);
        return DateTime.TryParseExact(latestTimestamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? MakeFileName(parsed.AddMilliseconds(1))
            : candidate;
    }

    public static bool IsLogFileName(string? name)
    {
        if (name is null) return false;
        var expectedLength = FilePrefix.Length + 8 + 1 + 6 + 1 + 3 + FileSuffix.Length;
        if (name.Length != expectedLength || !name.StartsWith(FilePrefix, StringComparison.Ordinal) || !name.EndsWith(FileSuffix, StringComparison.Ordinal)) return false;

        var body = name.AsSpan(FilePrefix.Length, name.Length - FilePrefix.Length - FileSuffix.Length);
        for (var index = 0; index < body.Length; index++)
        {
            if (index is 8 or 15)
            {
                if (body[index] != '-') return false;
            }
            else if (body[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<string> SelectFilesToDelete(IEnumerable<string> names, int keepCount)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (keepCount < 0) throw new ArgumentOutOfRangeException(nameof(keepCount));

        var logs = names.Where(IsLogFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return logs.Length <= keepCount ? [] : logs[..(logs.Length - keepCount)];
    }

    public static IReadOnlyList<string> CreateHeader(string appVersion, DiagnosticLogLevel level) =>
    [
        "=== ScreenRecorder 診断ログ ===",
        $"バージョン: {Sanitize(appVersion)}",
        $"記録レベル: {level.ToSettingName()}",
        "このログはこの PC の中に保存され、利用者の操作なしに外部へ送信されません。",
        "記録しないもの: 撮影した画像、録画した映像と音声、クリップボードの内容、ウィンドウのタイトル、IP アドレス、利用統計。"
    ];
}
