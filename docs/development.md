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

CLI の写しの作り方と Claude Desktop への登録の手順は [mcp-setup.md](mcp-setup.md) にあります。
開発中の変更を試すときは、同じ資料の「手元でビルドする」の本体を `--app` に指定します。

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

`build-package.bat` を実行し、`X.Y.Z` 形式のバージョンを入力します。入力の前に、公開中の版(`release-site.js published-version`)と最新のリリースタグが表示されます。
スクリプトはラベル検査、公開バージョンの確認、Release テスト、自己完結 publish、同梱ファイルの検査、zip とサイト情報の生成を順に行います。
zip と最新版情報を生成した後、ビルドしたコミットを指す注釈付きタグを push し、そのタグから GitHub Release を作ります。
公開 URL から zip を取得して SHA-256 を照合した後、FTPS で紹介ページと最新版情報を送ります。

`build-package.bat --legacy-site` を指定すると、旧版向けの `update.json` と `archives/` の zip も FTPS で送ります。
指定しない発行では旧版向けファイルを送信も削除もしません。
アップロードに失敗した場合は、表示された `release:upload` コマンドで同じタグのアップロードを再試行します。

配布前に Windows サンドボックスで zip を展開し、起動から録画の開始と停止、ファイル保存までを確認します。
実際に配布する zip と同じ内容を使い、開発環境にある FFmpeg や VC++ ランタイムに依存していないことを確かめてください。
