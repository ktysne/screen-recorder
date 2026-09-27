namespace ScreenRecorder.App;

internal sealed class UiDispatcher : IDisposable
{
    private readonly Control _control = new();

    public UiDispatcher()
    {
        _ = _control.Handle;
    }

    public void Post(Action action)
    {
        if (_control.IsDisposed || !_control.IsHandleCreated) return;
        try { _control.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    public Task<TResult> InvokeAsync<TResult>(Func<TResult> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<TResult>(cancellationToken);
        if (_control.IsDisposed || !_control.IsHandleCreated)
            return Task.FromException<TResult>(new ObjectDisposedException(nameof(UiDispatcher)));

        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            _control.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (!completion.Task.IsCompleted) completion.TrySetResult(action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    registration.Dispose();
                }
            }));
        }
        catch (ObjectDisposedException exception)
        {
            registration.Dispose();
            completion.TrySetException(exception);
        }
        catch (InvalidOperationException exception)
        {
            registration.Dispose();
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    public void Dispose() => _control.Dispose();
}
