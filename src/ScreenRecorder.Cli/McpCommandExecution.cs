using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenRecorder.Cli;

internal sealed record McpCallResult(int ExitCode, string Json);

// remoteAppPath が null なら、remote は CLI と同じく実行ファイル名だけで接続先を照合する。
internal sealed class McpCommandService(
    string appPath,
    McpPathAccessPolicy pathPolicy,
    Func<CliEnvironment> environmentFactory,
    string? remoteAppPath = null)
{
    internal const double MaximumRecordDurationSeconds = 30;
    internal const int MaximumRemoteWaitSeconds = 45;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal IReadOnlyList<string> AllowedDirectories => pathPolicy.AllowedDirectories;

    public McpCallResult Invoke(string toolName, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        var environment = environmentFactory();
        var tool = McpToolCatalog.Find(toolName);
        if (tool is null) return Failure(environment.CliVersion, toolName.Replace('_', ' '), "invalidArguments", "道具が見つかりません。", 2);

        var converted = McpToolInput.BuildArguments(tool, input);
        if (!converted.Success) return Failure(environment.CliVersion, tool.Command.Name, "invalidArguments", converted.Error!, 2);
        var arguments = converted.Arguments!.ToList();
        var pinnedAppPath = tool.Command.Name == "record" ? appPath
            : tool.Command.Name.StartsWith("remote ", StringComparison.Ordinal) ? remoteAppPath
            : null;
        if (pinnedAppPath is not null)
        {
            arguments.Add("--app");
            arguments.Add(pinnedAppPath);
        }

        var parsed = CliCommands.Parse(arguments);
        if (!parsed.Success) return Failure(environment.CliVersion, tool.Command.Name, "invalidArguments", parsed.Error!, 2);
        var command = parsed.Command!;
        if (_pathPolicyError(command) is { } pathError)
            return Failure(environment.CliVersion, command.Definition.Name, "pathNotAllowed", pathError, 2);
        if (GetLimitError(command) is { } limitError)
            return Failure(environment.CliVersion, command.Definition.Name, "invalidArguments", limitError, 2);

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = CliApplication.Run(arguments, output, TextWriter.Null, environment, cancellationToken);
        return new McpCallResult(exitCode, output.ToString());
    }

    public McpCallResult Busy(string toolName) => Failure(environmentFactory().CliVersion,
        McpToolCatalog.Find(toolName)?.Command.Name ?? toolName.Replace('_', ' '),
        "busy",
        "別の道具を実行中です。数秒後にもう一度呼び出してください。",
        3);

    private string? _pathPolicyError(ParsedCliCommand command) => pathPolicy.Validate(command);

    private static string? GetLimitError(ParsedCliCommand command)
    {
        if (command.Definition.Name == "record"
            && command.Options.TryGetValue("--duration", out var durationValue)
            && double.TryParse(durationValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            && duration > MaximumRecordDurationSeconds)
            return $"MCP から指定できる duration は {MaximumRecordDurationSeconds:0} 秒までです。長い録画は remote_perform で開始し、必要な時間の後に remote_perform の stopRecording で止めてください。";

        if (command.Definition.Name == "remote wait"
            && command.Options.TryGetValue("--timeout", out var timeoutValue)
            && int.TryParse(timeoutValue, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout)
            && timeout > MaximumRemoteWaitSeconds)
            return $"MCP から指定できる timeout は {MaximumRemoteWaitSeconds} 秒までです。長く待つときは remote_wait を繰り返してください。録画は remote_perform の stopRecording で止めるまで終わりません。";

        return null;
    }

    private static McpCallResult Failure(string cliVersion, string commandName, string code, string message, int exitCode)
    {
        var envelope = new McpFailureEnvelope(cliVersion, commandName, null, [], new CliError(code, message));
        return new McpCallResult(exitCode, JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private sealed record McpFailureEnvelope(
        [property: JsonPropertyOrder(0)] string CliVersion,
        [property: JsonPropertyOrder(1)] string Command,
        [property: JsonPropertyOrder(2)] object? Result,
        [property: JsonPropertyOrder(3)] IReadOnlyList<string> Warnings,
        [property: JsonPropertyOrder(4)] CliError? Error);
}

internal sealed class McpSerialExecutor : IDisposable
{
    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(3);
    private readonly BlockingCollection<Action> _queue = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Thread _thread;

    public McpSerialExecutor()
    {
        _thread = new Thread(WorkLoop)
        {
            IsBackground = true,
            Name = "ScreenRecorder MCP command"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async Task<McpCallResult?> ExecuteAsync(
        Func<McpCallResult> work,
        Func<McpCallResult> busy,
        CancellationToken cancellationToken)
    {
        if (!await _slot.WaitAsync(BusyWait, cancellationToken).ConfigureAwait(false)) return busy();
        try
        {
            var completion = new TaskCompletionSource<McpCallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { completion.TrySetResult(work()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            });
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _slot.Release();
        }
    }

    private void WorkLoop()
    {
        foreach (var work in _queue.GetConsumingEnumerable()) work();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join();
        _queue.Dispose();
        _slot.Dispose();
    }
}
