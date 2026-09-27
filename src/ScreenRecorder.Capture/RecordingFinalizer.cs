using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

public sealed record RecordingFinalizeResult(
    string? FinalPath, string? Warning, string? SupersededPath, string? Error, string? RetainedPath)
{
    public Exception? Failure { get; init; }
}

public static class RecordingFinalizer
{
    public static async Task<RecordingFinalizeResult> FinalizeAsync(
        string completedPath, Settings settings, string finalPath,
        Func<string>? nextAvailableFinalPath, IVideoRecordingPostProcessor postProcessor,
        CancellationToken cancellationToken, string? temporaryPath = null)
    {
        string? processedPath = null;
        try
        {
            var processResult = await postProcessor.ProcessAsync(completedPath, settings, cancellationToken);
            processedPath = processResult.FilePath;
            var pathToMove = processedPath;
            var savedPath = await Task.Run(() =>
            {
                if (!File.Exists(pathToMove))
                    throw new FileNotFoundException("録画ライブラリが完了を通知しましたが、一時ファイルが見つかりません。", pathToMove);
                var destination = finalPath;
                while (true)
                {
                    try
                    {
                        File.Move(pathToMove, destination);
                        return destination;
                    }
                    catch (IOException exception) when (CaptureText.IsAlreadyExists(exception))
                    {
                        if (nextAvailableFinalPath is null) throw;
                        destination = nextAvailableFinalPath();
                    }
                }
            });
            return new(savedPath, processResult.Warning, processResult.SupersededPath, null, null);
        }
        catch (Exception exception)
        {
            var retainedPath = await Task.Run(() =>
            {
                if (processedPath is not null && File.Exists(processedPath)) return processedPath;
                var fallbackPath = temporaryPath ?? completedPath;
                return !string.IsNullOrWhiteSpace(fallbackPath) && File.Exists(fallbackPath) ? fallbackPath : null;
            });
            return new(null, null, null, exception.Message, retainedPath) { Failure = exception };
        }
    }

    public static Task DeleteSupersededAsync(string path) => Task.Run(() =>
    {
        try { File.Delete(path); }
        catch (Exception exception)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Convert, $"変換前の録画ファイルを削除できませんでした: {path}; {exception}");
        }
    });
}
