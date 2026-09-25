namespace ScreenRecorder.Core;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public enum RecorderAction
{
    ScreenshotRegion,
    ScreenshotFullScreen,
    ScreenshotWindow,
    RecordingRegion,
    RecordingFullScreen,
    RecordingWindow,
    PauseResume,
    StopRecording
}

public sealed record HotkeyShortcut
{
    private const HotkeyModifiers AllModifiers = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows;

    private HotkeyShortcut(HotkeyModifiers modifiers, ushort virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public HotkeyModifiers Modifiers { get; }
    public ushort VirtualKey { get; }

    public static bool TryParse(string? notation, out HotkeyShortcut? shortcut)
    {
        shortcut = null;
        if (string.IsNullOrWhiteSpace(notation)) return true;

        var parts = notation.Split('+');
        var modifiers = HotkeyModifiers.None;
        ushort? virtualKey = null;
        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (part.Length == 0) return false;
            if (TryParseModifier(part, out var modifier))
            {
                if ((modifiers & modifier) != 0) return false;
                modifiers |= modifier;
                continue;
            }

            if (virtualKey is not null || !TryParseVirtualKey(part, out var parsedKey)) return false;
            virtualKey = parsedKey;
        }

        if (virtualKey is null) return false;
        shortcut = new HotkeyShortcut(modifiers, virtualKey.Value);
        return true;
    }

    public static bool TryCreate(HotkeyModifiers modifiers, int virtualKey, out HotkeyShortcut? shortcut)
    {
        shortcut = null;
        if ((modifiers & ~AllModifiers) != 0 || virtualKey is < 0 or > ushort.MaxValue || !IsSupportedVirtualKey((ushort)virtualKey)) return false;
        shortcut = new HotkeyShortcut(modifiers, (ushort)virtualKey);
        return true;
    }

    public string ToDisplayString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(DisplayVirtualKey(VirtualKey));
        return string.Join('+', parts);
    }

    public override string ToString() => ToDisplayString();

    private static bool TryParseModifier(string value, out HotkeyModifiers modifier)
    {
        modifier = value.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => HotkeyModifiers.Control,
            "ALT" => HotkeyModifiers.Alt,
            "SHIFT" => HotkeyModifiers.Shift,
            "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
            _ => HotkeyModifiers.None
        };
        return modifier != HotkeyModifiers.None;
    }

    private static bool TryParseVirtualKey(string value, out ushort virtualKey)
    {
        var key = value.Trim();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            virtualKey = char.ToUpperInvariant(key[0]);
            return true;
        }
        if (key.Length == 1 && key[0] is >= '0' and <= '9')
        {
            virtualKey = key[0];
            return true;
        }
        if (key.Length > 1 && key[0] is 'F' or 'f' && int.TryParse(key.AsSpan(1), out var functionNumber) && functionNumber is >= 1 and <= 24)
        {
            virtualKey = (ushort)(0x70 + functionNumber - 1);
            return true;
        }
        if (TryNamedVirtualKey(key, out virtualKey)) return true;
        virtualKey = 0;
        return false;
    }

    private static bool TryNamedVirtualKey(string name, out ushort virtualKey)
    {
        var normalized = name.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        virtualKey = normalized switch
        {
            "PRTSC" or "PRINTSCREEN" => 0x2C,
            "BACKSPACE" or "BACK" => 0x08,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "PAUSE" => 0x13,
            "CAPSLOCK" => 0x14,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "NUMLOCK" => 0x90,
            "SCROLLLOCK" => 0x91,
            "SEMICOLON" => 0xBA,
            "EQUALS" or "=" => 0xBB,
            "COMMA" => 0xBC,
            "MINUS" => 0xBD,
            "PERIOD" => 0xBE,
            "SLASH" => 0xBF,
            "BACKTICK" => 0xC0,
            "LBRACKET" => 0xDB,
            "BACKSLASH" => 0xDC,
            "RBRACKET" => 0xDD,
            "APOSTROPHE" => 0xDE,
            "PLUS" => 0xBB,
            _ => 0
        };
        if (virtualKey != 0) return true;

        if (normalized.StartsWith("NUMPAD", StringComparison.Ordinal) && normalized.Length == 7 && normalized[6] is >= '0' and <= '9')
        {
            virtualKey = (ushort)(0x60 + normalized[6] - '0');
            return true;
        }
        return false;
    }

    private static bool IsSupportedVirtualKey(ushort key)
    {
        if (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x69 or >= 0x70 and <= 0x87) return true;
        return key is 0x08 or 0x09 or 0x0D or 0x13 or 0x14 or 0x1B or 0x20 or 0x21 or 0x22 or 0x23 or 0x24
            or 0x25 or 0x26 or 0x27 or 0x28 or 0x2C or 0x2D or 0x2E or 0x90 or 0x91
            or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDE;
    }

    private static string DisplayVirtualKey(ushort key)
    {
        if (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)key).ToString();
        if (key is >= 0x60 and <= 0x69) return $"NumPad{key - 0x60}";
        if (key is >= 0x70 and <= 0x87) return $"F{key - 0x70 + 1}";
        return key switch
        {
            0x2C => "PrtSc",
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xBA => "Semicolon",
            0xBB => "Equals",
            0xBC => "Comma",
            0xBD => "Minus",
            0xBE => "Period",
            0xBF => "Slash",
            0xC0 => "Backtick",
            0xDB => "LBracket",
            0xDC => "Backslash",
            0xDD => "RBracket",
            0xDE => "Apostrophe",
            _ => throw new InvalidOperationException("Unsupported virtual key.")
        };
    }
}
