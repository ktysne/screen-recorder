# 開発と配布

## ビルドとテスト

開発ビルドは次のコマンドで作成します。

```powershell
dotnet build ScreenRecorder.slnx
```

テストは次のコマンドで実行します。

```powershell
dotnet test ScreenRecorder.slnx
```

CLI のコマンド一覧は次のコマンドで表示します。

```powershell
dotnet run --project src/ScreenRecorder.Cli -- help
```

`tests/ScreenRecorder.Capture.Tests` の実機テストは GPU、Media Foundation、画面を使います。
Windows の実機で `SCREENRECORDER_DESKTOP_TESTS=1` を設定して実行してください。
環境変数を設定しない場合、実機テストはスキップされます。

実機テストが本体を起動するときは、`SCREENRECORDER_TEST_DATA_DIR` に一時フォルダーを指定します。
設定、診断ログ、更新用ファイル、自動起動の登録をそのフォルダーの下に置くため、利用者の状態を変更しません。

## Claude Desktop から MCP を使う

Claude Desktop は、起動している間、登録した MCP のサーバーのプロセスを動かし続けます。
ビルドの出力先の `screenrecorder-cli.exe` を直接登録すると、Claude Desktop を終了するまでそのファイルを置き換えられず、再ビルドが失敗します。
これを避けるため、登録には Release の CLI の写しを使います。

1. リポジトリ直下の `install-mcp-cli.bat` を実行します。CLI を Release で publish し、`%LOCALAPPDATA%\ScreenRecorder\mcp-cli\` に写します。
2. `build-package.bat` で配布用の本体(`artifacts\publish\ScreenRecorder.exe`)を作ります。`record` と `remote` はこの本体を使います。
3. Claude Desktop の設定ファイル `%APPDATA%\Claude\claude_desktop_config.json` に、写しを次のように登録します。`<ユーザー名>` とリポジトリの場所は、実際の配置に合わせて置き換えてください。

```json
{
  "mcpServers": {
    "screenrecorder": {
      "command": "C:\\Users\\<ユーザー名>\\AppData\\Local\\ScreenRecorder\\mcp-cli\\screenrecorder-cli.exe",
      "args": [
        "mcp",
        "--app",
        "D:\\Desktop\\Develop\\screen-recorder\\artifacts\\publish\\ScreenRecorder.exe",
        "--allow-dir",
        "D:\\ScreenRecorder-MCP-output"
      ]
    }
  }
}
```

`--app` の本体は、録画の間だけ起動されます。
そのため、`--app` に指定した本体も、Debug と Release の再ビルドも、Claude Desktop の起動中に作り直せます。
`--app` には、`dotnet build` で作った `src\ScreenRecorder.Appin\...\ScreenRecorder.exe` も指定できます。
`remote` の各道具は、`--app` に指定した場所の本体だけを接続先として受け付けます。常駐させている本体と同じ場所を指定してください。
`--app` を省くと、`remote` は実行ファイルの名前が `ScreenRecorder.exe` の本体を接続先として受け付けます。

写しを新しくしたいときは、Claude Desktop を終了してから `install-mcp-cli.bat` を実行し直します。
Claude Desktop の起動中は写しを置き換えられないので、スクリプトはその旨を表示して、写しに触れずに失敗します。

`--allow-dir` は複数指定できます。
`%TEMP%` は常に出力先として許可されます。

## アプリのアイコン

`src/ScreenRecorder.App/app.ico` と配布ページのアイコン `site/assets/app-icon-256.png` は、`assets/screen-recorder-a1-transparent.png` から作ります。
素材を差し替えたときは、Pillow を入れた Python で次のコマンドを実行し、できた 2 つのファイルをコミットします。

```powershell
python tools/make-app-icon.py
```

## 配布前の準備

Node.js 22.15 以降をインストールし、リポジトリのルートで依存をインストールします。

```powershell
npm install
```

`tools/deploy.config.example.json` を `tools/deploy.config.json` にコピーし、FTPS サーバの接続先、ユーザー名、パスワード、配布先ディレクトリを設定します。
接続情報は `SCREENRECORDER_FTP_HOST`、`SCREENRECORDER_FTP_PORT`、`SCREENRECORDER_FTP_USER`、`SCREENRECORDER_FTP_PASSWORD`、`SCREENRECORDER_FTP_REMOTE_ROOT` 環境変数でも指定できます。

VC++ ランタイム DLL は、Visual Studio Build Tools の x64 CRT ディレクトリから取得します。
既定の場所は `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Redist\MSVC\14.44.35112\x64\Microsoft.VC143.CRT` です。
別の場所を使う場合は `SCREENRECORDER_VCREDIST_DIR` に指定します。

FFmpeg は LGPL の shared ビルドを使います。
GPL または nonfree の成分を含むビルドは配布できません。

取得候補は [BtbN FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases) の `lgpl-shared` Windows x64 ビルドです。
展開後、`ffmpeg.exe`、実行に必要な DLL、配布物に含めるライセンス文書を `third_party\ffmpeg\` に置きます。
取得元とリリースバージョンを記録し、`ffmpeg -version` の `configuration` に `--enable-gpl` が含まれていないことを確認します。
配布時にはライセンス条件と同梱物を改めて確認してください。

## 配布

`build-package.bat` を実行し、`X.Y.Z` 形式のバージョンを入力します。
スクリプトはラベル検査、公開バージョンの確認、Release テスト、自己完結 publish、同梱ファイルの検査、zip とサイト情報の生成を順に行います。
転送を選ぶと FTPS で配布物を送り、最後にビルドしたコミットへ注釈付きタグを付けて、そのタグだけを push します。

配布前に Windows サンドボックスで zip を展開し、起動から録画の開始と停止、ファイル保存までを確認します。
実際に配布する zip と同じ内容を使い、開発環境にある FFmpeg や VC++ ランタイムに依存していないことを確かめてください。
