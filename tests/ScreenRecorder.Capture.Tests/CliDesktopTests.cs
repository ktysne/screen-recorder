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
            // 自動のエンコーダは画面が静止している間フレームを間引き、動画の末尾が短くなるので、長さを比べるテストは固定フレームレートで録る。
            var settings = Path.Combine(directory, "settings.json");
            File.WriteAllText(settings, """{ "encoder": "softwareOnly" }""");
            var recording = Run(environment, "record", "--display", "0", "--duration", "3", "--settings", settings, "-o", video);
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

    [DesktopFact]
    public async Task CancellingRecordWaitsForTheMp4ToBeFinalized()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "cancelled.mp4");
        var settings = Path.Combine(directory, "settings.json");
        File.WriteAllText(settings, """{ "encoder": "softwareOnly" }""");
        var appFound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var baseEnvironment = CliEnvironment.Create();
        var environment = baseEnvironment with
        {
            FindApp = path =>
            {
                var result = baseEnvironment.FindApp(path);
                appFound.TrySetResult();
                return result;
            }
        };
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completed.TrySetResult(CliApplication.Run(
                    ["record", "--display", "0", "--duration", "60", "--settings", settings, "-o", video],
                    output,
                    error,
                    environment,
                    cancellation.Token));
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            await appFound.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            var exitCode = await completed.Task.WaitAsync(TimeSpan.FromMinutes(2));

            Assert.Equal(0, exitCode);
            using var response = JsonDocument.Parse(output.ToString());
            Assert.True(response.RootElement.GetProperty("result").GetProperty("interrupted").GetBoolean());
            Assert.Equal(video, response.RootElement.GetProperty("result").GetProperty("finalPath").GetString());
            var info = await MediaFileProbe.InspectAsync(video);
            Assert.True(info.Video.Width > 0);
            Assert.True(info.Duration > TimeSpan.Zero);
        }
        finally
        {
            if (!completed.Task.IsCompleted) cancellation.Cancel();
            if (completed.Task.IsCompleted && Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    [DesktopFact]
    public void ForcedRecordKeepsTheExistingFileAndReturnsTheSavedVideoWhenReplacingFails()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "existing.mp4");
        File.WriteAllText(video, "既存");
        // 読み取り専用のファイルは置き換えの移動を拒むので、保存後の置き換えだけを失敗させられる。
        File.SetAttributes(video, FileAttributes.ReadOnly);
        try
        {
            var recording = Run(CliEnvironment.Create(), "record", "--display", "0", "--duration", "1", "--defaults", "--force", "-o", video);

            Assert.Equal(3, recording.Code);
            using var document = JsonDocument.Parse(recording.Output);
            Assert.Equal("saveFailed", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            var retainedPath = document.RootElement.GetProperty("result").GetProperty("retainedPath").GetString();
            Assert.True(File.Exists(retainedPath), recording.Output);
            Assert.Equal("既存", File.ReadAllText(video));
        }
        finally
        {
            File.SetAttributes(video, FileAttributes.Normal);
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
