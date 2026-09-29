# MCP の導入と初期設定

この資料は、ScreenRecorder を導入し、Claude Desktop などの MCP のクライアントから使えるようにするまでの手順をまとめたものです。
利用者が自分で設定するときも、AI エージェントが利用者の設定を手伝うときも、この資料を上から順に進めてください。
MCP のサーバーの仕組みと安全性の設計は [automation-mcp.md](automation-mcp.md) にあります。

## 全体の流れ

MCP のサーバーは、自動化用 CLI(`screenrecorder-cli.exe`)の `mcp` サブコマンドです。
CLI は配布の zip に含まれていないので、このリポジトリから作ります。

1. 前提のソフトウェアを用意する
2. 本体(`ScreenRecorder.exe`)を用意する
3. CLI の写しを作る
4. 出力先のフォルダーを作る
5. Claude Desktop に登録する
6. 動作を確かめる

## 1. 前提のソフトウェア

- Windows 10 2004 以降の x64
- .NET 10 SDK(`dotnet --version` で 10 以降が表示されること)
- Git
- Claude Desktop(ほかの MCP のクライアントでもよい)

## 2. 本体を用意する

MCP の `record` は本体を録画の間だけ起動し、`remote_*` の道具は常駐中の本体に接続します。
どちらも、CLI と同じ版の本体を使います。版が違うと、通信の形が合わずに失敗することがあるためです。

次のどちらかで用意します。

