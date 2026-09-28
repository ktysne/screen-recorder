using System.Drawing;
using System.Globalization;
using System.Security;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenRecorder.Core;

namespace ScreenRecorder.Cli;

internal enum CliExitCode
{
    Success = 0,
    CheckFailed = 1,
    InvalidArguments = 2,
    IoFailure = 3
}

internal sealed record CliError(string Code, string Message);

internal sealed class CliEnvelope
{
    [JsonPropertyOrder(0)]
    public required string CliVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required string Command { get; init; }

    [JsonPropertyOrder(2)]
    public object? Result { get; init; }

    [JsonPropertyOrder(3)]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyOrder(4)]
    public CliError? Error { get; init; }
}

internal static class CliApplication
{
    private const int DefaultLogEntryLimit = 200;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static int Run(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CliEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(environment);

        var parsed = CliCommands.Parse(arguments);
        if (!parsed.Success)
        {
            var commandName = arguments.FirstOrDefault(argument => !CliCommands.GlobalOptions.Any(option => option.Name == argument)) ?? "unknown";
            var textMode = arguments.Any(argument => CliCommands.GlobalOptions.Any(option => option.Name == argument));
            return WriteFailure(standardOutput, environment, commandName, CliExitCode.InvalidArguments, "invalidArguments", parsed.Error!, textMode);
        }

        var command = parsed.Command!;
        try
        {
            var response = Execute(command, environment, cancellationToken);
            if (command.TextMode)
                standardOutput.WriteLine(FormatText(response.Result, response.Warnings, response.Error));
            else
                WriteJson(standardOutput, environment, command.Definition.Name, response.Result, response.Warnings, response.Error);
            return (int)response.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WriteFailure(standardOutput, environment, command.Definition.Name, CliExitCode.IoFailure, "cancelled", "コマンドを取り消しました。", command.TextMode);
        }
        catch (FileNotFoundException exception)
        {
            return WriteFailure(standardOutput, environment, command.Definition.Name, CliExitCode.IoFailure, "fileNotFound", $"ファイルが見つかりません: {exception.FileName ?? exception.Message}", command.TextMode);
        }
        catch (DirectoryNotFoundException)
        {
            return WriteFailure(standardOutput, environment, command.Definition.Name, CliExitCode.IoFailure, "directoryNotFound", "フォルダーが見つかりません。", command.TextMode);
        }
        catch (UnauthorizedAccessException)
        {
            return WriteFailure(standardOutput, environment, command.Definition.Name, CliExitCode.IoFailure, "accessDenied", "ファイルまたはフォルダーを読み取る権限がありません。", command.TextMode);
        }
        catch (IOException)
        {
            return WriteFailure(standardOutput, environment, command.Definition.Name, CliExitCode.IoFailure, "fileReadFailed", "ファイルを読み取れませんでした。", command.TextMode);
        }
        catch (SecurityException)
        {
            return WriteFailure(standardOutput, environment, command.Definition.Name, CliExitCode.IoFailure, "accessDenied", "ファイルまたはフォルダーを読み取る権限がありません。", command.TextMode);
        }
        catch (ArgumentException)
        {
            return WriteFailure(standardOutput, environment, command.Definition.Name, CliExitCode.InvalidArguments, "invalidArguments", "引数の形式または指定値が正しくありません。", command.TextMode);
        }
        catch (Exception)
        {
            return WriteFailure(standardOutput, environment, command.Definition.Name, CliExitCode.IoFailure, "commandFailed", "コマンドを実行できませんでした。", command.TextMode);
        }
    }

    private static CliExecutionResult Execute(ParsedCliCommand command, CliEnvironment environment, CancellationToken cancellationToken) => command.Definition.Name switch
    {
        "info" => Info(environment),
        "settings show" => Settings(command, environment),
        "settings validate" => Settings(command, environment),
        "logs list" => LogsList(environment),
        "logs show" => LogsShow(command, environment),
        "remote status" => AutomationRemoteCommand.ExecuteStatus(command, environment, cancellationToken),
        "remote wait" => AutomationRemoteCommand.ExecuteWait(command, environment, cancellationToken),
        "remote perform" => AutomationRemoteCommand.ExecutePerform(command, environment, cancellationToken),
        "remote select" => AutomationRemoteCommand.ExecuteSelect(command, environment, cancellationToken),
        "remote exit" => AutomationRemoteCommand.ExecuteExit(command, environment, cancellationToken),
        "naming preview" => NamingPreview(command, environment),
        "record" => RecordCommand.Execute(command, environment, cancellationToken),
        "screenshot" => ScreenshotCommand.Execute(command, environment),
        "probe" => ProbeCommand.Execute(command),
        "help" => Help(command),
        _ => throw new InvalidOperationException($"未対応のコマンドです: {command.Definition.Name}")
    };

