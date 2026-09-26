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

`tests/ScreenRecorder.Capture.Tests` は GPU と Media Foundation を使うため、Windows の実機で実行してください。

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
取得元とリリース版を記録し、`ffmpeg -version` の `configuration` に `--enable-gpl` が含まれていないことを確認します。
配布時にはライセンス条件と同梱物を改めて確認してください。

## 配布

`build-package.bat` を実行し、`X.Y.Z` 形式の版を入力します。
スクリプトはラベル検査、公開版の確認、Release テスト、自己完結 publish、同梱ファイルの検査、zip とサイト情報の生成を順に行います。
転送を選ぶと FTPS で配布物を送り、最後にビルドしたコミットへ注釈付きタグを付けて、そのタグだけを push します。

配布前に Windows サンドボックスで zip を展開し、起動から録画の開始と停止、ファイル保存までを確認します。
実際に配布する zip と同じ内容を使い、開発環境にある FFmpeg や VC++ ランタイムに依存していないことを確かめてください。
