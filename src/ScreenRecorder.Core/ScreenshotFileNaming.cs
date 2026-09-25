namespace ScreenRecorder.Core;

public enum ScreenshotMode { Full, Region, Window }

public static class ScreenshotFileNaming
{
    public const string DefaultTemplate = "ScreenRecorder_{date}_{time}";
    private static readonly HashSet<char> InvalidWindowsFileNameCharacters = CreateInvalidCharacters();

    public static string ExpandTemplate(string? template, DateTime capturedAt, ScreenshotMode mode, string? windowTitle = null)
    {
        var candidate = Expand(template, capturedAt, mode, windowTitle);
        if (IsUsableFileName(candidate)) return candidate;
        return Expand(DefaultTemplate, capturedAt, mode, windowTitle);
    }

    public static string GetAvailablePath(
        string baseDirectory,
        bool organizeByMonth,
        DateTime capturedAt,
        ScreenshotMode mode,
        string? windowTitle,
        string? fileNameTemplate,
        string extension,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);

        var directory = organizeByMonth
            ? Path.Combine(baseDirectory, capturedAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))
            : baseDirectory;
        var baseName = ExpandTemplate(fileNameTemplate, capturedAt, mode, windowTitle);
        var normalizedExtension = extension.StartsWith('.') ? extension : $".{extension}";
        for (var suffix = 1; ; suffix++)
        {
            var suffixText = suffix == 1 ? string.Empty : $"_{suffix}";
            var candidate = Path.Combine(directory, $"{baseName}{suffixText}{normalizedExtension}");
            if (!fileExists(candidate)) return candidate;
        }
    }

    private static string Expand(string? template, DateTime capturedAt, ScreenshotMode mode, string? windowTitle)
    {
        var value = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        value = value.Replace("{date}", capturedAt.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{time}", capturedAt.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{mode}", mode switch
            {
                ScreenshotMode.Full => "full",
                ScreenshotMode.Region => "region",
                ScreenshotMode.Window => "window",
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            }, StringComparison.Ordinal)
            .Replace("{window}", SanitizeWindowTitle(windowTitle), StringComparison.Ordinal);
        return value.TrimEnd(' ', '.');
    }

    private static string SanitizeWindowTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;
        var sanitized = new string(title.Select(character => InvalidWindowsFileNameCharacters.Contains(character) ? '_' : character).ToArray())
            .TrimEnd(' ', '.');
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(sanitized);
        var result = new System.Text.StringBuilder();
        for (var count = 0; count < 40 && elements.MoveNext(); count++) result.Append(elements.GetTextElement());
        return result.ToString().TrimEnd(' ', '.');
    }

    private static bool IsUsableFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..") return false;
        if (value.Any(InvalidWindowsFileNameCharacters.Contains)) return false;
        var deviceName = value.Split('.')[0].TrimEnd(' ', '.');
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return false;
        if (deviceName.Length == 4
            && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && deviceName[3] is >= '1' and <= '9') return false;
        return !deviceName.Equals("COM¹", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("COM²", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("COM³", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("LPT¹", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("LPT²", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("LPT³", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<char> CreateInvalidCharacters()
    {
        var characters = new HashSet<char>("<>:\"/\\|?*");
        for (var character = 0; character < 32; character++) characters.Add((char)character);
        return characters;
    }
}
