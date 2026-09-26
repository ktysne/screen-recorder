namespace ScreenRecorder.Core;

public enum RecordingWorkerExitCode
{
    Succeeded = 0,
    SavedAfterDisconnect = 10,
    SaveFailedAfterDisconnect = 11,
    UnhandledException = 20,
    StartFailed = 21,
    Unknown = 255
}

public static class RecordingWorkerExitCodes
{
    public static int ToInt32(RecordingWorkerExitCode exitCode) => exitCode switch
    {
        RecordingWorkerExitCode.Succeeded => 0,
        RecordingWorkerExitCode.SavedAfterDisconnect => 10,
        RecordingWorkerExitCode.SaveFailedAfterDisconnect => 11,
        RecordingWorkerExitCode.UnhandledException => 20,
        RecordingWorkerExitCode.StartFailed => 21,
        RecordingWorkerExitCode.Unknown => 255,
        _ => throw new ArgumentOutOfRangeException(nameof(exitCode))
    };

    public static RecordingWorkerExitCode FromInt32(int exitCode) => exitCode switch
    {
        0 => RecordingWorkerExitCode.Succeeded,
        10 => RecordingWorkerExitCode.SavedAfterDisconnect,
        11 => RecordingWorkerExitCode.SaveFailedAfterDisconnect,
        20 => RecordingWorkerExitCode.UnhandledException,
        21 => RecordingWorkerExitCode.StartFailed,
        _ => RecordingWorkerExitCode.Unknown
    };
}

public abstract record RecordingWorkerSessionEvent;

public sealed record RecordingWorkerStateChangedEvent(RecordingWorkerRecordingState State) : RecordingWorkerSessionEvent;

public sealed record RecordingWorkerCompletedEvent(string FilePath) : RecordingWorkerSessionEvent;

public sealed record RecordingWorkerFailedEvent(string FilePath, string Error, bool BeforeRecordingStarted) : RecordingWorkerSessionEvent;

public sealed record RecordingWorkerWarningEvent(string Message) : RecordingWorkerSessionEvent;

public sealed record RecordingWorkerLogEvent(DiagnosticLogLevel Level, string Tag, string Message) : RecordingWorkerSessionEvent;

public sealed record RecordingWorkerSessionTransition(
    IReadOnlyList<RecordingWorkerMessage> MessagesToSend,
    IReadOnlyList<RecordingWorkerSessionEvent> Events,
    RecordingTerminationOutcome TerminationOutcome,
    RecordingWorkerExitCode? ProcessExitCode,
    bool ShouldForceTerminateWorker,
    bool StartFailed);

public sealed class RecordingWorkerSession
{
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CompletedExitTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan IdleExitTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FailedExitTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DisconnectedExitTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ShortDisposeTimeout = TimeSpan.FromSeconds(10);

    private readonly DiagnosticLogLevel _minimumLogLevel;
    private readonly List<RecordingWorkerMessage> _queuedCommands = [];
    private DateTimeOffset? _startupDeadline;
    private DateTimeOffset? _stopDeadline;
    private DateTimeOffset? _disposeDeadline;
    private DateTimeOffset? _terminalDeadline;
    private int _expectedProcessId;
    private bool _processStarted;
    private bool _ready;
    private bool _stopRequested;
    private bool _disposeRequested;
    private bool _processExited;
    private bool _forceTermination;
    private bool _startFailed;
    private RecordingWorkerRecordingState? _recordingState;
    private RecordingTerminationOutcome? _terminationOutcome;
    private RecordingWorkerExitCode? _processExitCode;

    public RecordingWorkerSession(DiagnosticLogLevel minimumLogLevel = DiagnosticLogLevel.Info)
    {
        _minimumLogLevel = minimumLogLevel;
    }

    public bool IsReady => _ready;

    public bool IsProcessExited => _processExited;

    public RecordingTerminationOutcome TerminationOutcome => _terminationOutcome ?? RecordingTerminationOutcome.Waiting;

