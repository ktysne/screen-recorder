using System.Text;

namespace ScreenRecorder.Core;

public sealed class DailyLog(string? baseDirectory = null)
{
    private readonly string _directory = Path.Combine(baseDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenRecorder"), "logs");

    public string CurrentFilePath => Path.Combine(_directory, $"screen-recorder-{DateTime.Now:yyyyMMdd}.log");

    public void Prune(DateTime localDate)
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (var file in Directory.EnumerateFiles(_directory, "screen-recorder-*.log"))
                if (File.GetLastWriteTime(file).Date < localDate.Date.AddDays(-7))
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.AppendAllText(CurrentFilePath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