    private static CliExecutionResult Info(CliEnvironment environment)
    {
        var settingsPath = Path.Combine(environment.SettingsDirectory, "settings.json");
        var read = SettingsFileReader.Read(settingsPath);
        var warnings = read.Issues.Select(issue => issue.Message).ToList();
        var monitorList = environment.GetMonitors();
        var monitors = monitorList.Select(monitor => new
        {
            deviceName = monitor.DeviceName,
            bounds = new { x = monitor.X, y = monitor.Y, width = monitor.Width, height = monitor.Height },
            workArea = new { x = monitor.WorkAreaX, y = monitor.WorkAreaY, width = monitor.WorkAreaWidth, height = monitor.WorkAreaHeight },
            dpi = new { x = monitor.DpiX, y = monitor.DpiY },
            isPrimary = monitor.IsPrimary
        }).ToArray();
        foreach (var monitor in monitorList.Where(monitor => monitor.DpiX is null || monitor.DpiY is null))
            warnings.Add($"モニター {monitor.DeviceName} の DPI を取得できませんでした。");

        return new CliExecutionResult(CliExitCode.Success, new
        {
            settingsDirectory = environment.SettingsDirectory,
            settingsFile = settingsPath,
            logDirectory = environment.LogDirectory,
            defaultStillImageDirectory = read.Settings.StillImageDirectory,
            defaultSaveDirectory = read.Settings.VideoDirectory,
            monitors
        }, warnings, null);
    }

    private static CliExecutionResult Settings(ParsedCliCommand command, CliEnvironment environment)
    {
        var explicitFile = command.Options.TryGetValue("--file", out var configuredFile);
        var path = explicitFile ? Path.GetFullPath(configuredFile!) : Path.Combine(environment.SettingsDirectory, "settings.json");
        if (explicitFile)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        var read = SettingsFileReader.Read(path);
        var result = new
        {
            path,
            settings = read.Settings,
            defaultedProperties = read.DefaultedProperties.Select(ToJsonPropertyName).ToArray(),
            issues = ToIssueResults(read.Issues)
        };
        var exitCode = read.Issues.Count == 0 ? CliExitCode.Success : CliExitCode.CheckFailed;
        return new CliExecutionResult(exitCode, result, [], null);
    }

    private static CliExecutionResult LogsList(CliEnvironment environment)
    {
        var files = DiagnosticLogReader.ListFiles(environment.LogDirectory);
        return new CliExecutionResult(CliExitCode.Success, new { files }, [], null);
    }

    private static CliExecutionResult LogsShow(ParsedCliCommand command, CliEnvironment environment)
    {
        DateTimeOffset? since = null;
        if (command.Options.TryGetValue("--since", out var sinceValue))
        {
            if (!DateTimeOffset.TryParse(sinceValue, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedSince))
                return InvalidOption(command, "--since は ISO 8601 形式の日時で指定してください。");
            since = parsedSince;
        }

        string? level = null;
        if (command.Options.TryGetValue("--level", out var levelValue))
        {
            level = levelValue!.ToLowerInvariant();
            if (level is not ("error" or "warn" or "info" or "debug"))
                return InvalidOption(command, "--level は error、warn、info、debug のいずれかを指定してください。");
        }

        var limit = DefaultLogEntryLimit;
        if (command.Options.TryGetValue("--limit", out var limitValue))
        {
            if (!int.TryParse(limitValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit) || parsedLimit < 1)
                return InvalidOption(command, "--limit は 1 以上の整数で指定してください。");
            limit = parsedLimit;
        }

        var path = command.Positionals.Count == 1
            ? Path.GetFullPath(command.Positionals[0])
            : DiagnosticLogReader.ListFiles(environment.LogDirectory).FirstOrDefault()?.Path
                ?? throw new FileNotFoundException("診断ログがありません。", environment.LogDirectory);
        var maximumRank = DiagnosticLogReader.GetSeverityRank(level);
        command.Options.TryGetValue("--tag", out var tag);
        command.Options.TryGetValue("--grep", out var grep);
        bool Matches(DiagnosticLogEntry entry) =>
            (since is null || entry.Timestamp >= since)
            && (maximumRank is null || DiagnosticLogReader.GetSeverityRank(entry.Level) <= maximumRank)
            && (tag is null || string.Equals(entry.Tag, tag, StringComparison.OrdinalIgnoreCase))
            && (grep is null || entry.RawLine.Contains(grep, StringComparison.OrdinalIgnoreCase));
        var document = DiagnosticLogReader.ReadFile(path, Matches, limit);

        return new CliExecutionResult(CliExitCode.Success, new
        {
            path,
            header = document.Header,
            headerVersion = document.HeaderVersion,
            headerLevel = document.HeaderLevel,
            matchedCount = document.MatchedCount,
            entries = document.Entries
        }, [], null);
    }

