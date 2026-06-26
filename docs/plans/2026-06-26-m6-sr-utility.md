# M6 SR利便機能＆PC-Talker精緻化 — 実装計画

**Goal:** 照会ホットキー（現在位置・文字情報）・行ジャンプ・挿入/上書き読み上げを、Core 純ロジック＋既存 SR 通知で実装。座標APIは見送り。

**Architecture:** `yEdit.Core.Reading`（`CharacterDescriber`/`PositionFormatter`＝純関数・xUnit）＋ `yEdit.App`（`Announcer`＝Label の RaiseAutomationNotification、`GoToLineDialog`、MainForm のホットキー）。設計根拠: `docs/plans/2026-06-26-m6-sr-utility-design.md`。

**Tech Stack:** .NET 9 / C# / WinForms / Scintilla5.NET / xUnit。

## 前提・規約
- 作業ブランチ: `feature/m6-sr-utility`。各タスク末にコミット（日本語・末尾 Co-Authored-By）。
- ビルド 0 警告維持。`dotnet test tests/yEdit.Core.Tests`。
- 新規 SCI_* 呼び出し・別スレッド処理を作らない（既存 SnapshotText/選択から取得し Core で整形）。

---

## Task 1: Core — CharacterDescriber
**Files:** `src/yEdit.Core/Reading/CharacterDescriber.cs`
`Describe(codePoint)` と `DescribeAt(text, index)`（サロゲート考慮）。設計 §3 の分類。ビルド確認 → コミット。

## Task 2: Core — PositionFormatter
**Files:** `src/yEdit.Core/Reading/PositionFormatter.cs`
`Format(line,totalLines,column,totalChars,selectionLength)`。ビルド確認 → コミット。

## Task 3: Core テスト
**Files:** `tests/yEdit.Core.Tests/Reading/CharacterDescriberTests.cs`, `tests/yEdit.Core.Tests/Reading/PositionFormatterTests.cs`
設計 §5 の各観点。`dotnet test` 緑 → コミット。

## Task 4: App — Announcer
**Files:** `src/yEdit.App/Announcer.cs`
Label をラップし `Say(message)`＝視覚表示＋`RaiseAutomationNotification`。ビルド確認 → コミット。

## Task 5: App — GoToLineDialog
**Files:** `src/yEdit.App/GoToLineDialog.cs`
現在行/最大行を受け、NumericUpDown で行番号入力・OK/Cancel・`LineNumber` 公開。ビルド確認 → コミット。

## Task 6: MainForm 配線
**Files:** `src/yEdit.App/MainForm.cs`
- 底部に Announcer 用 Label を追加（`AccessibleName="通知"`）。`_announcer` 生成。
- `ProcessCmdKey`: `Ctrl+Alt+P`/`Ctrl+Alt+I`/`Ctrl+G`/`Insert`。
- `AnnouncePosition`/`AnnounceCharInfo`/`GoToLine`/`ToggleOvertype`。
- 「読み上げ(&R)」メニュー（表示のみショートカット）。`OpenAndSelect`（grep ジャンプ）末に「ファイル名 行 N」を通知（M4 申し送り対応）。
ビルド 0 警告・全テスト緑 → コミット。

## Task 7: マージ前レビュー
別エージェント 5 レンズ（正確性/SR/スレッド安全/Unicode・文字分類/統合）＋敵対的検証。指摘反映 → main へ no-ff マージ。
