using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ScreenRecorder.App;
using ScreenRecorder.Cli;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Capture.Tests;

public sealed class AutomationRemoteDesktopTests
{
    private const string TestDataDirectoryVariable = "SCREENRECORDER_TEST_DATA_DIR";

    [DesktopFact(true)]
    public async Task RemoteScreenshotWaitsForCaptureAndProbeFindsTheSavedImage()
    {
        if (Mutex.TryOpenExisting(UpdatePaths.SingletonMutexName, out var runningInstance))
        {
            runningInstance?.Dispose();
            throw Xunit.Sdk.SkipException.ForSkip("ScreenRecorder がすでに常駐しているため、実機テストをスキップしました。");
        }

        var testDataDirectory = Path.Combine(Path.GetTempPath(), "ScreenRecorder.RemoteDesktopTests", Guid.NewGuid().ToString("N"));
        var (applicationPath, searched) = CliEnvironment.Create().FindApp(null);
        Assert.True(applicationPath is not null, $"ScreenRecorder.exe が見つかりませんでした。探索先: {string.Join(", ", searched)}");

        Process? application = null;
        try
        {
            WriteTestSettings(testDataDirectory);
            application = Process.Start(CreateApplicationStartInfo(applicationPath!, testDataDirectory))
                ?? throw new InvalidOperationException("ScreenRecorder.exe を起動できませんでした。");

            await WaitForRemoteStatusAsync(application, applicationPath!, testDataDirectory);
            var captureStartedAt = DateTimeOffset.UtcNow;
            var perform = await RunCliAsync(applicationPath!, testDataDirectory, TimeSpan.FromSeconds(15),
                "remote", "perform", "screenshotFullScreen", "--app", applicationPath!);
            Assert.Equal(0, perform.ExitCode);

            var wait = await RunCliAsync(applicationPath!, testDataDirectory, TimeSpan.FromSeconds(45),
                "remote", "wait", "--state", "idle", "--capture-after", captureStartedAt.ToString("O", CultureInfo.InvariantCulture),
                "--timeout", "30", "--app", applicationPath!);
            Assert.Equal(0, wait.ExitCode);

            using var waitDocument = JsonDocument.Parse(wait.Output);
            var capture = waitDocument.RootElement.GetProperty("result").GetProperty("lastCapture");
            Assert.Equal("screenshot", capture.GetProperty("kind").GetString());
            Assert.True(DateTimeOffset.Parse(capture.GetProperty("at").GetString()!, CultureInfo.InvariantCulture) > captureStartedAt);
            var capturePath = capture.GetProperty("path").GetString();
            Assert.False(string.IsNullOrWhiteSpace(capturePath));
            Assert.True(File.Exists(capturePath), wait.Output);

            var probe = await RunCliAsync(applicationPath!, testDataDirectory, TimeSpan.FromSeconds(15), "probe", capturePath!);
            Assert.Equal(0, probe.ExitCode);

            var exit = await RunCliAsync(applicationPath!, testDataDirectory, TimeSpan.FromSeconds(15),
                "remote", "exit", "--app", applicationPath!);
            Assert.Equal(0, exit.ExitCode);
            using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await application.WaitForExitAsync(exitDeadline.Token);
            Assert.Equal(0, application.ExitCode);
        }
        finally
        {
            if (application is not null)
            {
                if (!application.HasExited)
                {
                    try { application.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
                await application.WaitForExitAsync();
                application.Dispose();
            }

            if (Directory.Exists(testDataDirectory)) Directory.Delete(testDataDirectory, recursive: true);
        }
    }

    private static void WriteTestSettings(string testDataDirectory)
    {
        var settings = new Settings
        {
            AutomationEnabled = true,
            StillImageDirectory = testDataDirectory,
            ConfirmedStillImageDirectory = testDataDirectory,
            VideoDirectory = testDataDirectory,
            ConfirmedVideoDirectory = testDataDirectory,
            ImageFormat = StillImageFormat.Png,
            CopyImageToClipboard = false,
            NotifyWhenSaved = false,
            PlayCaptureSound = false,
            StartWithWindows = false,
            CheckForUpdatesAutomatically = false,
            CaptureDelaySeconds = 0,
            CountdownSeconds = 0,
            ScreenshotRegionEnabled = false,
            ScreenshotFullScreenEnabled = false,
            ScreenshotWindowEnabled = false,
            RecordingRegionEnabled = false,
            RecordingFullScreenEnabled = false,
            RecordingWindowEnabled = false,
            PauseRecordingEnabled = false
        };
        new SettingsRepository(Path.Combine(testDataDirectory, "settings")).Save(settings);
    }

    private static ProcessStartInfo CreateApplicationStartInfo(string applicationPath, string testDataDirectory) => new(applicationPath)
    {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(applicationPath)!,
        Environment = { [TestDataDirectoryVariable] = testDataDirectory }
    };

    private static async Task WaitForRemoteStatusAsync(Process application, string applicationPath, string testDataDirectory)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (application.HasExited)
                throw new InvalidOperationException($"ScreenRecorder.exe が status に応答する前に終了しました。終了コード: {application.ExitCode}");

            var status = await RunCliAsync(applicationPath, testDataDirectory, TimeSpan.FromSeconds(10),
                "remote", "status", "--app", applicationPath);
            if (status.ExitCode == 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException("ScreenRecorder.exe が 30 秒以内に remote status を返しませんでした。");
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(
        string applicationPath,
        string testDataDirectory,
        TimeSpan timeout,
        params string[] arguments)
    {
        var assemblyPath = typeof(CliApplication).Assembly.Location;
        var cliPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "screenrecorder-cli.exe");
        var startInfo = File.Exists(cliPath)
            ? new ProcessStartInfo(cliPath)
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        if (!File.Exists(cliPath)) startInfo.ArgumentList.Add(assemblyPath);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = new UTF8Encoding(false);
        startInfo.StandardErrorEncoding = new UTF8Encoding(false);
        startInfo.Environment[TestDataDirectoryVariable] = testDataDirectory;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("screenrecorder-cli を起動できませんでした。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"screenrecorder-cli が期限内に終わりませんでした。stderr: {await errorTask}");
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0 || !string.IsNullOrWhiteSpace(output), error);
        return (process.ExitCode, output);
    }
}
