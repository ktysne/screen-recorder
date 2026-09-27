using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ScreenRecorder.Core;

public enum RecordingWorkerSourceKind
{
    Display,
    Region,
    Window
}

public enum RecordingWorkerRecordingState
{
    Recording,
    Paused,
    Saving
}

public enum RecordingWorkerOperationKind
{
    Pause,
    Resume
}

public readonly record struct RecordingWorkerRectangle(int X, int Y, int Width, int Height);

public readonly record struct RecordingWorkerSize(int Width, int Height);

public sealed record RecordingWorkerStartData(
    string OutputPath,
    RecordingWorkerSourceKind SourceKind,
    string DisplayDeviceName,
    RecordingWorkerRectangle? SourceRect,
    long WindowHandle,
    RecordingWorkerSize SourceFrameSize,
    RecordingWorkerSize OutputFrameSize,
    int FrameRate,
    int BitrateMbps,
    bool CaptureCursor,
    bool HighlightClicks,
    bool HardwareEncodingEnabled,
    bool RequireCaptureBorder,
    bool CaptureSystemAudio,
    bool CaptureMicrophone,
    string? MicrophoneDeviceId,
    int AacBitrateKbps);

public abstract record RecordingWorkerMessage
{
    public abstract string Type { get; }
}

public sealed record RecordingWorkerStartCommand(RecordingWorkerStartData Request) : RecordingWorkerMessage
{
    public override string Type => "start";
}

public sealed record RecordingWorkerPauseCommand(long OperationId) : RecordingWorkerMessage
{
    public override string Type => "pause";
}

public sealed record RecordingWorkerResumeCommand(long OperationId) : RecordingWorkerMessage
{
    public override string Type => "resume";
}

public sealed record RecordingWorkerStopCommand : RecordingWorkerMessage
{
    public override string Type => "stop";
}

public sealed record RecordingWorkerReadyResponseCommand(DiagnosticLogLevel MinimumLogLevel) : RecordingWorkerMessage
{
    public override string Type => "readyResponse";
}

public sealed record RecordingWorkerReadyMessage(int ProcessId, int ProtocolVersion) : RecordingWorkerMessage
{
    public override string Type => "ready";
}

public sealed record RecordingWorkerStateMessage(RecordingWorkerRecordingState State) : RecordingWorkerMessage
{
    public override string Type => "state";
}

public sealed record RecordingWorkerCompletedMessage(string FilePath) : RecordingWorkerMessage
{
    public override string Type => "completed";
}

public sealed record RecordingWorkerFailedMessage(string FilePath, string Error, bool BeforeRecordingStarted) : RecordingWorkerMessage
{
    public override string Type => "failed";
}

public sealed record RecordingWorkerWarningMessage(string Message) : RecordingWorkerMessage
{
    public override string Type => "warning";
}

public sealed record RecordingWorkerOperationFailedMessage(
    long OperationId,
    RecordingWorkerOperationKind Operation,
    string Error) : RecordingWorkerMessage
{
    public override string Type => "operationFailed";
}

public sealed record RecordingWorkerTerminationMessage(RecordingTerminationOutcome Outcome) : RecordingWorkerMessage
{
    public override string Type => "termination";
}

public sealed record RecordingWorkerLogMessage(DiagnosticLogLevel Level, string Tag, string Message) : RecordingWorkerMessage
{
    public override string Type => "log";
}

public static class RecordingWorkerProtocol
{
    public const int CurrentVersion = 1;

