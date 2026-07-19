# セキュリティ強化(HIGH 6 件)設計書 — 2026-07-19

## 背景

SECURITY.md が挙げている 4 攻撃面(Markdown プレビュー / CSV・テキスト読込 / バックアップ復元 / UIA プロバイダ)を並列エージェントで監査した結果、HIGH 相当の脆弱性 6 件を検出した。本設計書はそれらを 1 セキュリティリリース(`v0.2.0-sec` 想定)にまとめて対応するための全体設計を定める。

MEDIUM 8 件 / LOW 11 件は次リリース以降の別サイクルで扱う(スコープ外)。

## 対応対象の HIGH 6 件

| # | 系統 | 概要 | 主要修正箇所 |
|---|---|---|---|
| H-1 | バックアップ | `BackupRecord.Id` パストラバーサル → 任意ファイル書込・削除 | `Core/Backup/BackupStore.cs:27,42-44`, `App/BackupCoordinator.cs:162,189,294-296,326` |
| H-2 | バックアップ | `OriginalPath` 任意ファイル上書き | `App/FileController.cs:396`, `App/RestoreDialog.cs:57` |
| H-3 | CSV/テキスト | 巨大ファイル `InvalidOperationException` の未捕捉 → プロセス全体クラッシュ | `Core/Buffer/TextBufferBuilder.cs:81-82`, `App/FileController.cs:186-192` |
| H-4 | CSV/テキスト | `CsvParser` の無制限 sb/List → OOM クラッシュ | `Core/Csv/CsvParser.cs:33-121` |
| H-5 | Markdown | Markdig raw HTML 素通し(現状 CSP に単層依存) | `Core/Text/MarkdownRenderer.cs:15-17` |
| H-6 | 同期ロード DoS | 応答しない UNC で UI スレッド 60 秒凍結 | `App/FileController.cs:143-197`, `Core/Text/TextFileService.cs:52,149` |

## 全体方針

### 主要設計判断(ユーザ承認済)

1. **バックアップ復元の防御方針**: 白リスト検証(`Id` は GUID N 形式、`OriginalPath` は正規化後にシステムディレクトリ拒否)。UX は現状維持。
2. **スコープ**: HIGH 6 件のみ今リリース。MEDIUM/LOW は次リリース以降。
3. **PR 分割**: 系統別 5 PR(SDD で個別 no-ff マージ)。
4. **同期ロード DoS 対応**: リモートパス事前判定(UNC のみ)+ 短タイムアウト(5 秒)。マップドドライブは MEDIUM 送り。
5. **Markdown 対応範囲**: `DisableHtml()` の 1 行のみ(CSP 追加ディレクティブ・Navigation ハンドラは MEDIUM 送り)。

### 挙動不変性

全 5 PR で「合法な通常運用は一切変わらず、攻撃的入力のみ塞ぐ」を原則とする。

- 正常な数十 MB CSV / 通常 GUID の記録 / ローカルパス / マークダウン文法出力: 変化なし
- 攻撃的入力: LoadAll 段階で弾く / Untitled にフォールバック / エラープロンプト表示

## PR 分割詳細

### PR-1: バックアップ Id/OriginalPath 白リスト検証(HIGH-1 + HIGH-2)

**新規ファイル**:
- `Core/Backup/BackupIdValidator.cs` — `Guid.TryParseExact(id, "N", out _)` の薄いラッパ
- `Core/Backup/OriginalPathValidator.cs` — `Path.GetFullPath` 後にシステム系ルート(WINDIR/ProgramFiles/ProgramData/System/SystemX86)を先頭一致で拒否

