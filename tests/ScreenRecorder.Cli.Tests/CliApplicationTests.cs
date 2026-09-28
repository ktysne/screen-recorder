using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Text.Json;
using ScreenRecorder.Capture;
using ScreenRecorder.Cli;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Cli.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliCollection
{
    public const string Name = "CLI の static 状態を共有するテスト";
}

[Collection(CliCollection.Name)]
public sealed class CliApplicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _settingsDirectory;
    private readonly string _logDirectory;
    private readonly CliEnvironment _environment;

    public CliApplicationTests()
    {
        _settingsDirectory = Path.Combine(_root, "settings");
        _logDirectory = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_settingsDirectory);
        Directory.CreateDirectory(_logDirectory);
        _environment = new CliEnvironment(
            _settingsDirectory,
            _logDirectory,
            () =>
            [
                new CliMonitor("DISPLAY_TEST", -1920, 0, 1920, 1080, -1920, 0, 1920, 1040, 144, 144, false),
                new CliMonitor("DISPLAY_PRIMARY", 0, 0, 2560, 1440, 0, 0, 2560, 1400, 120, 120, true)
            ],
            () => new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(9)),
            "9.8.7")
        {
            AutomationPipeName = $"ScreenRecorder.Automation.Tests.{Guid.NewGuid():N}",
            AutomationPipeExists = _ => true,
            VerifyAutomationServer = (_, _) => new CliAutomationServerVerification(Environment.ProcessId, "C:\\ScreenRecorder.exe")
        };
    }

    [Fact]
    public void InfoReturnsVersionPathsAndMonitorWorkAreas()
    {
        var invocation = Run("info");
        using var json = AssertJson(invocation, 0, "info");
        var result = json.RootElement.GetProperty("result");

        Assert.Equal("9.8.7", json.RootElement.GetProperty("cliVersion").GetString());
        Assert.Equal(_settingsDirectory, result.GetProperty("settingsDirectory").GetString());
        Assert.Equal(_logDirectory, result.GetProperty("logDirectory").GetString());
        Assert.Equal(2, result.GetProperty("monitors").GetArrayLength());
        Assert.Equal(-1920, result.GetProperty("monitors")[0].GetProperty("bounds").GetProperty("x").GetInt32());
        Assert.Equal(1040, result.GetProperty("monitors")[0].GetProperty("workArea").GetProperty("height").GetInt32());
        Assert.False(result.GetProperty("monitors")[0].GetProperty("isPrimary").GetBoolean());
    }

    [Fact]
    public void SettingsShowReturnsValuesAndDefaultedProperties()
    {
        File.WriteAllText(Path.Combine(_settingsDirectory, "settings.json"), "{\"playCaptureSound\":true}");

        var invocation = Run("settings", "show");
        using var json = AssertJson(invocation, 0, "settings show");
        var result = json.RootElement.GetProperty("result");

        Assert.True(result.GetProperty("settings").GetProperty("playCaptureSound").GetBoolean());
        Assert.Contains("jpegQuality", result.GetProperty("defaultedProperties").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void SettingsValidateUsesCheckFailureForInvalidSettings()
    {
        File.WriteAllText(Path.Combine(_settingsDirectory, "settings.json"), "{\"jpegQuality\":0}");

        var invocation = Run("settings", "validate");
        using var json = AssertJson(invocation, 1, "settings validate");

        Assert.Contains(json.RootElement.GetProperty("result").GetProperty("issues").EnumerateArray(),
            issue => issue.GetProperty("code").GetString() == "valueOutOfRange");
    }

    [Fact]
    public void LogsListAndShowReturnHeadersParsedLinesAndMalformedLines()
    {
        var logPath = Path.Combine(_logDirectory, DiagnosticLogFormatting.MakeFileName(new DateTime(2026, 9, 27, 9, 0, 0)));
        var formattedLine = DiagnosticLogFormatting.FormatLine(new DateTime(2026, 9, 27, 9, 1, 2, 345), DiagnosticLogLevel.Error, "capture", "保存失敗");
        File.WriteAllLines(logPath, DiagnosticLogFormatting.CreateHeader("4.5.6", DiagnosticLogLevel.Debug).Append(formattedLine).Append("raw unformatted row"));

        var listInvocation = Run("logs", "list");
        using var list = AssertJson(listInvocation, 0, "logs list");
        Assert.Equal("4.5.6", list.RootElement.GetProperty("result").GetProperty("files")[0].GetProperty("headerVersion").GetString());

        var showInvocation = Run("logs", "show");
        using var show = AssertJson(showInvocation, 0, "logs show");
        var entries = show.RootElement.GetProperty("result").GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("error", entries[0].GetProperty("level").GetString());
        Assert.Equal("raw unformatted row", entries[1].GetProperty("rawLine").GetString());
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("timestamp").ValueKind);

        var filteredInvocation = Run(
            "logs", "show",
            "--since", "2026-09-27T09:00:00",
            "--level", "error",
            "--tag", "capture",
            "--grep", "保存失敗",
            "--limit", "1");
        using var filtered = AssertJson(filteredInvocation, 0, "logs show");
        Assert.Single(filtered.RootElement.GetProperty("result").GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public void NamingPreviewUsesOverridesAndSuppliedMonitorIndependentTime()
    {
        var invocation = Run(
            "naming", "preview",
            "--template", "snap_{date}_{time}_{mode}_{window}",
            "--mode", "window",
            "--window", "Demo",
            "--at", "2026-09-27T11:22:33+09:00",
            "--dir", Path.Combine(_root, "captures"));
        using var json = AssertJson(invocation, 0, "naming preview");

        Assert.Equal("snap_20260927_112233_window_Demo.jpg", json.RootElement.GetProperty("result").GetProperty("fileName").GetString());
        Assert.Equal("2026-09-27T11:22:33+09:00", json.RootElement.GetProperty("result").GetProperty("at").GetString());
        Assert.False(Directory.Exists(Path.Combine(_root, "captures")));
    }

    [Fact]
    public void NamingPreviewForVideoUsesTheVideoDirectoryAndMp4()
    {
        var videos = Path.Combine(_root, "videos");
        var stills = Path.Combine(_root, "stills");
        File.WriteAllText(
            Path.Combine(_settingsDirectory, "settings.json"),
            JsonSerializer.Serialize(new { videoDirectory = videos, stillImageDirectory = stills, imageFormat = "png" }));

        using var video = AssertJson(Run("naming", "preview", "--kind", "video", "--at", "2026-09-27T11:22:33+09:00"), 0, "naming preview");
        var videoResult = video.RootElement.GetProperty("result");
        Assert.Equal("video", videoResult.GetProperty("kind").GetString());
        Assert.Equal(videos, videoResult.GetProperty("directory").GetString());
        Assert.EndsWith(".mp4", videoResult.GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, videoResult.GetProperty("imageFormat").ValueKind);

        using var image = AssertJson(Run("naming", "preview", "--at", "2026-09-27T11:22:33+09:00"), 0, "naming preview");
        var imageResult = image.RootElement.GetProperty("result");
        Assert.Equal("image", imageResult.GetProperty("kind").GetString());
        Assert.Equal(stills, imageResult.GetProperty("directory").GetString());
        Assert.EndsWith(".png", imageResult.GetProperty("fileName").GetString());
    }

    [Fact]
    public void NamingPreviewForVideoSkipsNamesWhoseRecordingIsInProgress()
    {
        var videos = Path.Combine(_root, "videos");
        Directory.CreateDirectory(videos);
        File.WriteAllText(Path.Combine(videos, "clip.recording.mp4"), "");

        using var json = AssertJson(Run("naming", "preview", "--kind", "video", "--template", "clip", "--dir", videos), 0, "naming preview");

        Assert.NotEqual("clip.mp4", json.RootElement.GetProperty("result").GetProperty("fileName").GetString());
    }

    [Fact]
    public void NamingPreviewRejectsUnknownKind()
    {
        using var json = AssertJson(Run("naming", "preview", "--kind", "audio"), 2, "naming preview");
        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void HelpJsonIsGeneratedFromCommandDefinitions()
    {
        var invocation = Run("help");
        using var json = AssertJson(invocation, 0, "help");
        var names = json.RootElement.GetProperty("result").GetProperty("commands")
            .EnumerateArray().Select(command => command.GetProperty("name").GetString()).ToArray();

        Assert.Contains("info", names);
        Assert.Contains("settings validate", names);
        Assert.Contains("logs show", names);
        Assert.Contains("naming preview", names);
        Assert.Contains("screenshot", names);
    }

    [Fact]
    public void InvalidArgumentsUseExitCodeTwoAndErrorEnvelope()
    {
        var invocation = Run("settings", "unknown");
        using var json = AssertJson(invocation, 2, "settings");

        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public void RecognizedCommandIsUsedWhenOptionParsingFails()
    {
        using var json = AssertJson(Run("remote", "wait", "--unknown"), 2, "remote wait");

        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void InvalidRemoteWaitTimeoutUsesFullCommandName()
    {
        using var json = AssertJson(Run("remote", "wait", "--state", "idle", "--timeout", "x"), 2, "remote wait");

        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void UnknownCommandKeepsItsFirstWordInTheErrorEnvelope()
    {
        using var json = AssertJson(Run("unrecognized", "--unknown"), 2, "unrecognized");

        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void JapaneseMessagesAreWrittenWithoutEscaping()
    {
        var invocation = Run("nope");

        Assert.Contains("コマンドを認識できません", invocation.StandardOutput);
    }

    [Fact]
    public void MissingExplicitFileUsesExitCodeThree()
    {
        var invocation = Run("settings", "show", "--file", Path.Combine(_root, "missing.json"));
        using var json = AssertJson(invocation, 3, "settings show");

        Assert.Equal("fileNotFound", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void MissingExplicitLogUsesExitCodeThree()
    {
        var invocation = Run("logs", "show", Path.Combine(_root, "missing.log"));
        using var json = AssertJson(invocation, 3, "logs show");

        Assert.Equal("fileNotFound", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void InvalidLogFilterUsesExitCodeTwoBeforeReadingLogs()
    {
        var invocation = Run("logs", "show", "--level", "trace");
        using var json = AssertJson(invocation, 2, "logs show");

        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void TextOptionProducesHumanReadableOutputOnly()
    {
        var invocation = Run("help", "--text");

        Assert.Equal(0, invocation.ExitCode);
        Assert.Contains("settings show", invocation.StandardOutput);
        Assert.Contains("--file <パス>", invocation.StandardOutput);
        Assert.Contains("--text", invocation.StandardOutput);
        Assert.DoesNotContain("\"cliVersion\"", invocation.StandardOutput);
        Assert.Empty(invocation.StandardError);
    }

    [Fact]
    public void RecordRejectsInvalidArgumentsBeforeStartingWorker()
    {
        string[][] cases =
        [
            ["record", "--dry-run", "--defaults"],
            ["record", "--dry-run", "--defaults", "--display", "0", "--rect", "0,0,100,100"],
            ["record", "--display", "0", "--defaults", "--duration", "3", "--pause-at", "1", "-o", "out.mp4"],
            ["record", "--display", "0", "--defaults", "--duration", "3", "--pause-at", "2", "--resume-at", "1", "-o", "out.mp4"]
        ];
        foreach (var arguments in cases)
        {
            using var json = AssertJson(Run(arguments), 2, "record");
            Assert.NotEqual(JsonValueKind.Null, json.RootElement.GetProperty("error").ValueKind);
        }
    }

    [Fact]
    public void RecordDryRunConvertsLeftMonitorRectangleToMonitorCoordinates()
    {
        using var json = AssertJson(Run("record", "--dry-run", "--defaults", "--rect", "-1800,100,400,300"), 0, "record");
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("DISPLAY_TEST", result.GetProperty("target").GetProperty("monitor").GetString());
        Assert.Equal(120, result.GetProperty("startData").GetProperty("sourceRect").GetProperty("x").GetInt32());
        Assert.Equal(100, result.GetProperty("startData").GetProperty("sourceRect").GetProperty("y").GetInt32());
    }

    [Fact]
    public void RecordRejectsRectangleSpanningDisplays()
    {
        using var json = AssertJson(Run("record", "--dry-run", "--defaults", "--rect", "-100,0,200,100"), 2, "record");
        Assert.Equal("rectSpansDisplays", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void RecordReportsMissingAppWithoutStartingWorker()
    {
        var environment = _environment with { FindApp = _ => (null, ["missing.exe"]) };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(["record", "--defaults", "--display", "0", "--duration", "3", "-o", Path.Combine(_root, "out.mp4")], output, error, environment);
        using var json = AssertJson(new Invocation(code, output.ToString(), error.ToString()), 3, "record");
        Assert.Equal("appNotFound", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void RecordRejectsExistingOutput()
    {
        var path = Path.Combine(_root, "out.mp4");
        File.WriteAllText(path, "existing");
        using var json = AssertJson(Run("record", "--defaults", "--display", "0", "--duration", "3", "-o", path), 2, "record");
        Assert.Equal("outputExists", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void ScreenshotValidatesTargetRectangleExtensionAndExistingOutput()
    {
        using var noTarget = AssertJson(Run("screenshot", "--defaults", "-o", Path.Combine(_root, "image.png")), 2, "screenshot");
        using var twoTargets = AssertJson(Run("screenshot", "--defaults", "--display", "0", "--rect", "0,0,100,100", "-o", Path.Combine(_root, "image.png")), 2, "screenshot");
        using var spanningRectangle = AssertJson(Run("screenshot", "--defaults", "--rect", "-100,0,200,100", "-o", Path.Combine(_root, "image.png")), 2, "screenshot");
        using var invalidExtension = AssertJson(Run("screenshot", "--defaults", "--display", "0", "-o", Path.Combine(_root, "image.bmp")), 2, "screenshot");
        var existingPath = Path.Combine(_root, "existing.png");
        File.WriteAllText(existingPath, "existing");
        using var existingOutput = AssertJson(Run("screenshot", "--defaults", "--display", "0", "-o", existingPath), 2, "screenshot");

        Assert.Equal("invalidArguments", noTarget.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalidArguments", twoTargets.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("rectSpansDisplays", spanningRectangle.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalidArguments", invalidExtension.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("outputExists", existingOutput.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void ProbeRejectsInvalidArgumentsAndMissingFile()
    {
        using var invalid = AssertJson(Run("probe", "x.mp4", "--frame", "1"), 2, "probe");
        using var missing = AssertJson(Run("probe", Path.Combine(_root, "missing.mp4")), 3, "probe");
        Assert.Equal("fileNotFound", missing.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void ProbeJpegReturnsDimensionsAndFormat()
    {
        var path = WriteProbeImage("image.JPEG", ImageFormat.Jpeg);

        using var json = AssertJson(Run("probe", path), 0, "probe");
        var result = json.RootElement.GetProperty("result");

        Assert.Equal("jpeg", result.GetProperty("format").GetString());
        Assert.Equal(320, result.GetProperty("width").GetInt32());
        Assert.Equal(180, result.GetProperty("height").GetInt32());
    }

    [Fact]
    public void ProbeReportsContentFormatAndFailsWhenItDiffersFromTheExtension()
    {
        var path = WriteProbeImage("renamed.jpg", ImageFormat.Png);

        using var json = AssertJson(Run("probe", path), 1, "probe");
        var result = json.RootElement.GetProperty("result");

        Assert.Equal("png", result.GetProperty("format").GetString());
        Assert.Contains(result.GetProperty("mismatches").EnumerateArray(), mismatch =>
            mismatch.GetProperty("property").GetString() == "format"
            && mismatch.GetProperty("expected").GetString() == "jpeg"
            && mismatch.GetProperty("actual").GetString() == "png");
    }

    [Fact]
    public void ProbeJpegAddsWidthMismatchAndReturnsCheckFailure()
    {
        var path = WriteProbeImage("image.jpg", ImageFormat.Jpeg);

        using var json = AssertJson(Run("probe", path, "--expect-width", "640"), 1, "probe");
        var mismatches = json.RootElement.GetProperty("result").GetProperty("mismatches");

        Assert.Contains(mismatches.EnumerateArray(), mismatch =>
            mismatch.GetProperty("property").GetString() == "width"
            && mismatch.GetProperty("expected").GetDouble() == 640
            && mismatch.GetProperty("actual").GetDouble() == 320);
    }

    [Fact]
    public void ProbeJpegRejectsVideoExpectations()
    {
        var path = WriteProbeImage("image.jpeg", ImageFormat.Jpeg);

        using var json = AssertJson(Run("probe", path, "--expect-fps", "30"), 2, "probe");

        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void ProbePngReturnsPngFormat()
    {
        var path = WriteProbeImage("image.png", ImageFormat.Png);

        using var json = AssertJson(Run("probe", path), 0, "probe");

        Assert.Equal("png", json.RootElement.GetProperty("result").GetProperty("format").GetString());
    }

    private string WriteProbeImage(string fileName, ImageFormat format)
    {
        var path = Path.Combine(_root, fileName);
        using var image = new Bitmap(320, 180);
        image.Save(path, format);
        return path;
    }

    [Fact]
    public async Task RemoteStatusReturnsStatusJson()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName);
        var invocation = await RunAsync(_environment, "remote", "status");

        using var json = AssertRemoteSuccess(invocation, "remote status");
        Assert.Equal("recording", json.RootElement.GetProperty("result").GetProperty("recording").GetProperty("state").GetString());
        Assert.Equal(["hello", "status"], server.Methods.ToArray());
    }

    [Fact]
    public async Task RemoteWaitReturnsStatusAndSendsWaitConditions()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName);
        var invocation = await RunAsync(_environment, "remote", "wait", "--state", "recording", "--timeout", "7");

        using var json = AssertRemoteSuccess(invocation, "remote wait");
        Assert.Equal("recording", json.RootElement.GetProperty("result").GetProperty("recording").GetProperty("state").GetString());
        Assert.Equal("waitFor", server.Methods.Last());
        Assert.True(server.WaitParameters.HasValue);
        Assert.Equal("recording", server.WaitParameters.Value.GetProperty("state").GetString());
        Assert.Equal(7000, server.WaitParameters.Value.GetProperty("timeoutMilliseconds").GetInt32());
        Assert.False(server.WaitParameters.Value.TryGetProperty("captureAfter", out var captureAfter) && captureAfter.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task RemoteWaitSendsCaptureAfterTimestamp()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName);
        var invocation = await RunAsync(_environment, "remote", "wait", "--state", "idle", "--capture-after", "2026-09-27T10:00:00+09:00");

        using var json = AssertRemoteSuccess(invocation, "remote wait");
        Assert.Equal("2026-09-27T10:00:00+09:00", server.WaitParameters!.Value.GetProperty("captureAfter").GetString());
    }

    [Fact]
    public void RemoteWaitRejectsInvalidCaptureAfterTimestamp()
    {
        using var json = AssertJson(Run("remote", "wait", "--state", "idle", "--capture-after", "not-a-date"), 2, "remote wait");
        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RemoteWaitAcceptsResponseAfterTheHelloDeadline()
    {
        // hello の応答を待つ 5 秒より長く、--timeout の 7 秒より短くする。
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName, TimeSpan.FromSeconds(6));
        var invocation = await RunAsync(_environment, "remote", "wait", "--state", "recording", "--timeout", "7");

        using var json = AssertRemoteSuccess(invocation, "remote wait");
        Assert.Equal("recording", json.RootElement.GetProperty("result").GetProperty("recording").GetProperty("state").GetString());
    }

    [Fact]
    public async Task RemotePerformSendsTheRequestedAction()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName);
        var invocation = await RunAsync(_environment, "remote", "perform", "screenshotFullScreen");

        using var json = AssertRemoteSuccess(invocation, "remote perform");
        Assert.True(json.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal("screenshotFullScreen", server.ParametersFor("perform").GetProperty("action").GetString());
        Assert.Equal(["hello", "perform"], server.Methods.ToArray());
    }

    [Fact]
    public async Task RemoteSelectSendsPhysicalRectangleParameters()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName);
        var invocation = await RunAsync(_environment, "remote", "select", "--rect", "-1920,20,320,240");

        using var json = AssertRemoteSuccess(invocation, "remote select");
        var parameters = server.ParametersFor("selection");
        Assert.Equal("rect", parameters.GetProperty("kind").GetString());
        Assert.Equal(-1920, parameters.GetProperty("x").GetInt32());
        Assert.Equal(20, parameters.GetProperty("y").GetInt32());
        Assert.Equal(320, parameters.GetProperty("width").GetInt32());
        Assert.Equal(240, parameters.GetProperty("height").GetInt32());
    }

    [Theory]
    [InlineData("--window", "0x1234", "window")]
    [InlineData("--cancel", "", "cancel")]
    public async Task RemoteSelectSendsWindowOrCancelParameters(string option, string value, string kind)
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName);
        var arguments = option == "--cancel"
            ? new[] { "remote", "select", option }
            : new[] { "remote", "select", option, value };
        var invocation = await RunAsync(_environment, arguments);

        using var json = AssertRemoteSuccess(invocation, "remote select");
        var parameters = server.ParametersFor("selection");
        Assert.Equal(kind, parameters.GetProperty("kind").GetString());
        if (kind == "window") Assert.Equal(4660, parameters.GetProperty("hwnd").GetInt64());
    }

    [Fact]
    public void RemotePerformAndSelectRejectInvalidArguments()
    {
        using var invalidAction = AssertJson(Run("remote", "perform", "unknown"), 2, "remote perform");
        using var multipleSelections = AssertJson(Run("remote", "select", "--cancel", "--window", "0x1234"), 2, "remote select");
        using var invalidRectangle = AssertJson(Run("remote", "select", "--rect", "0,0,0,10"), 2, "remote select");

        Assert.Equal("invalidArguments", invalidAction.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalidArguments", multipleSelections.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalidArguments", invalidRectangle.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RemoteCommandsMapServerRejectionDataCodeToCliError()
    {
        await using var server = FakeAutomationServer.Start(
            _environment.AutomationPipeName,
            errorMethod: "perform",
            errorCode: "rejectedDuringCountdown");
        var invocation = await RunAsync(_environment, "remote", "perform", "stopRecording");

        using var json = AssertJson(invocation, 3, "remote perform");
        Assert.Equal("rejectedDuringCountdown", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RemoteExitSucceedsWhenTheServerReturnsAnAcceptedResponse()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName);
        var invocation = await RunAsync(_environment, "remote", "exit");

        using var json = AssertRemoteSuccess(invocation, "remote exit");
        Assert.True(json.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal(["hello", "exit"], server.Methods.ToArray());
    }

    [Fact]
    public async Task RemoteExitReportsDisconnectedWhenTheServerClosesBeforeItsResponse()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName, disconnectMethod: "exit");
        var invocation = await RunAsync(_environment, "remote", "exit");

        using var json = AssertJson(invocation, 3, "remote exit");
        Assert.Equal("disconnected", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RemoteStatusReportsImpersonatedServerFromVerificationBoundary()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName);
        var environment = _environment with
        {
            VerifyAutomationServer = (_, expectedApp) =>
            {
                Assert.Equal("C:\\Expected\\ScreenRecorder.exe", expectedApp);
                return new CliAutomationServerVerification(Environment.ProcessId, "C:\\Other\\ScreenRecorder.exe", "impersonatedServer");
            }
        };
        var invocation = await RunAsync(environment, "remote", "status", "--app", "C:\\Expected\\ScreenRecorder.exe");

        using var json = AssertJson(invocation, 3, "remote status");
        Assert.Equal("impersonatedServer", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(server.Methods);
    }

    [Fact]
    public void RemoteStatusReportsNotRunning()
    {
        var environment = _environment with
        {
            AutomationPipeExists = _ => false,
            IsApplicationRunning = () => false,
            VerifyAutomationServer = (_, _) => throw new InvalidOperationException("確認関数は接続後にだけ使います。")
        };
        var invocation = Run(environment, "remote", "status");

        using var json = AssertJson(invocation, 3, "remote status");
        Assert.Equal("notRunning", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void RemoteStatusReportsAutomationDisabledWithoutWaitingWhenPipeIsMissingAndApplicationIsRunning()
    {
        var environment = _environment with
        {
            AutomationPipeExists = _ => false,
            IsApplicationRunning = () => true,
            VerifyAutomationServer = (_, _) => throw new InvalidOperationException("確認関数は接続後にだけ使います。")
        };
        var stopwatch = Stopwatch.StartNew();

        var invocation = Run(environment, "remote", "status");

        stopwatch.Stop();
        using var json = AssertJson(invocation, 3, "remote status");
        Assert.Equal("automationDisabled", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("ScreenRecorder は起動していますが、自動化用の接続を受け付けていません。設定の「自動化用の接続を受け付ける」をオンにして保存してください。", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"判定に {stopwatch.Elapsed} かかりました。");
    }

    [Fact]
    public void RemoteStatusReportsNotRunningWithoutWaitingWhenPipeAndApplicationAreMissing()
    {
        var environment = _environment with
        {
            AutomationPipeExists = _ => false,
            IsApplicationRunning = () => false,
            VerifyAutomationServer = (_, _) => throw new InvalidOperationException("確認関数は接続後にだけ使います。")
        };
        var stopwatch = Stopwatch.StartNew();

        var invocation = Run(environment, "remote", "status");

        stopwatch.Stop();
        using var json = AssertJson(invocation, 3, "remote status");
        Assert.Equal("notRunning", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"判定に {stopwatch.Elapsed} かかりました。");
    }

    [McpPipeFact]
    public void AutomationPipeAvailabilityDetectsExistingAndMissingPipes()
    {
        var pipeName = $"ScreenRecorder.Automation.Exists.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = _environment with
        {
            AutomationPipeName = pipeName,
            AutomationPipeExists = AutomationRemoteCommand.AutomationPipeExists
        };

        Assert.True(environment.AutomationPipeExists(environment.AutomationPipeName));
        Assert.False(environment.AutomationPipeExists($"{pipeName}.Missing"));
    }

    [Fact]
    public void HelpListsMcpAndRepeatableAllowDirectoryOption()
    {
        using var json = AssertJson(Run("help", "mcp"), 0, "help");
        var command = json.RootElement.GetProperty("result").GetProperty("commands")[0];

        Assert.Equal("mcp", command.GetProperty("name").GetString());
        Assert.Contains("[--allow-dir <フォルダー>]...", command.GetProperty("usage").GetString());
        Assert.Contains(command.GetProperty("options").EnumerateArray(), option => option.GetProperty("name").GetString() == "--allow-dir");
    }

    [Fact]
    public void HelpUsageShowsTextOptionOnlyForCommandsThatAcceptIt()
    {
        using var mcpJson = AssertJson(Run("help", "mcp"), 0, "help");
        var mcpUsage = mcpJson.RootElement.GetProperty("result").GetProperty("commands")[0].GetProperty("usage").GetString();
        Assert.DoesNotContain("[--text]", mcpUsage);
        Assert.Equal(0, mcpJson.RootElement.GetProperty("result").GetProperty("commonOptions").GetArrayLength());
        Assert.DoesNotContain("--text", Run("help", "mcp", "--text").StandardOutput);

        using var remoteJson = AssertJson(Run("help", "remote", "wait"), 0, "help");
        var remoteUsage = remoteJson.RootElement.GetProperty("result").GetProperty("commands")[0].GetProperty("usage").GetString();
        Assert.Contains("[--text]", remoteUsage);
        Assert.Equal(1, remoteJson.RootElement.GetProperty("result").GetProperty("commonOptions").GetArrayLength());

        var mcpText = Run("help", "mcp", "--text").StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.StartsWith("mcp ", mcpText);
        Assert.DoesNotContain("[--text]", mcpText);

        var remoteText = Run("help", "remote", "wait", "--text").StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.StartsWith("remote wait ", remoteText);
        Assert.Contains("[--text]", remoteText);
    }

    [Fact]
    public void McpCommandRejectsTextOptionDuringParsing()
    {
        var parsed = CliCommands.Parse(["mcp", "--text"]);

        Assert.False(parsed.Success);
        Assert.Equal("mcp", parsed.CommandName);
    }

    [McpPipeFact]
    public async Task McpRemotePinsTheServerOnlyWhenTheAppWasGivenExplicitly()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName, TimeSpan.Zero);
        var expectedPaths = new List<string?>();
        var environment = _environment with
        {
            VerifyAutomationServer = (_, expected) =>
            {
                expectedPaths.Add(expected);
                return new CliAutomationServerVerification(Environment.ProcessId, "C:\\ScreenRecorder.exe");
            }
        };
        var policy = McpPathAccessPolicy.Create([Path.GetTempPath()]);

        new McpCommandService("C:\\found\\ScreenRecorder.exe", policy, () => environment)
            .Invoke("remote_status", McpInput("{}"), CancellationToken.None);
        new McpCommandService("C:\\found\\ScreenRecorder.exe", policy, () => environment, remoteAppPath: "C:\\ScreenRecorder.exe")
            .Invoke("remote_status", McpInput("{}"), CancellationToken.None);

        Assert.Equal(new string?[] { null, "C:\\ScreenRecorder.exe" }, expectedPaths);
    }

    [McpPipeFact]
    public async Task McpCallsBecomeBusyAndRemoteWaitCancellationReleasesTheStaExecutor()
    {
        await using var server = FakeAutomationServer.Start(_environment.AutomationPipeName, TimeSpan.FromSeconds(8));
        var service = new McpCommandService(
            "C:\\ScreenRecorder.exe",
            McpPathAccessPolicy.Create([Path.GetTempPath()]),
            () => _environment);
        using var executor = new McpSerialExecutor();
        using var cancellation = new CancellationTokenSource();
        var waitInput = McpInput("""{"state":"idle","timeout":45}""");
        var first = executor.ExecuteAsync(
            () => service.Invoke("remote_wait", waitInput, cancellation.Token),
            () => service.Busy("remote_wait"),
            cancellation.Token);
        await WaitUntilAsync(() => server.Methods.Contains("waitFor"), TimeSpan.FromSeconds(5));

        var busy = await executor.ExecuteAsync(
            () => service.Invoke("info", new Dictionary<string, JsonElement>(), CancellationToken.None),
            () => service.Busy("info"),
            CancellationToken.None);
        Assert.NotNull(busy);
        Assert.Equal(3, busy.ExitCode);
        using (var busyJson = JsonDocument.Parse(busy.Json))
            Assert.Equal("busy", busyJson.RootElement.GetProperty("error").GetProperty("code").GetString());

        cancellation.Cancel();
        var cancelled = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(cancelled);
        using (var cancelledJson = JsonDocument.Parse(cancelled.Json))
            Assert.Equal("cancelled", cancelledJson.RootElement.GetProperty("error").GetProperty("code").GetString());

        var available = await executor.ExecuteAsync(
            () => service.Invoke("remote_status", new Dictionary<string, JsonElement>(), CancellationToken.None),
            () => service.Busy("remote_status"),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(available);
        Assert.Equal(0, available.ExitCode);
        using var availableJson = JsonDocument.Parse(available.Json);
        Assert.Equal("remote status", availableJson.RootElement.GetProperty("command").GetString());
    }

    private Invocation Run(params string[] arguments) => Run(_environment, arguments);

    private static Invocation Run(CliEnvironment environment, params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CliApplication.Run(arguments, output, error, environment);
        return new Invocation(exitCode, output.ToString(), error.ToString());
    }

    private Task<Invocation> RunAsync(CliEnvironment environment, params string[] arguments) =>
        Task.Run(() =>
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = CliApplication.Run(arguments, output, error, environment);
            return new Invocation(exitCode, output.ToString(), error.ToString());
        });

    private static IReadOnlyDictionary<string, JsonElement> McpInput(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("指定した状態を待ち受けましたが、期限内に届きませんでした。");
            await Task.Delay(25);
        }
    }

    private static JsonDocument AssertRemoteSuccess(Invocation invocation, string expectedCommand)
    {
        Assert.Equal(0, invocation.ExitCode);
        Assert.Empty(invocation.StandardError);
        using var document = JsonDocument.Parse(invocation.StandardOutput);
        Assert.Equal("cliVersion", document.RootElement.EnumerateObject().First().Name);
        Assert.Equal(expectedCommand, document.RootElement.GetProperty("command").GetString());
        Assert.True(document.RootElement.GetProperty("result").ValueKind == JsonValueKind.Object);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").ValueKind);
        return JsonDocument.Parse(invocation.StandardOutput);
    }

    private static JsonDocument AssertJson(Invocation invocation, int expectedExitCode, string expectedCommand)
    {
        Assert.Equal(expectedExitCode, invocation.ExitCode);
        Assert.Empty(invocation.StandardError);
        using var document = JsonDocument.Parse(invocation.StandardOutput);
        Assert.Equal("cliVersion", document.RootElement.EnumerateObject().First().Name);
        Assert.Equal(expectedCommand, document.RootElement.GetProperty("command").GetString());
        Assert.True(document.RootElement.GetProperty("warnings").ValueKind == JsonValueKind.Array);
        Assert.True(document.RootElement.TryGetProperty("error", out _));
        return JsonDocument.Parse(invocation.StandardOutput);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed record Invocation(int ExitCode, string StandardOutput, string StandardError);

    private sealed class FakeAutomationServer : IAsyncDisposable
    {
        private readonly AutomationPipeServer _server;
        private readonly ConcurrentQueue<string> _methods = new();
        private readonly TimeSpan _waitForDelay;
        private readonly string? _errorMethod;
        private readonly string? _errorCode;
        private readonly string? _disconnectMethod;
        private JsonElement? _waitParameters;
        private readonly ConcurrentDictionary<string, JsonElement> _parameters = new(StringComparer.Ordinal);

        private FakeAutomationServer(string pipeName, TimeSpan waitForDelay, string? errorMethod, string? errorCode, string? disconnectMethod)
        {
            _waitForDelay = waitForDelay;
            _errorMethod = errorMethod;
            _errorCode = errorCode;
            _disconnectMethod = disconnectMethod;
            if (!AutomationPipeServer.TryStart(pipeName, HandleLineAsync, out var server) || server is null)
                throw new InvalidOperationException("自動化用パイプを作成できませんでした。");
            _server = server;
        }

        public IReadOnlyCollection<string> Methods => _methods.ToArray();
        public JsonElement? WaitParameters => _waitParameters;
        public JsonElement ParametersFor(string method) => _parameters[method];

        public static FakeAutomationServer Start(
            string pipeName,
            TimeSpan waitForDelay = default,
            string? errorMethod = null,
            string? errorCode = null,
            string? disconnectMethod = null) => new(pipeName, waitForDelay, errorMethod, errorCode, disconnectMethod);

        public ValueTask DisposeAsync() => _server.DisposeAsync();

        private async Task<AutomationPipeResponse?> HandleLineAsync(string line, CancellationToken cancellationToken)
        {
            var read = AutomationProtocol.ReadRequest(line);
            if (!read.Success) return new AutomationPipeResponse(AutomationProtocol.WriteError(read.Id, read.Error!));
            var request = read.Request!;
            _methods.Enqueue(request.Method!);
            if (request.Params is { } sentParameters)
                _parameters[request.Method!] = sentParameters.Clone();
            if (request.Method == "waitFor" && request.Params is { } parameters)
                _waitParameters = parameters.Clone();
            if (request.Method == "waitFor" && _waitForDelay > TimeSpan.Zero)
                await Task.Delay(_waitForDelay, cancellationToken);
            if (request.Method == _disconnectMethod) throw new IOException("応答前に切断しました。");
            await Task.Yield();
            var response = request.Method switch
            {
                _ when request.Method == _errorMethod => AutomationProtocol.WriteError(request.Id, AutomationProtocol.Error(AutomationRpcErrorCodes.RequestRejected, "要求を断りました。", _errorCode ?? "requestRejected")),
                "hello" => AutomationProtocol.WriteResult(request.Id, new AutomationHelloResult(AutomationProtocol.CurrentVersion, Environment.ProcessId), AutomationJsonContext.Default.AutomationHelloResult),
                "status" or "waitFor" => AutomationProtocol.WriteResult(request.Id, Status(), AutomationJsonContext.Default.AutomationStatus),
                "perform" or "selection" or "exit" => AutomationProtocol.WriteResult(request.Id, new AutomationAcceptedResult(true), AutomationJsonContext.Default.AutomationAcceptedResult),
                _ => AutomationProtocol.WriteError(request.Id, AutomationProtocol.Error(AutomationRpcErrorCodes.MethodNotFound, "未対応のメソッドです。", "methodNotFound"))
            };
            return new AutomationPipeResponse(response);
        }

        private static AutomationStatus Status() => new(
            new AutomationRecordingStatus("recording", null, false),
            false,
            false,
            new AutomationMenuStatus(true, true, "一時停止"),
            [],
            new AutomationDirectoriesStatus(new AutomationDirectoryStatus("C:\\Pictures", true), new AutomationDirectoryStatus("C:\\Videos", true)),
            new AutomationUiStatus(false, false, false, false, false, false, null),
            null,
            null);
    }
}
