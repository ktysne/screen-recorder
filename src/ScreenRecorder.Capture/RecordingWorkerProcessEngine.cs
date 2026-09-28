using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

public sealed class RecordingWorkerProcessEngine : IRecordingEngine
{
    private static readonly TimeSpan ForceTerminationWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReaderDrainWaitOnExit = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly string _executablePath;
    private readonly RecordingWorkerSession _session;
    private readonly DiagnosticLogLevel _workerLogLevel;
    private readonly Channel<RecordingWorkerMessage> _outgoing = Channel.CreateUnbounded<RecordingWorkerMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Queue<PendingEvent> _pendingEvents = new();
    private readonly SemaphoreSlim _eventSignal = new(0);
    private readonly CancellationTokenSource _connectionLifetime = new();
    private readonly TaskCompletionSource<bool> _forceTerminationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _stateChanged = NewStateSignal();
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private Task? _connectionTask;
    private Task? _readerTask;
    private Task? _writerTask;
    private Task? _standardOutputTask;
    private Task? _eventTask;
    private System.Threading.Timer? _timer;
    private Stopwatch? _startupStopwatch;
    private TimeSpan? _startupDuration;
    private TimeSpan? _recordingStartDuration;
    private long? _readyTimestamp;
    private bool _loggedRecordingStartDelay;
    private bool _started;
    private bool _eventQueueCompleted;
    private bool _communicationLost;
    private string _outputPath = string.Empty;
    private int _forceTerminationStarted;
    private int _disposeStarted;

    private sealed record PendingEvent(RecordingWorkerSessionEvent Event, RecordingTerminationOutcome Outcome);

    public RecordingWorkerProcessEngine(string executablePath, DiagnosticLogLevel workerLogLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
        _workerLogLevel = workerLogLevel;
        _session = new RecordingWorkerSession(workerLogLevel);
        _eventTask = Task.Run(DispatchEventsAsync);
    }

    public RecordingStartupTimings StartupTimings
    {
        get { lock (_gate) return new(_startupDuration, _recordingStartDuration); }
    }

    public event EventHandler<RecordingEngineStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<RecordingEngineCompletedEventArgs>? RecordingCompleted;
    public event EventHandler<RecordingEngineFailedEventArgs>? RecordingFailed;
    public event EventHandler<RecordingEngineWarningEventArgs>? RecordingWarning;
    public event EventHandler<RecordingEngineOperationFailedEventArgs>? OperationFailed;

    public void Start(RecordingStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pipeName = $"ScreenRecorder.RecordWorker.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var process = new Process
        {
            EnableRaisingEvents = true,
            StartInfo = CreateStartInfo(pipeName)
        };

        bool shouldForceTerminate;
        lock (_gate)
        {
            if (_started)
            {
                server.Dispose();
                process.Dispose();
                throw new InvalidOperationException("録画プロセスはすでに起動しています。");
            }

            _started = true;
            _outputPath = request.OutputPath;
            _pipe = server;
            _process = process;
            _startupStopwatch = Stopwatch.StartNew();
            process.Exited += HandleProcessExited;
            try
            {
                if (!process.Start()) throw new InvalidOperationException("録画プロセスを起動できませんでした。");
                _standardOutputTask = DrainStandardOutputAsync(process.StandardOutput);
            }
            catch
            {
                process.Exited -= HandleProcessExited;
                _process = null;
                _pipe = null;
                process.Dispose();
                server.Dispose();
                throw;
            }

            var now = DateTimeOffset.UtcNow;
            shouldForceTerminate = ApplyTransitionLocked(_session.OnProcessStarted(now, process.Id));
            shouldForceTerminate |= ApplyTransitionLocked(_session.RequestStart(request.ToWorkerStartData(), now));
            _timer = new System.Threading.Timer(AdvanceSessionTime, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        if (shouldForceTerminate) ForceTerminateWorker();
        _connectionTask = Task.Run(ConnectAndReadAsync);
    }

    public void Pause(long operationId) => RequestSession(session => session.RequestPause(operationId, DateTimeOffset.UtcNow));

    public void Resume(long operationId) => RequestSession(session => session.RequestResume(operationId, DateTimeOffset.UtcNow));

    public void Stop() => RequestSession(session => session.RequestStop(DateTimeOffset.UtcNow));

    public async Task<RecordingTerminationOutcome> WaitForTerminationAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var outcome = _session.TerminationOutcome;
                if (outcome != RecordingTerminationOutcome.Waiting) return outcome;
                changed = _stateChanged.Task;
            }

            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await changed.ConfigureAwait(false);
                continue;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero) return RecordingTerminationOutcome.TimedOut;
            if (await Task.WhenAny(changed, Task.Delay(remaining)).ConfigureAwait(false) != changed)
                return RecordingTerminationOutcome.TimedOut;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

