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

    public void Dispose() => _control.Dispose();
}
