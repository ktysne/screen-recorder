---
name: screenrecorder-cli
description: ScreenRecorder の自動化用 CLI(screenrecorder-cli)で、設定と診断ログの読み取り、録画と撮影の実行、動画と画像の検査を行う手順。録画や撮影に関わる変更を実機で確かめるとき、利用者から届いた設定や診断ログを調べるとき、動画の長さや解像度を確かめるときに使う。
---

# ScreenRecorder CLI の使い方

状態：段階 5 は実装済み。MCP 接続は `mcp` を使い、登録例は `docs/development.md` を参照する。

設計の正本は `docs/automation-cli.md`、引数の正本は `help` の出力である。
この Skill には場面ごとのコマンドの選び方だけを書き、引数の詳細は写さない。

## 準備

```powershell
dotnet build ScreenRecorder.slnx
$cli = "src/ScreenRecorder.Cli/bin/Debug/net10.0-windows10.0.22000.0/screenrecorder-cli.exe"
& $cli help
& $cli help record
```

- 出力は JSON を 1 つだけ返す。判断は `error.code` と終了コードで行い、`error.message` の文言に頼らない。
- 終了コード: 0 は成功、1 は検査で問題あり、2 は引数の誤り、3 は読み書きや録画の失敗。
- `record` は、本体と同じ `ScreenRecorder.exe` を使う。先に `dotnet build ScreenRecorder.slnx` で本体もビルドしておく。
- 録画には実時間がかかる。結果の `timeoutMs` を目安に、ツールの待ち時間を長めに取る。

## 場面ごとのコマンド

利用者の設定や診断ログを調べる:

- `settings validate` で、設定ファイルの壊れと範囲外の値を見る。`--file` を付けると、届いたファイルを検査できる。
- `logs list` でログを選び、`logs show --level warn` で警告以上に絞る。行数が多いときは `--since`、`--tag`、`--grep` で絞る。`matchedCount` と返った件数が違えば、`--limit` の件数だけ末尾から返している。

録画や撮影の変更を確かめる:

- `record` と `screenshot` は、対象として `--display <番号>`、`--rect <x,y,w,h>`、`--window <hwnd>` のどれか 1 つが必須である。
- 範囲や DPI の計算だけを見るなら、`record --dry-run --rect <x,y,w,h>` で開始データを確かめる(録画はしない)。
- 実際に録るなら、`record --display 0 -o <MP4> --duration <秒>` の結果を `probe <MP4> --expect-*` で確かめる。
- 中身を目で確かめるなら、`probe <MP4> --frame <秒> -o <PNG>` の画像を読む。
- 静止画は `screenshot --display 0 -o <PNG|JPEG>` で撮り、`probe <PNG|JPEG> --expect-width <px> --expect-height <px>` で検査する。
- 調査では `record --log-level debug` を付けると、録画プロセスの詳しいログが `log[]` に入る。

モニターの番号と座標:

- `info` のモニターの一覧の順が `--display` の番号になる。`--rect` は仮想画面の座標で、1 つのモニターに収める。

常駐中の本体を確かめる:

- `remote status` と `remote wait` で状態を読み、操作できる条件を待つ。
- 撮影の保存を確認するときは、開始前の ISO 8601 時刻を指定して `remote wait --state idle --capture-after <日時>` を実行し、返った `result.lastCapture.path` を `probe` に渡す。
- `remote perform` で本体の撮影や録画を始め、`remote select` で開いている選択画面を完了または取り消す。
- `remote exit` で本体を終了する。テスト後にトレイを残さないために使う。
- `automationDisabled` は本体が起動中で自動化用接続が無効、`notRunning` は本体が起動していない状態を示す。
- 引数と選択画面が操作を受け付ける条件は `help remote` と `help remote select` を正本とする。

## 長さを比べるときの注意

- 設定のエンコーダが「自動」だと、画面が静止している間はフレームが間引かれ、動画の末尾が短くなる。長さを比べるときは、`"encoder": "softwareOnly"` の設定ファイルを `--settings` で渡す。
- CPU の負荷が高いときに一時停止と再開を挟むと、動画が約 0.9 秒短くなる不具合がある(#97)。長さを比べるときは、ほかの重い処理と並行させない。

## 守ること

- 利用者の設定ファイルと診断ログのフォルダーには書き込まない(CLI 自身も書かない)。
- 本体の保存先に `-o` を向けない。テストの出力は一時フォルダーに置く。
