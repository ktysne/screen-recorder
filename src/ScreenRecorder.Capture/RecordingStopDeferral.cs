namespace ScreenRecorder.Capture;

internal sealed class RecordingStopDeferral : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _timeout;
    private readonly Func<TimeSpan, Action, IDisposable> _scheduleTimer;
    private IDisposable? _timer;
    private bool _isPaused;
    private bool _waitingForFrame;
    private bool _stopPending;
    private bool _stopIssued;
    private bool _disposed;

    public RecordingStopDeferral(TimeSpan timeout, Func<TimeSpan, Action, IDisposable> scheduleTimer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
        _scheduleTimer = scheduleTimer ?? throw new ArgumentNullException(nameof(scheduleTimer));
    }

    public event Action? StopReady;

    public void MarkPaused()
    {
        IDisposable? timer;
        Action? stopReady = null;
        lock (_gate)
        {
            if (_disposed || _stopIssued) return;
            _isPaused = true;
            _waitingForFrame = false;
            timer = _timer;
            _timer = null;
            if (_stopPending)
            {
                _stopPending = false;
                _stopIssued = true;
                stopReady = StopReady;
            }
        }
        timer?.Dispose();
        stopReady?.Invoke();
    }

    public void MarkResumed()
    {
        lock (_gate)
        {
            if (_disposed || _stopIssued || !_isPaused) return;
            _isPaused = false;
            _waitingForFrame = true;
            _timer = _scheduleTimer(_timeout, OnTimeout);
        }
    }

    public bool TryRequestStop(bool forceImmediate = false)
    {
        IDisposable? timer;
        lock (_gate)
        {
            if (_disposed || _stopIssued) return false;
            if (!forceImmediate && _waitingForFrame && !_isPaused)
            {
                _stopPending = true;
                return false;
            }
            _stopIssued = true;
            _stopPending = false;
            _waitingForFrame = false;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        return true;
    }

    public void MarkFrameRecorded()
    {
        CompleteFrameWait();
    }

    public void Cancel()
    {
        IDisposable? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopPending = false;
            _waitingForFrame = false;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }

    public void Dispose() => Cancel();

    private void OnTimeout() => CompleteFrameWait();

    private void CompleteFrameWait()
    {
        IDisposable? timer;
        Action? stopReady = null;
        lock (_gate)
        {
            if (_disposed || !_waitingForFrame) return;
            _waitingForFrame = false;
            timer = _timer;
            _timer = null;
            if (_stopPending)
            {
                _stopPending = false;
                _stopIssued = true;
                stopReady = StopReady;
            }
        }
        timer?.Dispose();
        stopReady?.Invoke();
    }
}