    public static byte[] Serialize(RecordingWorkerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message switch
        {
            RecordingWorkerStartCommand value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerStartCommand),
            RecordingWorkerPauseCommand value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerPauseCommand),
            RecordingWorkerResumeCommand value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerResumeCommand),
            RecordingWorkerStopCommand value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerStopCommand),
            RecordingWorkerReadyResponseCommand value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerReadyResponseCommand),
            RecordingWorkerReadyMessage value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerReadyMessage),
            RecordingWorkerStateMessage value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerStateMessage),
            RecordingWorkerCompletedMessage value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerCompletedMessage),
            RecordingWorkerFailedMessage value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerFailedMessage),
            RecordingWorkerWarningMessage value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerWarningMessage),
            RecordingWorkerOperationFailedMessage value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerOperationFailedMessage),
            RecordingWorkerTerminationMessage value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerTerminationMessage),
            RecordingWorkerLogMessage value => Serialize(value, RecordingWorkerJsonContext.Default.RecordingWorkerLogMessage),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.GetType(), "未対応の録画プロセスメッセージです。")
        };
    }

    public static RecordingWorkerMessageReadResult Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        var json = utf8Json.ToArray();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return RecordingWorkerMessageReadResult.Unreadable(RecordingWorkerMessageReadError.InvalidJson);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return RecordingWorkerMessageReadResult.Unreadable(RecordingWorkerMessageReadError.MissingType);
            }

            var type = typeElement.GetString();
            try
            {
                return type switch
                {
                    "start" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerStartCommand),
                    "pause" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerPauseCommand),
                    "resume" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerResumeCommand),
                    "stop" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerStopCommand),
                    "readyResponse" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerReadyResponseCommand),
                    "ready" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerReadyMessage),
                    "state" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerStateMessage),
                    "completed" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerCompletedMessage),
                    "failed" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerFailedMessage),
                    "warning" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerWarningMessage),
                    "operationFailed" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerOperationFailedMessage),
                    "termination" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerTerminationMessage),
                    "log" => Deserialize(json, RecordingWorkerJsonContext.Default.RecordingWorkerLogMessage),
                    _ => RecordingWorkerMessageReadResult.Unreadable(RecordingWorkerMessageReadError.UnknownType)
                };
            }
            catch (JsonException)
            {
                return RecordingWorkerMessageReadResult.Unreadable(RecordingWorkerMessageReadError.InvalidMessage);
            }
            catch (NotSupportedException)
            {
                return RecordingWorkerMessageReadResult.Unreadable(RecordingWorkerMessageReadError.InvalidMessage);
            }
        }
    }

    public static RecordingWorkerMessageReadResult Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Deserialize(Encoding.UTF8.GetBytes(json));
    }

    private static byte[] Serialize<TMessage>(TMessage message, JsonTypeInfo<TMessage> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);

    private static RecordingWorkerMessageReadResult Deserialize<TMessage>(byte[] json, JsonTypeInfo<TMessage> typeInfo)
        where TMessage : RecordingWorkerMessage
    {
        var message = JsonSerializer.Deserialize(json, typeInfo);
        return message is null || !IsValid(message)
            ? RecordingWorkerMessageReadResult.Unreadable(RecordingWorkerMessageReadError.InvalidMessage)
            : RecordingWorkerMessageReadResult.Readable(message);
    }

    private static bool IsValid(RecordingWorkerMessage message) => message switch
    {
        RecordingWorkerStartCommand { Request: { } request } =>
            request.OutputPath is not null
            && request.DisplayDeviceName is not null
            && Enum.IsDefined(request.SourceKind),
        RecordingWorkerReadyResponseCommand readyResponse => Enum.IsDefined(readyResponse.MinimumLogLevel),
        RecordingWorkerReadyMessage ready => ready.ProcessId > 0,
        RecordingWorkerStateMessage state => Enum.IsDefined(state.State),
        RecordingWorkerCompletedMessage completed => completed.FilePath is not null,
        RecordingWorkerFailedMessage failed => failed.FilePath is not null && failed.Error is not null,
        RecordingWorkerWarningMessage warning => warning.Message is not null,
        RecordingWorkerOperationFailedMessage operationFailed => operationFailed.OperationId > 0
            && Enum.IsDefined(operationFailed.Operation)
            && operationFailed.Error is not null,
        RecordingWorkerTerminationMessage termination => Enum.IsDefined(termination.Outcome),
        RecordingWorkerLogMessage log => Enum.IsDefined(log.Level) && log.Tag is not null && log.Message is not null,
        RecordingWorkerPauseCommand pause => pause.OperationId > 0,
        RecordingWorkerResumeCommand resume => resume.OperationId > 0,
        RecordingWorkerStopCommand => true,
        _ => false
    };
}

public enum RecordingWorkerMessageReadError
{
    None,
    InvalidJson,
    MissingType,
    UnknownType,
    InvalidMessage
}

