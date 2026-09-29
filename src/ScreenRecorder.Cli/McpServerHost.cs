using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ScreenRecorder.Cli;

internal static class McpServerHost
{
    public static int Run(IReadOnlyList<string> arguments, TextWriter standardError)
    {
        if (!TryParseStartupOptions(arguments, out var appPath, out var additionalAllowedDirectories, out var parseError))
        {
            standardError.WriteLine(parseError);
            return 2;
        }

        try
        {
            var resolvedAppPath = ResolveAppPath(appPath, CliEnvironment.Create().FindApp, standardError);
            var allowedDirectories = new[] { Path.GetTempPath() }.Concat(additionalAllowedDirectories);
            var pathPolicy = McpPathAccessPolicy.Create(allowedDirectories);
            var service = new McpCommandService(resolvedAppPath, pathPolicy, CliEnvironment.Create, remoteAppPath: appPath);
            using var executor = new McpSerialExecutor();
            var transport = new StdioServerTransport("screenrecorder-cli", loggerFactory: null);
            using var shutdown = new CancellationTokenSource();
            var options = CreateServerOptions(service, executor, transport, shutdown.Token);
            Console.CancelKeyPress += CancelServer;
            try
            {
                RunServerAsync(transport, options, shutdown.Token).GetAwaiter().GetResult();
            }
            finally
            {
                Console.CancelKeyPress -= CancelServer;
                shutdown.Cancel();
            }
            return 0;

            async Task RunServerAsync(ITransport serverTransport, McpServerOptions serverOptions, CancellationToken cancellationToken)
            {
                await using var server = McpServer.Create(serverTransport, serverOptions, loggerFactory: null, serviceProvider: null);
                var runTask = server.RunAsync(cancellationToken);
                var inputClosed = serverTransport.MessageReader.Completion;
                var completed = await Task.WhenAny(runTask, inputClosed).ConfigureAwait(false);
                if (completed == inputClosed || inputClosed.IsCompleted)
                    shutdown.Cancel();
                try { await runTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }

            void CancelServer(object? sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            standardError.WriteLine($"MCP サーバーを起動できませんでした: {exception.Message}");
            return 3;
        }
    }

    internal static McpServer CreateServer(
        ITransport transport,
        McpCommandService service,
        McpSerialExecutor executor,
        CancellationToken serverCancellationToken = default) =>
        McpServer.Create(transport, CreateServerOptions(service, executor, transport, serverCancellationToken), loggerFactory: null, serviceProvider: null);

    private static McpServerOptions CreateServerOptions(
        McpCommandService service,
        McpSerialExecutor executor,
        ITransport? progressTransport,
        CancellationToken serverCancellationToken)
    {
        var version = typeof(CliEnvironment).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "screenrecorder-cli", Version = version },
            ServerInstructions = McpServerInstructions.Create(service.AllowedDirectories),
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => new ValueTask<ListToolsResult>(new ListToolsResult
                {
                    Tools = McpToolCatalog.Tools.Select(ToProtocolTool).ToList()
                }),
                CallToolHandler = async (context, cancellationToken) =>
                    await CallToolAsync(context, cancellationToken).ConfigureAwait(false)
            }
        };
        return serverOptions;

        async ValueTask<CallToolResult> CallToolAsync(
            RequestContext<CallToolRequestParams> context,
            CancellationToken cancellationToken)
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, serverCancellationToken);
            var requestToken = requestCancellation.Token;
            var toolName = context.Params.Name ?? string.Empty;
            IReadOnlyDictionary<string, JsonElement> input = context.Params.Arguments is { } suppliedArguments
                ? new Dictionary<string, JsonElement>(suppliedArguments, StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var execution = executor.ExecuteAsync(
                () => service.Invoke(toolName, input, requestToken),
                () => service.Busy(toolName),
                requestToken);

            if (progressTransport is not null
                && context.Params.ProgressToken is { } progressToken
                && (toolName is "record" or "remote_wait"))
            {
                await WaitWithProgressAsync(execution, async (progress, token) =>
                {
                    var parameters = new JsonObject
                    {
                        ["progressToken"] = JsonSerializer.SerializeToNode(progressToken.Token),
                        ["progress"] = JsonValue.Create(progress),
                        ["message"] = JsonValue.Create(toolName)
                    };
                    await progressTransport.SendMessageAsync(new JsonRpcNotification
                    {
                        Method = "notifications/progress",
                        Params = parameters
                    }, token).ConfigureAwait(false);
                }, TimeSpan.FromSeconds(1), requestToken).ConfigureAwait(false);
            }

            var result = await execution.ConfigureAwait(false);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = result!.Json }],
                IsError = result.ExitCode != 0
            };
        }
    }

    // 取り消し後も録画の停止と MP4 の書き終えを待ってから返すため、待ちは取り消しで抜けず、進捗の送信だけを止める。
    internal static async Task WaitWithProgressAsync(
        Task execution,
        Func<int, CancellationToken, Task> sendProgress,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var progress = 0;
        while (!execution.IsCompleted)
        {
            await Task.WhenAny(execution, Task.Delay(interval)).ConfigureAwait(false);
            if (execution.IsCompleted || cancellationToken.IsCancellationRequested) continue;
            try { await sendProgress(++progress, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    private static Tool ToProtocolTool(McpToolDescriptor descriptor) => new()
    {
        Name = descriptor.Name,
        Description = descriptor.Description,
        InputSchema = descriptor.InputSchema,
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = descriptor.ReadOnlyHint,
            DestructiveHint = descriptor.DestructiveHint
        }
    };

    // 本体の場所は起動時に決めて固定する(docs/automation-mcp.md「安全性」)。
    internal static string ResolveAppPath(
        string? requestedAppPath,
        Func<string?, (string? Path, IReadOnlyList<string> Searched)> findApp,
        TextWriter standardError)
    {
        var (path, searched) = findApp(requestedAppPath);
        if (path is not null) return path;
        standardError.WriteLine($"ScreenRecorder.exe が見つかりません。record は appNotFound になります。探した場所: {string.Join(", ", searched)}");
        return searched[0];
    }

    internal static bool TryParseStartupOptions(
        IReadOnlyList<string> arguments,
        out string? appPath,
        out IReadOnlyList<string> allowDirectories,
        out string error)
    {
        string? requestedAppPath = null;
        var appSpecified = false;
        var allowed = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var option = arguments[index];
            if (option is not ("--app" or "--allow-dir"))
            {
                appPath = null;
                allowDirectories = [];
                error = $"mcp では {option} を使えません。";
                return false;
            }
            if (index + 1 >= arguments.Count
                || string.IsNullOrWhiteSpace(arguments[index + 1])
                || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                appPath = null;
                allowDirectories = [];
                error = $"{option} の値を指定してください。";
                return false;
            }

            var value = arguments[++index];
            if (option == "--allow-dir")
            {
                allowed.Add(value);
                continue;
            }
            if (appSpecified)
            {
                appPath = null;
                allowDirectories = [];
                error = "--app は 1 回だけ指定できます。";
                return false;
            }
            appSpecified = true;
            requestedAppPath = value;
        }

        try
        {
            appPath = requestedAppPath is null ? null : Path.GetFullPath(requestedAppPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            appPath = null;
            allowDirectories = [];
            error = "--app のパスが正しくありません。";
            return false;
        }
        allowDirectories = allowed;
        error = string.Empty;
        return true;
    }
}
