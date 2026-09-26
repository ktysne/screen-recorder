using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal enum HotkeyFailureReason
{
    InvalidNotation,
    Duplicate,
    RegistrationFailed
}

internal sealed record HotkeyFailure(RecorderAction Action, string Notation, HotkeyFailureReason Reason, int? ErrorCode, bool PrintScreenSettingsEnabled);

internal sealed class HotkeyManager : IDisposable
{
    private const uint NoRepeat = 0x4000;
    private readonly HotkeyMessageWindow _window;
    private readonly Dictionary<int, RecorderAction> _actionsById = [];
    private readonly HashSet<int> _registeredIds = [];
    private IReadOnlyList<HotkeyFailure> _failures = [];

    public HotkeyManager(Action<RecorderAction> actionHandler)
    {
        _actionHandler = actionHandler;
        _window = new HotkeyMessageWindow(Dispatch);
    }

    public IReadOnlyList<HotkeyFailure> Failures => _failures;

    public IReadOnlyList<HotkeyFailure> Replace(Settings settings)
    {
        UnregisterAll();
        var assignments = ShortcutSettingsValidator.GetAssignments(settings);
        var issuesByAction = ShortcutSettingsValidator.Validate(settings).ToDictionary(issue => issue.Action);
        var failures = new List<HotkeyFailure>();
        for (var index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            if (!assignment.Enabled) continue;
            if (!HotkeyShortcut.TryParse(assignment.Notation, out var shortcut))
            {
                failures.Add(new HotkeyFailure(assignment.Action, assignment.Notation, HotkeyFailureReason.InvalidNotation, null, false));
                continue;
            }
            if (shortcut is null) continue;
            if (issuesByAction.TryGetValue(assignment.Action, out var issue) && issue.Kind == ShortcutValidationIssueKind.Duplicate)
            {
                failures.Add(new HotkeyFailure(assignment.Action, assignment.Notation, HotkeyFailureReason.Duplicate, null, false));
                continue;
            }

            var id = index + 1;
            if (!RegisterHotKey(_window.Handle, id, ToNativeModifiers(shortcut.Modifiers) | NoRepeat, shortcut.VirtualKey))
            {
                var settingsEnabled = shortcut.Modifiers == HotkeyModifiers.None && shortcut.VirtualKey == 0x2C && IsPrintScreenSnippingEnabled();
                failures.Add(new HotkeyFailure(assignment.Action, assignment.Notation, HotkeyFailureReason.RegistrationFailed, Marshal.GetLastWin32Error(), settingsEnabled));
                continue;
            }
            _registeredIds.Add(id);
            _actionsById.Add(id, assignment.Action);
        }
        _failures = failures;
        return _failures;
    }

    public static void OpenPrintScreenKeyboardSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true });
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }

    private readonly Action<RecorderAction> _actionHandler;

    private void UnregisterAll()
    {
        foreach (var id in _registeredIds) UnregisterHotKey(_window.Handle, id);
        _registeredIds.Clear();
        _actionsById.Clear();
    }

    private void Dispatch(int id)
    {
        if (_actionsById.TryGetValue(id, out var action)) _actionHandler(action);
    }

    private static uint ToNativeModifiers(HotkeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) result |= 0x0001;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) result |= 0x0002;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) result |= 0x0004;
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) result |= 0x0008;
        return result;
    }

    private static bool IsPrintScreenSnippingEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
            return key?.GetValue("PrintScreenKeyForSnippingEnabled") is int value && value == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    private sealed class HotkeyMessageWindow : NativeWindow, IDisposable
    {
        private readonly Action<int> _dispatch;

        public HotkeyMessageWindow(Action<int> dispatch)
        {
            _dispatch = dispatch;
            var parameters = new CreateParams { Caption = "ScreenRecorder.Hotkeys", Parent = new IntPtr(-3) };
            CreateHandle(parameters);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312)
            {
                _dispatch(message.WParam.ToInt32());
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose() => DestroyHandle();
    }
}
