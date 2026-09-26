using System.Diagnostics;
using System.Text;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class DiagnosticLogWriterTests
{
    [Fact]
    public void WritesAcceptedEntriesInOrderWhenStopped()
    {
        using var destination = new TestDestination();
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Info, "test-version");
        for (var index = 0; index < 500; index++) writer.Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, $"entry {index}");

        Assert.True(writer.Flush(2000));
        writer.Stop();

        var lines = destination.Lines;
        Assert.Equal(500, lines.Count);
        Assert.Equal(Enumerable.Range(0, 500).Select(index => $"entry {index}"), lines.Select(line => line[(line.IndexOf("entry ", StringComparison.Ordinal))..]));
        Assert.Equal(0, writer.GetStats().TotalDropped);
    }

    [Fact]
    public async Task CallsDoNotWaitForAnActiveWrite()
    {
        using var destination = new TestDestination { HoldAppend = true };
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Info, "test-version");
        Assert.True(writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "first"));
        Assert.True(destination.AppendEntered.Wait(TimeSpan.FromSeconds(2)));

        var loggingCalls = Task.Run(() =>
        {
            for (var index = 0; index < 20; index++) writer.Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, $"entry {index}");
        });
        var completed = await Task.WhenAny(loggingCalls, Task.Delay(TimeSpan.FromSeconds(2)));
        destination.ReleaseAppend();
        await loggingCalls.WaitAsync(TimeSpan.FromSeconds(2));
        writer.Stop();

        Assert.Same(loggingCalls, completed);
        Assert.Equal(21, destination.Lines.Count);
    }

    [Fact]
    public void OverflowDropsNewestEntriesAndReportsTheCount()
    {
        using var destination = new TestDestination { HoldOpen = true };
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Info, "test-version");
        Assert.True(destination.OpenEntered.Wait(TimeSpan.FromSeconds(2)));

        for (var index = 0; index < DiagnosticLogWriter.MaximumQueuedEntries; index++) writer.Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, "normal");
        for (var index = 0; index < DiagnosticLogWriter.ErrorReserveEntries; index++) writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "error");
        for (var index = 0; index < 5; index++) writer.Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, "overflow");
        for (var index = 0; index < 3; index++) writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "error overflow");

        var stats = writer.GetStats();
        Assert.Equal(DiagnosticLogWriter.MaximumQueuedEntries + DiagnosticLogWriter.ErrorReserveEntries, stats.Queued);
        Assert.Equal(8, stats.PendingDropped);
        Assert.Equal(8, stats.TotalDropped);
        destination.ReleaseOpen();
        writer.Stop();

        Assert.Equal(stats.Queued + 1, destination.Lines.Count);
        Assert.Contains("WARN  [app] 診断ログの記録を 8 件破棄しました", destination.Lines[0]);
        Assert.DoesNotContain("overflow", destination.Lines);
    }

    [Fact]
    public void NormalAndErrorQueuesRespectTheirIndependentByteLimits()
    {
        using var destination = new TestDestination { HoldOpen = true };
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Info, "test-version");
        Assert.True(destination.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        var normalMessage = new string('n', 40_000);
        var acceptedNormal = 0;
        while (writer.Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, normalMessage)) acceptedNormal++;
        Assert.InRange(acceptedNormal, 1, DiagnosticLogWriter.MaximumQueuedEntries - 1);

        var errorMessage = new string('e', 30_000);
        Assert.True(writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, errorMessage));
        Assert.True(writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, errorMessage));
        Assert.False(writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, errorMessage));
        var stats = writer.GetStats();
        Assert.Equal(acceptedNormal + 2, stats.Queued);
        Assert.True(stats.QueuedBytes <= DiagnosticLogWriter.MaximumQueuedBytes + DiagnosticLogWriter.ErrorReserveBytes);

        destination.ReleaseOpen();
        writer.Stop();
    }

    [Fact]
    public void FailedWritesAreDiscardedAndCounted()
    {
        using var destination = new TestDestination { AppendResult = false };
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Info, "test-version");
        writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "failed write");

        Assert.True(writer.Flush(2000));
        var stats = writer.GetStats();
        writer.Stop();

        Assert.True(stats.WriteFailures > 0);
        Assert.True(stats.TotalDropped > 0);
        Assert.Empty(destination.Lines);
    }

    [Fact]
    public async Task FlushReturnsWhenItsTimeLimitExpires()
    {
        using var destination = new TestDestination { HoldAppend = true };
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Info, "test-version");
        writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "slow write");
        Assert.True(destination.AppendEntered.Wait(TimeSpan.FromSeconds(2)));

        var flushed = await Task.Run(() => writer.Flush(100)).WaitAsync(TimeSpan.FromSeconds(2));
        destination.ReleaseAppend();
        writer.Stop();

        Assert.False(flushed);
    }

    [Fact]
    public void StopReturnsAfterItsBoundAndWriterClosesWhenBlockedIoFinishes()
    {
        using var destination = new TestDestination { HoldAppend = true };
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Info, "test-version");
        writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "slow write");
        Assert.True(destination.AppendEntered.Wait(TimeSpan.FromSeconds(2)));
        var timer = Stopwatch.StartNew();

        writer.Stop();
        var elapsed = timer.Elapsed;
        destination.ReleaseAppend();

        Assert.True(elapsed < TimeSpan.FromMilliseconds(DiagnosticLogWriter.StopWaitMilliseconds + 500));
        Assert.True(destination.Closed.Wait(TimeSpan.FromSeconds(5)));
        Assert.Single(destination.Lines);
    }

    [Fact]
    public void DisposeReturnsAfterItsBoundAndDetachedWriterOwnsItsState()
    {
        using var destination = new TestDestination { HoldAppend = true };
        var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Info, "test-version");
        writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "slow write");
        Assert.True(destination.AppendEntered.Wait(TimeSpan.FromSeconds(2)));
        var timer = Stopwatch.StartNew();

        writer.Dispose();
        var elapsed = timer.Elapsed;
        destination.ReleaseAppend();

        Assert.True(elapsed < TimeSpan.FromMilliseconds(DiagnosticLogWriter.StopWaitMilliseconds + 500));
        Assert.True(destination.Closed.Wait(TimeSpan.FromSeconds(5)));
        Assert.Single(destination.Lines);
    }

    [Fact]
    public void RestartAfterStopTimeoutKeepsOldAndNewWritersSeparate()
    {
        using var slow = new TestDestination { HoldAppend = true };
        using var fresh = new TestDestination();
        var destinations = new Queue<IDiagnosticLogDestination>([slow, fresh]);
        using var writer = new DiagnosticLogWriter(() => destinations.Dequeue());
        writer.Start("old", DiagnosticLogLevel.Info, "old-version");
        writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "old entry");
        Assert.True(slow.AppendEntered.Wait(TimeSpan.FromSeconds(2)));
        writer.Stop();

        writer.Start("new", DiagnosticLogLevel.Info, "new-version");
        for (var index = 0; index < 20; index++) writer.Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, $"new entry {index}");
        Assert.True(writer.Flush(2000));
        slow.ReleaseAppend();
        Assert.True(slow.Closed.Wait(TimeSpan.FromSeconds(5)));
        writer.Stop();

        Assert.Contains(slow.Lines, line => line.EndsWith("old entry", StringComparison.Ordinal));
        Assert.Equal(20, fresh.Lines.Count);
        Assert.All(fresh.Lines, line => Assert.DoesNotContain("old entry", line));
    }

    [Fact]
    public void LevelChangeDuringCreateDoesNotOpenAnotherFile()
    {
        using var destination = new TestDestination { HoldOpen = true };
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Warn, "test-version");
        Assert.True(destination.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        writer.SetLevel(DiagnosticLogLevel.Debug);
        destination.ReleaseOpen();
        writer.Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, "after");

        Assert.True(writer.Flush(2000));
        writer.Stop();

        Assert.Equal(1, destination.OpenCount);
        Assert.Contains(destination.Lines, line => line.EndsWith("after", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultDestinationCreatesUtf8WithoutBomAndUsesCrlf()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ScreenRecorder-DiagnosticLog-{Guid.NewGuid():N}");
        try
        {
            using var writer = new DiagnosticLogWriter();
            writer.Start(directory, DiagnosticLogLevel.Info, "test-version");
            writer.Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, "entry");
            Assert.True(writer.Flush(2000));
            writer.Stop();

            var file = Assert.Single(Directory.GetFiles(directory, "*.log"));
            var bytes = File.ReadAllBytes(file);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.Contains("test-version", text);
            Assert.Contains("記録レベル: info\r\n", text);
            Assert.Contains("[app] entry\r\n", text);
            Assert.Contains("外部へ送信されません", text);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SilentLevelDoesNotTouchDestinationAndRaisingItOpensImmediately()
    {
        using var destination = new TestDestination();
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Silent, "test-version");
        Assert.False(writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "hidden"));
        Assert.True(writer.Flush(100));
        Assert.Equal(0, destination.OpenCount);

        writer.SetLevel(DiagnosticLogLevel.Info);
        Assert.True(writer.Flush(2000));
        writer.Stop();

        Assert.Equal(1, destination.OpenCount);
        Assert.Contains(destination.Lines, line => line.Contains("記録レベルを", StringComparison.Ordinal));
    }

    [Fact]
    public void LoweringFromInfoToSilentRecordsTheChangeBeforeStopping()
    {
        using var destination = new TestDestination();
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Info, "test-version");

        writer.SetLevel(DiagnosticLogLevel.Silent);
        Assert.False(writer.Write(DiagnosticLogLevel.Error, DiagnosticLogTags.App, "hidden"));
        Assert.True(writer.Flush(2000));
        writer.Stop();

        var line = Assert.Single(destination.Lines);
        Assert.Contains("INFO  [app] 記録レベルを", line);
    }

    [Fact]
    public void RaisingFromErrorOnlyToInfoRecordsTheChangeOnceAtTheNewLevel()
    {
        using var destination = new TestDestination();
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Error, "test-version");

        writer.SetLevel(DiagnosticLogLevel.Info);
        Assert.True(writer.Flush(2000));
        writer.Stop();

        var line = Assert.Single(destination.Lines);
        Assert.Contains("INFO  [app] 記録レベルを", line);
    }

    [Theory]
    [InlineData(DiagnosticLogLevel.Warn)]
    [InlineData(DiagnosticLogLevel.Silent)]
    public void ChangeBetweenLevelsThatDropInfoLeavesNoLine(DiagnosticLogLevel newLevel)
    {
        using var destination = new TestDestination();
        using var writer = new DiagnosticLogWriter(() => destination);
        writer.Start("test", DiagnosticLogLevel.Error, "test-version");

        writer.SetLevel(newLevel);
        Assert.True(writer.Flush(2000));
        writer.Stop();

        Assert.Empty(destination.Lines);
    }

    private sealed class TestDestination : IDiagnosticLogDestination, IDisposable
    {
        private readonly ManualResetEventSlim _openGate = new(true);
        private readonly ManualResetEventSlim _appendGate = new(true);
        private readonly ManualResetEventSlim _openEntered = new(false);
        private readonly ManualResetEventSlim _appendEntered = new(false);
        private readonly ManualResetEventSlim _closed = new(false);
        private readonly object _gate = new();
        private readonly List<string> _lines = [];
        private readonly List<string> _headerLines = [];
        private int _openCount;

        public bool HoldOpen
        {
            set { if (value) _openGate.Reset(); else _openGate.Set(); }
        }

        public bool HoldAppend
        {
            set { if (value) _appendGate.Reset(); else _appendGate.Set(); }
        }

        public bool AppendResult { get; set; } = true;
        public int OpenCount { get { lock (_gate) return _openCount; } }
        public IReadOnlyList<string> Lines { get { lock (_gate) return _lines.ToArray(); } }
        public IReadOnlyList<string> HeaderLines { get { lock (_gate) return _headerLines.ToArray(); } }
        public ManualResetEventSlim OpenEntered => _openEntered;
        public ManualResetEventSlim AppendEntered => _appendEntered;
        public ManualResetEventSlim Closed => _closed;

        public bool Open(string directory, IReadOnlyList<string> headerLines)
        {
            _openEntered.Set();
            if (!_openGate.Wait(TimeSpan.FromSeconds(15))) return false;
            lock (_gate)
            {
                _openCount++;
                _headerLines.AddRange(headerLines);
            }
            return true;
        }

        public bool Append(IReadOnlyList<string> lines)
        {
            _appendEntered.Set();
            if (!_appendGate.Wait(TimeSpan.FromSeconds(15))) return false;
            if (!AppendResult) return false;
            lock (_gate) _lines.AddRange(lines);
            return true;
        }

        public void Close() => _closed.Set();

        public void ReleaseOpen() => _openGate.Set();

        public void ReleaseAppend() => _appendGate.Set();

        public void Dispose()
        {
            _openGate.Set();
            _appendGate.Set();
            _openGate.Dispose();
            _appendGate.Dispose();
            _openEntered.Dispose();
            _appendEntered.Dispose();
            _closed.Dispose();
        }
    }
}
