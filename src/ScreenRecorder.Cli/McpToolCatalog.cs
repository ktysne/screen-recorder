using System.Globalization;
using System.Text.Json;

namespace ScreenRecorder.Cli;

internal sealed record McpToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    bool? ReadOnlyHint,
    bool? DestructiveHint,
    CliCommandDefinition Command);

internal sealed record McpArgumentResult(IReadOnlyList<string>? Arguments, string? Error)
{
    public bool Success => Arguments is not null;
}

internal static class McpToolCatalog
{
    public static IReadOnlyList<McpToolDescriptor> Tools { get; } = CliCommands.All
        .Where(command => command.Name is not ("help" or "mcp"))
        .Select(CreateTool)
        .ToArray();

    public static McpToolDescriptor? Find(string name) => Tools.FirstOrDefault(tool => tool.Name == name);

    private static McpToolDescriptor CreateTool(CliCommandDefinition command)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var positionals = command.McpPositionals ?? [];
        for (var index = 0; index < positionals.Count; index++)
        {
            var positional = positionals[index];
            properties.Add(positional.McpPropertyName, CreatePropertySchema(positional.ValueKind, positional.Choices, positional.Description));
        }

        foreach (var option in command.Options.Where(option => option.Name != "--app"))
            properties.Add(option.McpPropertyName, CreatePropertySchema(option.ValueKind, option.Choices, option.Description));

        var required = positionals.Take(command.MinimumPositionals).Select(positional => positional.McpPropertyName).ToArray();
        var inputSchema = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        });
        var name = command.Name.Replace(' ', '_');
        var description = command.Description;
        if (command.Name == "naming preview") description += " 開発向けの道具です。";
        if (command.Name == "record")
            description += " 録画の準備と書き終えの時間が加わるため、呼び出しは指定した長さより長くかかります。長い録画や時間切れが短いクライアントでは remote_perform と remote_wait を使ってください。";

        var readOnly = command.Name switch
        {
            "info" or "settings show" or "settings validate" or "logs list" or "logs show" or "remote status" or "remote wait" or "naming preview" => true,
            "remote perform" or "remote select" or "remote exit" or "record" or "screenshot" => false,
            _ => (bool?)null
        };
        var destructive = command.Name == "remote exit" || command.Options.Any(option => option.Name == "--force");
        return new McpToolDescriptor(name, description, inputSchema, readOnly, destructive ? true : null, command);
    }

    private static Dictionary<string, object?> CreatePropertySchema(CliValueKind kind, IReadOnlyList<string>? choices, string description)
    {
        var schemaType = kind switch
        {
            CliValueKind.Boolean => "boolean",
            CliValueKind.Integer => "integer",
            CliValueKind.Number => "number",
            _ => "string"
        };
        var schema = new Dictionary<string, object?>
        {
            ["type"] = schemaType,
            ["description"] = description
        };
        if (kind == CliValueKind.Enumeration)
            schema["enum"] = choices ?? [];
        return schema;
    }
}

internal static class McpToolInput
{
    public static McpArgumentResult BuildArguments(McpToolDescriptor tool, IReadOnlyDictionary<string, JsonElement> input)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var arguments = tool.Command.Name.Split(' ').ToList();
        var positionals = tool.Command.McpPositionals ?? [];
        foreach (var positional in positionals) allowed.Add(positional.McpPropertyName);

        var positionalValues = new List<string>();
        for (var index = 0; index < positionals.Count; index++)
        {
            var positional = positionals[index];
            if (!input.TryGetValue(positional.McpPropertyName, out var value))
            {
                if (index < tool.Command.MinimumPositionals)
                    return new(null, $"{positional.McpPropertyName} は必須です。");
                continue;
            }

            var converted = ConvertValue(value, positional.ValueKind, positional.Choices, positional.McpPropertyName);
            if (converted.Error is not null) return new(null, converted.Error);
            positionalValues.Add(converted.Value!);
        }

        arguments.AddRange(positionalValues);
        foreach (var option in tool.Command.Options.Where(option => option.Name != "--app"))
        {
            allowed.Add(option.McpPropertyName);
            if (!input.TryGetValue(option.McpPropertyName, out var value)) continue;
            var converted = ConvertValue(value, option.ValueKind, option.Choices, option.McpPropertyName);
            if (converted.Error is not null) return new(null, converted.Error);
            if (option.ValueKind == CliValueKind.Boolean)
            {
                if (value.ValueKind == JsonValueKind.True) arguments.Add(option.Name);
                continue;
            }

            arguments.Add(option.Name);
            arguments.Add(converted.Value!);
        }

        var unexpected = input.Keys.FirstOrDefault(key => !allowed.Contains(key));
        return unexpected is null
            ? new(arguments, null)
            : new(null, $"{unexpected} はこの道具の入力に指定できません。");
    }

    private static (string? Value, string? Error) ConvertValue(
        JsonElement value,
        CliValueKind kind,
        IReadOnlyList<string>? choices,
        string propertyName)
    {
        switch (kind)
        {
            case CliValueKind.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (value.GetBoolean() ? "true" : "false", null)
                    : (null, $"{propertyName} は真偽値で指定してください。");
            case CliValueKind.Integer:
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)
                    ? (value.GetRawText(), null)
                    : (null, $"{propertyName} は整数で指定してください。");
            case CliValueKind.Number:
                return value.ValueKind == JsonValueKind.Number
                    && value.TryGetDouble(out var number)
                    && double.IsFinite(number)
                    ? (value.GetRawText(), null)
                    : (null, $"{propertyName} は数値で指定してください。");
            case CliValueKind.Enumeration:
            case CliValueKind.String:
            case CliValueKind.Path:
                if (value.ValueKind != JsonValueKind.String)
                    return (null, $"{propertyName} は文字列で指定してください。");
                var text = value.GetString()!;
                if (kind == CliValueKind.Enumeration && !(choices ?? []).Contains(text, StringComparer.Ordinal))
                    return (null, $"{propertyName} の候補が正しくありません。");
                return (text, null);
            default:
                return (null, string.Format(CultureInfo.InvariantCulture, "{0} の値の種類が正しくありません。", propertyName));
        }
    }
}