- 配布版を使う：配布ページ <https://ktysne.info/screen-recorder/> から zip を入手して展開します。版は `ScreenRecorder.exe` のプロパティの「詳細」タブにある製品バージョンで確かめます。手順 3 では、その版のタグ(`v0.4.2` など)を取り出してから CLI を作ります。MCP は `v0.4.0` 以降で使えます。
- 手元でビルドする：リポジトリの直下で `dotnet build ScreenRecorder.slnx` を実行します。本体は `src\ScreenRecorder.App\bin\` の下にできます。開発中の変更を試すときはこちらを使います。

`remote_*` の道具を使うときは、本体の設定画面の「高度な設定」タブで「自動化用の接続を受け付ける」をオンにして保存します。
オフのままでも、`info`、`screenshot`、`record`、`probe` などは使えます。

## 3. CLI の写しを作る

CLI は、手順 2 の本体と同じソースから作ります。

- 配布版を使う場合：リポジトリを取得し、本体の製品バージョンと同じタグを取り出します。例の `v0.4.2` は、実際の版に置き換えます。

  ```powershell
  git clone https://github.com/ktysne/screen-recorder.git
  cd screen-recorder
  git checkout v0.4.2
  ```

- 手元でビルドする場合：本体をビルドしたのと同じ作業フォルダーをそのまま使います。別に取得したリポジトリやタグから作ると、本体と CLI の版がずれます。

そのリポジトリの直下で `install-mcp-cli.bat` を実行します。
CLI を Release で publish し、`%USERPROFILE%\.screenrecorder\mcp-cli\` に写します。

写しを登録するのは、Claude Desktop が起動している間、登録したサーバーのプロセスを動かし続けるためです。
ビルドの出力先の CLI を直接登録すると、そのファイルが使用中のままになり、再ビルドが失敗します。
写しを AppData の外に置く理由は [automation-mcp.md](automation-mcp.md)「登録の方法」にあります。

## 4. 出力先のフォルダーを作る

`record` と `screenshot` の出力先は、`%TEMP%` と、起動時に `--allow-dir` で指定したフォルダーの下に限られます。
`remote_perform` で常駐中の本体に撮らせたものは、この制限を受けず、本体の設定の保存先に保存されます。
`--allow-dir` に指定するフォルダー(例：`D:\ScreenRecorder-MCP-output`)を先に作っておきます。
`--allow-dir` は複数指定できます。

## 5. Claude Desktop に登録する

### 設定ファイルの場所

設定ファイルは `claude_desktop_config.json` です。
Claude Desktop の Settings → Developer → Edit Config で開くと、実際に読まれているファイルがエクスプローラーで表示されます。

通常の場所は `%APPDATA%\Claude\claude_desktop_config.json` です。
Claude Desktop は MSIX のパッケージなので、開くプロセスによっては `%LOCALAPPDATA%\Packages\Claude_<英数字>\LocalCache\Roaming\Claude\claude_desktop_config.json` に振り替えられます。
迷ったら Edit Config で開いたファイルを編集します。
ファイルが無ければ、同じ場所に作ります。

### 書く内容

次の PowerShell を実行すると、実際のパスを埋めた登録内容が表示されます。
先頭の 2 行を、手順 2 の本体と手順 4 のフォルダーに合わせて書き換えてから実行してください。

```powershell
$app = "D:\Tools\ScreenRecorder\ScreenRecorder.exe"
$allowDir = "D:\ScreenRecorder-MCP-output"
@{ screenrecorder = @{ command = "$env:USERPROFILE\.screenrecorder\mcp-cli\screenrecorder-cli.exe"; args = @("mcp", "--app", $app, "--allow-dir", $allowDir) } } | ConvertTo-Json -Depth 5
```

表示された `"screenrecorder": { ... }` の部分を、設定ファイルの `mcpServers` の中に貼り付けます。
設定ファイルにあるほかの項目(`preferences` やほかのサーバー)は消さずに残します。

設定ファイルが空か、`mcpServers` が無い場合の全体は次の形です。
`<ユーザー名>` と 2 つのパスを実際の値に置き換えます。JSON の中では `\` を `\\` と書きます。

```json
{
  "mcpServers": {
    "screenrecorder": {
      "command": "C:\\Users\\<ユーザー名>\\.screenrecorder\\mcp-cli\\screenrecorder-cli.exe",
      "args": [
        "mcp",
        "--app",
        "D:\\Tools\\ScreenRecorder\\ScreenRecorder.exe",
        "--allow-dir",
        "D:\\ScreenRecorder-MCP-output"
      ]
    }
  }
}
```

| 項目 | 値 |
|---|---|
| `command` | 手順 3 で作った CLI の写し。場所は固定 |
| `--app` の次 | 手順 2 で用意した `ScreenRecorder.exe` |
| `--allow-dir` の次 | 手順 4 で作ったフォルダー。`"--allow-dir", "<フォルダー>"` の組を足すと複数指定できる |

AI エージェントが代わりに編集するときは、既存のファイルを読み、`mcpServers.screenrecorder` だけを足すか置き換えて、ほかの項目を保ったまま書き戻します。

### 補足

`remote_*` の道具は、`--app` に指定した場所の本体だけを接続先として受け付けます。
常駐させている本体と同じ場所を指定してください。
`--app` を省くと、`record` は CLI と同じフォルダーなどから本体を探し、`remote_*` は実行ファイルの名前が `ScreenRecorder.exe` の本体を接続先として受け付けます。

登録したら、Claude Desktop をタスクトレイのアイコンも含めて終了し、起動し直します。

## 6. 動作を確かめる

Claude Desktop で、次の順に頼みます。

1. 「ScreenRecorder の info を呼んで」：CLI の版とモニターの一覧が返れば、サーバーは動いています。
2. 「ディスプレイ 0 を `D:\ScreenRecorder-MCP-output\test.png` に screenshot して」：画像ができれば、出力先の許可も正しく設定できています。
3. `remote_*` を使う場合は「remote_status を呼んで」：常駐中の本体の状態が返れば、接続できています。

## うまくいかないとき

| 症状 | 確かめること |
|---|---|
| 道具の一覧に screenrecorder が出ない | 設定ファイルの JSON の書式と `command` のパス。設定ファイルと同じフォルダーの `logs\mcp.log` に、サーバーの起動時のエラーが出ます |
| `pathNotAllowed` で失敗する | 出力先が絶対パスで、`%TEMP%` か `--allow-dir` のフォルダーの下にあるか。出力先の親フォルダーがすでにあるか |
| `appNotFound` で失敗する | `--app` のパスに `ScreenRecorder.exe` があるか |
| `remote_*` が接続できない | 本体が常駐しているか。「自動化用の接続を受け付ける」がオンで保存済みか。`--app` が常駐中の本体と同じ場所か |
| `protocolMismatch` で失敗する | 本体と CLI の版がそろっているか(手順 2 と 3) |
| `install-mcp-cli.bat` が使用中で失敗する | Claude Desktop をタスクトレイのアイコンも含めて終了してから、実行し直します |

## 更新するとき

本体を新しい版にしたら、CLI もその版のタグで作り直します。
Claude Desktop を終了してから `git checkout <新しいタグ>` と `install-mcp-cli.bat` を実行し、Claude Desktop を起動し直します。
Claude Desktop の起動中は写しを置き換えられないので、スクリプトはその旨を表示して、写しに触れずに失敗します。

`--app` の本体を常駐させたまま、同じ場所の本体を作り直すことはできません。
作り直す前に本体を終了してください。
