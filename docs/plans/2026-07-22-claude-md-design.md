# CLAUDE.md 新設 設計書

- **作成日**: 2026-07-22
- **対象**: リポジトリルートに `CLAUDE.md` を新設
- **区分**: ドキュメント整備(コード無変更・挙動不変)
- **前提**: README.md が環境構築/ビルド/アーキテクチャを既にカバー済み。`docs/lint-format-setup.md` が Lint/Format 詳細をカバー済み

## 0. 目的と決定事項サマリ

**目的**: これまでセッションメモリーにのみ蓄積されていた本プロジェクトの開発プロセスを明文化し、
誰もが同じプロセスで Claude Code による開発を行えるようにする。

**主要判断(ユーザー確定済み)**:

| 論点 | 決定 |
|------|------|
| スコープ | **プロセス規範に特化**。環境構築・ビルド・アーキテクチャは README へリンク(重複させない) |
| ブランチ統合フロー | **GitHub PR フローを「正」とする**(従来のローカル no-ff マージ規範から移行) |
| superpowers プラグイン | **手順を自然言語で規範化+スキル対応表を併記**(プラグインは推奨扱い・必須にしない) |
| 構成 | **単一 CLAUDE.md・工程順**。目安 150〜200 行。詳細は docs へリンク |

**非対象(scope out)**:
- 一時状態の記載(未 push commit 数・open PR 番号 等) — 陳腐化するため書かない
- テスト数などの具体値 — 「テスト実行結果が正」とだけ書く
- 歴史的経緯の詳細(PC-Talker 調査史 等) — README 補足と docs/plans に既存
- README / docs/lint-format-setup.md の内容移管 — リンク参照に留める

## 1. 文書構成(節立てと各節の内容)

### 前文 — 目的と役割分担

- この文書は「yEdit を Claude Code で開発する際のプロセス規範」であることを宣言
- 役割分担: 環境構築/ビルド/アーキテクチャ=README、Lint/Format 詳細=`docs/lint-format-setup.md`
- **一時状態(未 push・open PR 等)はこの文書に書かない**原則を宣言

### §1 言語規範

- ユーザーとの会話・Issue/PR の description とコメント・コミットメッセージ本文は**日本語**
- コード・識別子・技術用語は英語のまま

### §2 プロジェクト原則(不変条件)

- **晴眼・弱視ユーザーも第一級**。「SR の読み上げに効くか」を機能価値のゲート条件にしない
- **a11y 鉄則**:
  - UIA プロバイダは RPC スレッドからエディタ内部に触らない(UI スレッドのスナップショットで即答)
  - SR の実発声は自動検証不能=L5 実機検証でのみ確認できる
  - 現行の読み上げターゲットは NVDA 単一(PC-Talker 廃止の経緯は README 補足参照)
- **リファクタ・基盤変更は挙動不変が原則**。意図的な挙動変更・計画からの逸脱は必ず文書化

### §3 開発フロー(工程順)

1. ブレインストーミング(要件・設計の対話的確定)
2. 設計書 `docs/plans/YYYY-MM-DD-<topic>-design.md` 作成・ユーザー承認
3. 実装計画 `docs/plans/YYYY-MM-DD-<topic>.md` 作成
4. タスク分割実装(各 Task = 実装 → 仕様レビュー → コード品質レビューの 2 段)
5. 最終ブランチレビュー → fixup
6. 品質ゲート(§6) → PR(§7)

- superpowers スキル対応表: brainstorming / writing-plans / subagent-driven-development /
  requesting-code-review / receiving-code-review / finishing-a-development-branch
- 小規模変更(単一ファイル・数十行規模)は 1 implementer 統合・単一 commit へ簡略化可の基準を明記

### §4 レビュー標準

- マージ前に**別エージェントによるコードレビュー必須**
- 指摘は鵜呑みにせず技術検証してから対応(妥当でない指摘は理由付きで却下してよい)
- テストレビューは**ミューテーション検証**(実装行を一時変異 → テスト赤を確認 → 復元)を常用
- 指摘対応の 3 択: fixup commit / PR description に fold / 理由付き却下
- レビュー由来の修正は元 commit を残して**別 fixup commit**(履歴保存と正確性の両立)
- 蓄積された教訓:
  - no-change テストは非既定位置から検証開始する
  - partial-selection テストは prefix/suffix を除外する fixture にする
  - Cancel/Guard 系テストは assertion 前提と guard 発火条件を一致させる

### §5 テスト戦略

- 5 層ピラミッド: L1 Core.Tests / L2 Editor.Tests / L3 App.Tests(自動)、
  L4 性能ゲート(bench・手動)、**L5 実機 SR 手動検証(最終ゲート・自動化しない)**
