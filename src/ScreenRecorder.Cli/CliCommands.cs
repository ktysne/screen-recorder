namespace ScreenRecorder.Cli;

internal enum CliValueKind
{
    String,
    Integer,
    Number,
    Enumeration,
    Path,
    Boolean
}

internal sealed record CliOptionDefinition(
    string Name,
    string? ValueName,
    string Description,
    CliValueKind ValueKind,
    IReadOnlyList<string>? Choices = null,
    bool Repeatable = false,
    string? McpName = null)
{
    public string McpPropertyName => McpName ?? (Name == "-o" ? "output" : Name.TrimStart('-').Replace('-', '_'));
}

internal sealed record CliPositionDefinition(
    string McpPropertyName,
    CliValueKind ValueKind = CliValueKind.String,
    IReadOnlyList<string>? Choices = null,
    string Description = "");

internal sealed record CliCommandDefinition(
    string Name,
    string PositionalSyntax,
    string Description,
    IReadOnlyList<CliOptionDefinition> Options,
    int MinimumPositionals = 0,
    int MaximumPositionals = 0,
    IReadOnlyList<CliPositionDefinition>? McpPositionals = null,
    bool AcceptsTextOption = true);

internal sealed record ParsedCliCommand(
    CliCommandDefinition Definition,
    IReadOnlyList<string> Positionals,
    IReadOnlyDictionary<string, string?> Options,
    bool TextMode);

internal sealed record CliParseResult(ParsedCliCommand? Command, string? Error, string? CommandName = null)
{
    public bool Success => Command is not null;
}

internal static class CliCommands
{
    private static CliOptionDefinition Option(
        string name,
        string? valueName,
        string description,
        CliValueKind kind = CliValueKind.String,
        IReadOnlyList<string>? choices = null,
        bool repeatable = false,
        string? mcpName = null) => new(name, valueName, description, kind, choices, repeatable, mcpName);

    private static readonly CliOptionDefinition FileOption = Option("--file", "<パス>", "読み取る設定ファイルのパス。省略時は本体の設定を使います。", CliValueKind.Path);
    private static readonly CliOptionDefinition SinceOption = Option("--since", "<日時>", "指定した ISO 8601 の日時以降を表示します。");
    private static readonly CliOptionDefinition LevelOption = Option("--level", "<レベル>", "指定したレベル以上(error、warn、info、debug の順に重い)の行だけを表示します。", CliValueKind.Enumeration, ["error", "warn", "info", "debug"]);
    private static readonly CliOptionDefinition TagOption = Option("--tag", "<タグ>", "指定したタグの行だけを表示します。");
    private static readonly CliOptionDefinition GrepOption = Option("--grep", "<文字列>", "本文または生行に文字列を含む行だけを表示します。");
    private static readonly CliOptionDefinition LimitOption = Option("--limit", "<件数>", "条件に合う行のうち、末尾から表示する行数です。既定は 200 です。", CliValueKind.Integer);
    private static readonly CliOptionDefinition AutomationAppOption = Option("--app", "<パス>", "接続先の実行ファイルを指定して照合します。", CliValueKind.Path);
    private static readonly CliOptionDefinition CaptureAfterOption = Option("--capture-after", "<日時>", "指定した時刻より後の撮影結果を待ちます。時刻と時差を含む ISO 8601(例: 2026-09-28T10:00:00+09:00)で指定します。");

    public static IReadOnlyList<CliOptionDefinition> GlobalOptions { get; } =
    [
        Option("--text", null, "人が読む形式で出力します。", CliValueKind.Boolean)
    ];

