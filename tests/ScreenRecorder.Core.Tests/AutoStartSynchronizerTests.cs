using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class AutoStartSynchronizerTests
{
    [Fact]
    public void EnabledUsesHeldExecutablePathAndExpectedValueName()
    {
        var registry = new FakeRegistry();
        new AutoStartSynchronizer(registry, @"C:\Apps\ScreenRecorder.exe").Apply(true);
        Assert.Equal(("ScreenRecorder", @"C:\Apps\ScreenRecorder.exe"), registry.SetValue);
        Assert.Null(registry.RemovedValue);
    }

    [Fact]
    public void DisabledRemovesRunValue()
    {
        var registry = new FakeRegistry();
        new AutoStartSynchronizer(registry, "app.exe").Apply(false);
        Assert.Equal("ScreenRecorder", registry.RemovedValue);
        Assert.Null(registry.SetValue);
    }

    private sealed class FakeRegistry : IAutoStartRegistry
    {
        public (string Name, string Path)? SetValue { get; private set; }
        public string? RemovedValue { get; private set; }
        public void Set(string valueName, string executablePath) => SetValue = (valueName, executablePath);
        public void Remove(string valueName) => RemovedValue = valueName;
    }
}
