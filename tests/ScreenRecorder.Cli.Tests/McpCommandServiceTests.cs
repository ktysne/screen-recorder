using System.Text.Json;
using ScreenRecorder.Cli;
using Xunit;

namespace ScreenRecorder.Cli.Tests;

public sealed class McpCommandServiceTests
{
    [Fact]
    public void McpStartupAcceptsRepeatedAllowDirectoriesAndLeavesTheAppToTheSearchByDefault()
    {
        var firstAllowed = Path.Combine(Path.GetTempPath(), "mcp-first");
        var secondAllowed = Path.Combine(Path.GetTempPath(), "mcp-second");

        Assert.True(McpServerHost.TryParseStartupOptions(
            ["--allow-dir", firstAllowed, "--allow-dir", secondAllowed],
            out var appPath,
            out var allowDirectories,
            out var error));

        Assert.Null(appPath);
        Assert.Equal(new[] { firstAllowed, secondAllowed }, allowDirectories);
        Assert.Empty(error);
    }

    [Fact]
    public void McpUsesTheAppFoundByTheSameSearchAsRecord()
    {
        var found = Path.Combine(Path.GetTempPath(), "build", "ScreenRecorder.exe");
        var standardError = new StringWriter();

        var appPath = McpServerHost.ResolveAppPath(null, _ => (found, ["sibling.exe", found]), standardError);

        Assert.Equal(found, appPath);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public void McpReportsSearchedPlacesAtStartupWhenTheAppIsMissing()
    {
        var standardError = new StringWriter();

        var appPath = McpServerHost.ResolveAppPath(null, _ => (null, ["sibling.exe", "build.exe"]), standardError);

        Assert.Equal("sibling.exe", appPath);
        Assert.Contains("sibling.exe, build.exe", standardError.ToString());
    }

    [Fact]
    public void McpStartupRejectsDuplicateAppAndEmptyAllowDirectory()
    {
        Assert.False(McpServerHost.TryParseStartupOptions(
            ["--app", "one.exe", "--app", "two.exe"],
            out _, out _, out _));
        Assert.False(McpServerHost.TryParseStartupOptions(
            ["--allow-dir", " "],
            out _, out _, out _));
    }

    [Fact]
    public void McpCallsRejectRecordDurationAboveThirtySeconds()
    {
        var service = CreateService();

        var result = service.Invoke("record", Input("""{"duration":30.01}"""), CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        using var json = JsonDocument.Parse(result.Json);
        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("1.0.0", json.RootElement.GetProperty("cliVersion").GetString());
        Assert.Contains("remote_perform", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void McpCallsRejectRemoteWaitTimeoutAboveFortyFiveSeconds()
    {
        var service = CreateService();

        var result = service.Invoke("remote_wait", Input("""{"state":"idle","timeout":46}"""), CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        using var json = JsonDocument.Parse(result.Json);
        Assert.Equal("invalidArguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("remote_wait", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void McpCallsReturnPathNotAllowedBeforeRunningAWriteCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screenrecorder-mcp-{Guid.NewGuid():N}");
        var allowed = Path.Combine(root, "allowed");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        try
        {
            var service = new McpCommandService(
                "C:\\ScreenRecorder.exe",
                McpPathAccessPolicy.Create([allowed]),
                CreateEnvironment);
            var input = Input(JsonSerializer.Serialize(new { display = 0, output = Path.Combine(outside, "image.png") }));
            var result = service.Invoke("screenshot", input, CancellationToken.None);

            Assert.Equal(2, result.ExitCode);
            using var json = JsonDocument.Parse(result.Json);
            Assert.Equal("pathNotAllowed", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static McpCommandService CreateService() => new(
        "C:\\ScreenRecorder.exe",
        McpPathAccessPolicy.Create([Path.GetTempPath()]),
        CreateEnvironment);

    private static CliEnvironment CreateEnvironment() => new(
        Path.GetTempPath(),
        Path.GetTempPath(),
        () => [],
        () => DateTimeOffset.Now,
        "1.0.0");

    private static IReadOnlyDictionary<string, JsonElement> Input(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
    }
}
