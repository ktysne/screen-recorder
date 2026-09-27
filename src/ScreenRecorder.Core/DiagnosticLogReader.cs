using System.Globalization;
using System.Text;

namespace ScreenRecorder.Core;

/// <summary>診断ログの一覧に表示するファイル情報です。</summary>
public sealed record DiagnosticLogFileInfo(
    string Name,
    string Path,
    DateTimeOffset StartedAt,
    long SizeBytes,
    string? HeaderVersion);

/// <summary>診断ログの 1 行を、解析結果と元の文字列で表します。</summary>
public sealed record DiagnosticLogEntry(
    DateTimeOffset? Timestamp,
    string? Level,
    string? Tag,
    string? Message,
    string RawLine);

/// <summary>診断ログのヘッダーと行をまとめます。</summary>
public sealed record DiagnosticLogDocument(
    IReadOnlyList<string> Header,
    string? HeaderVersion,
    string? HeaderLevel,
    IReadOnlyList<DiagnosticLogEntry> Entries);

/// <summary>診断ログを読み取り専用で列挙し、書式に合わない行も保持して解析します。</summary>
public static class DiagnosticLogReader
{
    private const string HeaderTitle = "=== ScreenRecorder 診断ログ ===";
    private const string VersionPrefix = "バージョン: ";
    private const string LevelPrefix = "記録レベル: ";
    private const int HeaderLineCount = 5;
    private const string FileTimestampFormat = "yyyyMMdd-HHmmss-fff";
    private const string EntryTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>標準の名前形式に一致するログファイルを新しい順に返します。</summary>
    public static IReadOnlyList<DiagnosticLogFileInfo> ListFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        return paths
            .Where(path => DiagnosticLogFormatting.IsLogFileName(Path.GetFileName(path)))
            .Select(path =>
            {
                var file = new FileInfo(path);
                var header = ReadHeader(path);
                return new DiagnosticLogFileInfo(
                    file.Name,
                    file.FullName,
                    ParseStartedAt(file.Name),
                    file.Length,
                    header.Version);
            })
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>ログファイルを共有読み取りで開き、全行を返します。</summary>
    public static DiagnosticLogDocument ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var lines = new List<string>();
        using (var reader = OpenReader(path))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
                lines.Add(line);
        }

        var (header, version, level, entriesStart) = ExtractHeader(lines);
        var entries = lines.Skip(entriesStart).Select(ParseEntry).ToArray();
        return new DiagnosticLogDocument(header, version, level, entries);
    }

    private static (string[] Lines, string? Version, string? Level) ReadHeader(string path)
    {
        using var reader = OpenReader(path);
        var lines = new List<string>(HeaderLineCount);
        for (var i = 0; i < HeaderLineCount; i++)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            lines.Add(line);
        }
        var parsed = ExtractHeader(lines);
        return (parsed.Header, parsed.Version, parsed.Level);
    }

    private static StreamReader OpenReader(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    }

    private static (string[] Header, string? Version, string? Level, int EntriesStart) ExtractHeader(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || lines[0] != HeaderTitle) return ([], null, null, 0);

        var headerCount = Math.Min(lines.Count, HeaderLineCount);
        var header = lines.Take(headerCount).ToArray();
        var version = header.FirstOrDefault(line => line.StartsWith(VersionPrefix, StringComparison.Ordinal))?[VersionPrefix.Length..];
        var level = header.FirstOrDefault(line => line.StartsWith(LevelPrefix, StringComparison.Ordinal))?[LevelPrefix.Length..];
        return (header, version, level, headerCount);
    }

    private static DateTimeOffset ParseStartedAt(string name)
    {
        var timestamp = name["screen-recorder-".Length..^".log".Length];
        var local = DateTime.ParseExact(timestamp, FileTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
        return new DateTimeOffset(local);
    }

    private static DiagnosticLogEntry ParseEntry(string line)
    {
        const int timestampLength = 23;
        const int levelStart = timestampLength + 1;
        const int tagStart = levelStart + 7;
        if (line.Length < tagStart + 2
            || line[timestampLength] != ' '
            || line[levelStart + 5] != ' '
            || line[levelStart + 6] != '[')
            return Raw(line);

        if (!DateTime.TryParseExact(
                line[..timestampLength],
                EntryTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var localTimestamp))
            return Raw(line);

        var closeBracket = line.IndexOf("] ", tagStart, StringComparison.Ordinal);
        if (closeBracket < tagStart) return Raw(line);

        var rawLevel = line.Substring(levelStart, 5);
        var level = rawLevel.TrimEnd() switch
        {
            "ERROR" => "error",
            "WARN" => "warn",
            "INFO" => "info",
            "DEBUG" => "debug",
            _ => null
        };
        return new DiagnosticLogEntry(
            new DateTimeOffset(localTimestamp),
            level,
            line[tagStart..closeBracket],
            line[(closeBracket + 2)..],
            line);
    }

    private static DiagnosticLogEntry Raw(string line) => new(null, null, null, null, line);
}
