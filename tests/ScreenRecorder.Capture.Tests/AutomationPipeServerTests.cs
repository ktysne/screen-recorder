using System.IO.Pipes;
using System.Text;
using ScreenRecorder.Capture;
using Xunit;

namespace ScreenRecorder.Capture.Tests;

public sealed class AutomationPipeServerTests
{
    [Fact]
    public async Task FirstPipeInstanceCannotBeCreatedTwice()
    {
        var pipeName = NewPipeName();
        Assert.True(AutomationPipeServer.TryStart(pipeName, EchoAsync, out var first));

        try
        {
            Assert.False(AutomationPipeServer.TryStart(pipeName, EchoAsync, out var second));
            Assert.Null(second);
        }
        finally
        {
            await first!.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopWithoutNotificationReleasesThePipeNameBeforeCompleting()
    {
        var pipeName = NewPipeName();
        Assert.True(AutomationPipeServer.TryStart(pipeName, EchoAsync, out var first));
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);

        var stopping = first!.StopAsync();
        Assert.True(AutomationPipeServer.TryStart(pipeName, EchoAsync, out var second));

        await stopping;
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task FourConnectedClientsAreHandledAtTheSameTime()
    {
        var pipeName = NewPipeName();
        var allRequestsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCount = 0;
        Assert.True(AutomationPipeServer.TryStart(pipeName, async (line, cancellationToken) =>
        {
            if (Interlocked.Increment(ref receivedCount) == AutomationPipeServer.MaximumConnections)
                allRequestsReceived.TrySetResult();
            await allRequestsReceived.Task.WaitAsync(cancellationToken);
            return new AutomationPipeResponse(line);
        }, out var server));

        try
        {
            var clients = Enumerable.Range(0, AutomationPipeServer.MaximumConnections)
                .Select(index => SendAsync(pipeName, $"request-{index}"))
                .ToArray();
            var responses = await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(AutomationPipeServer.MaximumConnections, receivedCount);
            Assert.Equal(Enumerable.Range(0, AutomationPipeServer.MaximumConnections).Select(index => $"request-{index}"), responses);
        }
        finally
        {
            await server!.DisposeAsync();
        }
    }

    [Fact]
    public async Task IncompleteRequestTimesOutAndClosesConnection()
    {
        var pipeName = NewPipeName();
        Assert.True(AutomationPipeServer.TryStart(
            pipeName,
            EchoAsync,
            out var server,
            requestTimeout: TimeSpan.FromMilliseconds(150)));

        try
        {
            using var client = await ConnectAsync(pipeName);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await client.WriteAsync(Encoding.UTF8.GetBytes("partial"));
            await client.FlushAsync();

            var result = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Null(result);
        }
        finally
        {
            await server!.DisposeAsync();
        }
    }

    [Fact]
    public async Task LineOverLimitClosesTheConnection()
    {
        var pipeName = NewPipeName();
        Assert.True(AutomationPipeServer.TryStart(pipeName, EchoAsync, out var server, maximumLineLengthBytes: 8));

        try
        {
            using var client = await ConnectAsync(pipeName);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            // サーバーが閉じた後のパイプへ Flush すると例外になるため、Dispose で Flush する StreamWriter を使わない。
            await client.WriteAsync(Encoding.UTF8.GetBytes("123456789\n"));

            Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            await server!.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisconnectedClientCanConnectAgain()
    {
        var pipeName = NewPipeName();
        Assert.True(AutomationPipeServer.TryStart(pipeName, EchoAsync, out var server));

        try
        {
            Assert.Equal("first", await SendAsync(pipeName, "first"));
            Assert.Equal("second", await SendAsync(pipeName, "second"));
        }
        finally
        {
            await server!.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClientCanReadServerProcessIdAndExecutablePathFromWindows()
    {
        var pipeName = NewPipeName();
        Assert.True(AutomationPipeServer.TryStart(pipeName, EchoAsync, out var server));

        try
        {
            using var client = await ConnectAsync(pipeName);

            Assert.Equal(Environment.ProcessId, NamedPipeProcessIdentity.GetServerProcessId(client));
            Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(NamedPipeProcessIdentity.GetExecutablePath(Environment.ProcessId)!));
        }
        finally
        {
            await server!.DisposeAsync();
        }
    }

    [Fact]
    public async Task AfterWriteCallbackRunsOnceTheResponseIsWritten()
    {
        var pipeName = NewPipeName();
        var afterWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(AutomationPipeServer.TryStart(pipeName, (line, cancellationToken) => Task.FromResult<AutomationPipeResponse?>(new AutomationPipeResponse(
            line,
            () => afterWrite.TrySetResult())), out var server));

        try
        {
            using var client = await ConnectAsync(pipeName);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            // パイプの送信バッファーが 0 なので、読み手が待っていないと応答の書き込みが終わらない。
            var response = reader.ReadLineAsync();
            await writer.WriteLineAsync("accepted");

            Assert.Equal("accepted", await response.WaitAsync(TimeSpan.FromSeconds(5)));
            await afterWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await server!.DisposeAsync();
        }
    }

    private static Task<AutomationPipeResponse?> EchoAsync(string line, CancellationToken cancellationToken) =>
        Task.FromResult<AutomationPipeResponse?>(new AutomationPipeResponse(line));

    private static string NewPipeName() => $"ScreenRecorder.Automation.Tests.{Guid.NewGuid():N}";

    private static async Task<string?> SendAsync(string pipeName, string line)
    {
        using var client = await ConnectAsync(pipeName);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(line);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(3000);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
