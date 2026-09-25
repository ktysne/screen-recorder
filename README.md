# Screen Recorder

タスクトレイに常駐する、Windows 用のスクリーンショットと画面録画のツールです。
トレイアイコンのメニューかショートカットキーから、ディスプレイ全体、指定の範囲、指定のウィンドウを撮影、録画できます。

- 静止画は JPEG か PNG で保存します(既定は JPEG の品質 98)。
- 動画は H.264 の MP4 で保存し、PC の音とマイクの音を AAC か MP3 で収録できます(既定は AAC 192 kbps)。
- 保存先、ショートカットキー、画質は設定画面で変えられます。

## 動作環境

Windows 10 2004 以降(x64)

## 開発

仕様は [docs/design.md](docs/design.md)、実装の進め方は [docs/implementation-plan.md](docs/implementation-plan.md) を参照してください。

```powershell
dotnet build ScreenRecorder.slnx
dotnet test ScreenRecorder.slnx
```
