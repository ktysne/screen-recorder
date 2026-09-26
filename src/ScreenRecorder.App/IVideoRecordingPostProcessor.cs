using System.Diagnostics;
using System.Text;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal interface IVideoRecordingPostProcessor
{
    Task<VideoPostProcessResult> ProcessAsync(string temporaryPath, Settings settings, CancellationToken cancellationToken);
}

// SupersededPath は、FilePath を最終名へ移し終えてから消すファイル(MP3 へ変換する前の AAC の録画)。
internal sealed record VideoPostProcessResult(string FilePath, string? Warning, string? SupersededPath = null);

internal sealed class FfmpegVideoRecordingPostProcessor : IVideoRecordingPostProcessor
{
    private static readonly int StandardErrorTailLength = 4000;
    private static readonly TimeSpan ProcessExitTimeoutAfterKill = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(1);

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
                return KeepAac(temporaryPath, outputPath, "ffmpeg.exe が見つからないため、音声は AAC のまま保存しました。", "ffmpeg.exe が見つかりませんでした。");

            var sourceSize = new FileInfo(temporaryPath).Length;
            var rootPath = Path.GetPathRoot(temporaryPath) ?? throw new IOException("録画ファイルのドライブを特定できません。");
            if (!Mp3TranscodeRules.HasEnoughFreeSpace(new DriveInfo(rootPath).AvailableFreeSpace, sourceSize))
                return KeepAac(temporaryPath, outputPath, "変換に必要な空き容量がないため、音声は AAC のまま保存しました。", "変換に必要な空き容量がありませんでした。");

            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var argument in Mp3TranscodeRules.BuildArguments(temporaryPath, outputPath, settings.Mp3BitrateKbps))
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return KeepAac(temporaryPath, outputPath, "MP3 への変換を開始できなかったため、音声は AAC のまま保存しました。", "ffmpeg を起動できませんでした。");
            var stopwatch = Stopwatch.StartNew();
            long lastOutputTicks = 0;
            var standardErrorTail = new StandardErrorTail(StandardErrorTailLength);
            // 読み取りを止めると stderr のパイプが詰まり、ffmpeg が書き込み待ちで停止する。
            var standardErrorTask = ReadStandardErrorAsync(process, standardErrorTail, stopwatch, ticks => Interlocked.Exchange(ref lastOutputTicks, ticks));
            var exitTask = process.WaitForExitAsync(cancellationToken);

            while (!exitTask.IsCompleted)
            {
                await Task.Delay(StallCheckInterval, cancellationToken).ConfigureAwait(false);
                var sinceLastOutput = stopwatch.Elapsed - TimeSpan.FromSeconds(Interlocked.Read(ref lastOutputTicks) / (double)Stopwatch.Frequency);
                if (!Mp3TranscodeRules.IsStalled(sinceLastOutput)) continue;
                if (process.HasExited) break;

                var killed = false;
                try
                {
                    process.Kill(entireProcessTree: true);
                    killed = true;
                }
                catch (InvalidOperationException) { }
                if (!killed) break;

                // 出力ファイルを削除する前に ffmpeg のハンドル解放を待つ。
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(ProcessExitTimeoutAfterKill).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    DiagnosticLog.Warn(DiagnosticLogTags.Convert, "ffmpeg を終了してから 10 秒以内に終了を確認できませんでした。");
                }
                await ObserveStandardErrorAsync(standardErrorTask).ConfigureAwait(false);
                standardError = standardErrorTail.ToString();
                return KeepAac(temporaryPath, outputPath, "MP3 への変換に失敗したため、音声は AAC のまま保存しました。", $"ffmpeg が {Mp3TranscodeRules.OutputStallTimeout.TotalSeconds:0} 秒間何も出力しなかったため、変換を止めました; 標準エラー末尾={Tail(standardError)}");
            }

            await exitTask.ConfigureAwait(false);
            await standardErrorTask.ConfigureAwait(false);
            standardError = standardErrorTail.ToString();
            var outcome = Mp3TranscodeRules.DecideOutcome(true, process.ExitCode, File.Exists(outputPath));
            if (outcome != Mp3TranscodeOutcome.UseConvertedFile)
                return KeepAac(temporaryPath, outputPath, "MP3 への変換に失敗したため、音声は AAC のまま保存しました。", $"ffmpeg の終了コード={process.ExitCode}; 標準エラー末尾={Tail(standardError)}");

            DiagnosticLog.Info(DiagnosticLogTags.Convert, $"MP3 への変換に成功しました: {outputPath}");
            return new VideoPostProcessResult(outputPath, null, temporaryPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return KeepAac(temporaryPath, outputPath, "MP3 への変換に失敗したため、音声は AAC のまま保存しました。", $"ffmpeg の変換に失敗しました: {exception}; 標準エラー末尾={Tail(standardError)}");
        }
    }

    private VideoPostProcessResult KeepAac(string sourcePath, string outputPath, string warning, string reason)
    {
        try { if (File.Exists(outputPath)) File.Delete(outputPath); }
        catch (Exception exception) { DiagnosticLog.Warn(DiagnosticLogTags.Convert, $"変換に失敗した MP3 ファイルを削除できませんでした: {exception}"); }
        DiagnosticLog.Warn(DiagnosticLogTags.Convert, $"MP3 への変換を諦め、AAC ファイルを保存します: {reason}");
        return new VideoPostProcessResult(sourcePath, warning);
    }

    private static string Tail(string value) => value.Length <= StandardErrorTailLength ? value : value[^StandardErrorTailLength..];

    private static async Task ReadStandardErrorAsync(Process process, StandardErrorTail tail, Stopwatch stopwatch, Action<long> onOutput)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            tail.Append(line);
            tail.Append(Environment.NewLine);
            onOutput(stopwatch.ElapsedTicks);
        }
    }

    private static async Task ObserveStandardErrorAsync(Task standardErrorTask)
    {
        try { await standardErrorTask.WaitAsync(ProcessExitTimeoutAfterKill).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private sealed class StandardErrorTail(int capacity)
    {
        private readonly StringBuilder _value = new(capacity);
        private readonly object _lock = new();

        public void Append(string value)
        {
            lock (_lock)
            {
                _value.Append(value);
                if (_value.Length > capacity) _value.Remove(0, _value.Length - capacity);
            }
        }

        public override string ToString()
        {
            lock (_lock) return _value.ToString();
        }
    }
}