        Process? process;
        bool shouldForceTerminate;
        lock (_gate)
        {
            process = _process;
            shouldForceTerminate = ApplyTransitionLocked(_session.RequestDispose(DateTimeOffset.UtcNow));
        }

        if (shouldForceTerminate) ForceTerminateWorker();
        if (process is not null)
        {
            try
            {
                Task.WhenAny(process.WaitForExitAsync(), _forceTerminationFinished.Task).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException) { }
        }

        if (_timer is not null) _timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Volatile.Read(ref _forceTerminationStarted) != 0)
            _forceTerminationFinished.Task.Wait(ForceTerminationWait + TimeSpan.FromSeconds(1));
        _connectionLifetime.Cancel();
        _outgoing.Writer.TryComplete();
        _pipe?.Dispose();
        try { _connectionTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        try { _readerTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        try { _writerTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        try { _standardOutputTask?.Wait(ReaderDrainWaitOnExit); }
        catch (AggregateException) { }
        lock (_gate)
        {
            _eventQueueCompleted = true;
            _eventSignal.Release();
        }
        try { _eventTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        _eventSignal.Dispose();
        _connectionLifetime.Dispose();
        process?.Dispose();
    }

    private ProcessStartInfo CreateStartInfo(string pipeName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("--record-worker");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(_workerLogLevel.ToSettingName());
        return startInfo;
    }

    private static async Task DrainStandardOutputAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { }
    }

    private async Task ConnectAndReadAsync()
    {
        NamedPipeServerStream? pipe;
        lock (_gate) pipe = _pipe;
        if (pipe is null) return;

        try
        {
            await pipe.WaitForConnectionAsync(_connectionLifetime.Token).ConfigureAwait(false);
            Task readerTask;
            lock (_gate)
            {
                _writerTask = Task.Run(WriteMessagesAsync);
                _readerTask = readerTask = Task.Run(ReadMessagesAsync);
            }
            await readerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, $"録画プロセスとの接続に失敗しました: {exception.Message}");
            HandleCommunicationLost();
        }
    }

    private async Task ReadMessagesAsync()
    {
        var pipe = _pipe;
        if (pipe is null) return;
        var lineReader = new RecordingWorkerLineReader();
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[4096];
        try
        {
            while (!_connectionLifetime.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), _connectionLifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    HandleCommunicationLost();
                    return;
                }

                foreach (var line in lineReader.Append(new string(buffer, 0, count)))
                {
                    if (line.IsTooLong)
                    {
                        DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, "録画プロセスから受信したメッセージが長すぎるため破棄しました。");
                        continue;
                    }

                    if (line.Line is null) continue;
                    var result = RecordingWorkerProtocol.Deserialize(line.Line);
                    if (!result.IsReadable)
                    {
                        DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, $"録画プロセスから受信したメッセージを解釈できませんでした: {result.Error}");
                        continue;
                    }
                    ReceiveMessage(result.Message!);
                }
            }
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested)
        {
        }
        catch (Exception) when (_connectionLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, $"録画プロセスからの受信に失敗しました: {exception.Message}");
            HandleCommunicationLost();
        }
    }

    private async Task WriteMessagesAsync()
    {
        var pipe = _pipe;
        if (pipe is null) return;
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_connectionLifetime.Token).ConfigureAwait(false))
            {
                var payload = RecordingWorkerProtocol.Serialize(message);
                await pipe.WriteAsync(payload, _connectionLifetime.Token).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { (byte)'\n' }, _connectionLifetime.Token).ConfigureAwait(false);
                await pipe.FlushAsync(_connectionLifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, $"録画プロセスへの送信に失敗しました: {exception.Message}");
            HandleCommunicationLost();
        }
    }

    private void ReceiveMessage(RecordingWorkerMessage message)
    {
        bool shouldForceTerminate;
        bool logStartupDelay = false;
        bool logRecordingStartDelay = false;
        TimeSpan startupDelay = default;
        TimeSpan recordingStartDelay = default;
        lock (_gate)
        {
            if (message is RecordingWorkerReadyMessage)
            {
                _startupDuration = _startupStopwatch?.Elapsed;
                _startupStopwatch?.Stop();
                logStartupDelay = _startupDuration is not null;
                startupDelay = _startupDuration ?? TimeSpan.Zero;
                _readyTimestamp = Stopwatch.GetTimestamp();
            }
            else if (message is RecordingWorkerStateMessage { State: RecordingWorkerRecordingState.Recording }
                && !_loggedRecordingStartDelay
                && _readyTimestamp is { } readyTimestamp)
            {
                _loggedRecordingStartDelay = true;
                logRecordingStartDelay = true;
                recordingStartDelay = Stopwatch.GetElapsedTime(readyTimestamp);
                _recordingStartDuration = recordingStartDelay;
            }

            shouldForceTerminate = ApplyTransitionLocked(_session.OnMessage(message, DateTimeOffset.UtcNow));
        }

        if (logStartupDelay)
            DiagnosticLog.Debug(DiagnosticLogTags.RecordWorker, $"録画プロセスの起動から準備完了まで: {startupDelay.TotalMilliseconds:0} ms");
        if (logRecordingStartDelay)
            DiagnosticLog.Debug(DiagnosticLogTags.RecordWorker, $"準備完了から記録開始まで: {recordingStartDelay.TotalMilliseconds:0} ms");
        if (shouldForceTerminate) ForceTerminateWorker();
    }

    private void RequestSession(Func<RecordingWorkerSession, RecordingWorkerSessionTransition> request)
    {
        bool shouldForceTerminate;
        lock (_gate) shouldForceTerminate = ApplyTransitionLocked(request(_session));
        if (shouldForceTerminate) ForceTerminateWorker();
    }

    private void AdvanceSessionTime(object? state)
    {
        bool shouldForceTerminate;
        lock (_gate) shouldForceTerminate = ApplyTransitionLocked(_session.AdvanceTime(DateTimeOffset.UtcNow));
        if (shouldForceTerminate) ForceTerminateWorker();
    }

    private void HandleCommunicationLost()
    {
        bool shouldForceTerminate;
        lock (_gate)
        {
            if (_communicationLost || _session.IsProcessExited) return;
            _communicationLost = true;
            shouldForceTerminate = ApplyTransitionLocked(_session.OnCommunicationLost(DateTimeOffset.UtcNow));
        }
        if (shouldForceTerminate) ForceTerminateWorker();
    }

    private void HandleProcessExited(object? sender, EventArgs eventArgs)
    {
        Process? process;
        Task? readerTask;
        lock (_gate)
        {
            process = _process;
            readerTask = _readerTask;
        }
        if (process is null) return;

        int exitCode;
        try { exitCode = process.ExitCode; }
        catch (InvalidOperationException) { return; }
        var exitCodeName = RecordingWorkerExitCodes.FromInt32(exitCode);
        var logMessage = $"録画プロセスが終了しました: {exitCodeName} ({exitCode})";
        if (exitCodeName == RecordingWorkerExitCode.Succeeded)
            DiagnosticLog.Info(DiagnosticLogTags.RecordWorker, logMessage);
        else
            DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, logMessage);

        // 終了の直前に送られた完了を取りこぼさないため、パイプを終わりまで読んでから終了を知らせる。
        if (readerTask is null) _connectionLifetime.Cancel();
        else
        {
            try { readerTask.Wait(ReaderDrainWaitOnExit); }
            catch (AggregateException) { }
        }

        bool shouldForceTerminate;
        lock (_gate)
        {
            shouldForceTerminate = ApplyTransitionLocked(_session.OnProcessExited(exitCode, DateTimeOffset.UtcNow));
        }
        if (shouldForceTerminate) ForceTerminateWorker();
    }

    private bool ApplyTransitionLocked(RecordingWorkerSessionTransition transition)
    {
        foreach (var message in transition.MessagesToSend)
            _outgoing.Writer.TryWrite(message);
        foreach (var sessionEvent in transition.Events)
            _pendingEvents.Enqueue(new PendingEvent(sessionEvent, transition.TerminationOutcome));
        if (transition.Events.Count > 0) _eventSignal.Release();

        var previousSignal = _stateChanged;
        _stateChanged = NewStateSignal();
        previousSignal.TrySetResult(true);

        return transition.ShouldForceTerminateWorker;
    }

    private async Task DispatchEventsAsync()
    {
        while (true)
        {
            await _eventSignal.WaitAsync().ConfigureAwait(false);
            while (true)
            {
                PendingEvent? pendingEvent;
                lock (_gate)
                {
                    if (_pendingEvents.Count == 0)
                    {
                        if (_eventQueueCompleted) return;
                        break;
                    }
                    pendingEvent = _pendingEvents.Dequeue();
                }
                DispatchEvent(pendingEvent);
            }
        }
    }

    private void DispatchEvent(PendingEvent pending)
    {
        switch (pending.Event)
        {
            case RecordingWorkerStateChangedEvent stateChanged:
                StatusChanged?.Invoke(this, new RecordingEngineStatusChangedEventArgs(stateChanged.State switch
                {
                    RecordingWorkerRecordingState.Recording => RecordingEngineStatus.Recording,
                    RecordingWorkerRecordingState.Paused => RecordingEngineStatus.Paused,
                    RecordingWorkerRecordingState.Saving => RecordingEngineStatus.Saving,
                    _ => throw new ArgumentOutOfRangeException(nameof(stateChanged))
                }));
                break;
            case RecordingWorkerCompletedEvent completed:
                RecordingCompleted?.Invoke(this, new RecordingEngineCompletedEventArgs(
                    string.IsNullOrWhiteSpace(completed.FilePath) ? _outputPath : completed.FilePath));
                break;
            case RecordingWorkerFailedEvent failed:
                RecordingFailed?.Invoke(this, new RecordingEngineFailedEventArgs(
                    string.IsNullOrWhiteSpace(failed.FilePath) ? _outputPath : failed.FilePath,
                    failed.Error,
                    pending.Outcome,
                    failed.BeforeRecordingStarted));
                break;
            case RecordingWorkerWarningEvent warning:
                RecordingWarning?.Invoke(this, new RecordingEngineWarningEventArgs(warning.Message));
                break;
            case RecordingWorkerOperationFailedEvent operationFailed:
                OperationFailed?.Invoke(this, new RecordingEngineOperationFailedEventArgs(
                    operationFailed.OperationId,
                    operationFailed.Operation,
                    operationFailed.Error));
                break;
            case RecordingWorkerLogEvent log:
                WriteForwardedLog(log);
                break;
        }
    }

    private void WriteForwardedLog(RecordingWorkerLogEvent log)
    {
        var message = $"[{log.Tag}] {log.Message}";
        switch (log.Level)
        {
            case DiagnosticLogLevel.Error: DiagnosticLog.Error(DiagnosticLogTags.RecordWorker, message); break;
            case DiagnosticLogLevel.Warn: DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, message); break;
            case DiagnosticLogLevel.Info: DiagnosticLog.Info(DiagnosticLogTags.RecordWorker, message); break;
            case DiagnosticLogLevel.Debug: DiagnosticLog.Debug(DiagnosticLogTags.RecordWorker, message); break;
        }
    }

    private void ForceTerminateWorker()
    {
        if (Interlocked.Exchange(ref _forceTerminationStarted, 1) != 0) return;
        DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, "録画プロセスの強制終了を開始します。");
        _ = Task.Run(async () =>
        {
            try
            {
                Process? process;
                lock (_gate) process = _process;
                if (process is null) return;
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                catch (Exception exception)
                {
                    DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, $"録画プロセスを強制終了できませんでした: {exception.Message}");
                }

                try { await process.WaitForExitAsync().WaitAsync(ForceTerminationWait).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, "録画プロセスが強制終了から 5 秒以内に終了しませんでした。");
                }
                catch (InvalidOperationException) { }
            }
            finally
            {
                _forceTerminationFinished.TrySetResult(true);
            }
        });
    }

    private static TaskCompletionSource<bool> NewStateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