- L5 要否判定: SR 経路(`yEdit.Accessibility` / `EditorControl` の UIA 部 / Speech 系)に触れたら必須。
  SR 経路不変の挙動不変リファクタは省略可
- a11y 関連変更のマージ前・リリース前は `tools/sr-regression.ps1` を手動実行
  (限界: UIA 応答の検証まで。実発声は検出できない=L5 は省略しない)
- テスト数の「正」はテストプロジェクトの実行結果(文書に数値を固定しない)

### §6 品質ゲート

- マージ前に `tools/pre-merge-check.ps1` を実行し **EXIT 0 必須**(CI と同一ゲート)
- **0 warning 維持**(`-warnaserror` 稼働中)
- CI とローカルゲートの**ステップ名対称性**を維持(ローカル/CI の失敗ログを同じキーワードで探せる)
- pre-commit フック(Husky.Net + CSharpier)を `--no-verify` で飛ばさない

### §7 Git / PR 規範

- main から `feature/<topic>` ブランチ → pre-merge-check → push → **GitHub PR** → マージ
- PR は日本語 description。レビュー経緯と申し送りを記載
- コミットメッセージ: `feat/fix/docs/test/refactor/chore(scope):` 形式+日本語本文
- 長期・大規模改造はブランチに閉じて完成後に統合(main を段階的に変えない)。迷ったら統合前に確認

### §8 ドキュメント規範

- `docs/plans/` の日付付き文書は**策定時スナップショット=後日書き換えない**
  (実装時の精密化・実施記録の追記のみ可)
- 「現在」を語る docs(README / CLAUDE.md / docs/lint-format-setup.md)のみ同期更新の対象
- 申し送り(follow-up)は設計書の §申し送りへ記録し、将来タスク化して回収
- `説明書/yEdit説明書.md` は**ユーザー編集版が正**=勝手に改稿しない

### §9 リリース・セキュリティ

- タグ `v*` push でリリース CI が起動(手順詳細は README)。表示バージョン更新 commit の慣行
- セキュリティ修正も §3 と同一プロセス(設計書 → タスク分割 → レビュー → PR)
- 公開リポジトリでの脆弱性修正は GitHub Security Advisory の要否を検討

### §10 参照先一覧+環境ノート

- 参照先: README.md / docs/lint-format-setup.md / docs/plans/ / tools/
- Claude 向け環境ノート(少数精鋭):
  - ログをファイル出力する際は UTF-8 を明示(既定 UTF-16 LE は検索ツールが読めない)
  - 警告を集計したいビルドでは `-p:TreatWarningsAsErrors=false` を明示
  - ユーザーが起動する ps1 は BOM 付き UTF-8(WinPS 5.1 日本語ロケール対策)

## 2. 記述量の配分方針

- 全体 150〜200 行。各規範は「簡潔な規範文(1〜3 行)+必要なら理由 1 行+詳細リンク」
- 表は superpowers スキル対応表のみ。それ以外は見出し+箇条書きで構成

## 3. 情報源(メモリー → 節の対応)

| 節 | 主な情報源(セッションメモリー) |
|----|------|
| §1 | ユーザーのグローバル CLAUDE.md(言語規範をプロジェクトへ昇格) |
| §2 | yedit-sighted-users-first-class / scintilla-uia-architecture(鉄則の現行有効部) / pctalker-removal |
| §3 | production-build-started / test-strategy / lint-format-adoption-progress(SDD 実績) |
| §4 | review-by-separate-agent / test-strategy(ミューテーション検証・教訓) / lint-format(fixup・fold 3 択) |
| §5 | test-strategy(5 層・L5 要否・sr-regression) |
| §6 | phase-work-git-flow(pre-merge-check 必須) / lint-format(ステップ名対称性) |
| §7 | phase-work-git-flow(ブランチ運用・大規模改造)+ 直近 main の PR 実態(#17〜#19) |
| §8 | lint-format(snapshot 原則) / production-build-started(説明書はユーザー版が正) |
| §9 | release-approval-gate / security-md-h1(Advisory 検討) |
| §10 | lint-format(UTF-8・TreatWarningsAsErrors)/ test-strategy(BOM 付き ps1) |

## 4. 申し送り

- `docs/lint-format-setup.md` への back-link は CLAUDE.md §10 で対応
  (lint-format 導入 PR5 の申し送り「CLAUDE.md 新設時に back-link か内容移管を判断」→ **back-link を採用**・移管しない)
- 従来メモリー規範「main はローカル no-ff マージ・push は明示依頼時のみ」は本設計で **GitHub PR フローへ更新**。
  セッションメモリー側(phase-work-git-flow)の追随更新は CLAUDE.md マージ後に実施
