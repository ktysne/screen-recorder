# プロジェクトガイドライン

このファイルは AI コーディングエージェント(Claude Code、Codex など)向けの共通指示を記載する。

## 言語

このプロジェクトでは日本語を共通言語とする。
セッション中の出力、コミットメッセージ、Pull Request のタイトルと本文、Issue、レビューコメント、コードのコメント、ドキュメントを日本語で書く。
コード中の識別子は英語でよい。

例外として、`.bat` など cmd.exe が解釈するスクリプトのコメントと表示メッセージは ASCII(英語)で書く。
cmd のバッチパーサは UTF-8 の日本語をコマンドとして誤解釈し、実行自体が失敗するためである。

利用者が目にする日本語の文書(docs 配下、README、manual.html、UI 文言)を書くときは、`/japanese-tech-writing` スキルの規範に従う。

## CLAUDE.md と AGENTS.md の同期

`CLAUDE.md` と `AGENTS.md` は同一内容を保つ。
どちらか一方を変更したら、もう一方にも同じ変更を反映する。

## 設計の正本

- [docs/design.md](docs/design.md):要件、構成、各機能の方式
- [docs/implementation-plan.md](docs/implementation-plan.md):実装の分割と進捗

仕様に関わる変更では、先にこれらを読み、変更後は資料も追随させる。

## ビルドとテスト

```powershell
dotnet build ScreenRecorder.slnx
dotnet test ScreenRecorder.slnx
```

`tests/ScreenRecorder.Capture.Tests` は GPU と Media Foundation を使う結合テストで、実機の Windows でだけ動く。

## リモートセッション時の作業について
この節は、~/.claude 配下(グローバル CLAUDE.md、スキル、エージェント定義、codex-agent.sh)を読めないクラウド実行のための代替である。Claude Code のローカル実行では `~/.claude/CLAUDE.md` の規則に従う。

### モデル役割分担（メインセッションとサブエージェント）
メインセッションは設計・監査・レビューに専念し、実装は下位モデルのサブエージェント（Agent ツール）に切り出すことを基本とする。ただし、実装難易度が特に高い箇所はメインセッションが直接実装してよい。
- サブエージェントへの依頼文には、目的、変更対象、完了条件、止まって報告する条件、検証方法を書く。

### AI 相互レビュー（ai-cross-review）
相互レビューの手順の正本は [docs/cross-review.md](docs/cross-review.md)（vendored）と、グローバル SKILL `~/.claude/skills/cross-review/SKILL.md`（無い環境では vendored の [.claude/skills/cross-review/SKILL.md](.claude/skills/cross-review/SKILL.md)）である。
このリポジトリ固有のレビュー観点は `.cross-review.md` にある。

- レビュアー: このリポジトリのレビューは、実装者のベンダーにかかわらず一律 Codex で行う(`npm run review:codex`)。Codex が使えないときだけ、CLI のフォールバック(終了コード 75)に従う。
- 検証コマンド: `dotnet build ScreenRecorder.slnx` と `dotnet test ScreenRecorder.slnx`。
- 基盤の更新: `npm run sync:cross-review`(検査は `npm run sync:cross-review:check`)で上流から取り込む。
- 指摘、対応、妥当性確認は PR コメントに残し、本文は `.cross-review/` の `round-<N>-triage.md` を書いて `node tools/cross-review.js comment --round <N>` で生成する。
