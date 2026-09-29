namespace ScreenRecorder.Cli;

internal static class McpServerInstructions
{
    public static string Create(IReadOnlyList<string> allowedDirectories)
    {
        var allowedPaths = string.Join("、", allowedDirectories.Select(path => $"`{path}`"));
        return string.Join(Environment.NewLine,
        [
            "結果は CLI と同じ JSON です。`isError` と `error.code` で判断し、`error.message` の文言には依存しないでください。",
            $"許可フォルダー（`%TEMP%` と `--allow-dir` の実体パス）: {allowedPaths}",
            "`output` と `record`/`screenshot` の `settings` は絶対パスで指定し、許可フォルダーの下に置いてください。親フォルダーは既に存在する必要があります。違反は `pathNotAllowed` です。",
            "`display` の番号は `info` が返すモニター一覧の順です。`display`、`rect`、`window` はいずれか 1 つを指定してください。",
            $"`record` の `duration` は {McpCommandService.MaximumRecordDurationSeconds:0} 秒まで、`remote_wait` の `timeout` は {McpCommandService.MaximumRemoteWaitSeconds} 秒までです。",
            "長い録画は次の手順で行います。",
            "開始前の時刻を記録します。",
            "`remote_perform` で録画を開始します。範囲またはウィンドウを使う場合は、`remote_select` で選択を完了します。",
            "`state=recording` の `remote_wait` で録画の開始を待ちます。",
            "必要な時間の後に `remote_perform` の `stopRecording` で録画を止めます。",
            "`state=idle` の `remote_wait` を繰り返して保存完了を待ちます。",
            "`remote_status` で開始時刻より後の `lastCapture` を確認します。`lastCapture` があれば成功です。`lastFailure` もあれば警告として扱い、`lastCapture` がなく `lastFailure` があれば失敗です。",
            "`remote_*` は本体の「高度な設定」タブで「自動化用の接続を受け付ける」がオンのときだけ使えます。",
            "`automationDisabled` は本体が起動していても自動化用の接続が使えない状態です（通常は設定オフか起動直後）。`notRunning` は同じ Windows セッションで本体が起動していない状態です。",
            "`remote_perform` で本体が撮ったファイルは、本体の設定にある保存先へ保存されます。",
            "詳細: https://github.com/ktysne/screen-recorder/blob/main/docs/automation-usage.md"
        ]);
    }
}
