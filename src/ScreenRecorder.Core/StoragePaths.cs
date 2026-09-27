namespace ScreenRecorder.Core;

public static class StoragePaths
{
    private const string TestDataDirectoryEnvironmentVariable = "SCREENRECORDER_TEST_DATA_DIR";

    /// <summary>ScreenRecorder の設定フォルダーを返します。</summary>
    public static string GetSettingsDirectory() => GetTestDataDirectory() is { } testDirectory
        ? Path.Combine(testDirectory, "settings")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRecorder");

    /// <summary>ScreenRecorder の診断ログフォルダーを返します。</summary>
    public static string GetLogsDirectory() => GetTestDataDirectory() is { } testDirectory
        ? Path.Combine(testDirectory, "logs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenRecorder", "logs");

    public static string GetUpdateDirectory() => GetTestDataDirectory() is { } testDirectory
        ? Path.Combine(testDirectory, "update")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenRecorder", "update");

    public static string? GetTestDataDirectory() => Environment.GetEnvironmentVariable(TestDataDirectoryEnvironmentVariable) is { } testDirectory
        && !string.IsNullOrWhiteSpace(testDirectory)
        ? Path.GetFullPath(testDirectory)
        : null;

    public static string GetVolumeRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) throw new IOException("保存先のドライブを特定できません。");
        return Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    }
}
