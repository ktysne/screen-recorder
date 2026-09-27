using System.Text.Json;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class AutomationRemoteProtocolTests
{
    [Fact]
    public void RequestRoundTripsWithCamelCaseProperties()
    {
        using var idDocument = JsonDocument.Parse("17");
        var parameters = JsonSerializer.SerializeToElement(
            new AutomationWaitForParams("recording", null, 5000),
            AutomationJsonContext.Default.AutomationWaitForParams);

        var line = AutomationProtocol.WriteRequest(idDocument.RootElement, "waitFor", parameters);
        var read = AutomationProtocol.ReadRequest(line);

        Assert.True(read.Success);
        Assert.Equal("waitFor", read.Request!.Method);
        Assert.Equal("2.0", read.Request.Jsonrpc);
        Assert.Equal("17", read.Id!.Value.GetRawText());
        Assert.Equal("recording", read.Request.Params!.Value.GetProperty("state").GetString());
        Assert.Equal(5000, read.Request.Params.Value.GetProperty("timeoutMilliseconds").GetInt32());
    }

    [Fact]
    public void ResultRoundTripsWithoutAnErrorProperty()
    {
        using var idDocument = JsonDocument.Parse("1");
        var line = AutomationProtocol.WriteResult(
            idDocument.RootElement,
            new AutomationHelloResult(AutomationProtocol.CurrentVersion, 123),
            AutomationJsonContext.Default.AutomationHelloResult);

        var response = AutomationProtocol.ReadResponse(line);

        Assert.Equal("1", response.Id!.Value.GetRawText());
        Assert.Equal(AutomationProtocol.CurrentVersion, response.Result!.Value.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(123, response.Result.Value.GetProperty("processId").GetInt32());
        using var document = JsonDocument.Parse(line);
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void ErrorResponseContainsJsonRpcNumberAndCliCode()
    {
        var error = AutomationProtocol.Error(AutomationRpcErrorCodes.Busy, "待機中の要求が上限に達しました。", "busy");
        var line = AutomationProtocol.WriteError(null, error);

        using var document = JsonDocument.Parse(line);
        var responseError = document.RootElement.GetProperty("error");
        Assert.Equal(-32001, responseError.GetProperty("code").GetInt32());
        Assert.Equal("待機中の要求が上限に達しました。", responseError.GetProperty("message").GetString());
        Assert.Equal("busy", responseError.GetProperty("data").GetProperty("code").GetString());
        Assert.False(document.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public void InvalidJsonProducesParseError()
    {
        var read = AutomationProtocol.ReadRequest("{broken");

        Assert.False(read.Success);
        Assert.Equal(AutomationRpcErrorCodes.ParseError, read.Error!.Code);
        Assert.Equal("parseError", read.Error.Data.Code);
    }

    [Fact]
    public void StatusKeepsUnavailableCaptureHistoryFieldsAsNull()
    {
        using var idDocument = JsonDocument.Parse("1");
        var status = new AutomationStatus(
            new AutomationRecordingStatus("idle", null, false),
            false,
            false,
            new AutomationMenuStatus(false, false, "一時停止"),
            [],
            new AutomationDirectoriesStatus(new AutomationDirectoryStatus("C:\\Pictures", true), new AutomationDirectoryStatus("C:\\Videos", true)),
            new AutomationUiStatus(false, false, false, false, false, false, null),
            null,
            null);

        var line = AutomationProtocol.WriteResult(idDocument.RootElement, status, AutomationJsonContext.Default.AutomationStatus);

        using var document = JsonDocument.Parse(line);
        var result = document.RootElement.GetProperty("result");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("recording").GetProperty("countdownRemainingSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("ui").GetProperty("countdown").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("lastCapture").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("lastFailure").ValueKind);
    }
}
