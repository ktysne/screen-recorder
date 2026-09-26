using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal static class RecordingWorkerProgram
{
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(10);

    public static int Run(string pipeName)
    {
        OrphanDiagnosticLog.Activate(DiagnosticLog.Level);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            ApplicationConfiguration.Initialize();

            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                pipe.Connect((int)PipeConnectTimeout.TotalMilliseconds);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error(DiagnosticLogTags.RecordWorker, $"本体との接続に失敗しました: {exception}");
                return RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.StartFailed);
            }

            using var runtime = new RecordingWorkerRuntime(pipe);
            try
            {
                return runtime.Run();
            }
            catch (Exception exception)
            {
                runtime.ReportUnhandledException(exception);
                return RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.UnhandledException);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(DiagnosticLogTags.RecordWorker, $"録画プロセスで未処理の例外が発生しました: {exception}");
            return RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.UnhandledException);
        }
        finally
        {
            DiagnosticLog.ClearForwarder();
        }
    }
}

internal sealed class RecordingWorkerRuntime : ApplicationContext
{
    private static readonly TimeSpan IdleTerminationGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopFailureTerminationWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SaveWaitAfterDisconnect = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DisconnectedExitDeadline = SaveWaitAfterDisconnect + TimeSpan.FromSeconds(15);
    private readonly NamedPipeClientStream _pipe;
    private readonly Channel<OutgoingMessage> _outgoing = Channel.CreateUnbounded<OutgoingMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskCompletionSource<SynchronizationContext> _uiContextReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _writerTask;
    private Task? _readerTask;
    private SynchronizationContext? _uiContext;
    private object? _engine;
    private RecordingWorkerStartData? _startData;
    private RecordingWorkerRecordingState? _recordingState;
    private int _readyResponseReceived;
    private DiagnosticLogLevel _workerLogLevel = DiagnosticLog.Level;
    private int _hasObservedRecording;
    private int _disconnected;
    private int _disconnectHandling;
    private int _terminalHandling;
    private int _exitCode;
    private int _disposed;

    private sealed record OutgoingMessage(RecordingWorkerMessage Message, TaskCompletionSource<bool>? Written);

    public RecordingWorkerRuntime(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        Application.Idle += CaptureUiContext;
    }

    public int Run()
    {
        _writerTask = Task.Run(WriteMessagesAsync);
        try
        {
            EnqueueAndWait(new RecordingWorkerReadyMessage(Environment.ProcessId, RecordingWorkerProtocol.CurrentVersion));
        }
        catch when (Volatile.Read(ref _disconnected) != 0)
        {
            _exitCode = RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.SaveFailedAfterDisconnect);
            return _exitCode;
        }
        _readerTask = Task.Run(ReadMessagesAsync);

