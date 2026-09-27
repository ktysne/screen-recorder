namespace ScreenRecorder.Cli;

internal sealed record CliOptionDefinition(string Name, string? ValueName, string Description);

internal sealed record CliCommandDefinition(
    string Name,
    string PositionalSyntax,
    string Description,
    IReadOnlyList<CliOptionDefinition> Options,
    int MinimumPositionals = 0,
    int MaximumPositionals = 0);

internal sealed record ParsedCliCommand(
    CliCommandDefinition Definition,
    IReadOnlyList<string> Positionals,
    IReadOnlyDictionary<string, string?> Options,
    bool TextMode);

internal sealed record CliParseResult(ParsedCliCommand? Command, string? Error)
{
    public bool Success => Command is not null;
}

internal static class CliCommands
{
    private static readonly CliOptionDefinition FileOption = new("--file", "<パス>", "読み取る設定ファイルのパス。省略時は本体の設定を使います。");
    private static readonly CliOptionDefinition SinceOption = new("--since", "<日時>", "指定した ISO 8601 の日時以降を表示します。");
    private static readonly CliOptionDefinition LevelOption = new("--level", "<レベル>", "error、warn、info、debug の行だけを表示します。");
    private static readonly CliOptionDefinition TagOption = new("--tag", "<タグ>", "指定したタグの行だけを表示します。");
    private static readonly CliOptionDefinition GrepOption = new("--grep", "<文字列>", "本文または生行に文字列を含む行だけを表示します。");
    private static readonly CliOptionDefinition LimitOption = new("--limit", "<件数>", "表示する行数の上限です。");

    public static IReadOnlyList<CliOptionDefinition> GlobalOptions { get; } =
    [
        new("--text", null, "人が読む形式で出力します。")
    ];

    public static IReadOnlyList<CliCommandDefinition> All { get; } =
    [
        new("info", "", "CLI と ScreenRecorder の保存先、モニターを表示します。", []),
        new("settings show", "", "設定を読み取り、既定値を補った内容を表示します。", [FileOption]),
        new("settings validate", "", "設定ファイルの形式と値を検査します。", [FileOption]),
        new("logs list", "", "診断ログの一覧を表示します。", []),
        new("logs show", "[<ファイル>]", "診断ログのヘッダーと行を表示します。ファイルを省くと最新のログを使います。", [SinceOption, LevelOption, TagOption, GrepOption, LimitOption], 0, 1),
        new("naming preview", "", "設定と指定値から保存ファイル名を計算します。", [
            new("--template", "<ひな形>", "ファイル名のひな形を上書きします。"),
            new("--mode", "<種類>", "full、region、window のいずれかを指定します。"),
            new("--window", "<タイトル>", "{window} に使うウィンドウタイトルです。"),
            new("--at", "<日時>", "計算に使う ISO 8601 の日時です。"),
            new("--dir", "<フォルダー>", "保存先フォルダーを上書きします。")
        ]),
        new("help", "[<コマンド>]", "コマンド一覧または指定したコマンドの使い方を表示します。", [], 0, 2)
    ];

    public static string GetUsage(CliCommandDefinition definition)
    {
        var parts = new List<string> { definition.Name };
        if (definition.PositionalSyntax.Length > 0) parts.Add(definition.PositionalSyntax);
        parts.AddRange(definition.Options.Select(option => option.ValueName is null
            ? $"[{option.Name}]"
            : $"[{option.Name} {option.ValueName}]"));
        return string.Join(' ', parts);
    }

    public static CliParseResult Parse(IReadOnlyList<string> arguments)
    {
        var args = new List<string>(arguments.Count);
        var textMode = false;
        foreach (var argument in arguments)
        {
            var globalOption = GlobalOptions.FirstOrDefault(option => option.Name == argument);
            if (globalOption is not null)
            {
                if (globalOption.ValueName is not null)
                    return new CliParseResult(null, $"共通オプション {argument} は値を指定する形式に対応していません。");
                if (textMode) return new CliParseResult(null, $"{argument} は 1 回だけ指定できます。");
                textMode = true;
            }
            else
            {
                args.Add(argument);
            }
        }

        if (args.Count == 0) return new CliParseResult(null, "コマンドを指定してください。使い方は help を実行してください。");

        var definition = All
            .Where(item => args.Count >= item.Name.Split(' ').Length)
            .OrderByDescending(item => item.Name.Split(' ').Length)
            .FirstOrDefault(item => item.Name.Split(' ').SequenceEqual(args.Take(item.Name.Split(' ').Length), StringComparer.Ordinal));
        if (definition is null)
            return new CliParseResult(null, $"コマンドを認識できません: {args[0]}");

        var remaining = args.Skip(definition.Name.Split(' ').Length).ToArray();
        var optionDefinitions = definition.Options.ToDictionary(option => option.Name, StringComparer.Ordinal);
        var parsedOptions = new Dictionary<string, string?>(StringComparer.Ordinal);
        var positionals = new List<string>();
        for (var index = 0; index < remaining.Length; index++)
        {
            var token = remaining[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            if (!optionDefinitions.TryGetValue(token, out var option))
                return new CliParseResult(null, $"このコマンドでは {token} を使えません。");
            if (parsedOptions.ContainsKey(token))
                return new CliParseResult(null, $"オプション {token} は 1 回だけ指定できます。");

            if (option.ValueName is null)
            {
                parsedOptions.Add(token, null);
                continue;
            }

            if (index + 1 >= remaining.Length || remaining[index + 1].StartsWith("--", StringComparison.Ordinal))
                return new CliParseResult(null, $"オプション {token} の値を指定してください。");
            parsedOptions.Add(token, remaining[++index]);
        }

        if (positionals.Count < definition.MinimumPositionals || positionals.Count > definition.MaximumPositionals)
            return new CliParseResult(null, $"引数の数が正しくありません。使い方: {GetUsage(definition)}");

        if (definition.Name == "help" && positionals.Count > 0)
        {
            var topic = string.Join(' ', positionals);
            if (!All.Any(item => item.Name == topic || item.Name.StartsWith(topic + " ", StringComparison.Ordinal)))
                return new CliParseResult(null, $"ヘルプに該当するコマンドがありません: {topic}");
        }

        return new CliParseResult(
            new ParsedCliCommand(definition, positionals, parsedOptions, textMode),
            null);
    }
}