    public static IReadOnlyList<CliCommandDefinition> All { get; } =
    [
        new("info", "", "CLI と ScreenRecorder の保存先、モニターを表示します。", []),
        new("settings show", "", "設定を読み取り、既定値を補った内容を表示します。", [FileOption]),
        new("settings validate", "", "設定ファイルの形式と値を検査します。", [FileOption]),
        new("logs list", "", "診断ログの一覧を表示します。", []),
        new("logs show", "[<ファイル>]", "診断ログのヘッダーと行を表示します。ファイルを省くと最新のログを使います。", [SinceOption, LevelOption, TagOption, GrepOption, LimitOption], 0, 1, [new("file", CliValueKind.Path, Description: "読み取る診断ログのパスです。省略時は最新のログを使います。")]),
        new("remote status", "", "常駐中の ScreenRecorder の状態を表示します。", [AutomationAppOption]),
        new("remote wait", "", "常駐中の ScreenRecorder が指定状態になるまで待ちます。", [
            Option("--state", "<状態>", "idle、countdown、preparing、recording、paused、saving のいずれかです。", CliValueKind.Enumeration, ["idle", "countdown", "preparing", "recording", "paused", "saving"]),
            CaptureAfterOption,
            Option("--timeout", "<秒>", "待ち時間です。既定は 30 秒、上限は 3600 秒です。", CliValueKind.Integer),
            AutomationAppOption
        ]),
        new("remote perform", "<動作>", "status の shortcuts[] に表示された動作を常駐中の ScreenRecorder で始めます。", [AutomationAppOption], 1, 1,
            [new("action", CliValueKind.Enumeration, ["screenshotRegion", "screenshotFullScreen", "screenshotWindow", "recordingRegion", "recordingFullScreen", "recordingWindow", "pauseResume", "stopRecording"], "status の shortcuts[] にある動作です。")]),
        new("remote select", "", "開いている選択画面を範囲、ウィンドウ、取り消しのいずれかで完了させます。", [
            Option("--rect", "<x,y,w,h>", "仮想画面上の物理ピクセル範囲です。"),
            Option("--window", "<hwnd>", "選択画面が受け付けるウィンドウハンドルです。"),
            Option("--cancel", null, "選択を取り消します。", CliValueKind.Boolean),
            AutomationAppOption
        ]),
        new("remote exit", "", "常駐中の ScreenRecorder の終了を要求します。", [AutomationAppOption]),
        new("naming preview", "", "設定と指定値から保存ファイル名を計算します。", [
            Option("--template", "<ひな形>", "ファイル名のひな形を上書きします。"),
            Option("--mode", "<種類>", "full、region、window のいずれかを指定します。", CliValueKind.Enumeration, ["full", "region", "window"]),
            Option("--window", "<タイトル>", "{window} に使うウィンドウタイトルです。"),
            Option("--at", "<日時>", "計算に使う ISO 8601 の日時です。"),
            Option("--dir", "<フォルダー>", "保存先フォルダーを上書きします。", CliValueKind.Path)
        ]),
        new("record", "", "画面を録画します。--dry-run では開始データだけを計算します。", [
            Option("--display", "<番号>", "info の順のモニター番号です。", CliValueKind.Integer),
            Option("--rect", "<x,y,w,h>", "仮想画面上の範囲です。"),
            Option("--window", "<hwnd>", "ウィンドウハンドルです。"),
            Option("-o", "<MP4>", "保存先です。", CliValueKind.Path),
            Option("--duration", "<秒>", "録画する実時間です。", CliValueKind.Number),
            Option("--pause-at", "<秒>", "録画開始から一時停止までの時間です。", CliValueKind.Number),
            Option("--resume-at", "<秒>", "録画開始から再開までの時間です。", CliValueKind.Number),
            Option("--settings", "<パス>", "設定ファイルです。", CliValueKind.Path),
            Option("--defaults", null, "組み込みの既定値を使います。", CliValueKind.Boolean),
            Option("--app", "<パス>", "ScreenRecorder.exe の場所です。", CliValueKind.Path),
            Option("--force", null, "既存の出力を上書きします。", CliValueKind.Boolean),
            Option("--dry-run", null, "録画せずに開始データを表示します。", CliValueKind.Boolean),
            Option("--log-level", "<レベル>", "silent、error、warn、info、debug。", CliValueKind.Enumeration, ["silent", "error", "warn", "info", "debug"])
        ]),
        new("screenshot", "", "画面を静止画として保存します。", [
            Option("--display", "<番号>", "info の順のモニター番号です。", CliValueKind.Integer),
            Option("--rect", "<x,y,w,h>", "仮想画面上の範囲です。"),
            Option("--window", "<hwnd>", "ウィンドウハンドルです。"),
            Option("-o", "<パス>", "保存先です。.png、.jpg、.jpeg を指定できます。", CliValueKind.Path),
            Option("--settings", "<パス>", "設定ファイルです。", CliValueKind.Path),
            Option("--defaults", null, "組み込みの既定値を使います。", CliValueKind.Boolean),
            Option("--force", null, "既存の出力を上書きします。", CliValueKind.Boolean)
        ]),
        new("probe", "<ファイル>", "MP4 または静止画を検査します。", [
            Option("--expect-width", "<px>", "期待する幅です。", CliValueKind.Integer),
            Option("--expect-height", "<px>", "期待する高さです。", CliValueKind.Integer),
            Option("--expect-fps", "<fps>", "期待するフレームレートです。", CliValueKind.Number),
            Option("--expect-duration-ms", "<ms>", "期待する長さです。", CliValueKind.Integer),
            Option("--tolerance-ms", "<ms>", "長さの許容差です。既定は 500 ms。", CliValueKind.Integer),
            Option("--expect-video-codec", "<名称>", "期待する映像コーデックです。"),
            Option("--expect-audio-channels", "<数>", "期待する音声チャンネル数です。", CliValueKind.Integer),
            Option("--expect-audio-rate", "<Hz>", "期待する音声サンプリングレートです。", CliValueKind.Integer),
            Option("--expect-no-audio", null, "音声がないことを確認します。", CliValueKind.Boolean),
            Option("--frame", "<秒>", "指定時刻のフレームを書き出します。", CliValueKind.Number),
            Option("-o", "<PNG>", "フレームの保存先です。", CliValueKind.Path),
            Option("--force", null, "既存の出力を上書きします。", CliValueKind.Boolean)
        ], 1, 1, [new("file", CliValueKind.Path, Description: "検査する MP4 または静止画ファイルのパスです。")]),
        new("mcp", "", "標準入出力で MCP サーバーを起動します。", [
            Option("--app", "<パス>", "接続先にする ScreenRecorder.exe の場所です。", CliValueKind.Path),
            Option("--allow-dir", "<フォルダー>", "出力を許可するフォルダーです。複数回指定できます。", CliValueKind.Path, repeatable: true)
        ], AcceptsTextOption: false),
        new("help", "[<コマンド>]", "コマンド一覧または指定したコマンドの使い方を表示します。", [], 0, 2)
    ];

