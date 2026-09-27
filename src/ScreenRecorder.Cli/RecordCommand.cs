using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.Cli;

internal static class RecordCommand
{
    private static CliApplication.CliExecutionResult Invalid(string message, string code = "invalidArguments") =>
        new(CliExitCode.InvalidArguments, null, [], new CliError(code, message));

    public static CliApplication.CliExecutionResult Execute(ParsedCliCommand command, CliEnvironment environment)
    {
        var options = command.Options;
        var dryRun = options.ContainsKey("--dry-run");
        var targetOptions = new[] { "--display", "--rect", "--window" }.Count(options.ContainsKey);
        if (targetOptions != 1) return Invalid("--display、--rect、--window のいずれかを 1 つ指定してください。");
        if (options.ContainsKey("--defaults") && options.ContainsKey("--settings"))
            return Invalid("--defaults と --settings は同時に指定できません。");
        if (!dryRun && (!options.TryGetValue("-o", out var requestedOutput) || !string.Equals(Path.GetExtension(requestedOutput), ".mp4", StringComparison.OrdinalIgnoreCase)))
            return Invalid("-o には MP4 の保存先を指定してください。");
        if (!dryRun && !TrySeconds(options, "--duration", out var duration))
            return Invalid("--duration は 0 より大きい秒数で指定してください。");
        if (dryRun && options.ContainsKey("--duration") && !TrySeconds(options, "--duration", out _))
            return Invalid("--duration は 0 より大きい秒数で指定してください。");
        duration = options.ContainsKey("--duration") ? double.Parse(options["--duration"]!, CultureInfo.InvariantCulture) : 0;
        var hasPause = options.ContainsKey("--pause-at");
        var hasResume = options.ContainsKey("--resume-at");
        double pauseAt = 0, resumeAt = 0;
        if (hasPause != hasResume || (hasPause && (!TrySeconds(options, "--pause-at", out pauseAt)
            || !TrySeconds(options, "--resume-at", out resumeAt) || pauseAt >= resumeAt || resumeAt >= duration)))
            return Invalid("--pause-at と --resume-at は 0 < pause-at < resume-at < duration の順に指定してください。");
        var levelName = options.GetValueOrDefault("--log-level") ?? "info";
        if (levelName is not ("silent" or "error" or "warn" or "info" or "debug"))
            return Invalid("--log-level の値が正しくありません。");
        var level = DiagnosticLogLevels.FromSettingName(levelName);
        var outputPath = options.TryGetValue("-o", out var output) ? Path.GetFullPath(output!) : Path.Combine(Path.GetTempPath(), "screenrecorder-dry-run.mp4");
        if (!dryRun && File.Exists(outputPath) && !options.ContainsKey("--force"))
            return Invalid("保存先のファイルは既に存在します。", "outputExists");
        if (options.TryGetValue("--settings", out var explicitSettings) && !File.Exists(explicitSettings))
            return new(CliExitCode.IoFailure, null, [], new CliError("fileNotFound", "設定ファイルが見つかりません。"));

        var warnings = new List<string>();
        var settings = options.ContainsKey("--defaults") ? new Settings() : ReadSettings(options, environment, warnings);
        var monitors = environment.GetMonitors();
        if (monitors.Count == 0) return Invalid("モニターが見つかりません。");
        CliMonitor selected;
        Rectangle bounds;
        ScreenshotMode mode;
        long windowHandle = 0;
        if (options.TryGetValue("--display", out var displayValue))
        {
            if (!int.TryParse(displayValue, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= monitors.Count)
                return Invalid("--display の番号が正しくありません。");
            selected = monitors[index];
            bounds = Bounds(selected);
            mode = ScreenshotMode.Full;
        }
        else if (options.TryGetValue("--rect", out var rectValue))
        {
            var parts = rectValue!.Split(',');
            if (parts.Length != 4 || !parts.All(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                return Invalid("--rect は x,y,w,h で指定してください。");
            var numbers = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            if (numbers[2] <= 0 || numbers[3] <= 0) return Invalid("--rect の幅と高さは正の整数にしてください。");
            bounds = new Rectangle(numbers[0], numbers[1], numbers[2], numbers[3]);
            var display = RecordingStartPlanner.FindContainingDisplay(
                monitors.Select(monitor => new RecordingDisplayInfo(monitor.DeviceName, Bounds(monitor))).ToArray(), bounds);
            selected = monitors.FirstOrDefault(monitor => monitor.DeviceName == display?.DeviceName)!;
            if (selected is null) return Invalid("指定範囲は 1 つのモニターに収めてください。", "rectSpansDisplays");
            mode = ScreenshotMode.Region;
        }
        else
        {
            var value = options["--window"]!;
            var style = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.None;
            var digits = style == NumberStyles.HexNumber ? value[2..] : value;
            if (!long.TryParse(digits, style, CultureInfo.InvariantCulture, out windowHandle)) return Invalid("--window の値が正しくありません。");
            if (!WindowRecordingTarget.TryDescribe(new IntPtr(windowHandle), out bounds, out _, out var reason))
                return Invalid(reason, "windowNotRecordable");
            var screen = Screen.FromRectangle(bounds);
            selected = monitors.FirstOrDefault(monitor => monitor.DeviceName == screen.DeviceName)
                ?? monitors.FirstOrDefault(monitor => Bounds(monitor).IntersectsWith(bounds))!;
            if (selected is null) return Invalid("ウィンドウのモニターを見つけられません。", "windowNotRecordable");
            mode = ScreenshotMode.Window;
        }

        var temporaryPath = Path.Combine(Path.GetDirectoryName(outputPath)!, $"{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.cli-partial.mp4");
        var planResult = RecordingStartPlanner.Plan(mode, new RecordingDisplayInfo(selected.DeviceName, Bounds(selected)), bounds,
            windowHandle, temporaryPath, settings, !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
        if (planResult.Plan is null) return Invalid($"録画範囲または出力サイズが小さすぎます: {planResult.Error}", planResult.Error!.Value.ToString());
        var plan = planResult.Plan;
        var target = new { kind = mode.ToString().ToLowerInvariant(), monitor = selected.DeviceName, bounds = plan.TargetBounds };
        if (dryRun)
            return new(CliExitCode.Success, new { target, plan.TargetBounds, plan.DisplayBounds, plan.StartData }, warnings, null);

        var appSearch = environment.FindApp(options.GetValueOrDefault("--app"));
        if (appSearch.Path is null)
            return new(CliExitCode.IoFailure, new { app = new { path = (string?)null, searched = appSearch.Searched } }, warnings,
                new CliError("appNotFound", "ScreenRecorder.exe が見つかりません。"));
        var directory = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(directory)) return Invalid("保存先のフォルダーがありません。");
        warnings.AddRange(Directory.EnumerateFiles(directory, "*.cli-partial.mp4"));
        return Record(plan, target, settings, outputPath, temporaryPath, appSearch, duration, pauseAt, resumeAt, hasPause,
            options.ContainsKey("--force"), level, warnings);
    }

    private static Settings ReadSettings(IReadOnlyDictionary<string, string?> options, CliEnvironment environment, List<string> warnings)
    {
        var path = options.TryGetValue("--settings", out var value) ? Path.GetFullPath(value!) : Path.Combine(environment.SettingsDirectory, "settings.json");
        var read = SettingsFileReader.Read(path);
        warnings.AddRange(read.Issues.Select(issue => issue.Message));
        return read.Settings;
    }

    private static long? ToMilliseconds(TimeSpan? value) => value is { } span ? (long)Math.Round(span.TotalMilliseconds) : null;

    private static Rectangle Bounds(CliMonitor monitor) => new(monitor.X, monitor.Y, monitor.Width, monitor.Height);

    private static bool TrySeconds(IReadOnlyDictionary<string, string?> options, string key, out double seconds)
    {
        seconds = 0;
        return options.TryGetValue(key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
            && double.IsFinite(seconds) && seconds > 0 && seconds <= TimeSpan.MaxValue.TotalSeconds;
    }

    private static CliApplication.CliExecutionResult Record(RecordingStartPlan plan, object target, Settings settings,
        string outputPath, string temporaryPath, (string? Path, IReadOnlyList<string> Searched) app, double duration,
        double pauseAt, double resumeAt, bool hasPause, bool force, DiagnosticLogLevel level, List<string> warnings)
    {
        var logs = new ConcurrentQueue<object>();
        var events = new BlockingCollection<object>();
        var interrupted = 0;
        var pauseFailed = false;
        var stopSent = false;
        var startTimedOut = false;
        var started = false;
        var paused = false;
        var resumed = false;
        string? completedPath = null;
        RecordingEngineFailedEventArgs? failure = null;
        RecordingStartupTimings? timings = null;
        var recordingClock = new Stopwatch();
        using var inhibitor = new SystemSleepInhibitor();
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; Interlocked.Exchange(ref interrupted, 1); };
        DiagnosticLog.SetForwarder(level, (severity, tag, message) => logs.Enqueue(new { at = DateTimeOffset.Now, level = severity.ToSettingName(), tag, message }));
        Console.CancelKeyPress += cancel;
        try
        {
            inhibitor.Inhibit();
            using var engine = new RecordingWorkerProcessEngine(app.Path!, level);
            engine.StatusChanged += (_, args) => events.Add(args.Status);
            engine.RecordingCompleted += (_, args) => events.Add(args);
            engine.RecordingFailed += (_, args) => events.Add(args);
            engine.RecordingWarning += (_, args) => events.Add(args);
            engine.OperationFailed += (_, args) => events.Add(args);
            engine.Start(RecordingStartRequest.FromWorkerStartData(plan.StartData));
            var startup = Stopwatch.StartNew();
            var finalization = new Stopwatch();
            while (true)
            {
                if (!stopSent && Volatile.Read(ref interrupted) != 0)
                {
                    engine.Stop();
                    stopSent = true;
                    finalization.Restart();
                }
                if (!started && startup.Elapsed >= RecordingTimeouts.Start && !stopSent)
                {
                    engine.Stop();
                    stopSent = true;
                    startTimedOut = true;
                    finalization.Restart();
                    warnings.Add("録画開始がタイムアウトしました。");
                }
                if (started && !stopSent)
                {
                    var elapsed = recordingClock.Elapsed.TotalSeconds;
                    if (hasPause && !paused && elapsed >= pauseAt) { engine.Pause(1); paused = true; }
                    if (hasPause && paused && !resumed && elapsed >= resumeAt) { engine.Resume(2); resumed = true; }
                    if (elapsed >= duration) { engine.Stop(); stopSent = true; finalization.Restart(); }
                }
                if (stopSent && finalization.Elapsed >= RecordingTimeouts.Finalization) break;
                if (!events.TryTake(out var next, 50)) continue;
                Handle(next);
                if (completedPath is not null || failure is not null) break;
            }
            timings = engine.StartupTimings;
            var termination = engine.WaitForTerminationAsync(RecordingTimeouts.Termination).GetAwaiter().GetResult();
            // 期限で待ちを抜けた直後に届いた完了や失敗を取りこぼさない。
            while (completedPath is null && failure is null && events.TryTake(out var late)) Handle(late);
            if (completedPath is null && RecordingTerminationRules.Decide(termination) == RecordingTerminationDecision.ContinueCompletedSave)
                warnings.Add("録画完了イベントを受信できませんでした。");
            void Handle(object next)
            {
                switch (next)
                {
                    case RecordingEngineStatus.Recording when !started:
                        started = true;
                        recordingClock.Start();
                        break;
                    case RecordingEngineCompletedEventArgs completed:
                        completedPath = completed.FilePath;
                        break;
                    case RecordingEngineFailedEventArgs failed:
                        failure = failed;
                        break;
                    case RecordingEngineWarningEventArgs warning:
                        warnings.Add(warning.Message);
                        break;
                    case RecordingEngineOperationFailedEventArgs operation:
                        pauseFailed = true;
                        warnings.Add($"{operation.Operation}: {operation.Error}");
                        break;
                }
            }

            string? finalPath = null;
            string? retainedPath = failure is not null && File.Exists(failure.FilePath)
                ? failure.FilePath
                : File.Exists(temporaryPath) ? temporaryPath : null;
            CliError? error = null;
            if (completedPath is null)
                error = new CliError(startTimedOut ? "recordingStartTimedOut" : "recordingFailed", failure?.Error ?? "録画が完了しませんでした。");
            else
            {
                var ffmpeg = Path.Combine(Path.GetDirectoryName(app.Path!)!, "ffmpeg", "ffmpeg.exe");
                // --force でも既存のファイルは保存に成功するまで残すため、別名で確定してから置き換える。
                var replaceExisting = force && File.Exists(outputPath);
                var saveTarget = replaceExisting
                    ? Path.Combine(Path.GetDirectoryName(outputPath)!, $"{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.cli-final.mp4")
                    : outputPath;
                var finalized = RecordingFinalizer.FinalizeAsync(completedPath, settings, saveTarget, null,
                    new FfmpegVideoRecordingPostProcessor(ffmpeg), CancellationToken.None, temporaryPath).GetAwaiter().GetResult();
                if (replaceExisting && finalized.FinalPath is not null)
                {
                    File.Move(finalized.FinalPath, outputPath, overwrite: true);
                    finalized = finalized with { FinalPath = outputPath };
                }
                if (finalized.Warning is not null) warnings.Add(finalized.Warning);
                finalPath = finalized.FinalPath;
                retainedPath = finalized.RetainedPath;
                if (finalized.SupersededPath is not null)
                    RecordingFinalizer.DeleteSupersededAsync(finalized.SupersededPath).GetAwaiter().GetResult();
                if (finalized.Error is not null) error = new CliError("saveFailed", finalized.Error);
                else if (pauseFailed)
                    error = new CliError("pauseResumeFailed", "一時停止または再開に失敗したため、動画の長さが指定と異なります。保存した動画は finalPath にあります。");
            }
            var result = new
            {
                app = new { path = app.Path, searched = app.Searched }, target, plan.StartData, temporaryPath, finalPath, retainedPath,
                timings = new { processStartToReadyMs = ToMilliseconds(timings?.ProcessStartToReady), readyToRecordingMs = ToMilliseconds(timings?.ReadyToRecording) },
                requestedDurationMs = (long)Math.Round(duration * 1000),
                expectedVideoDurationMs = (long)Math.Round((duration - (hasPause ? resumeAt - pauseAt : 0)) * 1000),
                timeoutMs = (long)Math.Round(duration * 1000) + (long)RecordingTimeouts.Start.TotalMilliseconds + (long)RecordingTimeouts.Finalization.TotalMilliseconds,
                pause = hasPause ? new { pauseAtMs = (long)Math.Round(pauseAt * 1000), resumeAtMs = (long)Math.Round(resumeAt * 1000), failed = pauseFailed } : null,
                interrupted = Volatile.Read(ref interrupted) != 0, log = logs.ToArray()
            };
            return new(error is null ? CliExitCode.Success : CliExitCode.IoFailure, result, warnings, error);
        }
        catch (Exception exception)
        {
            return new(CliExitCode.IoFailure, null, warnings, new CliError("recordingFailed", exception.Message));
        }
        finally
        {
            inhibitor.Release();
            Console.CancelKeyPress -= cancel;
            DiagnosticLog.ClearForwarder();
            events.Dispose();
        }
    }
}
