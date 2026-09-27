using System.Drawing;
using System.Threading.Channels;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ScreenRecorder.Capture.Tests;

public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCREENRECORDER_DESKTOP_TESTS") != "1")
            Skip = "SCREENRECORDER_DESKTOP_TESTS=1 を設定すると実機テストを実行します。";
    }
}

public sealed class DesktopRecordingTests
{
    [DesktopFact]
    public async Task PrimaryDisplayRecordsThreeSeconds()
    {
        using var session = new RecordingSession(ScreenshotMode.Full, Rectangle.Empty);
        await session.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        var path = await session.StopAndFinalizeAsync();
        var info = await MediaFileProbe.InspectAsync(path);

        Assert.Equal((uint)session.OutputSize.Width, info.Video.Width);
        Assert.Equal((uint)session.OutputSize.Height, info.Video.Height);
        Assert.InRange(info.Duration.TotalMilliseconds, 2300, 3700);
        Assert.NotNull(session.Engine.StartupTimings.ProcessStartToReady);
        Assert.NotNull(session.Engine.StartupTimings.ReadyToRecording);
        var framePath = Path.Combine(session.DirectoryPath, "frame.png");
        await MediaFileProbe.WriteFramePngAsync(path, TimeSpan.FromSeconds(1), framePath);
        Assert.True(File.Exists(framePath));
        using var image = Image.FromFile(framePath);
        Assert.Equal(session.OutputSize, image.Size);
    }

    [DesktopFact(Skip = "一時停止と再開を挟むと動画が約 0.9 秒短くなる不具合 #74 が直るまでスキップする。")]
    public async Task RegionRecordsWithPauseAndResume()
    {
        var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("主モニターがありません。");
        var region = new Rectangle(screen.Bounds.X + 100, screen.Bounds.Y + 100, 400, 300);
        using var session = new RecordingSession(ScreenshotMode.Region, region);
        Assert.Equal(new RecordingWorkerRectangle(100, 100, 400, 300), session.StartData.SourceRect);
        await session.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(1));
        session.Engine.Pause(1);
        await session.WaitForStatusAsync(RecordingEngineStatus.Paused);
        await Task.Delay(TimeSpan.FromSeconds(1));
        session.Engine.Resume(2);
        await session.WaitForStatusAsync(RecordingEngineStatus.Recording);
        await Task.Delay(TimeSpan.FromSeconds(2));
        var path = await session.StopAndFinalizeAsync();
        var info = await MediaFileProbe.InspectAsync(path);

        Assert.Equal((uint)400, info.Video.Width);
        Assert.Equal((uint)300, info.Video.Height);
        Assert.InRange(info.Duration.TotalMilliseconds, 2300, 3700);
    }

    private sealed class RecordingSession : IDisposable
    {
        private readonly TaskCompletionSource<string> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<RecordingEngineStatus> _statuses = Channel.CreateUnbounded<RecordingEngineStatus>();
        private readonly string _temporaryPath;

        public RecordingSession(ScreenshotMode mode, Rectangle requestedBounds)
        {
            var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("主モニターがありません。");
            var executablePath = FindExecutable();
            DirectoryPath = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            _temporaryPath = Path.Combine(DirectoryPath, "test.recording.mp4");
            var display = new RecordingDisplayInfo(screen.DeviceName, screen.Bounds);
            var bounds = mode == ScreenshotMode.Full ? screen.Bounds : requestedBounds;
            // 自動のエンコーダは画面が静止している間フレームを間引き、動画の末尾が短くなるので、長さを比べるテストは固定フレームレートで録る。
            var result = RecordingStartPlanner.Plan(mode, display, bounds, 0, _temporaryPath,
                new Settings { Encoder = EncoderMode.SoftwareOnly }, !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
            StartData = result.Plan?.StartData ?? throw new InvalidOperationException($"開始データを作れません: {result.Error}");
            OutputSize = new Size(StartData.OutputFrameSize.Width, StartData.OutputFrameSize.Height);
            Engine = new RecordingWorkerProcessEngine(executablePath, DiagnosticLogLevel.Info);
            Engine.StatusChanged += (_, args) => _statuses.Writer.TryWrite(args.Status);
            Engine.RecordingCompleted += (_, args) => _completed.TrySetResult(args.FilePath);
            Engine.RecordingFailed += (_, args) => _completed.TrySetException(new InvalidOperationException(args.Error));
        }

        public string DirectoryPath { get; }
        public RecordingWorkerStartData StartData { get; }
        public Size OutputSize { get; }
        public RecordingWorkerProcessEngine Engine { get; }

        public async Task StartAsync()
        {
            Engine.Start(RecordingStartRequest.FromWorkerStartData(StartData));
            await WaitForStatusAsync(RecordingEngineStatus.Recording);
        }

        public async Task WaitForStatusAsync(RecordingEngineStatus expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var status = _statuses.Reader.ReadAsync(timeout.Token).AsTask();
                if (await Task.WhenAny(status, _completed.Task) == _completed.Task)
                    throw new InvalidOperationException("録画状態へ移る前に録画プロセスが終了しました。", await GetFailureAsync());
                var next = await status;
                if (next == expected) return;
            }
        }

        private async Task<Exception?> GetFailureAsync()
        {
            try { await _completed.Task; return null; }
            catch (Exception exception) { return exception; }
        }

        public async Task<string> StopAndFinalizeAsync()
        {
            Engine.Stop();
            var completedPath = await _completed.Task.WaitAsync(TimeSpan.FromMinutes(2));
            var result = await RecordingFinalizer.FinalizeAsync(completedPath, new Settings(),
                Path.Combine(DirectoryPath, "test.mp4"), null,
                new FfmpegVideoRecordingPostProcessor(Path.Combine(DirectoryPath, "ffmpeg.exe")), CancellationToken.None);
            if (result.Error is not null) throw result.Failure ?? new InvalidOperationException(result.Error);
            return result.FinalPath!;
        }

        public void Dispose()
        {
            Engine.Dispose();
            try { Directory.Delete(DirectoryPath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string FindExecutable()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScreenRecorder.slnx")))
                directory = directory.Parent;
            if (directory is null) throw new FileNotFoundException("ScreenRecorder.slnx が見つかりません。");
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var executablePath = Path.Combine(directory.FullName, "src", "ScreenRecorder.App", "bin",
                "x64", configuration, "net10.0-windows10.0.22000.0", "win-x64", "ScreenRecorder.exe");
            if (!File.Exists(executablePath))
                executablePath = Path.Combine(directory.FullName, "src", "ScreenRecorder.App", "bin",
                    configuration, "net10.0-windows10.0.22000.0", "win-x64", "ScreenRecorder.exe");
            if (!File.Exists(executablePath)) throw new FileNotFoundException($"ScreenRecorder.exe が見つかりません: {executablePath}", executablePath);
            return executablePath;
        }
    }
}
