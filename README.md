# ScreenRecorder

タスクトレイに常駐し、ショートカットキーかトレイメニューから画面の静止画と動画を撮る Windows アプリです。

- 静止画：ディスプレイ全体、指定の範囲、指定のウィンドウ(JPEG / PNG)
- 動画：ディスプレイ全体、指定の範囲、指定のウィンドウ(MP4 / H.264、PC の音とマイクを AAC か MP3 で収録)

対応 OS は Windows 10 2004 以降の x64 です。

## 入手と使い方

配布ページ <https://ktysne.info/screen-recorder/> から zip を入手し、好きな場所に展開して `ScreenRecorder.exe` を起動します。
使い方は同梱の `manual.html` を参照してください。

## 開発者向けの情報

仕様は [docs/design.md](docs/design.md)、実装の順序は [docs/implementation-plan.md](docs/implementation-plan.md) にあります。

```powershell
dotnet build ScreenRecorder.slnx
dotnet test ScreenRecorder.slnx
```

配布は `build-package.bat` をダブルクリックして行います。
事前にリポジトリの直下で `npm install` を実行し、`tools/deploy.config.example.json` をコピーして `tools/deploy.config.json` を作り、配布サーバの接続情報を書いてください。

## ライセンス

MIT License