public sealed record RecordingWorkerMessageReadResult(
    RecordingWorkerMessage? Message,
    RecordingWorkerMessageReadError Error)
{
    public bool IsReadable => Message is not null;

    internal static RecordingWorkerMessageReadResult Readable(RecordingWorkerMessage message) =>
        new(message, RecordingWorkerMessageReadError.None);

    internal static RecordingWorkerMessageReadResult Unreadable(RecordingWorkerMessageReadError error) =>
        new(null, error);
}

public sealed record RecordingWorkerLineReadResult(string? Line, bool IsTooLong);

public sealed class RecordingWorkerLineReader
{
    public const int MaximumLineLengthBytes = 1_048_576;

    private readonly int _maximumLineLengthBytes;
    private readonly StringBuilder _line = new();
    private int _lineLengthBytes;
    private bool _pendingHighSurrogate;
    private bool _discardingLine;

    public RecordingWorkerLineReader(int maximumLineLengthBytes = MaximumLineLengthBytes)
    {
        if (maximumLineLengthBytes < 1 || maximumLineLengthBytes == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumLineLengthBytes));

        _maximumLineLengthBytes = maximumLineLengthBytes;
    }

    public IReadOnlyList<RecordingWorkerLineReadResult> Append(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var completedLines = new List<RecordingWorkerLineReadResult>();

        foreach (var character in fragment)
        {
            if (character == '\n')
            {
                CompleteLine(completedLines);
                continue;
            }

            if (_discardingLine) continue;

            _line.Append(character);
            AddUtf8Length(character);
            if (_lineLengthBytes > _maximumLineLengthBytes + 1)
            {
                _discardingLine = true;
                _line.Clear();
            }
        }

        return completedLines;
    }

    private void CompleteLine(List<RecordingWorkerLineReadResult> completedLines)
    {
        if (_discardingLine)
        {
            completedLines.Add(new RecordingWorkerLineReadResult(null, true));
            ResetLine();
            return;
        }

        var isCarriageReturnTerminator = _line.Length > 0 && _line[^1] == '\r';
        var contentLengthBytes = _lineLengthBytes - (isCarriageReturnTerminator ? 1 : 0);
        if (contentLengthBytes > _maximumLineLengthBytes)
        {
            completedLines.Add(new RecordingWorkerLineReadResult(null, true));
        }
        else
        {
            if (isCarriageReturnTerminator) _line.Length--;
            completedLines.Add(new RecordingWorkerLineReadResult(_line.ToString(), false));
        }

        ResetLine();
    }

    private void AddUtf8Length(char character)
    {
        if (_pendingHighSurrogate)
        {
            _pendingHighSurrogate = false;
            if (char.IsLowSurrogate(character))
            {
                _lineLengthBytes++;
                return;
            }
        }

        if (char.IsHighSurrogate(character))
        {
            _pendingHighSurrogate = true;
            _lineLengthBytes += 3;
        }
        else if (char.IsLowSurrogate(character) || character > 0x07ff)
        {
            _lineLengthBytes += 3;
        }
        else if (character > 0x7f)
        {
            _lineLengthBytes += 2;
        }
        else
        {
            _lineLengthBytes++;
        }
    }

    private void ResetLine()
    {
        _line.Clear();
        _lineLengthBytes = 0;
        _pendingHighSurrogate = false;
        _discardingLine = false;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RecordingWorkerStartCommand))]
[JsonSerializable(typeof(RecordingWorkerPauseCommand))]
[JsonSerializable(typeof(RecordingWorkerResumeCommand))]
[JsonSerializable(typeof(RecordingWorkerStopCommand))]
[JsonSerializable(typeof(RecordingWorkerReadyResponseCommand))]
[JsonSerializable(typeof(RecordingWorkerReadyMessage))]
[JsonSerializable(typeof(RecordingWorkerStateMessage))]
[JsonSerializable(typeof(RecordingWorkerCompletedMessage))]
[JsonSerializable(typeof(RecordingWorkerFailedMessage))]
[JsonSerializable(typeof(RecordingWorkerWarningMessage))]
[JsonSerializable(typeof(RecordingWorkerOperationFailedMessage))]
[JsonSerializable(typeof(RecordingWorkerTerminationMessage))]
[JsonSerializable(typeof(RecordingWorkerLogMessage))]
internal partial class RecordingWorkerJsonContext : JsonSerializerContext
{
}
