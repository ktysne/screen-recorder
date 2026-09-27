using System.Drawing;
using System.Globalization;
using ScreenRecorder.Capture;

namespace ScreenRecorder.Cli;

internal static class ProbeCommand
{
    private static CliApplication.CliExecutionResult Invalid(string message, string code = "invalidArguments") =>
        new(CliExitCode.InvalidArguments, null, [], new CliError(code, message));

    public static CliApplication.CliExecutionResult Execute(ParsedCliCommand command)
    {
        var options = command.Options;
        var path = Path.GetFullPath(command.Positionals[0]);
        var isMp4 = string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase);
        var isPng = string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);
        if (!isMp4 && !isPng) return Invalid("MP4 または PNG を指定してください。");
        if (options.ContainsKey("--frame") != options.ContainsKey("-o"))
            return Invalid("--frame と -o は一緒に指定してください。");
        if (isPng && (options.ContainsKey("--frame") || options.Keys.Any(key => key.StartsWith("--expect-", StringComparison.Ordinal) && key is not ("--expect-width" or "--expect-height"))))
            return Invalid("PNG では動画と音声の検査、フレームの書き出しはできません。");
        var numbers = new Dictionary<string, double>();
        foreach (var key in new[] { "--expect-width", "--expect-height", "--expect-fps", "--expect-duration-ms", "--tolerance-ms", "--expect-audio-channels", "--expect-audio-rate", "--frame" })
        {
            if (!options.TryGetValue(key, out var value)) continue;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                || !double.IsFinite(number) || number < 0 || (key is "--expect-width" or "--expect-height" or "--expect-audio-channels" or "--expect-audio-rate" && number != Math.Truncate(number)))
                return Invalid($"{key} の値が正しくありません。");
            numbers[key] = number;
        }
        if (options.ContainsKey("--expect-no-audio") && (options.ContainsKey("--expect-audio-channels") || options.ContainsKey("--expect-audio-rate")))
            return Invalid("音声なしと音声の値は同時に指定できません。");
        var tolerance = numbers.GetValueOrDefault("--tolerance-ms", 500);
        string? output = null;
        if (options.TryGetValue("-o", out var requested))
        {
            if (!string.Equals(Path.GetExtension(requested), ".png", StringComparison.OrdinalIgnoreCase)) return Invalid("-o には PNG を指定してください。");
            output = Path.GetFullPath(requested!);
            if (File.Exists(output) && !options.ContainsKey("--force")) return Invalid("出力ファイルは既に存在します。", "outputExists");
        }
        if (!File.Exists(path)) return new(CliExitCode.IoFailure, null, [], new CliError("fileNotFound", "検査するファイルが見つかりません。"));
        var mismatches = new List<object>();
        void Compare(string key, object? expected, object? actual)
        {
            if (expected is null || Equals(expected, actual)) return;
            mismatches.Add(new { property = key, expected, actual });
        }
        object result;
        if (isPng)
        {
            using var image = Image.FromFile(path);
            Compare("width", numbers.TryGetValue("--expect-width", out var width) ? width : null, (double)image.Width);
            Compare("height", numbers.TryGetValue("--expect-height", out var height) ? height : null, (double)image.Height);
            result = new { path, width = image.Width, height = image.Height, mismatches };
        }
        else
        {
            var info = MediaFileProbe.InspectAsync(path).GetAwaiter().GetResult();
            var fps = info.Video.FrameRateDenominator == 0 ? 0 : (double)info.Video.FrameRateNumerator / info.Video.FrameRateDenominator;
            Compare("width", numbers.TryGetValue("--expect-width", out var width) ? width : null, (double)info.Video.Width);
            Compare("height", numbers.TryGetValue("--expect-height", out var height) ? height : null, (double)info.Video.Height);
            if (numbers.TryGetValue("--expect-fps", out var expectedFps) && Math.Abs(expectedFps - fps) > 0.01)
                mismatches.Add(new { property = "fps", expected = (object)expectedFps, actual = (object)fps });
            if (numbers.TryGetValue("--expect-duration-ms", out var expectedDuration) && Math.Abs(expectedDuration - info.Duration.TotalMilliseconds) > tolerance)
                mismatches.Add(new { property = "durationMs", expected = (object)expectedDuration, actual = (object)info.Duration.TotalMilliseconds });
            Compare("videoCodec", options.GetValueOrDefault("--expect-video-codec")?.ToUpperInvariant(), info.Video.Subtype.ToUpperInvariant());
            Compare("audioChannels", numbers.TryGetValue("--expect-audio-channels", out var channels) ? channels : null, info.Audio is null ? null : (double)info.Audio.ChannelCount);
            Compare("audioRate", numbers.TryGetValue("--expect-audio-rate", out var rate) ? rate : null, info.Audio is null ? null : (double)info.Audio.SampleRate);
            if (options.ContainsKey("--expect-no-audio")) Compare("noAudio", true, info.Audio is null);
            if (output is not null)
            {
                // 書き出しに失敗しても既存のファイルを残すため、別名に書き終えてから置き換える。
                var staging = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.png");
                try
                {
                    MediaFileProbe.WriteFramePngAsync(path, TimeSpan.FromSeconds(numbers["--frame"]), staging).GetAwaiter().GetResult();
                    File.Move(staging, output, overwrite: true);
                }
                finally
                {
                    if (File.Exists(staging)) File.Delete(staging);
                }
            }
            result = new
            {
                path, durationMs = (long)Math.Round(info.Duration.TotalMilliseconds), width = info.Video.Width, height = info.Video.Height,
                fps, videoCodec = info.Video.Subtype, videoBitrate = info.Video.Bitrate,
                audioCodec = info.Audio?.Subtype, audioChannels = info.Audio?.ChannelCount,
                audioRate = info.Audio?.SampleRate, audioBitrate = info.Audio?.Bitrate,
                framePath = output, mismatches
            };
        }
        return new(mismatches.Count == 0 ? CliExitCode.Success : CliExitCode.CheckFailed, result, [], null);
    }
}
