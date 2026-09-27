using System.Text.Json;
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
            "9.8.7");
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

    private Invocation Run(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CliApplication.Run(arguments, output, error, _environment);
        return new Invocation(exitCode, output.ToString(), error.ToString());
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
}
