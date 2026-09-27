using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class RecordingWorkerSessionTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 27, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 準備完了前の要求を受けた順に送る()
    {
        var session = CreateStartedSession();
        var start = session.RequestStart(CreateStartData(), StartedAt.AddSeconds(1));
        var pause = session.RequestPause(StartedAt.AddSeconds(2));
        var resume = session.RequestResume(StartedAt.AddSeconds(3));
        var stop = session.RequestStop(StartedAt.AddSeconds(4));

        Assert.Empty(start.MessagesToSend);
        Assert.Empty(pause.MessagesToSend);
        Assert.Empty(resume.MessagesToSend);
        Assert.Empty(stop.MessagesToSend);

        var ready = session.OnMessage(
            new RecordingWorkerReadyMessage(1234, RecordingWorkerProtocol.CurrentVersion),
            StartedAt.AddSeconds(5));

        Assert.Collection(
            ready.MessagesToSend,
            message => Assert.IsType<RecordingWorkerReadyResponseCommand>(message),
            message => Assert.IsType<RecordingWorkerStartCommand>(message),
            message => Assert.IsType<RecordingWorkerPauseCommand>(message),
            message => Assert.IsType<RecordingWorkerResumeCommand>(message),
            message => Assert.IsType<RecordingWorkerStopCommand>(message));
    }

    [Fact]
    public void 準備完了が起動期限を過ぎると開始失敗として強制終了を求める()
    {
        var session = CreateStartedSession();

        var result = session.AdvanceTime(StartedAt + RecordingWorkerSession.StartupTimeout);

        Assert.Equal(RecordingTerminationOutcome.Failed, result.TerminationOutcome);
        Assert.True(result.StartFailed);
        Assert.True(result.ShouldForceTerminateWorker);
        Assert.True(Assert.IsType<RecordingWorkerFailedEvent>(Assert.Single(result.Events)).BeforeRecordingStarted);
    }

    [Theory]
    [InlineData(1235, RecordingWorkerProtocol.CurrentVersion)]
    [InlineData(1234, RecordingWorkerProtocol.CurrentVersion + 1)]
    public void PIDまたは版が一致しない準備完了を開始失敗にする(int processId, int version)
    {
        var session = CreateStartedSession();

        var result = session.OnMessage(new RecordingWorkerReadyMessage(processId, version), StartedAt.AddSeconds(1));

        Assert.Equal(RecordingTerminationOutcome.Failed, result.TerminationOutcome);
        Assert.True(result.StartFailed);
        Assert.True(result.ShouldForceTerminateWorker);
        Assert.True(Assert.IsType<RecordingWorkerFailedEvent>(Assert.Single(result.Events)).BeforeRecordingStarted);
    }

    [Fact]
    public void 最初に確定した終端だけを有効にする()
    {
        var session = CreateReadySession();
        var completed = session.OnMessage(new RecordingWorkerCompletedMessage("done.mp4"), StartedAt.AddSeconds(1));
        var failed = session.OnMessage(new RecordingWorkerFailedMessage("", "late", true), StartedAt.AddSeconds(2));
        var terminated = session.OnMessage(new RecordingWorkerTerminationMessage(RecordingTerminationOutcome.Failed), StartedAt.AddSeconds(3));

        Assert.Equal(RecordingTerminationOutcome.Completed, completed.TerminationOutcome);
        Assert.Single(completed.Events);
        Assert.Empty(failed.Events);
        Assert.Empty(terminated.Events);
        Assert.Equal(RecordingTerminationOutcome.Completed, terminated.TerminationOutcome);
    }

    [Fact]
    public void 完了後の切断は完了判定を変えない()
    {
        var session = CreateReadySession();
        session.OnMessage(new RecordingWorkerCompletedMessage("done.mp4"), StartedAt.AddSeconds(1));

        var result = session.OnCommunicationLost(StartedAt.AddSeconds(2));

        Assert.Equal(RecordingTerminationOutcome.Completed, result.TerminationOutcome);
        Assert.False(result.ShouldForceTerminateWorker);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void 記録中の切断を録画プロセスの予期しない終了として一度だけ通知する()
    {
        var session = CreateReadySession();
        session.OnMessage(new RecordingWorkerStateMessage(RecordingWorkerRecordingState.Recording), StartedAt);

        var disconnected = session.OnCommunicationLost(StartedAt.AddSeconds(1));
        var exited = session.OnProcessExited(
            RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.Unknown),
            StartedAt.AddSeconds(2));

        var failure = Assert.IsType<RecordingWorkerFailedEvent>(Assert.Single(disconnected.Events));
        Assert.Equal(string.Empty, failure.FilePath);
        Assert.Contains("予期せず終了", failure.Error);
        Assert.False(failure.BeforeRecordingStarted);
        Assert.Equal(RecordingTerminationOutcome.ProcessExited, disconnected.TerminationOutcome);
        Assert.Empty(exited.Events);
    }

    [Fact]
    public void 準備完了前の切断を開始失敗にして残ったプロセスの強制終了を求める()
    {
        var session = CreateStartedSession();

        var disconnected = session.OnCommunicationLost(StartedAt.AddSeconds(1));
        var exited = session.OnProcessExited(
            RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.StartFailed),
            StartedAt.AddSeconds(2));

        Assert.Equal(RecordingTerminationOutcome.Failed, disconnected.TerminationOutcome);
        Assert.True(disconnected.StartFailed);
        Assert.True(disconnected.ShouldForceTerminateWorker);
        Assert.True(Assert.IsType<RecordingWorkerFailedEvent>(Assert.Single(disconnected.Events)).BeforeRecordingStarted);
        Assert.Empty(exited.Events);
    }

    [Fact]
    public void 切断後に届いた完了を無視する()
    {
        var session = CreateReadySession();
        var disconnected = session.OnCommunicationLost(StartedAt.AddSeconds(1));

        var late = session.OnMessage(new RecordingWorkerCompletedMessage("late.mp4"), StartedAt.AddSeconds(2));

        Assert.Equal(RecordingTerminationOutcome.ProcessExited, disconnected.TerminationOutcome);
        Assert.Equal(disconnected.TerminationOutcome, late.TerminationOutcome);
        Assert.Empty(late.Events);
    }

    [Fact]
    public void 期限切れ後に届いた完了を無視する()
    {
        var session = CreateReadySession();
        session.RequestStop(StartedAt);
        var timedOut = session.AdvanceTime(StartedAt + RecordingWorkerSession.StopTimeout);

        var late = session.OnMessage(new RecordingWorkerCompletedMessage("late.mp4"), StartedAt + RecordingWorkerSession.StopTimeout);

        Assert.Equal(RecordingTerminationOutcome.TimedOut, timedOut.TerminationOutcome);
        Assert.True(timedOut.ShouldForceTerminateWorker);
        Assert.Equal(RecordingTerminationOutcome.TimedOut, late.TerminationOutcome);
        Assert.Empty(late.Events);
    }

    [Theory]
    [InlineData("completed", 10)]
    [InlineData("idle", 10)]
    [InlineData("failed", 120)]
    [InlineData("disconnected", 120)]
    public void 各終了判定に強制終了までの期限がある(string ending, int timeoutSeconds)
    {
        var session = CreateReadySession();
        var endedAt = StartedAt.AddSeconds(1);
        switch (ending)
        {
            case "completed":
                session.OnMessage(new RecordingWorkerCompletedMessage("done.mp4"), endedAt);
                break;
            case "idle":
                session.OnMessage(new RecordingWorkerTerminationMessage(RecordingTerminationOutcome.Idle), endedAt);
                break;
            case "failed":
                session.OnMessage(new RecordingWorkerFailedMessage("file.mp4", "error", false), endedAt);
                break;
            case "disconnected":
                session.OnCommunicationLost(endedAt);
                break;
        }

        Assert.False(session.AdvanceTime(endedAt.AddSeconds(timeoutSeconds - 1)).ShouldForceTerminateWorker);
        Assert.True(session.AdvanceTime(endedAt.AddSeconds(timeoutSeconds)).ShouldForceTerminateWorker);
    }

    [Theory]
    [InlineData(RecordingTerminationOutcome.Completed)]
    [InlineData(RecordingTerminationOutcome.Failed)]
    [InlineData(RecordingTerminationOutcome.ProcessExited)]
    public void 完了も失敗も届かないまま終了の判定を受けたら失敗として知らせ二分の期限を置く(RecordingTerminationOutcome outcome)
    {
        var session = CreateReadySession();
        var endedAt = StartedAt.AddSeconds(1);

        var result = session.OnMessage(new RecordingWorkerTerminationMessage(outcome), endedAt);

        Assert.Equal(RecordingTerminationOutcome.Failed, result.TerminationOutcome);
        Assert.IsType<RecordingWorkerFailedEvent>(Assert.Single(result.Events));
        Assert.False(session.AdvanceTime(endedAt + RecordingWorkerSession.FailedExitTimeout - TimeSpan.FromSeconds(1)).ShouldForceTerminateWorker);
        Assert.True(session.AdvanceTime(endedAt + RecordingWorkerSession.FailedExitTimeout).ShouldForceTerminateWorker);
    }

    [Fact]
    public void 完了の後に届いた完了の判定は完了のまま変えない()
    {
        var session = CreateReadySession();
        session.OnMessage(new RecordingWorkerCompletedMessage("done.mp4"), StartedAt.AddSeconds(1));

        var result = session.OnMessage(new RecordingWorkerTerminationMessage(RecordingTerminationOutcome.Completed), StartedAt.AddSeconds(2));

        Assert.Equal(RecordingTerminationOutcome.Completed, result.TerminationOutcome);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void 録画プロセス終了後は強制終了を求めない()
    {
        var session = CreateReadySession();

        var result = session.OnProcessExited(RecordingWorkerExitCodes.ToInt32(RecordingWorkerExitCode.Succeeded), StartedAt.AddSeconds(1));

        Assert.Equal(RecordingTerminationOutcome.ProcessExited, result.TerminationOutcome);
        Assert.Equal(RecordingWorkerExitCode.Succeeded, result.ProcessExitCode);
        Assert.True(session.IsProcessExited);
        Assert.False(result.ShouldForceTerminateWorker);
        Assert.True(Assert.IsType<RecordingWorkerFailedEvent>(Assert.Single(result.Events)).BeforeRecordingStarted);
    }

    [Fact]
    public void 録画中の破棄は停止を送り二分後に強制終了を求める()
    {
        var session = CreateReadySession();
        session.OnMessage(new RecordingWorkerStateMessage(RecordingWorkerRecordingState.Recording), StartedAt);

        var disposed = session.RequestDispose(StartedAt.AddSeconds(1));

        Assert.IsType<RecordingWorkerStopCommand>(Assert.Single(disposed.MessagesToSend));
        Assert.False(disposed.ShouldForceTerminateWorker);
        Assert.False(session.AdvanceTime(StartedAt.AddSeconds(1) + RecordingWorkerSession.StopTimeout - TimeSpan.FromSeconds(1)).ShouldForceTerminateWorker);
        Assert.True(session.AdvanceTime(StartedAt.AddSeconds(1) + RecordingWorkerSession.StopTimeout).ShouldForceTerminateWorker);
    }

    [Fact]
    public void 録画中でない破棄には短い期限を使う()
    {
        var session = CreateReadySession();

        var disposed = session.RequestDispose(StartedAt);

        Assert.IsType<RecordingWorkerStopCommand>(Assert.Single(disposed.MessagesToSend));
        Assert.False(session.AdvanceTime(StartedAt + RecordingWorkerSession.ShortDisposeTimeout - TimeSpan.FromSeconds(1)).ShouldForceTerminateWorker);
        Assert.True(session.AdvanceTime(StartedAt + RecordingWorkerSession.ShortDisposeTimeout).ShouldForceTerminateWorker);
    }

    [Theory]
    [InlineData(RecordingWorkerRecordingState.Paused)]
    [InlineData(RecordingWorkerRecordingState.Saving)]
    public void 一時停止中と保存中の破棄は二分後に強制終了を求める(RecordingWorkerRecordingState state)
    {
        var session = CreateReadySession();
        session.OnMessage(new RecordingWorkerStateMessage(state), StartedAt);

        var disposed = session.RequestDispose(StartedAt.AddSeconds(1));
        var deadline = StartedAt.AddSeconds(1) + RecordingWorkerSession.StopTimeout;

        Assert.IsType<RecordingWorkerStopCommand>(Assert.Single(disposed.MessagesToSend));
        Assert.False(session.AdvanceTime(deadline - TimeSpan.FromSeconds(1)).ShouldForceTerminateWorker);
        Assert.True(session.AdvanceTime(deadline).ShouldForceTerminateWorker);
    }

    [Fact]
    public void 停止後二分の期限切れを受けた破棄は直ちに強制終了を求める()
    {
        var session = CreateReadySession();
        session.RequestStop(StartedAt);
        session.AdvanceTime(StartedAt + RecordingWorkerSession.StopTimeout);

        var result = session.RequestDispose(StartedAt + RecordingWorkerSession.StopTimeout + TimeSpan.FromSeconds(1));

        Assert.True(result.ShouldForceTerminateWorker);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 失敗イベントに記録開始前かどうかを保持する(bool beforeRecordingStarted)
    {
        var session = CreateReadySession();

        var result = session.OnMessage(
            new RecordingWorkerFailedMessage("file.mp4", "write failed", beforeRecordingStarted),
            StartedAt.AddSeconds(1));

        var failed = Assert.IsType<RecordingWorkerFailedEvent>(Assert.Single(result.Events));
        Assert.Equal(beforeRecordingStarted, failed.BeforeRecordingStarted);
        Assert.Equal(beforeRecordingStarted, result.StartFailed);
        Assert.Equal(RecordingTerminationOutcome.Failed, result.TerminationOutcome);
    }

    [Theory]
    [InlineData(RecordingWorkerExitCode.Succeeded, 0)]
    [InlineData(RecordingWorkerExitCode.SavedAfterDisconnect, 10)]
    [InlineData(RecordingWorkerExitCode.SaveFailedAfterDisconnect, 11)]
    [InlineData(RecordingWorkerExitCode.UnhandledException, 20)]
    [InlineData(RecordingWorkerExitCode.StartFailed, 21)]
    [InlineData(RecordingWorkerExitCode.Unknown, 255)]
    public void 終了コードを整数と相互変換できる(RecordingWorkerExitCode exitCode, int value)
    {
        Assert.Equal(value, RecordingWorkerExitCodes.ToInt32(exitCode));
        Assert.Equal(exitCode, RecordingWorkerExitCodes.FromInt32(value));
    }

    [Fact]
    public void 未知の終了コードをUnknownに変換する()
    {
        Assert.Equal(RecordingWorkerExitCode.Unknown, RecordingWorkerExitCodes.FromInt32(87));
    }

    [Theory]
    [InlineData(RecordingWorkerRecordingState.Recording)]
    [InlineData(RecordingWorkerRecordingState.Paused)]
    [InlineData(RecordingWorkerRecordingState.Saving)]
    public void 状態メッセージを状態変化イベントへ変換する(RecordingWorkerRecordingState state)
    {
        var session = CreateReadySession();

        var result = session.OnMessage(new RecordingWorkerStateMessage(state), StartedAt.AddSeconds(1));

        Assert.Equal(new RecordingWorkerStateChangedEvent(state), Assert.Single(result.Events));
    }

    [Fact]
    public void 警告と診断ログをイベントへ変換する()
    {
        var session = CreateReadySession();

        var warning = session.OnMessage(new RecordingWorkerWarningMessage("warning"), StartedAt.AddSeconds(1));
        var log = session.OnMessage(new RecordingWorkerLogMessage(DiagnosticLogLevel.Debug, "record-worker", "entry"), StartedAt.AddSeconds(2));

        Assert.Equal(new RecordingWorkerWarningEvent("warning"), Assert.Single(warning.Events));
        Assert.Equal(new RecordingWorkerLogEvent(DiagnosticLogLevel.Debug, "record-worker", "entry"), Assert.Single(log.Events));
    }

    [Fact]
    public void 完了後に届いた診断ログをイベントへ変換する()
    {
        var session = CreateReadySession();
        session.OnMessage(new RecordingWorkerCompletedMessage("done.mp4"), StartedAt);

        var result = session.OnMessage(
            new RecordingWorkerLogMessage(DiagnosticLogLevel.Info, "record-worker", "保存処理を終了しました"),
            StartedAt.AddSeconds(1));

        Assert.Equal(
            new RecordingWorkerLogEvent(DiagnosticLogLevel.Info, "record-worker", "保存処理を終了しました"),
            Assert.Single(result.Events));
        Assert.Equal(RecordingTerminationOutcome.Completed, result.TerminationOutcome);
    }

    [Fact]
    public void 完了後の破棄では停止を重ねて送らない()
    {
        var session = CreateReadySession();
        session.OnMessage(new RecordingWorkerCompletedMessage("done.mp4"), StartedAt);

        var result = session.RequestDispose(StartedAt.AddSeconds(1));

        Assert.Empty(result.MessagesToSend);
        Assert.False(result.ShouldForceTerminateWorker);
    }

    [Fact]
    public void 完了イベントを完了通知へ変換する()
    {
        var session = CreateReadySession();

        var result = session.OnMessage(new RecordingWorkerCompletedMessage("done.mp4"), StartedAt.AddSeconds(1));

        Assert.Equal(new RecordingWorkerCompletedEvent("done.mp4"), Assert.Single(result.Events));
    }

    private static RecordingWorkerSession CreateStartedSession()
    {
        var session = new RecordingWorkerSession(DiagnosticLogLevel.Debug);
        session.OnProcessStarted(StartedAt, 1234);
        return session;
    }

    private static RecordingWorkerSession CreateReadySession()
    {
        var session = CreateStartedSession();
        var ready = session.OnMessage(
            new RecordingWorkerReadyMessage(1234, RecordingWorkerProtocol.CurrentVersion),
            StartedAt.AddSeconds(1));
        Assert.IsType<RecordingWorkerReadyResponseCommand>(Assert.Single(ready.MessagesToSend));
        return session;
    }

    private static RecordingWorkerStartData CreateStartData() => new(
        "sample.mp4",
        RecordingWorkerSourceKind.Window,
        "DISPLAY1",
        null,
        987654321,
        new RecordingWorkerSize(1920, 1080),
        new RecordingWorkerSize(1280, 720),
        30,
        12,
        true,
        false,
        true,
        true,
        true,
        false,
        null,
        192);
}
