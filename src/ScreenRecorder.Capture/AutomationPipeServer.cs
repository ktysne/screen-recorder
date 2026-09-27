using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

public sealed class AutomationPipeServer : IAsyncDisposable
{
    public const int MaximumConnections = 4;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    private sealed class Connection(NamedPipeServerStream pipe)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public StreamWriter? Writer { get; set; }
    }

    private readonly string _pipeName;
    private readonly Func<string, CancellationToken, Task<AutomationPipeResponse?>> _handleLine;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maximumLineLengthBytes;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Connection> _connections = [];
    private Task[] _workers = [];
    private int _stopped;

    private AutomationPipeServer(
        string pipeName,
        Func<string, CancellationToken, Task<AutomationPipeResponse?>> handleLine,
        TimeSpan requestTimeout,
        int maximumLineLengthBytes)
    {
        _pipeName = pipeName;
        _handleLine = handleLine;
        _requestTimeout = requestTimeout;
        _maximumLineLengthBytes = maximumLineLengthBytes;
    }

    public static bool TryStart(
        string pipeName,
        Func<string, CancellationToken, Task<string?>> handleLine,
        out AutomationPipeServer? server,
        TimeSpan? requestTimeout = null,
        int maximumLineLengthBytes = RecordingWorkerLineReader.MaximumLineLengthBytes)
    {
        ArgumentNullException.ThrowIfNull(handleLine);
        Func<string, CancellationToken, Task<AutomationPipeResponse?>> adapter = async (line, cancellationToken) =>
        {
            var response = await handleLine(line, cancellationToken).ConfigureAwait(false);
            return response is null ? null : new AutomationPipeResponse(response);
        };
        return TryStart(pipeName, adapter, out server, requestTimeout, maximumLineLengthBytes);
    }

    public static bool TryStart(
        string pipeName,
        Func<string, CancellationToken, Task<AutomationPipeResponse?>> handleLine,
        out AutomationPipeServer? server,
        TimeSpan? requestTimeout = null,
        int maximumLineLengthBytes = RecordingWorkerLineReader.MaximumLineLengthBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(handleLine);
        var timeout = requestTimeout ?? DefaultRequestTimeout;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));

        var candidate = new AutomationPipeServer(pipeName, handleLine, timeout, maximumLineLengthBytes);
        if (!candidate.TryCreateConnections())
        {
            candidate._shutdown.Dispose();
            server = null;
            return false;
        }

        candidate._workers = candidate._connections.Select(candidate.ServeConnectionAsync).ToArray();
        server = candidate;
        return true;
    }

    public async Task StopAsync(string? notificationLine = null)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        if (notificationLine is not null)
            await Task.WhenAll(_connections.Select(connection => WriteNotificationAsync(connection, notificationLine))).ConfigureAwait(false);

        _shutdown.Cancel();
        foreach (var connection in _connections)
            connection.Pipe.Dispose();

        if (_workers.Length > 0)
        {
            var workers = Task.WhenAll(_workers);
            await Task.WhenAny(workers, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }

        foreach (var connection in _connections)
            connection.WriteGate.Dispose();
        _shutdown.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private bool TryCreateConnections()
    {
        try
        {
            _connections.Add(new Connection(CreatePipe(firstInstance: true)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Automation, $"自動化用パイプを作成できませんでした: {exception.Message}");
            return false;
        }

        for (var index = 1; index < MaximumConnections; index++)
        {
            try
            {
                _connections.Add(new Connection(CreatePipe(firstInstance: false)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(DiagnosticLogTags.Automation, $"自動化用パイプの接続枠を作成できませんでした: {exception.Message}");
                break;
            }
        }

        return true;
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (firstInstance) options |= PipeOptions.FirstPipeInstance;
        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            MaximumConnections,
            PipeTransmissionMode.Byte,
            options);
    }

    private async Task ServeConnectionAsync(Connection connection)
    {
        var pipe = connection.Pipe;
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                if (!_shutdown.IsCancellationRequested)
                    DiagnosticLog.Warn(DiagnosticLogTags.Automation, $"自動化用パイプの接続を受け付けられませんでした: {exception.Message}");
                break;
            }

            var processId = ReadClientProcessId(pipe);
            if (processId is { } connectedProcessId)
                DiagnosticLog.Info(DiagnosticLogTags.Automation, $"自動化用パイプに接続しました: 接続元 PID={connectedProcessId}");
            else
                DiagnosticLog.Info(DiagnosticLogTags.Automation, "自動化用パイプに接続しました: 接続元 PID を取得できませんでした。");

            try
            {
                await HandleConnectionAsync(connection).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or DecoderFallbackException)
            {
                if (!_shutdown.IsCancellationRequested)
                    DiagnosticLog.Warn(DiagnosticLogTags.Automation, $"自動化用パイプの接続を閉じました: {exception.Message}");
            }
            finally
            {
                connection.Writer = null;
                try
                {
                    if (pipe.IsConnected) pipe.Disconnect();
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                if (processId is { } disconnectedProcessId)
                    DiagnosticLog.Info(DiagnosticLogTags.Automation, $"自動化用パイプを切断しました: 接続元 PID={disconnectedProcessId}");
            }
        }
    }

    private async Task HandleConnectionAsync(Connection connection)
    {
        var pipe = connection.Pipe;
        var reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        connection.Writer = writer;
        var lineReader = new RecordingWorkerLineReader(_maximumLineLengthBytes);
        var incomingLines = Channel.CreateBounded<RecordingWorkerLineReadResult>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var clientLifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var inputPump = ReadIncomingLinesAsync(reader, lineReader, incomingLines.Writer, clientLifetime.Token);

        try
        {
            while (!_shutdown.IsCancellationRequested && pipe.IsConnected)
            {
                RecordingWorkerLineReadResult line;
                using (var lineDeadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
                {
                    lineDeadline.CancelAfter(_requestTimeout);
                    var nextLine = incomingLines.Reader.ReadAsync(lineDeadline.Token).AsTask();
                    var completed = await Task.WhenAny(nextLine, inputPump).ConfigureAwait(false);
                    if (completed == inputPump && !nextLine.IsCompleted) return;
                    line = await nextLine.ConfigureAwait(false);
                }

                if (line.IsTooLong || line.Line is null)
                {
                    DiagnosticLog.Warn(DiagnosticLogTags.Automation, "要求の行が上限を超えたため接続を閉じました。");
                    return;
                }

                using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var responseTask = _handleLine(line.Line, requestLifetime.Token);
                var requestCompleted = await Task.WhenAny(responseTask, inputPump).ConfigureAwait(false);
                if (requestCompleted == inputPump && !responseTask.IsCompleted)
                {
                    requestLifetime.Cancel();
                    try { await responseTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    return;
                }

                var response = await responseTask.ConfigureAwait(false);
                if (response?.Line is { } responseLine)
                {
                    await WriteLineAsync(connection, responseLine, _shutdown.Token).ConfigureAwait(false);
                    response.AfterWrite?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Automation, $"要求の行が {_requestTimeout.TotalSeconds:0.###} 秒以内に完了しなかったため接続を閉じました。");
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            clientLifetime.Cancel();
            try { await inputPump.ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or DecoderFallbackException or OperationCanceledException or ChannelClosedException) { }
            reader.Dispose();
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ReadIncomingLinesAsync(
        StreamReader reader,
        RecordingWorkerLineReader lineReader,
        ChannelWriter<RecordingWorkerLineReadResult> lines,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                foreach (var line in lineReader.Append(new string(buffer, 0, count)))
                    await lines.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or DecoderFallbackException or OperationCanceledException)
        {
        }
        finally
        {
            lines.TryComplete();
        }
    }

    private static async Task WriteNotificationAsync(Connection connection, string line)
    {
        if (!connection.Pipe.IsConnected || connection.Writer is not { } writer) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { await WriteLineAsync(connection, line, deadline.Token, writer).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static async Task WriteLineAsync(Connection connection, string line, CancellationToken cancellationToken) =>
        await WriteLineAsync(connection, line, cancellationToken, connection.Writer).ConfigureAwait(false);

    private static async Task WriteLineAsync(Connection connection, string line, CancellationToken cancellationToken, StreamWriter? writer)
    {
        if (writer is null) return;
        await connection.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connection.WriteGate.Release();
        }
    }

    private static int? ReadClientProcessId(NamedPipeServerStream pipe)
    {
        try { return NamedPipeProcessIdentity.GetClientProcessId(pipe); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            DiagnosticLog.Warn(DiagnosticLogTags.Automation, $"自動化用パイプの接続元 PID を取得できませんでした: {exception.Message}");
            return null;
        }
    }
}

public sealed record AutomationPipeResponse(string Line, Action? AfterWrite = null);