        Application.Run(this);
        _lifetime.Cancel();
        _outgoing.Writer.TryComplete();
        try { _readerTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        try { _writerTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        return _exitCode;
    }

    public void ReportUnhandledException(Exception exception)
    {
        if (Volatile.Read(ref _disconnected) != 0)
        {
            DiagnosticLog.Error(DiagnosticLogTags.RecordWorker, $"録画プロセスで未処理の例外が発生しました: {exception}");
            return;
        }
        var message = new RecordingWorkerLogMessage(
            DiagnosticLogLevel.Error,
            DiagnosticLogTags.RecordWorker,
            $"録画プロセスで未処理の例外が発生しました: {exception}");
        try { EnqueueAndWait(message); }
        catch { }
        _exitCode = RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.UnhandledException);
        if (_uiContext is not null) PostToUi(FinishOnUi);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Application.Idle -= CaptureUiContext;
            DiagnosticLog.ClearForwarder();
            _lifetime.Cancel();
            _outgoing.Writer.TryComplete();
            _pipe.Dispose();
        }
        base.Dispose(disposing);
    }

    private void CaptureUiContext(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= CaptureUiContext;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_uiContext);
        _uiContextReady.TrySetResult(_uiContext);
    }

    private async Task ReadMessagesAsync()
    {
        var lineReader = new RecordingWorkerLineReader();
        var reader = new StreamReader(_pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[4096];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    HandleCommunicationLost();
                    return;
                }

                foreach (var line in lineReader.Append(new string(buffer, 0, count)))
                {
                    if (line.IsTooLong)
                    {
                        SendProtocolWarning("本体から受信したメッセージが長すぎるため破棄しました。");
                        continue;
                    }

                    if (line.Line is null) continue;
                    var result = RecordingWorkerProtocol.Deserialize(line.Line);
                    if (!result.IsReadable)
                    {
                        SendProtocolWarning($"本体から受信したメッセージを解釈できませんでした: {result.Error}");
                        continue;
                    }

                    if (result.Message is RecordingWorkerReadyResponseCommand readyResponse)
                    {
                        ReceiveReadyResponse(readyResponse);
                        continue;
                    }

                    if (Volatile.Read(ref _readyResponseReceived) == 0)
                    {
                        SendProtocolWarning("準備完了への応答より前に本体からコマンドを受信しました。");
                        continue;
                    }

                    var uiContext = await _uiContextReady.Task.ConfigureAwait(false);
                    uiContext.Post(_ => ExecuteCommand(result.Message!), null);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception) when (Volatile.Read(ref _disconnected) != 0 || _lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            HandleCommunicationLost();
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void ReceiveReadyResponse(RecordingWorkerReadyResponseCommand readyResponse)
    {
        if (Interlocked.Exchange(ref _readyResponseReceived, 1) != 0)
        {
            SendProtocolWarning("本体から準備完了への応答を重複して受信しました。");
            return;
        }

        _workerLogLevel = readyResponse.MinimumLogLevel;
        DiagnosticLog.SetForwarder(readyResponse.MinimumLogLevel, (level, tag, message) =>
        {
            Enqueue(new RecordingWorkerLogMessage(level, tag, message));
        });
    }

    private async Task WriteMessagesAsync()
    {
        try
        {
            await foreach (var outgoing in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    var payload = RecordingWorkerProtocol.Serialize(outgoing.Message);
                    await _pipe.WriteAsync(payload, _lifetime.Token).ConfigureAwait(false);
                    await _pipe.WriteAsync(new byte[] { (byte)'\n' }, _lifetime.Token).ConfigureAwait(false);
                    await _pipe.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                    outgoing.Written?.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    outgoing.Written?.TrySetException(exception);
                    HandleCommunicationLost();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void ExecuteCommand(RecordingWorkerMessage message)
    {
        try
        {
            switch (message)
            {
                case RecordingWorkerStartCommand start:
                    StartRecording(start.Request);
                    break;
                case RecordingWorkerPauseCommand:
                    if (_engine is { } pauseEngine) RecordingWorkerLibraryBridge.Pause(pauseEngine);
                    break;
                case RecordingWorkerResumeCommand:
                    if (_engine is { } resumeEngine) RecordingWorkerLibraryBridge.Resume(resumeEngine);
                    break;
                case RecordingWorkerStopCommand:
                    if (_engine is { } stopEngine) RecordingWorkerLibraryBridge.Stop(stopEngine);
                    break;
                default:
                    SendProtocolWarning($"本体から未対応のコマンドを受信しました: {message.Type}");
                    break;
            }
        }
        catch (Exception exception) when (message is RecordingWorkerPauseCommand or RecordingWorkerResumeCommand)
        {
            Enqueue(new RecordingWorkerWarningMessage(exception.Message));
        }
        catch (Exception exception) when (message is RecordingWorkerStopCommand && _engine is { } engine)
        {
            _ = FailAfterTerminationAsync(engine, exception);
        }
        catch (Exception exception)
        {
            SendFailure(exception.ToString());
        }
    }

    // 停止が例外になっても停止の処理は進んでいることがあるため、書き終えを待ってから失敗を送る。
    private async Task FailAfterTerminationAsync(object engine, Exception stopException)
    {
        RecordingTerminationOutcome outcome;
        try
        {
            outcome = await Task.Run(
                () => RecordingWorkerLibraryBridge.WaitForTerminationAsync(engine, StopFailureTerminationWait),
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        if (outcome == RecordingTerminationOutcome.Completed) return;
        SendFailure(stopException.ToString());
    }

    private void SendFailure(string error) =>
        BeginTerminalMessage(new RecordingWorkerFailedMessage(
            _startData?.OutputPath ?? string.Empty,
            error,
            Volatile.Read(ref _hasObservedRecording) == 0));

    private void StartRecording(RecordingWorkerStartData request)
    {
        if (_engine is not null) throw new InvalidOperationException("録画要求を重複して受信しました。");
        _startData = request;
        _engine = RecordingWorkerLibraryBridge.Start(
            request,
            OnEngineStatusChanged,
            OnEngineCompleted,
            OnEngineFailed,
            OnEngineWarning);
        _ = ObserveTerminationAsync(_engine);
    }

    private void OnEngineStatusChanged(RecordingWorkerRecordingState state)
    {
        if (state == RecordingWorkerRecordingState.Recording)
            Interlocked.Exchange(ref _hasObservedRecording, 1);
        _recordingState = state;
        Enqueue(new RecordingWorkerStateMessage(state));
    }

    private void OnEngineCompleted(string filePath)
    {
        var path = string.IsNullOrWhiteSpace(filePath) ? _startData?.OutputPath ?? string.Empty : filePath;
        BeginTerminalMessage(new RecordingWorkerCompletedMessage(path));
    }

    private void OnEngineFailed(string filePath, string error, bool beforeRecordingStarted)
    {
        var path = string.IsNullOrWhiteSpace(filePath) ? _startData?.OutputPath ?? string.Empty : filePath;
        BeginTerminalMessage(new RecordingWorkerFailedMessage(path, error, beforeRecordingStarted));
    }

    private void OnEngineWarning(string message) => Enqueue(new RecordingWorkerWarningMessage(message));

    private async Task ObserveTerminationAsync(object engine)
    {
        try
        {
            var outcome = await Task.Run(
                () => RecordingWorkerLibraryBridge.WaitForTerminationAsync(engine, Timeout.InfiniteTimeSpan),
                _lifetime.Token).ConfigureAwait(false);
            if (outcome != RecordingTerminationOutcome.Idle) return;
            // 録画中の失敗でもライブラリは先に Idle になり、失敗の知らせが後から届くため、それを待ってから判定を送る。
            await Task.Delay(IdleTerminationGrace, _lifetime.Token).ConfigureAwait(false);
            BeginTerminalMessage(new RecordingWorkerTerminationMessage(RecordingTerminationOutcome.Idle));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportUnhandledException(exception);
        }
    }

    private void BeginTerminalMessage(RecordingWorkerMessage message)
    {
        if (Volatile.Read(ref _disconnected) != 0 || Interlocked.Exchange(ref _terminalHandling, 1) != 0) return;
        var written = EnqueueWithAcknowledgement(message);
        _ = FinishAfterWriteAsync(written, RecordingWorkerExitCode.Succeeded);
    }

    private async Task FinishAfterWriteAsync(Task written, RecordingWorkerExitCode exitCode)
    {
        var sent = true;
        try { await written.ConfigureAwait(false); }
        catch { sent = false; }
        if (!sent && Volatile.Read(ref _disconnected) != 0) return;
        _exitCode = RecordingWorkerExitCodes.ToInt32(exitCode);
        PostToUi(FinishOnUi);
    }

    private void HandleCommunicationLost()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        OrphanDiagnosticLog.Activate(_workerLogLevel);
        DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, "本体との通信が切断されました。");
        // 本体が落ちた後はライブラリの破棄が返らなくても強制終了する者がいないため、期限を過ぎたら自分で終わる。
        _ = Task.Run(async () =>
        {
            await Task.Delay(DisconnectedExitDeadline).ConfigureAwait(false);
            var exitCode = Volatile.Read(ref _exitCode);
            if (exitCode == 0)
            {
                exitCode = RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.SaveFailedAfterDisconnect);
                Volatile.Write(ref _exitCode, exitCode);
            }
            DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, $"切断後の終了期限を過ぎたため録画プロセスを終了します: {RecordingWorkerExitCodes.FromInt32(exitCode)} ({exitCode})");
            Environment.Exit(exitCode);
        });
        _ = _uiContextReady.Task.ContinueWith(
            _ => PostToUi(HandleCommunicationLostOnUi),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void HandleCommunicationLostOnUi()
    {
        if (Interlocked.Exchange(ref _disconnectHandling, 1) != 0) return;
        if (_engine is null)
        {
            _exitCode = RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.SaveFailedAfterDisconnect);
            DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, "録画の停止要求前に録画エンジンが終了していました。");
            DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, "録画エンジンがなく、切断後の書き終え結果を確認できませんでした。");
            LogOrphanExitCode();
            FinishOnUi();
            return;
        }

        try
        {
            DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, "本体との通信切断を受けて録画の停止を要求しました。");
            // 記録が始まる前の停止はライブラリの包みが保留し、始まった時点で止める。
            if (_recordingState is not RecordingWorkerRecordingState.Saving)
                RecordingWorkerLibraryBridge.Stop(_engine);
        }
        catch
        {
        }

        _ = FinishAfterDisconnectAsync(_engine);
    }

    private async Task FinishAfterDisconnectAsync(object engine)
    {
        RecordingTerminationOutcome outcome;
        try
        {
            outcome = await Task.Run(
                () => RecordingWorkerLibraryBridge.WaitForTerminationAsync(engine, SaveWaitAfterDisconnect),
                _lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            outcome = RecordingTerminationOutcome.TimedOut;
        }

        var saved = RecordingWorkerLibraryBridge.HasCompleted(engine) || outcome == RecordingTerminationOutcome.Completed;
        _exitCode = RecordingWorkerExitCodes.ToInt32(saved
            ? RecordingWorkerExitCode.SavedAfterDisconnect
            : RecordingWorkerExitCode.SaveFailedAfterDisconnect);
        DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, saved
            ? "切断後の録画ファイルを書き終えました。"
            : "切断後の録画ファイルを書き終えられませんでした。");
        LogOrphanExitCode();
        PostToUi(FinishOnUi);
    }

    private void LogOrphanExitCode()
    {
        var exitCode = RecordingWorkerExitCodes.FromInt32(Volatile.Read(ref _exitCode));
        DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, $"録画プロセスの終了コード: {exitCode} ({RecordingWorkerExitCodes.ToInt32(exitCode)})");
    }

    private void FinishOnUi()
    {
        try
        {
            if (_engine is not null)
            {
                RecordingWorkerLibraryBridge.Dispose(_engine);
                _engine = null;
            }
        }
        catch (Exception exception)
        {
            _exitCode = RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.UnhandledException);
            if (Volatile.Read(ref _disconnected) == 0)
            {
                try
                {
                    EnqueueAndWait(new RecordingWorkerLogMessage(
                        DiagnosticLogLevel.Error,
                        DiagnosticLogTags.RecordWorker,
                        $"録画プロセスで未処理の例外が発生しました: {exception}"));
                }
                catch { }
            }
        }
        finally
        {
            Application.ExitThread();
        }
    }

    private void ReportUnhandledExceptionOnUi(Exception exception)
    {
        if (Interlocked.Exchange(ref _terminalHandling, 1) != 0) return;
        var written = Volatile.Read(ref _disconnected) == 0
            ? EnqueueWithAcknowledgement(new RecordingWorkerLogMessage(
                DiagnosticLogLevel.Error,
                DiagnosticLogTags.RecordWorker,
                $"録画プロセスで未処理の例外が発生しました: {exception}"))
            : Task.CompletedTask;
        _ = FinishAfterWriteAsync(written, RecordingWorkerExitCode.UnhandledException);
    }

    private void SendProtocolWarning(string message)
    {
        if (Volatile.Read(ref _readyResponseReceived) != 0)
            DiagnosticLog.Warn(DiagnosticLogTags.RecordWorker, message);
        else
            Enqueue(new RecordingWorkerLogMessage(DiagnosticLogLevel.Warn, DiagnosticLogTags.RecordWorker, message));
    }

    private void Enqueue(RecordingWorkerMessage message)
    {
        if (Volatile.Read(ref _disconnected) != 0) return;
        _outgoing.Writer.TryWrite(new OutgoingMessage(message, null));
    }

    private Task EnqueueWithAcknowledgement(RecordingWorkerMessage message)
    {
        if (Volatile.Read(ref _disconnected) != 0) return Task.CompletedTask;
        var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_outgoing.Writer.TryWrite(new OutgoingMessage(message, written)))
            written.TrySetException(new IOException("録画プロセスの送信列が閉じています。"));
        return written.Task;
    }

    private void EnqueueAndWait(RecordingWorkerMessage message) => EnqueueWithAcknowledgement(message).GetAwaiter().GetResult();

    private void PostToUi(Action action)
    {
        var context = _uiContext;
        if (context is null) return;
        context.Post(_ =>
        {
            try { action(); }
            catch (Exception exception) { ReportUnhandledExceptionOnUi(exception); }
        }, null);
    }
}
