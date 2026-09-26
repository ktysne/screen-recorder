namespace ScreenRecorder.Core;

public static class StoragePaths
{
    public static string GetVolumeRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) throw new IOException("保存先のドライブを特定できません。");
        return Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    }
}
