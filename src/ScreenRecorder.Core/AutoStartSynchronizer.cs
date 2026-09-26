namespace ScreenRecorder.Core;

public interface IAutoStartRegistry
{
    void Set(string valueName, string executablePath);
    void Remove(string valueName);
}

public sealed class AutoStartSynchronizer(IAutoStartRegistry registry, string executablePath)
{
    public const string ValueName = "ScreenRecorder";

    public void Apply(bool enabled)
    {
        if (enabled) registry.Set(ValueName, executablePath);
        else registry.Remove(ValueName);
    }
}
