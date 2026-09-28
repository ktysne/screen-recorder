using ScreenRecorder.Capture;
using Xunit;

namespace ScreenRecorder.Capture.Tests;

public sealed class RecordingStopDeferralTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1200);

    [Fact]
    public void StopWaitsForFirstFrameAfterResumeAndThenRunsOnce()
    {
        var timers = new FakeTimerScheduler();
        using var deferral = new RecordingStopDeferral(Timeout, timers.Schedule);
        var stopCount = 0;
        deferral.StopReady += () => stopCount++;
        deferral.MarkPaused();
        deferral.MarkResumed();

        Assert.False(deferral.TryRequestStop());
        Assert.Equal(0, stopCount);
        deferral.MarkFrameRecorded();

        Assert.Equal(1, stopCount);
        Assert.False(deferral.TryRequestStop());
    }

    [Fact]
    public void StopRunsWhenNoFrameArrivesBeforeTheDeadline()
    {
        var timers = new FakeTimerScheduler();
        using var deferral = new RecordingStopDeferral(Timeout, timers.Schedule);
        var stopCount = 0;
        deferral.StopReady += () => stopCount++;
        deferral.MarkPaused();
        deferral.MarkResumed();

        Assert.False(deferral.TryRequestStop());
        Assert.Equal(Timeout, timers.LastDelay);
        timers.FireLatest();

        Assert.Equal(1, stopCount);
    }

    [Fact]
    public void StopRunsImmediatelyWhenAFrameWasWrittenAfterResume()
    {
        var timers = new FakeTimerScheduler();
        using var deferral = new RecordingStopDeferral(Timeout, timers.Schedule);
        deferral.MarkPaused();
        deferral.MarkResumed();
        deferral.MarkFrameRecorded();

        Assert.True(deferral.TryRequestStop());
    }

    [Fact]
    public void StopRunsImmediatelyWhenRecordingWasNeverPaused()
    {
        using var deferral = new RecordingStopDeferral(Timeout, new FakeTimerScheduler().Schedule);

        Assert.True(deferral.TryRequestStop());
    }

    [Fact]
    public void StopRunsImmediatelyWhilePaused()
    {
        using var deferral = new RecordingStopDeferral(Timeout, new FakeTimerScheduler().Schedule);
        deferral.MarkPaused();

        Assert.True(deferral.TryRequestStop());
    }

    [Fact]
    public void StopIsIssuedOnlyOnce()
    {
        var timers = new FakeTimerScheduler();
        using var deferral = new RecordingStopDeferral(Timeout, timers.Schedule);
        var stopCount = 0;
        deferral.StopReady += () => stopCount++;
        deferral.MarkPaused();
        deferral.MarkResumed();

        Assert.False(deferral.TryRequestStop());
        Assert.False(deferral.TryRequestStop());
        timers.FireLatest();
        deferral.MarkFrameRecorded();

        Assert.Equal(1, stopCount);
        Assert.False(deferral.TryRequestStop());
    }

    [Fact]
    public void CancelDiscardsADeferredStop()
    {
        var timers = new FakeTimerScheduler();
        var deferral = new RecordingStopDeferral(Timeout, timers.Schedule);
        var stopCount = 0;
        deferral.StopReady += () => stopCount++;
        deferral.MarkPaused();
        deferral.MarkResumed();
        Assert.False(deferral.TryRequestStop());

        deferral.Cancel();
        timers.FireLatest();
        deferral.MarkFrameRecorded();

        Assert.Equal(0, stopCount);
        Assert.False(deferral.TryRequestStop());
    }

    private sealed class FakeTimerScheduler
    {
        private readonly List<FakeTimer> _timers = [];

        public TimeSpan? LastDelay { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            LastDelay = delay;
            var timer = new FakeTimer(callback);
            _timers.Add(timer);
            return timer;
        }

        public void FireLatest() => _timers[^1].Fire();
    }

    private sealed class FakeTimer(Action callback) : IDisposable
    {
        private bool _disposed;

        public void Fire()
        {
            if (!_disposed) callback();
        }

        public void Dispose() => _disposed = true;
    }
}
