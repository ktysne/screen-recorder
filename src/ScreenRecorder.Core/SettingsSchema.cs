namespace ScreenRecorder.Core;

public abstract class IntSetting
{
    private readonly Func<int, bool> _isValid;

    protected IntSetting(
        string name,
        int defaultValue,
        Func<int, bool> isValid,
        Func<Settings, int> get,
        Action<Settings, int> set)
    {
        Name = name;
        Default = defaultValue;
        _isValid = isValid;
        Get = get;
        Set = set;
        if (!IsValid(Default)) throw new ArgumentException($"設定 {Name} の既定値が許容範囲外です。", nameof(defaultValue));
    }

    public string Name { get; }
    public int Default { get; }
    public Func<Settings, int> Get { get; }
    public Action<Settings, int> Set { get; }
    public bool IsValid(int value) => _isValid(value);
}

public sealed class IntChoiceSetting : IntSetting
{
    internal IntChoiceSetting(
        string name,
        int defaultValue,
        int[] choices,
        Func<Settings, int> get,
        Action<Settings, int> set)
        : base(name, defaultValue, choices.Contains, get, set)
    {
        Choices = Array.AsReadOnly(choices);
    }

    public IReadOnlyList<int> Choices { get; }
}

public sealed class IntRangeSetting : IntSetting
{
    internal IntRangeSetting(
        string name,
        int defaultValue,
        int min,
        int max,
        Func<Settings, int> get,
        Action<Settings, int> set)
        : base(name, defaultValue, value => value >= min && value <= max, get, set)
    {
        Min = min;
        Max = max;
    }

    public int Min { get; }
    public int Max { get; }
}

public static class SettingsSchema
{
    public static IntRangeSetting JpegQuality { get; } = new(
        nameof(Settings.JpegQuality), 98, 1, 100,
        settings => settings.JpegQuality,
        (settings, value) => settings.JpegQuality = value);

    public static IntChoiceSetting CaptureDelaySeconds { get; } = new(
        nameof(Settings.CaptureDelaySeconds), 0, [0, 3, 5, 10],
        settings => settings.CaptureDelaySeconds,
        (settings, value) => settings.CaptureDelaySeconds = value);

    public static IntChoiceSetting FrameRate { get; } = new(
        nameof(Settings.FrameRate), 30, [15, 24, 30, 60],
        settings => settings.FrameRate,
        (settings, value) => settings.FrameRate = value);

    public static IntRangeSetting VideoBitrateMbps { get; } = new(
        nameof(Settings.VideoBitrateMbps), 12, 1, 100,
        settings => settings.VideoBitrateMbps,
        (settings, value) => settings.VideoBitrateMbps = value);

    public static IntChoiceSetting CountdownSeconds { get; } = new(
        nameof(Settings.CountdownSeconds), 3, [0, 3, 5],
        settings => settings.CountdownSeconds,
        (settings, value) => settings.CountdownSeconds = value);

    // 選択肢の順序は設定画面の表示順として使う。
    public static IntChoiceSetting OutputScalePercent { get; } = new(
        nameof(Settings.OutputScalePercent), 100, [100, 75, 50],
        settings => settings.OutputScalePercent,
        (settings, value) => settings.OutputScalePercent = value);

    public static IntChoiceSetting AacBitrateKbps { get; } = new(
        nameof(Settings.AacBitrateKbps), 192, [96, 128, 160, 192],
        settings => settings.AacBitrateKbps,
        (settings, value) => settings.AacBitrateKbps = value);

    public static IntChoiceSetting Mp3BitrateKbps { get; } = new(
        nameof(Settings.Mp3BitrateKbps), 192, [128, 192, 256, 320],
        settings => settings.Mp3BitrateKbps,
        (settings, value) => settings.Mp3BitrateKbps = value);

    public static IReadOnlyList<IntSetting> IntSettings { get; } = Array.AsReadOnly<IntSetting>(
    [
        JpegQuality,
        CaptureDelaySeconds,
        FrameRate,
        VideoBitrateMbps,
        CountdownSeconds,
        OutputScalePercent,
        AacBitrateKbps,
        Mp3BitrateKbps
    ]);
}
