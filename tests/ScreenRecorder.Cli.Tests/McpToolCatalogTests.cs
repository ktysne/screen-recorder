using System.Text.Json;
using ScreenRecorder.Cli;
using Xunit;

namespace ScreenRecorder.Cli.Tests;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void ToolNamesMatchEveryNonMcpCliCommand()
    {
        var expected = CliCommands.All
            .Where(command => command.Name is not ("help" or "mcp"))
            .Select(command => command.Name.Replace(' ', '_'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, McpToolCatalog.Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SchemasUseTypedValuesAndRequiredPositionals()
    {
        var record = GetTool("record").InputSchema.GetProperty("properties");
        Assert.Equal("integer", record.GetProperty("display").GetProperty("type").GetString());
        Assert.Equal("number", record.GetProperty("duration").GetProperty("type").GetString());
        Assert.Equal("string", record.GetProperty("output").GetProperty("type").GetString());
        Assert.Equal("boolean", record.GetProperty("force").GetProperty("type").GetString());
        Assert.Equal("string", record.GetProperty("log_level").GetProperty("type").GetString());
        Assert.Equal(new[] { "silent", "error", "warn", "info", "debug" },
            record.GetProperty("log_level").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "file" }, GetTool("probe").InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "action" }, GetTool("remote_perform").InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Empty(GetTool("logs_show").InputSchema.GetProperty("required").EnumerateArray());
    }

    [Fact]
    public void ToolAnnotationsFollowTheMcpSpecification()
    {
        Assert.True(GetTool("info").ReadOnlyHint);
        Assert.False(GetTool("record").ReadOnlyHint);
        Assert.Null(GetTool("probe").ReadOnlyHint);
        Assert.True(GetTool("remote_exit").DestructiveHint);
        Assert.True(GetTool("screenshot").DestructiveHint);
        Assert.Contains("開発向け", GetTool("naming_preview").Description);
        Assert.Contains("remote_perform と remote_wait", GetTool("record").Description);
    }

    [Fact]
    public void InputConvertsEnumNumbersBooleansAndOutputName()
    {
        var result = Convert("record", """
            {"display":1,"duration":5.25,"output":"C:\\capture.mp4","force":true,"log_level":"debug"}
            """);

        Assert.True(result.Success);
        Assert.Equal(new[] { "record", "--display", "1", "-o", "C:\\capture.mp4", "--duration", "5.25", "--force", "--log-level", "debug" }, result.Arguments);
    }

    [Fact]
    public void InputConvertsPositionalsAndEnumPositionals()
    {
        var probe = Convert("probe", """{"file":"C:\\capture.mp4"}""");
        var perform = Convert("remote_perform", """{"action":"recordingFullScreen"}""");

        Assert.Equal(new[] { "probe", "C:\\capture.mp4" }, probe.Arguments);
        Assert.Equal(new[] { "remote", "perform", "recordingFullScreen" }, perform.Arguments);
    }

    [Fact]
    public void InputOmitsFalseBooleansAndRejectsInvalidOrHiddenProperties()
    {
        var omitted = Convert("screenshot", """{"display":0,"output":"C:\\image.png","force":false}""");
        var invalidEnum = Convert("naming_preview", """{"mode":"invalid"}""");
        var externalApp = Convert("record", """{"app":"C:\\other.exe"}""");
        var textMode = Convert("info", """{"text":true}""");

        Assert.Equal(new[] { "screenshot", "--display", "0", "-o", "C:\\image.png" }, omitted.Arguments);
        Assert.False(invalidEnum.Success);
        Assert.False(externalApp.Success);
        Assert.False(textMode.Success);
    }

    private static McpToolDescriptor GetTool(string name) => McpToolCatalog.Find(name)!;

    private static McpArgumentResult Convert(string toolName, string json)
    {
        using var document = JsonDocument.Parse(json);
        var input = document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
        return McpToolInput.BuildArguments(GetTool(toolName), input);
    }
}
