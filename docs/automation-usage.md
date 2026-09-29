# 自動化用 CLI と MCP の使い方

この資料は、このリポジトリで開発する AI と、Claude Desktop などから MCP で ScreenRecorder を使う AI を対象にしています。
CLI の引数の正本は `screenrecorder-cli help` の出力で、MCP の引数の正本は各道具の入力スキーマです。
MCP の導入と登録は [mcp-setup.md](mcp-setup.md) を参照してください。

## CLI と MCP の名前

MCP に公開される道具名は、CLI コマンド名の空白を `_` に置き換えた名前です。
たとえば `remote wait` は `remote_wait` です。
コマンドと道具の一覧は [CliCommands.cs](../src/ScreenRecorder.Cli/CliCommands.cs) と [McpToolCatalog.cs](../src/ScreenRecorder.Cli/McpToolCatalog.cs) で定義されています。

入力のプロパティ名は、オプション名から先頭の `--` を除いて snake_case にした名前です(`--capture-after` は `capture_after`)。
`-o` は `output` になるなど例外があるため、MCP では道具の入力スキーマに従ってください。

## 結果の読み方

CLI は標準出力に JSON を 1 つ返します。
終了コード 0 は成功、1 は検査結果に問題がある状態、2 は引数の誤り、3 は読み書きや録画などの処理失敗を示します。
失敗の種類は `error.code` と終了コードで判断し、説明文の `error.message` には依存しないでください。

MCP は道具の応答に CLI と同じ JSON を含め、失敗時は `isError` を立てます。
MCP では `isError` と JSON 内の `error.code` を使って結果を判断してください。

## 設定と診断ログを調べる

設定ファイルの形式や値を確かめるときは `settings validate` を使います。
利用者から届いた設定ファイルは `--file` で指定できます。
設定値を読むときは `settings show` を使います。

診断ログを調べるときは `logs list` で対象を選び、`logs show` で読みます。
警告以上に絞る例は `logs show --level warn` です。
対象期間、タグ、本文の文字列でも絞れます。
条件に合った総数 `matchedCount` と返却件数が違う場合、返却件数は `--limit` の上限に達しています。

MCP ではそれぞれの名前の空白を `_` にした道具を呼び、指定できる入力は道具の入力スキーマに従います。

## 録画と撮影を確かめる

画面全体、矩形範囲、ウィンドウのいずれかを対象にするときは、`record` と `screenshot` のどちらかを使います。
`--display`、`--rect`、`--window` は同時に指定せず、いずれか 1 つを指定します。

範囲や DPI の計算だけを確かめるときは、`record --dry-run --rect <x,y,w,h>` を使います。
`record` は録画の長さの間終わりません。結果の `timeoutMs` を目安に、呼び出し側の待ち時間を長めに取ります。
実際に録画するときは `record --display 0 -o <MP4> --duration <秒>` を実行し、`probe <MP4> --expect-*` で解像度や長さなどを検査します。
映像の内容を目で確かめるときは、`probe <MP4> --frame <秒> -o <PNG>` でフレームを書き出します。

静止画は `screenshot --display 0 -o <PNG|JPEG>` で撮り、`probe <PNG|JPEG> --expect-width <px> --expect-height <px>` で大きさを確認します。
録画の調査で詳細な録画プロセスのログが必要なときは、`record --log-level debug` を指定します。

MCP では入力のプロパティ名と型を道具のスキーマで確認してください。
録画や撮影の出力先は、MCP の書き込み制限にも従います。

## モニター番号と座標を調べる

`info` が返すモニター一覧の順番が `--display` に指定する番号です。
`--rect` は仮想画面上の座標で指定し、範囲全体を 1 つのモニター内に収めます。

## 常駐中の本体を操作する

本体の状態を見るときは `remote status` を使い、指定した状態や撮影結果を待つときは `remote wait` を使います。
動作を始めるときは `remote perform` に `status` の `shortcuts[]` にある動作を渡します。
本体が開いた選択画面を完了または取り消すときは `remote select` を使います。