**修正ファイル**:
- `Core/Backup/BackupStore.cs:42-44` — deserialize 直後に `BackupIdValidator.IsValid(rec.Id)` チェック → 攻撃レコードは復元ダイアログに現れない
- `App/FileController.cs:392-429` — `RestoreFromBackup` で `OriginalPath` を検証。Rejected の場合は Untitled にフォールバック + `_prompt.Warn` 表示
- `App/RestoreDialog.cs:57` — 表示改善(フルパスの 2 段表示・中央省略)
- `App/BackupCoordinator.cs:171,197` — トレースログの ID を `SafeIdForLog` で無害化

**テスト**:
- Core.Tests: `BackupIdValidatorTests`(6 件)、`OriginalPathValidatorTests`(8 件)、`BackupStoreTests` に攻撃 Id ケース追加(1 件)
- App.Tests: `FileControllerRestoreTests` に 3-5 件(正常/攻撃/Untitled 各パス)

**実機ドライブ(L5・必須)**:
- 正常 `.txt` の復元→上書き保存
- 攻撃 Id の JSON を手動配置し起動 → ダイアログ非表示を目視確認
- 攻撃 OriginalPath 版 JSON を配置 → 復元後は無題タブ + 警告表示

**差分予測**: 200-250 行(テスト込みで 400-500 行)

---

### PR-2: CSV/テキスト Critical(HIGH-3)

**新規ファイル**:
- `Core/Text/DocumentTooLargeException.cs` — 専用例外型(int.MaxValue バイト上限用)

**修正ファイル**:
- `Core/Buffer/TextBufferBuilder.cs:81-82` — throw する例外を `DocumentTooLargeException` に変更 + テスト用 `internal` ctor(小さい上限を注入可能に)
- `App/FileController.cs:186-192` — catch 節に `DocumentTooLargeException` を追加

**テスト**:
- Core.Tests: `TextBufferBuilderTests` に 3-5 件(境界値・throw 型確認・catch (Exception) 経由の受信確認)
- App.Tests: 0-1 件(seam の有無で決定)

**実機ドライブ**: 不要

**差分予測**: 40-60 行(テスト込みで 100-150 行)

---

### PR-3: CSV OOM 防御(HIGH-4)

**修正ファイル**:
- `Core/Csv/CsvParser.cs` — 定数 3 個追加(`MaxFieldChars=8M`, `MaxTotalCells=10M`, `MaxTotalRows=1M`)+ ホットループに 3 箇所の上限チェック + `internal` オーバーロード `ParseCore(TextReader, ParseLimits)` でテスト用小さい上限を注入可能に

**テスト**:
- Core.Tests: `CsvParserTests` に 5-6 件(単一フィールド超過・総セル超過・総行超過・境界・正常回帰)
- App.Tests: `CsvControllerTests` に 1 件(攻撃テキストで `TryEnterMode` が false + `ParseError` 発話)

**実機ドライブ**: 不要

**差分予測**: 40-70 行(テスト込みで 150-200 行)

---

### PR-4: Markdown raw HTML(HIGH-5)

**修正ファイル**:
- `Core/Text/MarkdownRenderer.cs:15-17` — `.DisableHtml()` を Markdig パイプラインに追加(1 行)

**テスト**:
- Core.Tests: `MarkdownRendererTests` に 5 件(script/iframe/onclick escape 確認・table/code block 非退行)

**実機ドライブ**: 不要(release smoke で通常マークダウンプレビュー確認のみ)

**差分予測**: 1 行 + テスト 60-80 行

---

### PR-5: 同期ロード DoS(HIGH-6)

**新規ファイル**:
- `Core/IO/UncPathDetector.cs` — 純粋述語(`IsUnc(path)`)
- `App/Abstractions/IReachabilityProbe.cs` — DI シーム
- `App/FileReachabilityProbe.cs` — 本番実装(`Task.Run` + `task.Wait(timeout)`)

**修正ファイル**:
- `App/FileController.cs:143-197` — `LoadInto` 冒頭にプローブ挿入(UNC 判定 → 5 秒プローブ → 失敗時 error prompt)
- `App/Program.cs` — DI 登録 2 行追加

