using System.Text.Json;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class RecordingWorkerProtocolTests
{
    public static TheoryData<RecordingWorkerMessage> AllMessages => new()
    {
        new RecordingWorkerStartCommand(new RecordingWorkerStartData(
            "C:\\Videos\\sample.mp4",
            RecordingWorkerSourceKind.Region,
            "DISPLAY1",
            new RecordingWorkerRectangle(10, 20, 640, 480),
            123456789,
            new RecordingWorkerSize(640, 480),
            new RecordingWorkerSize(320, 240),
            30,
            8,
            true,
            false,
            true,
            false,
            true,
            true,
            "microphone-1",
            160)),
        new RecordingWorkerPauseCommand(1),
        new RecordingWorkerResumeCommand(2),
        new RecordingWorkerStopCommand(),
        new RecordingWorkerReadyResponseCommand(DiagnosticLogLevel.Debug),
        new RecordingWorkerReadyMessage(4321, RecordingWorkerProtocol.CurrentVersion),
        new RecordingWorkerStateMessage(RecordingWorkerRecordingState.Paused),
        new RecordingWorkerCompletedMessage("C:\\Videos\\sample.mp4"),
        new RecordingWorkerFailedMessage("C:\\Videos\\sample.mp4", "保存に失敗しました", true),
        new RecordingWorkerWarningMessage("音声デバイスが切断されました"),
        new RecordingWorkerOperationFailedMessage(3, RecordingWorkerOperationKind.Pause, "一時停止に失敗しました"),
        new RecordingWorkerTerminationMessage(RecordingTerminationOutcome.Idle),
        new RecordingWorkerLogMessage(DiagnosticLogLevel.Warn, "record-worker", "encoder warning"),
    };

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void すべてのメッセージをJSONで往復できる(RecordingWorkerMessage message)
    {
        var json = RecordingWorkerProtocol.Serialize(message);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(message.Type, document.RootElement.GetProperty("type").GetString());
        var result = RecordingWorkerProtocol.Deserialize(json);
        Assert.True(result.IsReadable);
        Assert.Equal(RecordingWorkerMessageReadError.None, result.Error);
        Assert.Equal(message, result.Message);
    }

    [Fact]
    public void 未知のJSONフィールドを無視してメッセージを読める()
    {
        var result = RecordingWorkerProtocol.Deserialize("{\"type\":\"ready\",\"processId\":7,\"protocolVersion\":1,\"futureField\":true}");

        Assert.Equal(new RecordingWorkerReadyMessage(7, 1), result.Message);
    }

    [Fact]
    public void 未知のメッセージ種別を読めなかった結果として返す()
    {
        var result = RecordingWorkerProtocol.Deserialize("{\"type\":\"future\"}");

        Assert.False(result.IsReadable);
        Assert.Equal(RecordingWorkerMessageReadError.UnknownType, result.Error);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("not-json")]
    public void 壊れたJSONを例外にせず読めなかった結果として返す(string json)
    {
        var result = RecordingWorkerProtocol.Deserialize(json);

        Assert.False(result.IsReadable);
        Assert.Equal(RecordingWorkerMessageReadError.InvalidJson, result.Error);
    }

    [Fact]
    public void 種類フィールドのないJSONを読めなかった結果として返す()
    {
        var result = RecordingWorkerProtocol.Deserialize("{\"processId\":7}");

        Assert.False(result.IsReadable);
        Assert.Equal(RecordingWorkerMessageReadError.MissingType, result.Error);
    }

    [Fact]
    public void 必須フィールドのないメッセージを読めなかった結果として返す()
    {
        var result = RecordingWorkerProtocol.Deserialize("{\"type\":\"failed\",\"filePath\":\"sample.mp4\"}");

        Assert.False(result.IsReadable);
        Assert.Equal(RecordingWorkerMessageReadError.InvalidMessage, result.Error);
    }

    [Fact]
    public void 断片をまたぐ行を切り出せる()
    {
        var reader = new RecordingWorkerLineReader();

        Assert.Empty(reader.Append("{\"type\":\"warn"));
        var lines = reader.Append("ing\",\"message\":\"注意\"}\n");

        Assert.Collection(lines, line => Assert.Equal("{\"type\":\"warning\",\"message\":\"注意\"}", line.Line));
    }

    [Fact]
    public void CRLFの行末からCRを取り除く()
    {
        var reader = new RecordingWorkerLineReader();

        var lines = reader.Append("first\r\n");

        Assert.Equal("first", Assert.Single(lines).Line);
    }

    [Fact]
    public void 一度の断片から複数行を切り出す()
    {
        var reader = new RecordingWorkerLineReader();

        var lines = reader.Append("one\ntwo\r\nthree\n");

        Assert.Equal(new[] { "one", "two", "three" }, lines.Select(line => line.Line));
    }

    [Fact]
    public void 上限を超えた行をエラーとして返し次の行を読める()
    {
        var reader = new RecordingWorkerLineReader(maximumLineLengthBytes: 4);

        var lines = reader.Append("123456\nnext\n");

        Assert.True(lines[0].IsTooLong);
        Assert.Null(lines[0].Line);
        Assert.Equal("next", lines[1].Line);
    }

    [Fact]
    public void UTF8バイト数で行の上限を判定する()
    {
        var reader = new RecordingWorkerLineReader(maximumLineLengthBytes: 5);

        var lines = reader.Append("あい\n");

        Assert.True(Assert.Single(lines).IsTooLong);
    }
}
