using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class ShortcutCaptureStateTests
{
    [Fact]
    public void ModifierPreviewReturnsToCommittedValueWhenAllModifiersAreReleased()
    {
        var state = new ShortcutCaptureState("F2");

        state.UpdateModifiers(HotkeyModifiers.Control | HotkeyModifiers.Shift);
        Assert.Equal("Ctrl+Shift+", state.DisplayText);
        Assert.True(state.IsPreviewing);

        state.UpdateModifiers(HotkeyModifiers.None);

        Assert.Equal("F2", state.DisplayText);
        Assert.False(state.IsPreviewing);
    }

    [Fact]
    public void SupportedKeyCommitsAndRepeatedKeyIsIdempotent()
    {
        var state = new ShortcutCaptureState("F2");

        Assert.Equal(ShortcutCaptureResult.Assigned, state.CaptureKey(0x2C, HotkeyModifiers.Control));
        Assert.Equal("Ctrl+PrtSc", state.CommittedValue);
        Assert.Equal(ShortcutCaptureResult.Assigned, state.CaptureKey(0x2C, HotkeyModifiers.Control));
        Assert.Equal("Ctrl+PrtSc", state.CommittedValue);
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x2E)]
    public void BackspaceAndDeleteClearCommittedValue(int virtualKey)
    {
        var state = new ShortcutCaptureState("Ctrl+F2");

        Assert.Equal(ShortcutCaptureResult.Cleared, state.CaptureKey(virtualKey, HotkeyModifiers.None));

        Assert.Equal(string.Empty, state.CommittedValue);
        Assert.Equal(string.Empty, state.DisplayText);
    }

    [Theory]
    [InlineData(0x09, HotkeyModifiers.None, true)]
    [InlineData(0x09, HotkeyModifiers.Shift, true)]
    [InlineData(0x09, HotkeyModifiers.Control, false)]
    [InlineData(0x09, HotkeyModifiers.Alt, false)]
    [InlineData(0x09, HotkeyModifiers.Windows, false)]
    [InlineData(0x0D, HotkeyModifiers.None, true)]
    [InlineData(0x0D, HotkeyModifiers.Shift, false)]
    [InlineData(0x1B, HotkeyModifiers.None, true)]
    [InlineData(0x1B, HotkeyModifiers.Control, false)]
    public void FormCommandsPassThroughOnlyForSpecifiedModifierCombinations(int virtualKey, HotkeyModifiers modifiers, bool expected)
    {
        Assert.Equal(expected, ShortcutCaptureState.ShouldPassToForm(virtualKey, modifiers));
    }

    [Fact]
    public void UnsupportedKeyKeepsCommittedValueAndEndsPreview()
    {
        var state = new ShortcutCaptureState("Ctrl+F2");
        state.UpdateModifiers(HotkeyModifiers.Alt);

        Assert.Equal(ShortcutCaptureResult.Unsupported, state.CaptureKey(0xE5, HotkeyModifiers.Alt));

        Assert.Equal("Ctrl+F2", state.CommittedValue);
        Assert.Equal("Ctrl+F2", state.DisplayText);
    }

    [Fact]
    public void ModifierKeyOnlyUpdatesPreview()
    {
        var state = new ShortcutCaptureState("F2");

        Assert.Equal(ShortcutCaptureResult.ModifierPreview, state.CaptureKey(0x10, HotkeyModifiers.Shift));

        Assert.Equal("F2", state.CommittedValue);
        Assert.Equal("Shift+", state.DisplayText);
    }
}