**テスト**:
- Core.Tests: `UncPathDetectorTests` 10 件
- App.Tests: `FileControllerLoadTests` に 3 件(unreachable / local / reachable)

**実機ドライブ**: 部分的に必要(存在しない UNC / ローカル / 実在リモート の 3 パスで挙動確認)

**差分予測**: 60-80 行(テスト込みで 200 行前後)

## マージ順序と依存

```
PR-4 (Markdown, 1 行) ──┐
PR-2 (CSV Critical, 3 行)┼─→ (main へ順次 no-ff マージ)─→ PR-1 (Backup) ──→ PR-3 (CSV OOM) ──→ PR-5 (Sync DoS)
                        │                                (最重要 & 監視最厚)      (Core中心)         (DI変更)
```

**順序の意図**:
- PR-4/PR-2 を先行(1〜3 行の極小・レビュー負担ゼロ)
- PR-1 を中盤(最重要・レビュー最厚・L5 実機必須)
- PR-5 を最後(FileController への DI 追加・他 PR とのコンフリクトを最小化)

**コンフリクト予測**: PR-1/PR-2/PR-5 が `FileController.cs` を触るが領域は独立(catch 節/ RestoreFromBackup / LoadInto 冒頭)。各 PR 開始前に `git fetch && git rebase main` を必ず走らせる。

## 共通のマージ前ゲート

1. `tools/pre-merge-check.ps1`(既存)完全緑
2. `dotnet csharpier check`(既存 CI)
3. 新規テストが CI で緑
4. 変更ファイルの CSharpier フォーマット済み
5. `code-reviewer` subagent によるレビュー通過
6. L5 実機ドライブ: PR-1 は必須、PR-5 は部分実施、他 3 PR は不要

## テスト戦略

**追加テスト予測**: ~49 件(Core ~40 + App ~9)。現状 1085 tests → 約 1134 tests。0 警告維持は必須。

**共通原則**:
- Positive path(既存挙動不変)を最低 1-2 件でリグレッションガード
- Attack path(HIGH-1〜6 の PoC を単体で再現)を主要テスト
- Boundary(上限ちょうど・null・空文字列)を境界値カバー
- Test seam は `internal` + 既存 `InternalsVisibleTo` 設定のみ(新規 friend assembly は増やさない)
- App 層静的依存(`TextFileService` 等)は `FileController` レベルの injection シームで対応

## リリース工程

1. 全 5 PR main 完了 → `origin/main` に push
2. 手動で `v0.2.0-sec` タグを打つ(既存 release.yml が `v*` トリガ)
3. release CI 自動実行 → zip + SHA256 → GitHub Releases 公開
4. GitHub Security Advisory 5 件公開(1 系統 1 advisory)
5. `SECURITY.md`「対応方針」節に修正済みリリースを追記

## ロールバック計画

各 PR は独立した no-ff マージコミット → 万一リリース後に退行判明で個別 `git revert -m 1 <merge-commit>` で剥がして再リリース。PR-1/PR-5 は FileController.cs で近接するため、revert 時のマージコンフリクトは手動で解消する新規 revert PR を作る。

## 実施しない事項(明示的除外)

- MEDIUM 8 件 + LOW 11 件(次リリース以降)
- CSV/UIA/Markdown の SR 実機検証(SR 経路不変のため)
- CVE 割当(個人プロジェクトのため任意・希望時に別途)

## メモリ更新方針

リリース完了後:
- `memory/security-hardening-v020-complete.md` を新規作成(HIGH 6 件全解消・PR/コミット SHA・実装期間・L5 結果・GHSA 番号)
- `MEMORY.md` の index に 1 行追加

## 参照

- SECURITY.md — 対象攻撃面の定義
- 監査時の並列エージェント結果(4 系統・2026-07-19 実施)
- `[[refactor-separation-phase3-complete]]` — SDD ワークフローの参考
- `tools/pre-merge-check.ps1` — ローカルゲート
