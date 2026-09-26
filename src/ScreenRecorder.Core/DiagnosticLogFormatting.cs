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

        TryParseTimestamp(latest, out var latestTimestamp);
        return MakeFileName(latestTimestamp.AddMilliseconds(1));
    }

    // 日時として読めない名前は、次のログの名前を決める基準にも削除の対象にもしない。
    public static bool IsLogFileName(string? name) => TryParseTimestamp(name, out _);

    private static bool TryParseTimestamp(string? name, out DateTime timestamp)
    {
        timestamp = default;
        if (name is null || name.Length != FilePrefix.Length + TimestampFormat.Length + FileSuffix.Length) return false;
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) || !name.EndsWith(FileSuffix, StringComparison.Ordinal)) return false;

        var body = name.Substring(FilePrefix.Length, TimestampFormat.Length);
        return body.All(character => character is '-' or (>= '0' and <= '9'))
            && DateTime.TryParseExact(body, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
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
