using System.Diagnostics;
using System.Text;

namespace ScreenRecorder.Core;

public interface IDiagnosticLogDestination
{
    bool Open(string directory, IReadOnlyList<string> headerLines);
    bool Append(IReadOnlyList<string> lines);
    void Close();
}

public sealed record DiagnosticLogWriterStats(
    int Queued,
    long QueuedBytes,
    long PendingDropped,
    long TotalDropped,
    long Written,
    long WriteFailures);

public sealed class DiagnosticLogWriter : IDisposable
{
    public const int FlushIntervalMilliseconds = 500;
    public const int FlushEntryCount = 64;
    public const int MaximumQueuedEntries = 4096;
    public const long MaximumQueuedBytes = 1024 * 1024;
    public const int ErrorReserveEntries = 64;
    public const long ErrorReserveBytes = 64 * 1024;
    public const int StopWaitMilliseconds = 2000;
    public const int FlushWaitMilliseconds = 300;

    private readonly object _ownerGate = new();
    private readonly Func<IDiagnosticLogDestination> _destinationFactory;
    private WriterState? _state;
    private Thread? _thread;
    private int _level = (int)DiagnosticLogLevels.Default;
    private string? _logsDirectory;

    public DiagnosticLogWriter(Func<IDiagnosticLogDestination>? destinationFactory = null) =>
        _destinationFactory = destinationFactory ?? (() => new FileDiagnosticLogDestination());

    public string? LogsDirectory
    {
        get
        {
            lock (_ownerGate) return _logsDirectory;
        }
    }

    public DiagnosticLogLevel Level => (DiagnosticLogLevel)Volatile.Read(ref _level);

    public void Start(string logsDirectory, DiagnosticLogLevel initialLevel, string appVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(appVersion);
        Stop();

        var state = new WriterState(_destinationFactory(), logsDirectory, appVersion, initialLevel)
        {
            OpenRequested = initialLevel != DiagnosticLogLevel.Silent
        };
        var thread = new Thread(() => RunWriter(state))
        {
            IsBackground = true,
            Name = "ScreenRecorder DiagnosticLog"
        };

        lock (_ownerGate)
        {
            _logsDirectory = logsDirectory;
            _state = state;
            _thread = thread;
            Volatile.Write(ref _level, (int)initialLevel);
            thread.Start();
        }
    }

    public void Stop()
    {
        WriterState? state;
        Thread? thread;
        lock (_ownerGate)
        {
            state = _state;
            thread = _thread;
            _thread = null;
        }

        if (state is null || thread is null) return;
        lock (state.Gate)
        {
            state.Started = false;
            state.ExitRequested = true;
            state.WakeRequested = true;
            Monitor.PulseAll(state.Gate);
        }

        if (!state.Exited.Wait(StopWaitMilliseconds)) return;
        if (thread.IsAlive) thread.Join(0);
    }

    public bool Write(DiagnosticLogLevel level, string tag, string message)
    {
        var threshold = Level;
        if (!threshold.ShouldRecord(level)) return false;

        WriterState? state;
        lock (_ownerGate) state = _state;
        if (state is null) return false;

        var line = DiagnosticLogFormatting.FormatLine(DateTime.Now, level, tag, message);
        var byteCount = Encoding.UTF8.GetByteCount(line);
        var isError = level == DiagnosticLogLevel.Error;
        lock (state.Gate)
        {
            if (!state.Started) return false;

            var count = isError ? state.ErrorQueuedCount : state.NormalQueuedCount;
            var bytes = isError ? state.ErrorQueuedBytes : state.NormalQueuedBytes;
            var countLimit = isError ? ErrorReserveEntries : MaximumQueuedEntries;
            var byteLimit = isError ? ErrorReserveBytes : MaximumQueuedBytes;
            if (count >= countLimit || bytes + byteCount > byteLimit)
            {
                state.PendingDropped++;
                state.TotalDropped++;
                if (isError)
                {
                    state.WakeRequested = true;
                    Monitor.PulseAll(state.Gate);
                }
                return false;
            }

            state.Queue.Enqueue(new QueuedLine(line, byteCount, isError));
            if (isError)
            {
                state.ErrorQueuedCount++;
                state.ErrorQueuedBytes += byteCount;
            }
            else
            {
                state.NormalQueuedCount++;
                state.NormalQueuedBytes += byteCount;
            }
            state.EnqueuedCount++;
            if (state.Queue.Count >= FlushEntryCount || isError)
            {
                state.WakeRequested = true;
                Monitor.PulseAll(state.Gate);
            }
            return true;
        }
    }