    public static string GetUsage(CliCommandDefinition definition)
    {
        var parts = new List<string> { definition.Name };
        if (definition.PositionalSyntax.Length > 0) parts.Add(definition.PositionalSyntax);
        parts.AddRange(definition.Options.Select(option => option.ValueName is null
            ? $"[{option.Name}]" + (option.Repeatable ? "..." : "")
            : $"[{option.Name} {option.ValueName}]" + (option.Repeatable ? "..." : "")));
        return string.Join(' ', parts);
    }

    public static CliParseResult Parse(IReadOnlyList<string> arguments)
    {
        var args = new List<string>(arguments.Count);
        var textMode = false;
        string? globalOptionError = null;
        foreach (var argument in arguments)
        {
            var globalOption = GlobalOptions.FirstOrDefault(option => option.Name == argument);
            if (globalOption is not null)
            {
                if (globalOption.ValueName is not null)
                    globalOptionError ??= $"共通オプション {argument} は値を指定する形式に対応していません。";
                else if (textMode)
                    globalOptionError ??= $"{argument} は 1 回だけ指定できます。";
                else
                    textMode = true;
            }
            else
            {
                args.Add(argument);
            }
        }

        var definition = args.Count == 0
            ? null
            : All
                .Where(item => args.Count >= item.Name.Split(' ').Length)
                .OrderByDescending(item => item.Name.Split(' ').Length)
                .FirstOrDefault(item => item.Name.Split(' ').SequenceEqual(args.Take(item.Name.Split(' ').Length), StringComparer.Ordinal));
        if (globalOptionError is not null)
            return new CliParseResult(null, globalOptionError, definition?.Name);
        if (args.Count == 0)
            return new CliParseResult(null, "コマンドを指定してください。使い方は help を実行してください。");
        if (definition is null)
            return new CliParseResult(null, $"コマンドを認識できません: {args[0]}");
        if (textMode && !definition.AcceptsTextOption)
            return new CliParseResult(null, "このコマンドでは --text を使えません。", definition.Name);

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
                return new CliParseResult(null, $"このコマンドでは {token} を使えません。", definition.Name);
            if (parsedOptions.ContainsKey(token) && !option.Repeatable)
                return new CliParseResult(null, $"オプション {token} は 1 回だけ指定できます。", definition.Name);

            if (option.ValueName is null)
            {
                parsedOptions.Add(token, null);
                continue;
            }

            if (index + 1 >= remaining.Length || remaining[index + 1].StartsWith("--", StringComparison.Ordinal))
                return new CliParseResult(null, $"オプション {token} の値を指定してください。", definition.Name);
            parsedOptions.Add(token, remaining[++index]);
        }

        if (positionals.Count < definition.MinimumPositionals || positionals.Count > definition.MaximumPositionals)
            return new CliParseResult(null, $"引数の数が正しくありません。使い方: {GetUsage(definition)}", definition.Name);

        if (definition.Name == "help" && positionals.Count > 0)
        {
            var topic = string.Join(' ', positionals);
            if (!All.Any(item => item.Name == topic || item.Name.StartsWith(topic + " ", StringComparison.Ordinal)))
                return new CliParseResult(null, $"ヘルプに該当するコマンドがありません: {topic}", definition.Name);
        }

        return new CliParseResult(
            new ParsedCliCommand(definition, positionals, parsedOptions, textMode),
            null,
            definition.Name);
    }
}
