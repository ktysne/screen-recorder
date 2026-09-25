using System.Diagnostics;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal interface IVideoRecordingPostProcessor
{
    Task<VideoPostProcessResult> ProcessAsync(string temporaryPath, Settings settings, CancellationToken cancellationToken);
}

internal sealed record VideoPostProcessResult(string FilePath, string? Warning);

internal sealed class FfmpegVideoRecordingPostProcessor(DailyLog log) : IVideoRecordingPostProcessor
{
    private static readonly int StandardErrorTailLength = 4000;

    public Task<VideoPostProcessResult> ProcessAsync(string temporaryPath, Settings settings, CancellationToken cancellationToken) =>
        Task.Run(() => ProcessCoreAsync(temporaryPath, settings, cancellationToken), cancellationToken);

    private async Task<VideoPostProcessResult> ProcessCoreAsync(string temporaryPath, Settings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.AudioFormat != AudioFormat.Mp3) return new VideoPostProcessResult(temporaryPath, null);

        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        var outputPath = Path.Combine(Path.GetDirectoryName(temporaryPath)!, $".{Path.GetFileNameWithoutExtension(temporaryPath)}.{Guid.NewGuid():N}.mp3.mp4");
        string standardError = string.Empty;
        try
        {
            if (!File.Exists(ffmpegPath))
                return KeepAac(temporaryPath, outputPath, "ffmpeg.exe が見つからないため、音声は AAC のまま保存しました。", "ffmpeg executable not found");

            var sourceSize = new FileInfo(temporaryPath).Length;
            var rootPath = Path.GetPathRoot(temporaryPath) ?? throw new IOException("録画ファイルのドライブを特定できません。");
            if (!Mp3TranscodeRules.HasEnoughFreeSpace(new DriveInfo(rootPath).AvailableFreeSpace, sourceSize))
                return KeepAac(temporaryPath, outputPath, "変換に必要な空き容量がないため、音声は AAC のまま保存しました。", "insufficient free space for MP3 conversion");

            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var argument in Mp3TranscodeRules.BuildArguments(temporaryPath, outputPath, settings.Mp3BitrateKbps))
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return KeepAac(temporaryPath, outputPath, "MP3 への変換を開始できなかったため、音声は AAC のまま保存しました。", "ffmpeg did not start");
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            standardError = await standardErrorTask.ConfigureAwait(false);
            var outcome = Mp3TranscodeRules.DecideOutcome(true, process.ExitCode, File.Exists(outputPath));
            if (outcome != Mp3TranscodeOutcome.UseConvertedFile)
                return KeepAac(temporaryPath, outputPath, "MP3 への変換に失敗したため、音声は AAC のまま保存しました。", $"ffmpeg exit code={process.ExitCode}; stderr tail={Tail(standardError)}");

            File.Delete(temporaryPath);
            return new VideoPostProcessResult(outputPath, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return KeepAac(temporaryPath, outputPath, "MP3 への変換に失敗したため、音声は AAC のまま保存しました。", $"ffmpeg conversion failed: {exception}; stderr tail={Tail(standardError)}");
        }
    }

    private VideoPostProcessResult KeepAac(string sourcePath, string outputPath, string warning, string reason)
    {
        try { if (File.Exists(outputPath)) File.Delete(outputPath); }
        catch (Exception exception) { log.Write($"Removing failed MP3 output failed: {exception}"); }
        log.Write($"MP3 conversion skipped or failed; keeping AAC file: {reason}");
        return new VideoPostProcessResult(sourcePath, warning);
    }

    private static string Tail(string value) => value.Length <= StandardErrorTailLength ? value : value[^StandardErrorTailLength..];
}
