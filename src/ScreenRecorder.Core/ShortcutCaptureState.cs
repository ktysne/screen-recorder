namespace ScreenRecorder.Core;

public enum ShortcutCaptureResult
{
    PassThrough,
    ModifierPreview,
    Assigned,
    Cleared,
    Unsupported
}

public sealed class ShortcutCaptureState
{
    private const HotkeyModifiers AllModifiers = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows;
    private HotkeyModifiers _previewModifiers;

    public ShortcutCaptureState(string? committedValue = null)
    {
        CommittedValue = committedValue ?? string.Empty;
    }

    public string CommittedValue { get; private set; }

    public bool IsPreviewing => _previewModifiers != HotkeyModifiers.None;

    public string DisplayText => IsPreviewing ? FormatModifierPreview(_previewModifiers) : CommittedValue;

    public void SetCommittedValue(string? value)
    {
        CommittedValue = value ?? string.Empty;
        _previewModifiers = HotkeyModifiers.None;
    }

    public void UpdateModifiers(HotkeyModifiers modifiers) => _previewModifiers = modifiers & AllModifiers;

    public ShortcutCaptureResult CaptureKey(int virtualKey, HotkeyModifiers modifiers)
    {
        UpdateModifiers(modifiers);
        if (ShouldPassToForm(virtualKey, modifiers)) return ShortcutCaptureResult.PassThrough;
        if (virtualKey is 0x08 or 0x2E)
        {
            CommittedValue = string.Empty;
            _previewModifiers = HotkeyModifiers.None;
            return ShortcutCaptureResult.Cleared;
        }
        if (IsModifierKey(virtualKey)) return ShortcutCaptureResult.ModifierPreview;
        if (!HotkeyShortcut.TryCreate(modifiers, virtualKey, out var shortcut))
        {
            _previewModifiers = HotkeyModifiers.None;
            return ShortcutCaptureResult.Unsupported;
        }

        CommittedValue = shortcut!.ToDisplayString();
        _previewModifiers = HotkeyModifiers.None;
        return ShortcutCaptureResult.Assigned;
    }

    public void ClearPreview() => _previewModifiers = HotkeyModifiers.None;

    public static bool ShouldPassToForm(int virtualKey, HotkeyModifiers modifiers) =>
        virtualKey == 0x09
            ? (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) == HotkeyModifiers.None
            : (virtualKey is 0x0D or 0x1B) && modifiers == HotkeyModifiers.None;

    public static bool IsModifierKey(int virtualKey) => virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
        or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static string FormatModifierPreview(HotkeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        return string.Join('+', parts) + "+";
    }
}
