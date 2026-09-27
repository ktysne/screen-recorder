using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.Cli;

internal static class ScreenshotCommand
{
    private static CliApplication.CliExecutionResult Invalid(string message, string code = "invalidArguments") =>
        new(CliExitCode.InvalidArguments, null, [], new CliError(code, message));

    public static CliApplication.CliExecutionResult Execute(ParsedCliCommand command, CliEnvironment environment)
    {
        var options = command.Options;
        var targetOptions = new[] { "--display", "--rect", "--window" }.Count(options.ContainsKey);
        if (targetOptions != 1) return Invalid("--display、--rect、--window のいずれかを 1 つ指定してください。");
        if (options.ContainsKey("--defaults") && options.ContainsKey("--settings"))
            return Invalid("--defaults と --settings は同時に指定できません。");
        if (!options.TryGetValue("-o", out var requestedOutput) || string.IsNullOrWhiteSpace(requestedOutput))
            return Invalid("-o に保存先を指定してください。");

        var outputFormat = Path.GetExtension(requestedOutput).ToLowerInvariant() switch
        {
            ".png" => StillImageFormat.Png,
            ".jpg" or ".jpeg" => StillImageFormat.Jpeg,
            _ => (StillImageFormat)(-1)
        };
        if (!Enum.IsDefined(outputFormat)) return Invalid("-o には .png、.jpg、.jpeg のいずれかを指定してください。");

        var outputPath = Path.GetFullPath(requestedOutput);
        var force = options.ContainsKey("--force");
        if (File.Exists(outputPath) && !force)
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

        if (!Directory.Exists(Path.GetDirectoryName(outputPath)!)) return Invalid("保存先のフォルダーがありません。");

        Bitmap? image = null;
        try
        {
            if (mode == ScreenshotMode.Window)
            {
                var windowCapture = ScreenshotWindowCapture.CaptureAsync(new IntPtr(windowHandle)).GetAwaiter().GetResult();
                image = windowCapture.Image;
                bounds = windowCapture.Bounds;
                // 撮影までにウィンドウが動いていても、結果のモニターを撮影した範囲と合わせる。
                var capturedScreen = Screen.FromRectangle(bounds);
                selected = monitors.FirstOrDefault(monitor => monitor.DeviceName == capturedScreen.DeviceName) ?? selected;
            }
            else
            {
                image = DesktopCapture.Capture(bounds);
            }

            if (settings.CaptureImageCursor) CursorOverlay.Draw(image, bounds);
            DesktopCapture.MakeOpaque(image);
        }
        catch (Exception exception)
        {
            image?.Dispose();
            return new(CliExitCode.IoFailure, null, warnings, new CliError("captureFailed", exception.Message));
        }

        var capturedImage = image!;
        var width = capturedImage.Width;
        var height = capturedImage.Height;
        var target = new
        {
            kind = mode.ToString().ToLowerInvariant(),
            monitor = selected.DeviceName,
            bounds
        };
        try
        {
            ScreenshotImageStorage.SaveImageToPath(capturedImage, outputPath, outputFormat, settings.PngCompression, settings.JpegQuality, force);
        }
        catch (IOException exception) when (!force && CaptureText.IsAlreadyExists(exception))
        {
            return Invalid("保存先のファイルは既に存在します。", "outputExists");
        }
        catch (Exception exception)
        {
            return new(CliExitCode.IoFailure, null, warnings, new CliError("saveFailed", exception.Message));
        }
        finally
        {
            capturedImage.Dispose();
        }

        return new(CliExitCode.Success, new
        {
            path = outputPath,
            format = outputFormat == StillImageFormat.Png ? "png" : "jpeg",
            width,
            height,
            target
        }, warnings, null);
    }

    private static Settings ReadSettings(IReadOnlyDictionary<string, string?> options, CliEnvironment environment, List<string> warnings)
    {
        var path = options.TryGetValue("--settings", out var value) ? Path.GetFullPath(value!) : Path.Combine(environment.SettingsDirectory, "settings.json");
        var read = SettingsFileReader.Read(path);
        warnings.AddRange(read.Issues.Select(issue => issue.Message));
        return read.Settings;
    }

    private static Rectangle Bounds(CliMonitor monitor) => new(monitor.X, monitor.Y, monitor.Width, monitor.Height);
}
