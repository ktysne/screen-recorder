namespace ScreenRecorder.Core;

public enum SaveDirectoryKind
{
    StillImage,
    Video
}

public static class SaveDirectoryRules
{
    public static bool IsConfirmed(string? confirmedDirectory, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(confirmedDirectory) || string.IsNullOrWhiteSpace(currentDirectory)) return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(confirmedDirectory),
                Path.GetFullPath(currentDirectory),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    public static IReadOnlyList<string> GetFallbackDirectories(SaveDirectoryKind kind, string? userProfile, string? localAppData)
    {
        var folderName = kind switch
        {
            SaveDirectoryKind.StillImage => "Pictures",
            SaveDirectoryKind.Video => "Videos",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var candidates = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(userProfile))
            candidates.Add(Path.Combine(userProfile, "ScreenRecorder", folderName));
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "ScreenRecorder", folderName));
        return candidates;
    }
}
