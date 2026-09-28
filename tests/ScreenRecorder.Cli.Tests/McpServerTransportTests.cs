using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol.Protocol;
using ScreenRecorder.Cli;
using Xunit;

namespace ScreenRecorder.Cli.Tests;

public sealed class McpServerTransportTests
{
    [McpPipeFact]
    public async Task SdkHandlesInitializationToolsListAndInfoCallOverTwoPipes()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var clientToServerName = $"ScreenRecorder.Mcp.Request.{suffix}";
        var serverToClientName = $"ScreenRecorder.Mcp.Response.{suffix}";
        using var serverInput = new NamedPipeServerStream(clientToServerName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var serverOutput = new NamedPipeServerStream(serverToClientName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var clientOutput = new NamedPipeClientStream(".", clientToServerName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var clientInput = new NamedPipeClientStream(".", serverToClientName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connectionTasks = new[]
        {
            serverInput.WaitForConnectionAsync(),
            serverOutput.WaitForConnectionAsync(),
            clientOutput.ConnectAsync(5000),
            clientInput.ConnectAsync(5000)
        };
        await Task.WhenAll(connectionTasks).WaitAsync(TimeSpan.FromSeconds(8));

        await using var transport = new PipeMcpTransport(serverInput, serverOutput);
        var root = Path.Combine(Path.GetTempPath(), $"screenrecorder-mcp-test-{suffix}");
        var settingsDirectory = Path.Combine(root, "settings");
        var logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(settingsDirectory);
        Directory.CreateDirectory(logDirectory);
        var environment = new CliEnvironment(settingsDirectory, logDirectory, () => [], () => DateTimeOffset.Now, "1.0.0");
        var service = new McpCommandService(
            Path.Combine(root, "ScreenRecorder.exe"),
            McpPathAccessPolicy.Create([Path.GetTempPath()]),
            () => environment);
        using var executor = new McpSerialExecutor();
        await using var server = McpServerHost.CreateServer(transport, service, executor);
        using var shutdown = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.RunAsync(shutdown.Token));
        var clientWriter = new StreamWriter(clientOutput, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        var clientReader = new StreamReader(clientInput, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);

        try
        {
            await clientWriter.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1.0.0"}}}""");
            using var initialized = await ReadResponseAsync(clientReader, 1, CancellationToken.None);
            Assert.True(initialized.RootElement.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _));
            await clientWriter.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            await clientWriter.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
            using var listed = await ReadResponseAsync(clientReader, 2, CancellationToken.None);
            var tools = listed.RootElement.GetProperty("result").GetProperty("tools");
            Assert.Contains(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "info");
            Assert.DoesNotContain(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() is "mcp" or "help");

            await clientWriter.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"info","arguments":{}}}""");
            using var called = await ReadResponseAsync(clientReader, 3, CancellationToken.None);
            Assert.False(called.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
            var cliJson = called.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text");
            using var info = JsonDocument.Parse(cliJson.GetString()!);
            Assert.Equal("info", info.RootElement.GetProperty("command").GetString());
            Assert.Equal(JsonValueKind.Object, info.RootElement.GetProperty("result").ValueKind);
        }
        finally
        {
            clientWriter.Dispose();
            clientReader.Dispose();
            clientOutput.Dispose();
            clientInput.Dispose();
            shutdown.Cancel();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<JsonDocument> ReadResponseAsync(StreamReader reader, int expectedId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (await reader.ReadLineAsync(timeout.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) is { } line)
            {
                using var message = JsonDocument.Parse(line);
                if (!message.RootElement.TryGetProperty("id", out var id) || id.GetInt32() != expectedId) continue;
                return JsonDocument.Parse(line);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP サーバーが request id {expectedId} に応答しませんでした。");
        }
        throw new EndOfStreamException("MCP サーバーが応答する前にパイプを閉じました。");
    }

    private sealed class PipeMcpTransport : ITransport
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly Channel<JsonRpcMessage> _messages = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _closed = new();
        private readonly Task _readTask;
        private int _disposeStarted;

        public PipeMcpTransport(Stream input, Stream output)
        {
            _reader = new StreamReader(input, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n" };
            SessionId = Guid.NewGuid().ToString("N");
            _readTask = ReadMessagesAsync();
        }

        public string SessionId { get; }
        public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;

        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize<JsonRpcMessage>(message).AsMemory(), cancellationToken).ConfigureAwait(false);
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadMessagesAsync()
        {
            try
            {
                while (await _reader.ReadLineAsync(_closed.Token).ConfigureAwait(false) is { } line)
                {
                    var message = JsonSerializer.Deserialize<JsonRpcMessage>(line)
                        ?? throw new JsonException("JSON-RPC メッセージが空です。");
                    message.Context = new JsonRpcMessageContext { RelatedTransport = this };
                    await _messages.Writer.WriteAsync(message, _closed.Token).ConfigureAwait(false);
                }
                _messages.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested)
            {
                _messages.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                _messages.Writer.TryComplete(exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            _closed.Cancel();
            _reader.Dispose();
            _writer.Dispose();
            try { await _readTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _writeLock.Dispose();
            _closed.Dispose();
        }
    }
}

public sealed class McpPipeFactAttribute : FactAttribute
{
    public McpPipeFactAttribute()
    {
        var pipeName = $"ScreenRecorder.Mcp.Access.{Guid.NewGuid():N}";
        try
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var waiting = server.WaitForConnectionAsync();
            client.Connect(1000);
            if (!waiting.Wait(1000)) Skip = "この環境では名前付きパイプ接続を利用できません。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException or PlatformNotSupportedException)
        {
            Skip = "この環境では名前付きパイプ接続を利用できません。";
        }
    }
}