    private static CliExecutionResult NamingPreview(ParsedCliCommand command, CliEnvironment environment)
    {
        var mode = ScreenshotMode.Full;
        if (command.Options.TryGetValue("--mode", out var modeValue))
        {
            mode = modeValue!.ToLowerInvariant() switch
            {
                "full" => ScreenshotMode.Full,
                "region" => ScreenshotMode.Region,
                "window" => ScreenshotMode.Window,
                _ => (ScreenshotMode)(-1)
            };
            if (!Enum.IsDefined(mode)) return InvalidOption(command, "--mode は full、region、window のいずれかを指定してください。");
        }

        var capturedAt = environment.Now();
        if (command.Options.TryGetValue("--at", out var atValue))
        {
            if (!DateTimeOffset.TryParse(atValue, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out capturedAt))
                return InvalidOption(command, "--at は ISO 8601 形式の日時で指定してください。");
        }
        if (command.Options.TryGetValue("--dir", out var directoryOverride) && string.IsNullOrWhiteSpace(directoryOverride))
            return InvalidOption(command, "保存先フォルダーを空にできません。");

        var settingsPath = Path.Combine(environment.SettingsDirectory, "settings.json");
        var settingsRead = SettingsFileReader.Read(settingsPath);
        var settings = settingsRead.Settings;
        var directory = command.Options.TryGetValue("--dir", out var directoryValue)
            ? directoryValue!
            : settings.StillImageDirectory;
        if (string.IsNullOrWhiteSpace(directory))
            return InvalidOption(command, "保存先フォルダーを空にできません。");
        directory = Path.GetFullPath(directory);

        var template = command.Options.TryGetValue("--template", out var templateValue)
            ? templateValue
            : settings.FileNameTemplate;
        var window = command.Options.TryGetValue("--window", out var windowValue) ? windowValue : null;
        var extension = settings.ImageFormat == StillImageFormat.Png ? ".png" : ".jpg";
        var path = ScreenshotFileNaming.GetAvailablePath(
            directory,
            settings.OrganizeByMonth,
            capturedAt.DateTime,
            mode,
            window,
            template,
            extension,
            File.Exists);

        return new CliExecutionResult(CliExitCode.Success, new
        {
            mode = mode.ToString().ToLowerInvariant(),
            at = capturedAt,
            directory = Path.GetDirectoryName(path),
            fileName = Path.GetFileName(path),
            path,
            imageFormat = settings.ImageFormat,
            organizeByMonth = settings.OrganizeByMonth,
            settingsIssues = ToIssueResults(settingsRead.Issues)
        }, settingsRead.Issues.Select(issue => issue.Message).ToArray(), null);
    }

    private static CliExecutionResult Help(ParsedCliCommand command)
    {
        var topic = string.Join(' ', command.Positionals);
        var definitions = topic.Length == 0
            ? CliCommands.All
            : CliCommands.All.Where(item => item.Name == topic || item.Name.StartsWith(topic + " ", StringComparison.Ordinal)).ToArray();
        var help = new
        {
            topic = topic.Length == 0 ? null : topic,
            commands = definitions.Select(definition => new
            {
                name = definition.Name,
                usage = AddTextOption(CliCommands.GetUsage(definition)),
                description = definition.Description,
                options = definition.Options.Select(option => new
                {
                    name = option.Name,
                    value = option.ValueName,
                    description = option.Description
                }).ToArray()
            }).ToArray(),
            commonOptions = CliCommands.GlobalOptions.Select(option => new
            {
                name = option.Name,
                description = option.Description
            }).ToArray()
        };
        return new CliExecutionResult(CliExitCode.Success, help, [], null);
    }

