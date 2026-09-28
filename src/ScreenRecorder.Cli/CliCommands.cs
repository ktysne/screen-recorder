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
    private static readonly CliOptionDefinition LevelOption = new("--level", "<レベル>", "指定したレベル以上(error、warn、info、debug の順に重い)の行だけを表示します。");
    private static readonly CliOptionDefinition TagOption = new("--tag", "<タグ>", "指定したタグの行だけを表示します。");
    private static readonly CliOptionDefinition GrepOption = new("--grep", "<文字列>", "本文または生行に文字列を含む行だけを表示します。");
    private static readonly CliOptionDefinition LimitOption = new("--limit", "<件数>", "条件に合う行のうち、末尾から表示する行数です。既定は 200 です。");
    private static readonly CliOptionDefinition AutomationAppOption = new("--app", "<パス>", "接続先の実行ファイルを指定して照合します。");

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
        new("remote status", "", "常駐中の ScreenRecorder の状態を表示します。", [AutomationAppOption]),
        new("remote wait", "", "常駐中の ScreenRecorder が指定状態になるまで待ちます。", [
            new("--state", "<状態>", "idle、countdown、preparing、recording、paused、saving のいずれかです。"),
            new("--timeout", "<秒>", "待ち時間です。既定は 30 秒、上限は 3600 秒です。"),
            AutomationAppOption
        ]),
        new("naming preview", "", "設定と指定値から保存ファイル名を計算します。", [
            new("--template", "<ひな形>", "ファイル名のひな形を上書きします。"),
            new("--mode", "<種類>", "full、region、window のいずれかを指定します。"),
            new("--window", "<タイトル>", "{window} に使うウィンドウタイトルです。"),
            new("--at", "<日時>", "計算に使う ISO 8601 の日時です。"),
            new("--dir", "<フォルダー>", "保存先フォルダーを上書きします。")
        ]),
        new("record", "", "画面を録画します。--dry-run では開始データだけを計算します。", [
            new("--display", "<番号>", "info の順のモニター番号です。"),
            new("--rect", "<x,y,w,h>", "仮想画面上の範囲です。"),
            new("--window", "<hwnd>", "ウィンドウハンドルです。"),
            new("-o", "<MP4>", "保存先です。"),
            new("--duration", "<秒>", "録画する実時間です。"),
            new("--pause-at", "<秒>", "録画開始から一時停止までの時間です。"),
            new("--resume-at", "<秒>", "録画開始から再開までの時間です。"),
            new("--settings", "<パス>", "設定ファイルです。"),
            new("--defaults", null, "組み込みの既定値を使います。"),
            new("--app", "<パス>", "ScreenRecorder.exe の場所です。"),
            new("--force", null, "既存の出力を上書きします。"),
            new("--dry-run", null, "録画せずに開始データを表示します。"),
            new("--log-level", "<レベル>", "silent、error、warn、info、debug。")
        ]),
        new("screenshot", "", "画面を静止画として保存します。", [
            new("--display", "<番号>", "info の順のモニター番号です。"),
            new("--rect", "<x,y,w,h>", "仮想画面上の範囲です。"),
            new("--window", "<hwnd>", "ウィンドウハンドルです。"),
            new("-o", "<パス>", "保存先です。.png、.jpg、.jpeg を指定できます。"),
            new("--settings", "<パス>", "設定ファイルです。"),
            new("--defaults", null, "組み込みの既定値を使います。"),
            new("--force", null, "既存の出力を上書きします。")
        ]),
        new("probe", "<ファイル>", "MP4 または PNG を検査します。", [
            new("--expect-width", "<px>", "期待する幅です。"),
            new("--expect-height", "<px>", "期待する高さです。"),
            new("--expect-fps", "<fps>", "期待するフレームレートです。"),
            new("--expect-duration-ms", "<ms>", "期待する長さです。"),
            new("--tolerance-ms", "<ms>", "長さの許容差です。既定は 500 ms。"),
            new("--expect-video-codec", "<名称>", "期待する映像コーデックです。"),
            new("--expect-audio-channels", "<数>", "期待する音声チャンネル数です。"),
            new("--expect-audio-rate", "<Hz>", "期待する音声サンプリングレートです。"),
            new("--expect-no-audio", null, "音声がないことを確認します。"),
            new("--frame", "<秒>", "指定時刻のフレームを書き出します。"),
            new("-o", "<PNG>", "フレームの保存先です。"),
            new("--force", null, "既存の出力を上書きします。")
        ], 1, 1),
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
            if (!token.StartsWith("--", StringComparison.Ordinal) && token != "-o")
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
