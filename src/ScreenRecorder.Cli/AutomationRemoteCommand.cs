using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.Cli;

internal sealed record CliAutomationServerVerification(int? ProcessId, string? ExecutablePath, string? ErrorCode = null);

internal static class AutomationRemoteCommand
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaximumTimeoutSeconds = 3600;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> RecordingStates = new(StringComparer.Ordinal)
    {
        "idle", "countdown", "preparing", "recording", "paused", "saving"
    };

    private sealed class PipeSession : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly RecordingWorkerLineReader _lineReader = new();
        private readonly char[] _buffer = new char[4096];
        private readonly Queue<RecordingWorkerLineReadResult> _pending = new();

        public PipeSession(NamedPipeClientStream pipe)
        {
            Pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        }

        public NamedPipeClientStream Pipe { get; }

        public async Task SendAsync(JsonElement id, string method, JsonElement? parameters, CancellationToken cancellationToken)
        {
            await _writer.WriteLineAsync(AutomationProtocol.WriteRequest(id, method, parameters).AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }

        public async Task<AutomationJsonRpcResponse> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            while (_pending.Count == 0)
            {
                var count = await _reader.ReadAsync(_buffer.AsMemory(), deadline.Token);
                if (count == 0) throw new IOException("接続先が応答を返す前に切断しました。");
                foreach (var line in _lineReader.Append(new string(_buffer, 0, count)))
                    _pending.Enqueue(line);
            }

            var message = _pending.Dequeue();
            if (message.IsTooLong || message.Line is null)
                throw new IOException("接続先の応答が行の上限を超えています。");
            return AutomationProtocol.ReadResponse(message.Line);
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            Pipe.Dispose();
        }
    }

    public static CliApplication.CliExecutionResult ExecuteStatus(ParsedCliCommand command, CliEnvironment environment) =>
        Execute(command, environment, null, TimeSpan.Zero);

    public static CliApplication.CliExecutionResult ExecuteWait(ParsedCliCommand command, CliEnvironment environment)
    {
        if (!command.Options.TryGetValue("--state", out var state) || state is null)
            return Invalid("--state を指定してください。");
        if (!RecordingStates.Contains(state))
            return Invalid("--state は idle、countdown、preparing、recording、paused、saving のいずれかを指定してください。");

        var timeoutSeconds = DefaultTimeoutSeconds;
        if (command.Options.TryGetValue("--timeout", out var timeoutValue)
            && (!int.TryParse(timeoutValue, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds)
                || timeoutSeconds < 1
                || timeoutSeconds > MaximumTimeoutSeconds))
            return Invalid($"--timeout は 1 から {MaximumTimeoutSeconds} までの整数で指定してください。");

        var parameters = new AutomationWaitForParams(state, null, checked(timeoutSeconds * 1000));
        var json = JsonSerializer.SerializeToElement(parameters, AutomationJsonContext.Default.AutomationWaitForParams);
        return Execute(command, environment, json, TimeSpan.FromSeconds(timeoutSeconds));
    }

    public static CliAutomationServerVerification VerifyServer(NamedPipeClientStream pipe, string? expectedAppPath)
    {
        int processId;
        try { processId = NamedPipeProcessIdentity.GetServerProcessId(pipe); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or ObjectDisposedException or IOException)
        {
            return new CliAutomationServerVerification(null, null, "serverUnverified");
        }

        var executablePath = NamedPipeProcessIdentity.GetExecutablePath(processId);
        if (string.IsNullOrWhiteSpace(executablePath))
            return new CliAutomationServerVerification(processId, null, "serverUnverified");

        try
        {
            var matches = expectedAppPath is null
                ? string.Equals(Path.GetFileName(executablePath), "ScreenRecorder.exe", StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetFullPath(executablePath), Path.GetFullPath(expectedAppPath), StringComparison.OrdinalIgnoreCase);
            return new CliAutomationServerVerification(processId, executablePath, matches ? null : "impersonatedServer");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new CliAutomationServerVerification(processId, executablePath, "impersonatedServer");
        }
    }

    private static CliApplication.CliExecutionResult Execute(
        ParsedCliCommand command,
        CliEnvironment environment,
        JsonElement? waitParameters,
        TimeSpan waitTimeout)
    {
        command.Options.TryGetValue("--app", out var expectedAppPath);
        return ExecuteAsync(environment, expectedAppPath, waitParameters, waitTimeout).GetAwaiter().GetResult();
    }

    private static async Task<CliApplication.CliExecutionResult> ExecuteAsync(
        CliEnvironment environment,
        string? expectedAppPath,
        JsonElement? waitParameters,
        TimeSpan waitTimeout)
    {
        using var pipe = new NamedPipeClientStream(".", environment.AutomationPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(3000);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return Failure(CliExitCode.IoFailure, "notRunning", "ScreenRecorder の自動化用接続を見つけられませんでした。");
        }

        CliAutomationServerVerification verification;
        try { verification = environment.VerifyAutomationServer(pipe, expectedAppPath); }
        catch (Exception)
        {
            verification = new CliAutomationServerVerification(null, null, "serverUnverified");
        }
        if (verification.ErrorCode is { } verificationError)
            return Failure(CliExitCode.IoFailure, verificationError, verificationError switch
            {
                "impersonatedServer" => "接続先の実行ファイルが ScreenRecorder と一致しません。",
                _ => "接続先の実行ファイルを確認できませんでした。"
            });

        using var session = new PipeSession(pipe);
        using var idDocument = JsonDocument.Parse("1");
        try
        {
            await session.SendAsync(idDocument.RootElement, "hello", null, CancellationToken.None);
            var hello = await session.ReceiveAsync(ResponseTimeout, CancellationToken.None);
            if (hello.Error is not null) return RemoteError(hello.Error);
            if (hello.Result is not { } helloResult
                || !helloResult.TryGetProperty("protocolVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != AutomationProtocol.CurrentVersion)
                return Failure(CliExitCode.IoFailure, "protocolMismatch", "接続先と通信プロトコルの版が一致しません。");

            using var requestIdDocument = JsonDocument.Parse("2");
            var method = waitParameters is null ? "status" : "waitFor";
            await session.SendAsync(requestIdDocument.RootElement, method, waitParameters, CancellationToken.None);
            // 本体は期限ちょうどに timeout を返すので、その応答が届く分だけ長く待つ。
            var response = await session.ReceiveAsync(waitTimeout + ResponseTimeout, CancellationToken.None);
            if (response.Error is not null) return RemoteError(response.Error);
            if (response.Result is not { } result)
                return Failure(CliExitCode.IoFailure, "invalidResponse", "接続先の応答に状態がありません。");

            return new CliApplication.CliExecutionResult(CliExitCode.Success, result, [], null);
        }
        catch (OperationCanceledException)
        {
            return Failure(CliExitCode.IoFailure, "timeout", "接続先からの応答を待つ時間を超えました。");
        }
        catch (Exception exception) when (exception is IOException or JsonException or DecoderFallbackException or ObjectDisposedException)
        {
            return Failure(CliExitCode.IoFailure, "disconnected", "接続先が応答を返す前に切断しました。");
        }
    }

    private static CliApplication.CliExecutionResult RemoteError(AutomationJsonRpcError error)
    {
        var code = string.IsNullOrWhiteSpace(error.Data?.Code) ? "remoteError" : error.Data.Code;
        return Failure(CliExitCode.IoFailure, code, error.Message);
    }

    private static CliApplication.CliExecutionResult Invalid(string message) =>
        Failure(CliExitCode.InvalidArguments, "invalidArguments", message);

    private static CliApplication.CliExecutionResult Failure(CliExitCode exitCode, string code, string message) =>
        new(exitCode, null, [], new CliError(code, message));
}
