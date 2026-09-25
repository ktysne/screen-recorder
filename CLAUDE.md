# プロジェクトガイドライン

このファイルは AI コーディングエージェント(Claude Code、Codex など)向けの共通指示を記載する。

## 言語

このプロジェクトでは日本語を共通言語とする。
セッション中の応答、コミットメッセージ、Pull Request のタイトルと本文、Issue、レビューコメント、コードのコメント、ドキュメントを日本語で書く。
コード中の識別子は英語でよい。

例外として、`.bat` など cmd.exe が解釈するスクリプトのコメントと表示メッセージは ASCII(英語)で書く。
cmd のバッチパーサは UTF-8 の日本語をコマンドとして誤解釈し、実行自体が失敗するためである。

利用者が目にする日本語の文書(docs 配下、README、UI 文言)を書くときは、`/japanese-tech-writing` スキルの文章規範に従う。

UI に関わる実装(設定画面、トレイメニュー、範囲とウィンドウの選択画面、録画中の操作バー、ダイアログ、通知、マニュアル)では、`/ux-load-review`、`/ux-readability-review`、`/ux-state-review`、`/ux-structure-review` の 4 つのスキルを使い、その規則で設計と実装を点検する。
スキルを呼び出せない環境では、`~/.claude/skills/<スキル名>/SKILL.md` を読んで同じ規則を適用する。

## 仕様の正本

- 要件、構成、各機能の仕様、配布と更新の仕組み：[docs/design.md](docs/design.md)
- 実装の順序と Issue の分け方：[docs/implementation-plan.md](docs/implementation-plan.md)

仕様を変える変更では、同じ差分で docs/design.md を更新する。
UI の文言は日本語で、表示名は「ScreenRecorder」で統一する。

## CLAUDE.md と AGENTS.md の同期

`CLAUDE.md` と `AGENTS.md` は同一内容を保つ。
どちらか一方を変更したら、必ずもう一方にも同じ変更を反映する。

## ビルドとテスト

```powershell
dotnet build ScreenRecorder.slnx
dotnet test ScreenRecorder.slnx
```

`tools/` の Node スクリプトを触ったときは `npm run test:tools` も実行する。
ビルド用の bat は `build-debug.bat`、`build-release.bat`、`build-package.bat`(配布)である。

## リモートセッション時の作業について
この節は、~/.claude 配下(グローバル CLAUDE.md、スキル、エージェント定義、codex-agent.sh)を読めないクラウド実行のための代替である。Claude Code のローカル実行では `~/.claude/CLAUDE.md` の規則に従う。

### モデル役割分担(メインセッションとサブエージェント)
メインセッションは設計、監査、レビューに専念し、実装はサブエージェント(Agent ツール)に切り出すことを基本とする。ただし、実装難易度が特に高い箇所はメインセッションが直接実装してよい。
- サブエージェントへの依頼文には、目的、変更対象、完了条件、止まって報告する条件、検証方法を書く。

### AI 相互レビュー(ai-cross-review)
相互レビューの手順の正本は [docs/cross-review.md](docs/cross-review.md)(vendored)と、グローバル SKILL `~/.claude/skills/cross-review/SKILL.md`(無い環境では vendored の [.claude/skills/cross-review/SKILL.md](.claude/skills/cross-review/SKILL.md))である。
このリポジトリ固有のレビュー観点は `.cross-review.md` にある。
このリポジトリではレビューを一律 Codex 側で行う。

- 検証コマンド: `dotnet build ScreenRecorder.slnx` と `dotnet test ScreenRecorder.slnx`。`tools/` の Node スクリプトを触ったときは `npm run test:tools` も実行する。
- 基盤の更新: `npm run sync:cross-review`(検査は `npm run sync:cross-review:check`)で上流から取り込む。
- レビューの起点: 実装を一区切りしたら 3 択を `AskUserQuestion` で提示する(詳細は SKILL)。指摘、対応、妥当性確認は PR コメントに残し、本文は `.cross-review/round-<N>-triage.md` を書いて `node tools/cross-review.js comment --round <N>` で生成する。
