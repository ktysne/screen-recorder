namespace ScreenRecorder.App;

internal sealed class ShortcutInputBox : TextBox
{
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private bool _printScreenKeyDownHandled;

    public ShortcutInputBox()
    {
        ReadOnly = true;
        ImeMode = ImeMode.Disable;
    }

    public event Func<Keys, bool>? ShortcutKeyDown;
    public event Action? PrintScreenKeyUp;
    public event Action<Keys>? ShortcutKeyUp;

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (message.Msg is WmKeyDown or WmSysKeyDown)
        {
            if (HandleShortcutKeyDown(keyData)) return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (HandleShortcutKeyDown(eventArgs.KeyData))
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }
        base.OnKeyDown(eventArgs);
    }

    protected override void WndProc(ref Message message)
    {
        var isKeyUp = message.Msg is WmKeyUp or WmSysKeyUp;
        var key = (Keys)message.WParam.ToInt32() & Keys.KeyCode;
        base.WndProc(ref message);
        if (!isKeyUp) return;

        if (key == Keys.PrintScreen)
        {
            if (!_printScreenKeyDownHandled) PrintScreenKeyUp?.Invoke();
            _printScreenKeyDownHandled = false;
        }
        ShortcutKeyUp?.Invoke(key);
    }

    private bool HandleShortcutKeyDown(Keys keyData)
    {
        var handled = ShortcutKeyDown?.Invoke(keyData) ?? false;
        if ((keyData & Keys.KeyCode) == Keys.PrintScreen) _printScreenKeyDownHandled = handled;
        return handled;
    }
}