    private static CliExecutionResult InvalidOption(ParsedCliCommand command, string message) =>
        new(CliExitCode.InvalidArguments, null, [], new CliError("invalidArguments", message));

    private static int WriteFailure(
        TextWriter output,
        CliEnvironment environment,
        string command,
        CliExitCode exitCode,
        string errorCode,
        string message,
        bool textMode = false)
    {
        var error = new CliError(errorCode, message);
        if (textMode)
            output.WriteLine(message);
        else
            WriteJson(output, environment, command, null, [], error);
        return (int)exitCode;
    }

    private static void WriteJson(
        TextWriter output,
        CliEnvironment environment,
        string command,
        object? result,
        IReadOnlyList<string> warnings,
        CliError? error)
    {
        var envelope = new CliEnvelope
        {
            CliVersion = environment.CliVersion,
            Command = command,
            Result = result,
            Warnings = warnings,
            Error = error
        };
        output.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private static string FormatText(object? result, IReadOnlyList<string> warnings, CliError? error)
    {
        if (error is not null) return error.Message;
        if (result is null) return string.Empty;
        var lines = new List<string>();
        if (result.GetType().GetProperty("commands")?.GetValue(result) is System.Collections.IEnumerable commands)
        {
            foreach (var item in commands)
            {
                if (item is null) continue;
                var type = item.GetType();
                var usage = type.GetProperty("usage")?.GetValue(item)?.ToString();
                var description = type.GetProperty("description")?.GetValue(item)?.ToString();
                if (usage is not null) lines.Add($"{usage}  {description}");
                if (type.GetProperty("options")?.GetValue(item) is System.Collections.IEnumerable options)
                {
                    foreach (var option in options)
                    {
                        if (option is null) continue;
                        var optionType = option.GetType();
                        var name = optionType.GetProperty("name")?.GetValue(option)?.ToString();
                        var value = optionType.GetProperty("value")?.GetValue(option)?.ToString();
                        var optionDescription = optionType.GetProperty("description")?.GetValue(option)?.ToString();
                        if (name is not null) lines.Add($"  {name} {value}  {optionDescription}".TrimEnd());
                    }
                }
            }
            foreach (var option in CliCommands.GlobalOptions)
                lines.Add($"  {option.Name}  {option.Description}");
        }
        else
        {
            AppendText(JsonSerializer.SerializeToElement(result, JsonOptions), string.Empty, lines);
        }
        foreach (var warning in warnings) lines.Add($"警告: {warning}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string AddTextOption(string usage)
    {
        var commonOptions = CliCommands.GlobalOptions.Select(option => option.ValueName is null
            ? option.Name
            : $"{option.Name} {option.ValueName}");
        return commonOptions.Any() ? $"{usage} [{string.Join(' ', commonOptions)}]" : usage;
    }

    private static string ToJsonPropertyName(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);

    private static object[] ToIssueResults(IReadOnlyList<SettingsFileIssue> issues) => issues
        .Select(issue => (object)new
        {
            issue.Code,
            property = issue.Property is null ? null : ToJsonPropertyName(issue.Property),
            issue.Message
        })
        .ToArray();

    private static void AppendText(JsonElement element, string prefix, List<string> lines)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    AppendText(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}", lines);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    AppendText(item, $"{prefix}[{index++}]", lines);
                break;
            case JsonValueKind.String:
                lines.Add($"{prefix}: {element.GetString()}");
                break;
            case JsonValueKind.Null:
                lines.Add($"{prefix}: (なし)");
                break;
            default:
                lines.Add($"{prefix}: {element}");
                break;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            // 出力は HTML に埋め込まず AI と人が直接読むので、日本語をエスケープしない。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new DiagnosticLogLevelJsonConverter());
        options.Converters.Add(new RectangleJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class RectangleJsonConverter : JsonConverter<Rectangle>
    {
        public override Rectangle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Rectangle value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("width", value.Width);
            writer.WriteNumber("height", value.Height);
            writer.WriteEndObject();
        }
    }

    private sealed class DiagnosticLogLevelJsonConverter : JsonConverter<DiagnosticLogLevel>
    {
        public override DiagnosticLogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DiagnosticLogLevels.FromSettingName(reader.TokenType == JsonTokenType.String ? reader.GetString() : null);

        public override void Write(Utf8JsonWriter writer, DiagnosticLogLevel value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToSettingName());
    }

    internal sealed record CliExecutionResult(
        CliExitCode ExitCode,
        object? Result,
        IReadOnlyList<string> Warnings,
        CliError? Error);
}
