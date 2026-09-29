---
name: screenrecorder-cli
description: ScreenRecorder の自動化用 CLI(screenrecorder-cli)で、設定と診断ログの読み取り、録画と撮影の実行、動画と画像の検査を行う手順。録画や撮影に関わる変更を実機で確かめるとき、利用者から届いた設定や診断ログを調べるとき、動画の長さや解像度を確かめるときに使う。
---

# ScreenRecorder CLI を開発で使う

CLI と MCP の場面別の使い方は [docs/automation-usage.md](../../../docs/automation-usage.md) を参照する。
CLI の引数は `help` の出力を正本とする。

## Debug ビルドの準備

```powershell
dotnet build ScreenRecorder.slnx
$cli = "src/ScreenRecorder.Cli/bin/Debug/net10.0-windows10.0.22000.0/screenrecorder-cli.exe"
& $cli help
& $cli help record
```

- `record` は録画用に `ScreenRecorder.exe` を起動するため、実行前に本体もビルドしておく。
- テストの出力は一時フォルダーに置き、本体の保存先へ `-o` を向けない。