撮影結果を確かめるときは、開始前の ISO 8601 時刻を記録し、`remote wait --state idle --capture-after <日時>` を呼びます。
`result.lastCapture.path` が返った場合は `probe` に渡して、保存されたファイルを検査できます。

`remote exit` は本体を終了させるため、本体を終了する意図がある場合だけ呼びます。
`remote` の各コマンドは本体を起動しません。

## 動画の長さを比べる

エンコーダが自動の場合、画面が静止している間にフレームが間引かれ、動画の末尾が短くなることがあります。
長さを比較するときは、`"encoder": "softwareOnly"` にした設定ファイルを `--settings` で渡します。

## MCP で使う場合の制限

MCP の `output` は絶対パスで指定し、`%TEMP%` または `mcp --allow-dir <フォルダー>` で許可したフォルダーの下に置きます。
`record`、`screenshot`、`probe` の出力先の親フォルダーは、呼び出す前に作成しておきます。
`record` と `screenshot` の `settings` も絶対パスで指定し、同じ許可範囲に置きます。
`--allow-dir` に指定するフォルダーも、MCP サーバーの起動前に作成しておきます。
パスが条件を満たさないと `pathNotAllowed` で失敗します。

MCP の `record` で指定できる録画時間は 30 秒までです。
`remote_wait` の待ち時間は 45 秒までです。
上限を超える値は `invalidArguments` で失敗します。
長い録画は本体に録らせます。本体の録画は時間を指定できず、止めるまで続きます。

1. 開始前の時刻を記録し、`remote_perform` で録画の動作を始めます。範囲やウィンドウの録画では選択画面が開くので、`remote_select` で選択を完了します。
2. `state` を `recording` にした `remote_wait` で、カウントダウンが終わり録画が始まるのを待ちます。録画の長さは、ここから測ります。待ちが時間切れになったら `remote_status` の `recording.state` を見ます。`countdown` か `preparing` なら待ち続け、`recording` なら手順 3 へ進みます。`idle` なら、空き容量の不足などで録画が始まらなかったので、手順 5 の確認に進みます。
3. 必要な時間が経ったら、`remote_perform` の `stopRecording` で止めます。
4. `state` を `idle` にした `remote_wait` を繰り返し、保存が終わるのを待ちます。
5. `remote_status` を呼び、`kind` が `recording` で `at` が記録した時刻より後の記録だけを見ます。その `lastCapture` に `path` があれば保存は成功しているので、そのパスを `probe` で確かめます。このとき `lastFailure` もあれば、MP3 への変換ができなかったなどの警告として併せて伝えます。`lastCapture` が無く `lastFailure` だけがあれば、録画は失敗しているので、その内容を利用者に伝えます。
MCP クライアントの時間切れはクライアントごとに異なるため、進捗通知の扱いも確認してください。

`remote_perform` で本体に撮影や録画をさせた場合、ファイルは本体の設定にある保存先へ保存され、`--allow-dir` の制限を受けません。

MCP のパス制限と時間切れの詳細は [automation-mcp.md](automation-mcp.md) の「安全性」「時間切れと進捗」を参照してください。

## 常駐中の本体への接続条件

`remote_*` の道具を使うには、本体の設定画面の「高度な設定」タブで「自動化用の接続を受け付ける」をオンにして保存します。
詳しい条件は [automation-remote.md](automation-remote.md) の「有効にする条件」を参照してください。

`automationDisabled` は、本体が起動しているものの自動化用のパイプが使えない状態です。
通常は自動化用の接続設定がオフのときに返り、本体の起動直後にも返ることがあります。
`notRunning` は、接続を確認できる同じ Windows セッションで本体が起動していない状態です。

## 利用者のデータを扱う

利用者の設定ファイルは変更せず、診断ログのフォルダーに検査用ファイルを置きません。
検査用の設定ファイルはコピーを使い、録画、撮影、フレーム書き出しの CLI 出力は一時フォルダーに保存します。
CLI の `-o` を利用者の既定の保存先へ向けないでください。
