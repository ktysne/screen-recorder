using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class ShortcutBindingsTests
{
    [Fact]
    public void AllContainsEveryShortcutActionExactlyOnceInRegistrationOrder()
    {
        Assert.Equal(
            Enum.GetValues<RecorderAction>().Where(action => action != RecorderAction.StopRecording),
            ShortcutBindings.All.Select(binding => binding.Action));
        Assert.Equal(ShortcutBindings.All.Count, ShortcutBindings.All.Select(binding => binding.Action).Distinct().Count());
    }

    [Fact]
    public void SettingNamesReferToSettingsProperties()
    {
        foreach (var binding in ShortcutBindings.All)
        {
            Assert.NotNull(typeof(Settings).GetProperty(binding.SettingName));
            Assert.NotNull(typeof(Settings).GetProperty(binding.EnabledSettingName));
        }
    }

    [Fact]
    public void SettersChangeOnlyTheirOwnProperties()
    {
        foreach (var binding in ShortcutBindings.All)
        {
            var settings = new Settings();
            var otherBindings = ShortcutBindings.All.Where(other => other.Action != binding.Action).ToArray();
            var otherNotations = otherBindings.Select(other => other.GetNotation(settings)).ToArray();
            var otherEnabled = otherBindings.Select(other => other.GetEnabled(settings)).ToArray();

            binding.SetNotation(settings, "Ctrl+F24");
            Assert.Equal("Ctrl+F24", binding.GetNotation(settings));
            Assert.Equal(otherNotations, otherBindings.Select(other => other.GetNotation(settings)));

            binding.SetEnabled(settings, false);
            Assert.False(binding.GetEnabled(settings));
            Assert.Equal(otherEnabled, otherBindings.Select(other => other.GetEnabled(settings)));
        }
    }
}
