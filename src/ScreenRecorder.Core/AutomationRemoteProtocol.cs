using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ScreenRecorder.Core;

public static class AutomationProtocol
{
    public const int CurrentVersion = 1;
    public const string PipeName = "ScreenRecorder.Automation";

    public static AutomationRequestReadResult ReadRequest(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var utf8Json = Encoding.UTF8.GetBytes(line);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json);
        }
        catch (JsonException)
        {
            return AutomationRequestReadResult.Failed(null, Error(-32700, "JSON を解析できません。", "parseError"));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return AutomationRequestReadResult.Failed(null, Error(-32600, "要求の形式が正しくありません。", "invalidRequest"));

            JsonElement? id = null;
            if (root.TryGetProperty("id", out var idElement))
            {
                if (idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                    return AutomationRequestReadResult.Failed(null, Error(-32600, "要求 ID の形式が正しくありません。", "invalidRequest"));
                id = idElement.Clone();
            }

            if (!root.TryGetProperty("jsonrpc", out var version)
                || version.ValueKind != JsonValueKind.String
                || version.GetString() != "2.0"
                || !root.TryGetProperty("method", out var method)
                || method.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(method.GetString()))
                return AutomationRequestReadResult.Failed(id, Error(-32600, "要求の形式が正しくありません。", "invalidRequest"));

            JsonElement? parameters = null;
            if (root.TryGetProperty("params", out var paramsElement))
            {
                if (paramsElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    return AutomationRequestReadResult.Failed(id, Error(-32600, "要求の引数の形式が正しくありません。", "invalidRequest"));
                parameters = paramsElement.Clone();
            }

            var request = new AutomationJsonRpcRequest("2.0", id, method.GetString(), parameters);
            return AutomationRequestReadResult.Read(request);
        }
    }

    public static AutomationJsonRpcResponse ReadResponse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var response = JsonSerializer.Deserialize(line, AutomationJsonContext.Default.AutomationJsonRpcResponse);
        if (response is null || response.Jsonrpc != "2.0" || response.Id is null || response.Result is null == (response.Error is null))
            throw new JsonException("JSON-RPC 応答の形式が正しくありません。");
        return response;
    }

    public static string WriteRequest(JsonElement id, string method, JsonElement? parameters = null) =>
        JsonSerializer.Serialize(
            new AutomationJsonRpcRequest("2.0", id, method, parameters),
            AutomationJsonContext.Default.AutomationJsonRpcRequest);

    public static string WriteNotification(string method, JsonElement? parameters = null) =>
        JsonSerializer.Serialize(
            new AutomationJsonRpcRequest("2.0", null, method, parameters),
            AutomationJsonContext.Default.AutomationJsonRpcRequest);

    public static string WriteResult<T>(JsonElement? id, T result, JsonTypeInfo<T> resultType) =>
        JsonSerializer.Serialize(
            new AutomationJsonRpcResponse("2.0", id, JsonSerializer.SerializeToElement(result, resultType), null),
            AutomationJsonContext.Default.AutomationJsonRpcResponse);

    public static string WriteError(JsonElement? id, AutomationJsonRpcError error) =>
        JsonSerializer.Serialize(
            new AutomationJsonRpcResponse("2.0", id, null, error),
            AutomationJsonContext.Default.AutomationJsonRpcResponse);

    public static AutomationJsonRpcError Error(int number, string message, string dataCode) =>
        new(number, message, new AutomationJsonRpcErrorData(dataCode));

    public static T ReadParameters<T>(JsonElement parameters, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(parameters, typeInfo)
        ?? throw new JsonException("要求の引数がありません。");
}

public static class AutomationRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int Busy = -32001;
    public const int Timeout = -32002;
}

public sealed record AutomationJsonRpcRequest(string Jsonrpc, JsonElement? Id, string? Method, JsonElement? Params);

public sealed record AutomationJsonRpcErrorData(string Code);

public sealed record AutomationJsonRpcError(int Code, string Message, AutomationJsonRpcErrorData Data);

public sealed record AutomationJsonRpcResponse(
    string Jsonrpc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Id,
    JsonElement? Result,
    AutomationJsonRpcError? Error);

public sealed record AutomationRequestReadResult(
    AutomationJsonRpcRequest? Request,
    JsonElement? Id,
    AutomationJsonRpcError? Error)
{
    public bool Success => Request is not null;

    internal static AutomationRequestReadResult Read(AutomationJsonRpcRequest request) => new(request, request.Id, null);

    internal static AutomationRequestReadResult Failed(JsonElement? id, AutomationJsonRpcError error) => new(null, id, error);
}

public sealed record AutomationHelloResult(int ProtocolVersion, int ProcessId);

public sealed record AutomationWaitForParams(string State, DateTimeOffset? CaptureAfter, int TimeoutMilliseconds);

public sealed record AutomationRecordingStatus(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? CountdownRemainingSeconds,
    bool SelectionInProgress);

public sealed record AutomationMenuStatus(bool PauseResumeEnabled, bool StopEnabled, string PauseResumeText);

public sealed record AutomationShortcutStatus(string Action, string Key, bool Enabled, bool RegistrationFailed);

public sealed record AutomationDirectoryStatus(string Path, bool Confirmed);

public sealed record AutomationDirectoriesStatus(AutomationDirectoryStatus StillImage, AutomationDirectoryStatus Video);

public sealed record AutomationCountdownStatus(string Kind, int RemainingSeconds);

public sealed record AutomationUiStatus(
    bool SettingsOpen,
    bool UpdateDialogOpen,
    bool SaveDirectoryDialogOpen,
    bool SelectionScreenOpen,
    bool RecordingToolbarOpen,
    bool ModalDialogOpen,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AutomationCountdownStatus? Countdown);

public sealed record AutomationCaptureRecord(string Kind, string? Path, DateTimeOffset At, string? Notification);

public sealed record AutomationFailureRecord(string Kind, string? Path, DateTimeOffset At, string Notification);

public sealed record AutomationStatus(
    AutomationRecordingStatus Recording,
    bool ScreenshotInProgress,
    bool ExitRequested,
    AutomationMenuStatus Menu,
    IReadOnlyList<AutomationShortcutStatus> Shortcuts,
    AutomationDirectoriesStatus Directories,
    AutomationUiStatus Ui,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AutomationCaptureRecord? LastCapture,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AutomationFailureRecord? LastFailure);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AutomationJsonRpcRequest))]
[JsonSerializable(typeof(AutomationJsonRpcResponse))]
[JsonSerializable(typeof(AutomationJsonRpcError))]
[JsonSerializable(typeof(AutomationJsonRpcErrorData))]
[JsonSerializable(typeof(AutomationHelloResult))]
[JsonSerializable(typeof(AutomationWaitForParams))]
[JsonSerializable(typeof(AutomationStatus))]
public partial class AutomationJsonContext : JsonSerializerContext
{
}
