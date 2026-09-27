using System.Text.Json;
using ScreenRecorder.Cli;
using Xunit;

namespace ScreenRecorder.Capture.Tests;

public sealed class CliDesktopTests
{
    [DesktopFact]
    public void RecordAndProbeProducedVideoAndFrame()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var video = Path.Combine(directory, "cli.mp4");
            var frame = Path.Combine(directory, "frame.png");
            var environment = CliEnvironment.Create();
            var recording = Run(environment, "record", "--display", "0", "--duration", "3", "--defaults", "-o", video);
            Assert.Equal(0, recording.Code);
            Assert.True(File.Exists(video), recording.Output);
            var probe = Run(environment, "probe", video, "--expect-duration-ms", "3000", "--tolerance-ms", "700");
            Assert.Equal(0, probe.Code);
            using var document = JsonDocument.Parse(probe.Output);
            Assert.Empty(document.RootElement.GetProperty("result").GetProperty("mismatches").EnumerateArray());
            var frameProbe = Run(environment, "probe", video, "--frame", "1", "-o", frame);
            Assert.Equal(0, frameProbe.Code);
            Assert.True(File.Exists(frame));
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static (int Code, string Output) Run(CliEnvironment environment, params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return (CliApplication.Run(arguments, output, error, environment), output.ToString());
    }
}