    public void SetLevel(DiagnosticLogLevel newLevel)
    {
        var oldLevel = Level;
        if (oldLevel == newLevel) return;

        var message = $"記録レベルを「{DisplayName(oldLevel)}」から「{DisplayName(newLevel)}」に変更しました。";
        var recordedBeforeChange = Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, message);

        WriterState? state;
        lock (_ownerGate) state = _state;
        if (state is not null)
        {
            lock (state.Gate)
            {
                state.Level = newLevel;
                if (oldLevel == DiagnosticLogLevel.Silent && newLevel != DiagnosticLogLevel.Silent && state.Started && !state.DestinationOpen && !state.DestinationOpening)
                    state.OpenRequested = true;
                state.WakeRequested = true;
                Monitor.PulseAll(state.Gate);
            }
        }

        Volatile.Write(ref _level, (int)newLevel);
        if (!recordedBeforeChange)
            Write(DiagnosticLogLevel.Info, DiagnosticLogTags.App, message);
    }

    public bool Flush(int timeoutMilliseconds = FlushWaitMilliseconds)
    {
        WriterState? state;
        lock (_ownerGate) state = _state;
        if (state is null) return true;

        long target;
        lock (state.Gate)
        {
            target = state.EnqueuedCount;
            state.WakeRequested = true;
            Monitor.PulseAll(state.Gate);
        }

        var timer = Stopwatch.StartNew();
        lock (state.Gate)
        {
            while (state.ProcessedCount < target && !state.Exited.IsSet)
            {
                var remaining = timeoutMilliseconds - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0 || !Monitor.Wait(state.Gate, remaining)) break;
            }
            return state.ProcessedCount >= target || state.Exited.IsSet;
        }
    }

    public DiagnosticLogWriterStats GetStats()
    {
        WriterState? state;
        lock (_ownerGate) state = _state;
        if (state is null) return new(0, 0, 0, 0, 0, 0);

        lock (state.Gate)
            return new(state.Queue.Count, state.NormalQueuedBytes + state.ErrorQueuedBytes, state.PendingDropped, state.TotalDropped, state.Written, state.WriteFailures);
    }

    public void Dispose() => Stop();

    private static string DisplayName(DiagnosticLogLevel level) => level switch
    {
        DiagnosticLogLevel.Silent => "記録しない",
        DiagnosticLogLevel.Error => "エラーのみ",
        DiagnosticLogLevel.Warn => "警告まで",
        DiagnosticLogLevel.Debug => "詳細",
        _ => "情報まで"
    };

    private static void RunWriter(WriterState state)
    {
        try
        {
            while (true)
            {
                DrainOnce(state);
                lock (state.Gate)
                {
                    if (state.ExitRequested && state.Queue.Count == 0) break;
                    if (!state.WakeRequested) Monitor.Wait(state.Gate, FlushIntervalMilliseconds);
                    state.WakeRequested = false;
                }
            }

            DrainOnce(state);
        }
        finally
        {
            try { state.Destination.Close(); }
            catch { lock (state.Gate) state.WriteFailures++; }
            lock (state.Gate)
            {
                state.Exited.Set();
                Monitor.PulseAll(state.Gate);
            }
        }
    }

    private static void DrainOnce(WriterState state)
    {
        QueuedLine[] batch;
        long droppedNow;
        long processedTo;
        bool wantOpen;
        IReadOnlyList<string> header = [];
        lock (state.Gate)
        {
            batch = state.Queue.ToArray();
            state.Queue.Clear();
            state.NormalQueuedCount = 0;
            state.NormalQueuedBytes = 0;
            state.ErrorQueuedCount = 0;
            state.ErrorQueuedBytes = 0;
            droppedNow = state.PendingDropped;
            state.PendingDropped = 0;
            processedTo = state.EnqueuedCount;
            wantOpen = state.OpenRequested && !state.DestinationOpen;
            state.OpenRequested = false;
            if (wantOpen)
            {
                state.DestinationOpening = true;
                header = DiagnosticLogFormatting.CreateHeader(state.AppVersion, state.Level);
            }
        }

        if (wantOpen)
        {
            var opened = false;
            try { opened = state.Destination.Open(state.Directory, header); }
            catch { }
            lock (state.Gate)
            {
                state.DestinationOpening = false;
                state.DestinationOpen = opened;
                if (!opened)
                {
                    state.Started = false;
                    state.WriteFailures++;
                }
            }
        }

        if (batch.Length > 0 || droppedNow > 0)
        {
            var canWrite = false;
            lock (state.Gate) canWrite = state.DestinationOpen;

            var lines = new List<string>(batch.Length + (droppedNow > 0 ? 1 : 0));
            if (droppedNow > 0)
                lines.Add(DiagnosticLogFormatting.FormatLine(DateTime.Now, DiagnosticLogLevel.Warn, DiagnosticLogTags.App, $"診断ログの記録を {droppedNow} 件破棄しました（書き出しが追いつかなかったため）。"));
            lines.AddRange(batch.Select(entry => entry.Line));

            var written = false;
            try { written = canWrite && state.Destination.Append(lines); }
            catch { }
            lock (state.Gate)
            {
                if (written)
                {
                    state.Written += batch.Length;
                }
                else
                {
                    state.WriteFailures++;
                    state.PendingDropped += batch.Length + droppedNow;
                    state.TotalDropped += batch.Length;
                }
            }
        }

        lock (state.Gate)
        {
            state.ProcessedCount = processedTo;
            Monitor.PulseAll(state.Gate);
        }
    }

    private sealed record QueuedLine(string Line, int ByteCount, bool IsError);

    private sealed class WriterState(IDiagnosticLogDestination destination, string directory, string appVersion, DiagnosticLogLevel level)
    {
        public object Gate { get; } = new();
        public ManualResetEventSlim Exited { get; } = new(false);
        public IDiagnosticLogDestination Destination { get; } = destination;
        public string Directory { get; } = directory;
        public string AppVersion { get; } = appVersion;
        public DiagnosticLogLevel Level { get; set; } = level;
        public Queue<QueuedLine> Queue { get; } = new();
        public int NormalQueuedCount { get; set; }
        public long NormalQueuedBytes { get; set; }
        public int ErrorQueuedCount { get; set; }
        public long ErrorQueuedBytes { get; set; }
        public long PendingDropped { get; set; }
        public long TotalDropped { get; set; }
        public long Written { get; set; }
        public long WriteFailures { get; set; }
        public long EnqueuedCount { get; set; }
        public long ProcessedCount { get; set; }
        public bool Started { get; set; } = true;
        public bool OpenRequested { get; set; }
        public bool DestinationOpen { get; set; }
        public bool DestinationOpening { get; set; }
        public bool WakeRequested { get; set; }
        public bool ExitRequested { get; set; }
    }
}

internal sealed class FileDiagnosticLogDestination : IDiagnosticLogDestination
{
    private string? _filePath;

    public bool Open(string directory, IReadOnlyList<string> headerLines)
    {
        _filePath = null;
        Directory.CreateDirectory(directory);

        var existing = Directory.EnumerateFiles(directory)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>();
        foreach (var name in DiagnosticLogFormatting.SelectFilesToDelete(existing, DiagnosticLogFormatting.MaximumFiles - 1))
        {
            try { File.Delete(Path.Combine(directory, name)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var path = Path.Combine(directory, DiagnosticLogFormatting.MakeFileName(DateTime.Now));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            WriteLines(stream, headerLines);
            stream.Flush();
        }
        _filePath = path;
        return true;
    }

    public bool Append(IReadOnlyList<string> lines)
    {
        if (_filePath is null) return false;
        if (lines.Count == 0) return true;

        using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        WriteLines(stream, lines);
        stream.Flush();
        return true;
    }

    public void Close() => _filePath = null;

    private static void WriteLines(Stream stream, IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            stream.Write(bytes);
        }
    }
}
