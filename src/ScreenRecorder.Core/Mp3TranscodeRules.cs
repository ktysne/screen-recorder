namespace ScreenRecorder.Core;

public enum Mp3TranscodeOutcome
{
    UseConvertedFile,
    KeepAacFile
}

public static class Mp3TranscodeRules
{
    public static TimeSpan OutputStallTimeout { get; } = TimeSpan.FromSeconds(60);

    public static bool IsStalled(TimeSpan sinceLastOutput) => sinceLastOutput > OutputStallTimeout;

    public static IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int bitrateKbps) =>
    [
        "-y",
        "-i", inputPath,
        "-c:v", "copy",
        "-c:a", "libmp3lame",
        "-b:a", $"{bitrateKbps}k",
        outputPath
    ];

    public static Mp3TranscodeOutcome DecideOutcome(bool ffmpegAvailable, int? exitCode, bool outputExists) =>
        ffmpegAvailable && exitCode == 0 && outputExists
            ? Mp3TranscodeOutcome.UseConvertedFile
            : Mp3TranscodeOutcome.KeepAacFile;

    public static bool HasEnoughFreeSpace(long freeBytes, long inputBytes) =>
        inputBytes >= 0 && freeBytes >= inputBytes;
}
