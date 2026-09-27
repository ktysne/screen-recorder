using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class DiagnosticLogReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidAndMalformedLinesAreReturnedWithTheirRawText()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 27, 10, 20, 30)));
        var timestamp = new DateTime(2026, 9, 27, 10, 20, 30, 123);
        var formattedLine = DiagnosticLogFormatting.FormatLine(timestamp, DiagnosticLogLevel.Warn, "capture", "保存に失敗しました");
        File.WriteAllLines(path, DiagnosticLogFormatting.CreateHeader("1.2.3", DiagnosticLogLevel.Info).Append(formattedLine).Append("not a log entry"));

        var document = DiagnosticLogReader.ReadFile(path);

        Assert.Equal("1.2.3", document.HeaderVersion);
        Assert.Equal("info", document.HeaderLevel);
        Assert.Equal(2, document.Entries.Count);
        Assert.Equal(("warn", "capture", "保存に失敗しました"),
            (document.Entries[0].Level, document.Entries[0].Tag, document.Entries[0].Message));
        Assert.Equal(formattedLine, document.Entries[0].RawLine);
        Assert.Null(document.Entries[1].Timestamp);
        Assert.Equal("not a log entry", document.Entries[1].RawLine);
    }

    [Fact]
    public void FileCanBeReadWhileAnotherHandleKeepsItOpenForWriting()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, DiagnosticLogFormatting.MakeFileName(DateTime.Now));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        foreach (var line in DiagnosticLogFormatting.CreateHeader("1.2.3", DiagnosticLogLevel.Debug))
            writer.WriteLine(line);
        writer.WriteLine(DiagnosticLogFormatting.FormatLine(DateTime.Now, DiagnosticLogLevel.Info, "capture", "記録中"));
        writer.Flush();

        var document = DiagnosticLogReader.ReadFile(path);

        Assert.Single(document.Entries);
        Assert.Equal("記録中", document.Entries[0].Message);
    }

    [Fact]
    public void OnlyTheLastMatchingEntriesAreKeptWithTheTotalMatchCount()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 27, 10, 0, 0)));
        var levels = new[] { DiagnosticLogLevel.Error, DiagnosticLogLevel.Info, DiagnosticLogLevel.Warn, DiagnosticLogLevel.Debug, DiagnosticLogLevel.Warn };
        var lines = levels.Select((level, index) =>
            DiagnosticLogFormatting.FormatLine(new DateTime(2026, 9, 27, 10, 0, index), level, "app", $"行{index}"));
        File.WriteAllLines(path, DiagnosticLogFormatting.CreateHeader("1.2.3", DiagnosticLogLevel.Debug).Concat(lines));
        var warnRank = DiagnosticLogReader.GetSeverityRank("warn");

        var document = DiagnosticLogReader.ReadFile(path, entry => DiagnosticLogReader.GetSeverityRank(entry.Level) <= warnRank, lastCount: 2);

        Assert.Equal(3, document.MatchedCount);
        Assert.Equal(["行2", "行4"], document.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void FileListIncludesHeaderVersionAndSize()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 27, 10, 20, 30)));
        File.WriteAllLines(path, DiagnosticLogFormatting.CreateHeader("2.0.0", DiagnosticLogLevel.Info));

        var file = Assert.Single(DiagnosticLogReader.ListFiles(_directory));

        Assert.Equal("2.0.0", file.HeaderVersion);
        Assert.Equal(new FileInfo(path).Length, file.SizeBytes);
        Assert.Equal(path, file.Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
