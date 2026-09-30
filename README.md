# ScreenRecorder

タスクトレイに常駐し、ショートカットキーかトレイメニューから画面の静止画と動画を撮る Windows アプリです。

- 静止画：ディスプレイ全体、指定の範囲、指定のウィンドウ(JPEG / PNG)
- 動画：ディスプレイ全体、指定の範囲、指定のウィンドウ(MP4 / H.264、PC の音とマイクを AAC か MP3 で収録)

対応 OS は Windows 10 2004 以降の x64 です。

## 入手と使い方

配布ページ <https://ktysne.info/screen-recorder/> から zip を入手し、好きな場所に展開して `ScreenRecorder.exe` を起動します。
zip は [GitHub Releases](https://github.com/ktysne/screen-recorder/releases) から配布します。
使い方は同梱の `manual.html` を参照してください。

## MCP で使う

Claude Desktop などの MCP のクライアントから撮影と録画を頼めます。
導入と初期設定の手順は [docs/mcp-setup.md](docs/mcp-setup.md) にまとめています。
AI に読ませる資料は、設定が [docs/mcp-setup.md](docs/mcp-setup.md)、使い方が [docs/automation-usage.md](docs/automation-usage.md) です。

## 開発者向けの情報

仕様は [docs/design.md](docs/design.md)、実装の順序は [docs/implementation-plan.md](docs/implementation-plan.md) にあります。

```powershell
dotnet build ScreenRecorder.slnx
dotnet test ScreenRecorder.slnx
```

配布は `build-package.bat` をダブルクリックして行います。
旧版利用者にも更新を届ける場合は `build-package.bat --legacy-site` を実行します。
事前にリポジトリの直下で `npm ci` を実行し、`tools/deploy.config.example.json` をコピーして `tools/deploy.config.json` を作り、配布サーバの接続情報を書いてください。

## ライセンス

MIT License
