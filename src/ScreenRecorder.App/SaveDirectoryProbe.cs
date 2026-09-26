namespace ScreenRecorder.App;

internal static class SaveDirectoryProbe
{
    public static void Check(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $".screenrecorder-write-test-{Guid.NewGuid():N}.check");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        stream.WriteByte(0);
        stream.Flush(flushToDisk: true);
    }
}