    public RecordingWorkerSessionTransition OnProcessStarted(DateTimeOffset at, int processId)
    {
        if (_processStarted) throw new InvalidOperationException("録画プロセスはすでに起動しています。");
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));

        _processStarted = true;
        _expectedProcessId = processId;
        _startupDeadline = at + StartupTimeout;
        return Transition();
    }

    public RecordingWorkerSessionTransition RequestStart(RecordingWorkerStartData request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_terminationOutcome is null && !_processExited)
            QueueCommand(new RecordingWorkerStartCommand(request));
        return Transition();
    }

    public RecordingWorkerSessionTransition RequestPause(DateTimeOffset at)
    {
        if (_terminationOutcome is null && !_processExited)
            QueueCommand(new RecordingWorkerPauseCommand());
        return Transition();
    }

    public RecordingWorkerSessionTransition RequestResume(DateTimeOffset at)
    {
        if (_terminationOutcome is null && !_processExited)
            QueueCommand(new RecordingWorkerResumeCommand());
        return Transition();
    }

    public RecordingWorkerSessionTransition RequestStop(DateTimeOffset at)
    {
        if (_terminationOutcome is null && !_processExited)
            RequestStopCore(at);
        return EvaluateDeadlines(at);
    }

    public RecordingWorkerSessionTransition RequestDispose(DateTimeOffset at)
    {
        if (_processExited) return Transition();
        if (_terminationOutcome == RecordingTerminationOutcome.TimedOut)
        {
            _forceTermination = true;
            return Transition();
        }
        if (_terminationOutcome is not null) return Transition();

        if (!_disposeRequested)
        {
            _disposeRequested = true;
            RequestStopCore(at);
            var timeout = _recordingState is not null
                ? StopTimeout
                : ShortDisposeTimeout;
            _disposeDeadline = at + timeout;
        }

        return EvaluateDeadlines(at);
    }

    public RecordingWorkerSessionTransition OnMessage(RecordingWorkerMessage message, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message is RecordingWorkerReadyMessage readyMessage)
            return ReceiveReady(readyMessage, at);

        var elapsed = EvaluateDeadlines(at);
        if (!_ready) return elapsed;

        if (message is RecordingWorkerWarningMessage warningMessage)
            return Transition(events: [new RecordingWorkerWarningEvent(warningMessage.Message)]);
        if (message is RecordingWorkerLogMessage logMessage)
            return Transition(events: [new RecordingWorkerLogEvent(logMessage.Level, logMessage.Tag, logMessage.Message)]);
        if (_terminationOutcome is not null || _processExited) return elapsed;

        var events = new List<RecordingWorkerSessionEvent>();
        switch (message)
        {
            case RecordingWorkerStateMessage stateMessage when Enum.IsDefined(stateMessage.State):
                _recordingState = stateMessage.State;
                events.Add(new RecordingWorkerStateChangedEvent(stateMessage.State));
                break;
            case RecordingWorkerCompletedMessage completedMessage:
                events.Add(new RecordingWorkerCompletedEvent(completedMessage.FilePath));
                SetTermination(RecordingTerminationOutcome.Completed, at, CompletedExitTimeout);
                break;
            case RecordingWorkerFailedMessage failedMessage:
                _startFailed = failedMessage.BeforeRecordingStarted;
                events.Add(new RecordingWorkerFailedEvent(
                    failedMessage.FilePath,
                    failedMessage.Error,
                    failedMessage.BeforeRecordingStarted));
                SetTermination(RecordingTerminationOutcome.Failed, at, FailedExitTimeout);
                break;
            case RecordingWorkerTerminationMessage terminationMessage:
                ReceiveTermination(terminationMessage.Outcome, at);
                break;
        }

        return Transition(events: events);
    }

    public RecordingWorkerSessionTransition OnCommunicationLost(DateTimeOffset at)
    {
        var elapsed = EvaluateDeadlines(at);
        if (elapsed.Events.Count > 0 || _terminationOutcome is not null || _processExited)
            return elapsed;
        if (!_ready)
            return FailStart(at, "録画プロセスとの通信が準備完了前に切断されました。");

        var failure = CreateUnexpectedProcessExitFailure();
        SetTermination(RecordingTerminationOutcome.ProcessExited, at, DisconnectedExitTimeout);
        return Transition(events: [failure]);
    }

    public RecordingWorkerSessionTransition OnProcessExited(int exitCode, DateTimeOffset at)
    {
        var elapsed = EvaluateDeadlines(at);
        _processExited = true;
        _processExitCode = RecordingWorkerExitCodes.FromInt32(exitCode);
        _forceTermination = false;

        if (_terminationOutcome is null)
        {
            if (!_ready)
            {
                _startFailed = true;
                _terminationOutcome = RecordingTerminationOutcome.ProcessExited;
                return Transition(events:
                [
                    new RecordingWorkerFailedEvent(
                        string.Empty,
                        $"録画プロセスが準備完了前に終了しました (終了コード: {exitCode})。",
                        true)
                ]);
            }

            _terminationOutcome = RecordingTerminationOutcome.ProcessExited;
            return Transition(events: [CreateUnexpectedProcessExitFailure()]);
        }

        return elapsed.Events.Count > 0 ? Transition(events: elapsed.Events) : Transition();
    }

    public RecordingWorkerSessionTransition AdvanceTime(DateTimeOffset at) => EvaluateDeadlines(at);

    private RecordingWorkerSessionTransition ReceiveReady(RecordingWorkerReadyMessage message, DateTimeOffset at)
    {
        if (_ready || _terminationOutcome is not null || _processExited) return EvaluateDeadlines(at);
        if (!_processStarted || (_startupDeadline is { } deadline && at > deadline))
            return FailStart(at, "録画プロセスの準備完了を期限内に受信できませんでした。");
        if (message.ProcessId != _expectedProcessId || message.ProtocolVersion != RecordingWorkerProtocol.CurrentVersion)
            return FailStart(at, "録画プロセスの ID またはやり取りの版が一致しません。");

        _ready = true;
        _startupDeadline = null;
        var commands = new List<RecordingWorkerMessage>
        {
            new RecordingWorkerReadyResponseCommand(_minimumLogLevel)
        };
        commands.AddRange(_queuedCommands);
        _queuedCommands.Clear();
        return Transition(commands: commands);
    }

    private RecordingWorkerSessionTransition FailStart(DateTimeOffset at, string message)
    {
        _startFailed = true;
        _startupDeadline = null;
        _forceTermination = _processStarted && !_processExited;
        _terminationOutcome ??= RecordingTerminationOutcome.Failed;
        _terminalDeadline = _forceTermination ? at : null;
        return Transition(events:
        [
            new RecordingWorkerFailedEvent(string.Empty, message, true)
        ]);
    }

    private RecordingWorkerFailedEvent CreateUnexpectedProcessExitFailure()
    {
        var beforeRecordingStarted = _recordingState is null;
        _startFailed = beforeRecordingStarted;
        return new RecordingWorkerFailedEvent(
            string.Empty,
            "録画プロセスが終了判定を通知せずに予期せず終了しました。",
            beforeRecordingStarted);
    }

    private void RequestStopCore(DateTimeOffset at)
    {
        if (_stopRequested) return;

        _stopRequested = true;
        _stopDeadline = at + StopTimeout;
        QueueCommand(new RecordingWorkerStopCommand());
    }

    private void QueueCommand(RecordingWorkerMessage command)
    {
        _queuedCommands.Add(command);
    }

    private void ReceiveTermination(RecordingTerminationOutcome outcome, DateTimeOffset at)
    {
        if (_terminationOutcome is not null) return;

        var timeout = outcome switch
        {
            RecordingTerminationOutcome.Completed => CompletedExitTimeout,
            RecordingTerminationOutcome.Idle => IdleExitTimeout,
            RecordingTerminationOutcome.Failed => FailedExitTimeout,
            RecordingTerminationOutcome.ProcessExited => DisconnectedExitTimeout,
            _ => (TimeSpan?)null
        };

        if (timeout is { } duration)
            SetTermination(outcome, at, duration);
    }

    private void SetTermination(RecordingTerminationOutcome outcome, DateTimeOffset at, TimeSpan exitTimeout)
    {
        if (_terminationOutcome is not null) return;
        _terminationOutcome = outcome;
        _terminalDeadline = at + exitTimeout;
    }

    private RecordingWorkerSessionTransition EvaluateDeadlines(DateTimeOffset at)
    {
        if (_processExited) return Transition();

        if (_processStarted && !_ready && _terminationOutcome is null && _startupDeadline is { } startupDeadline && at >= startupDeadline)
            return FailStart(at, "録画プロセスの準備完了を期限内に受信できませんでした。");

        if (_terminationOutcome is null)
        {
            var pendingDeadline = Earlier(_stopDeadline, _disposeDeadline);
            if (pendingDeadline is { } deadline && at >= deadline)
            {
                _terminationOutcome = RecordingTerminationOutcome.TimedOut;
                _terminalDeadline = at;
                _forceTermination = _processStarted;
            }
        }
        else if (_terminalDeadline is { } terminalDeadline && at >= terminalDeadline)
        {
            _forceTermination = _processStarted;
        }

        return Transition();
    }

    private RecordingWorkerSessionTransition Transition(
        IReadOnlyList<RecordingWorkerMessage>? commands = null,
        IReadOnlyList<RecordingWorkerSessionEvent>? events = null)
    {
        if (commands is null && _ready && _queuedCommands.Count > 0)
        {
            commands = _queuedCommands.ToArray();
            _queuedCommands.Clear();
        }

        return new RecordingWorkerSessionTransition(
            commands ?? Array.Empty<RecordingWorkerMessage>(),
            events ?? Array.Empty<RecordingWorkerSessionEvent>(),
            _terminationOutcome ?? RecordingTerminationOutcome.Waiting,
            _processExitCode,
            _forceTermination && !_processExited,
            _startFailed);
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first < second ? first : second;
}
