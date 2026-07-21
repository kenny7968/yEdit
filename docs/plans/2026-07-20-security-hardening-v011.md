# セキュリティ強化 v0.11 実装計画 — PR-E CSV/Text 完備

**設計書**: [`2026-07-20-security-hardening-v011-design.md`](./2026-07-20-security-hardening-v011-design.md)
**対象 PR**: PR-E (CSV-L-1 / CSV-L-4 / CSV-L-5 / CSV-L-6 / CSV-L-7 / CSV-L-8 / CSV-L-10 の 7 件)
**ブランチ**: `feature/security-pr-e-csv-text`
**マージ方針**: `main` へ GitHub PR 経由 (PR-D と同じ)
**開発プロセス**: SDD (Subagent-Driven Development・implementer subagent + 2 段レビュー)

## 前提と原則

- 既存挙動を壊さない (「攻撃入力を白リスト/上限/専用例外/サニタイズで受け止め、正常運用の挙動は一切変えない」)。
- TDD (test-driven-development): 各 Task で失敗テスト → 実装 → 緑 → refactor。
- Release ビルドは 0 warnings 維持 (`dotnet build ... -warnaserror`)。
- コミット前に `tools/pre-merge-check.ps1` 全緑を確認。CSharpier 未整形はゲート NG。
- 各 Task ごとに atomic commit。`test:` → `feat:` の順が原則、fixup は `refactor:` prefix。

## Task 一覧

| # | Audit ID | 変更対象 | 想定 tests | L5 実機 |
|---|----------|----------|-----------|---------|
| 1 | CSV-L-1  | `CsvWriter` (formula injection) | +8 | 不要 |
| 2 | CSV-L-4  | `RecentFilesList` + `SettingsStore` (deserialize 上限) | +3 | 不要 |
| 3 | CSV-L-5  | `FileController` (パスプロンプト sanitize) | +4 | 不要 |
| 4 | CSV-L-6  | `EncodingDetector` (UTF-8 BOM 優先固定 tests のみ) | +2 | 不要 |
| 5 | CSV-L-7  | `AtomicFile` + 新 `IO/ReparsePointCheck.cs` (symlink follow 抑止) | +3 | 不要 |
| 6 | CSV-L-8  | `PathKey` (例外時 empty fallback) | +2 | 不要 |
| 7 | CSV-L-10 | `TextBufferBuilder` + `TextBuffer` (MaxTotalBytes 512 MB 統一) | +3 | 不要 |

合計 +25 tests 想定 (Core 側)。App 側 (Task 3) は追加 3 tests 前後。

---

## Task 1 — CSV-L-1 CsvWriter formula injection

**目的**: `=`, `+`, `-`, `@`, `\t`, `\r` を先頭に持つセルには `'` (apostrophe) を先頭付加し、Excel / Sheets の formula 実行を阻止する (OWASP CSV Injection 対策)。

### 変更ファイル

- `src/yEdit.Core/Csv/CsvWriter.cs`
- `tests/yEdit.Core.Tests/Csv/CsvWriterTests.cs`

### 実装手順

1. **Red**: `CsvWriterTests` に以下 8 tests 追加 (先に失敗するはず):
   - 6 chars × prefix (`=1+1`, `+cmd`, `-2+3`, `@SUM`, `\tX`, `\rX`) がそれぞれ apostrophe 前置になる。
   - `Plain_value_is_unchanged` は継続 (`abc` → `abc`)。
   - `Formula_char_in_middle_is_untouched` (`x=1` は無変更・実運用「値の中に=」を守る)。
2. **Green**: `EscapeField` の頭で `value.Length > 0` かつ `value[0] ∈ FormulaPrefixChars` なら `"'" + value` を先頭に付ける。既存の quote ロジックの前で分岐。
3. `internal const string FormulaPrefixChars = "=+-@\t\r";` を宣言。テストから参照可能に。
4. **境界**: 空セル (`""`) は無変更 (index 0 アクセスなし)。apostrophe を付けた結果カンマ等を含むなら既存 quote 分岐が包む (副作用不要)。
5. SECURITY.md への追記は最終 Task の直前にまとめる。

### 完了条件

- `dotnet test tests/yEdit.Core.Tests` 全緑。
- `CsvWriter` の既存 Roundtrip tests が失敗しない (`CsvParser.Parse` は apostrophe 前置を単なるプレーン文字として扱うため往復挙動は変わる → ここは「CSV Injection 対策のトレードオフ」として既存 Roundtrip test の期待値を必要に応じて更新)。

### 注意

- Roundtrip テストで `"=SUM(1)"` のようなケースは今後「先頭 apostrophe が保存される」= 復号側は `'=SUM(1)` を返す。既存 Roundtrip tests に formula 前置ケースは無い (`abc`, `a,b,c`, `he said "hi"`, `line1\nline2`, `comma, and "quote"`) ので影響なし。

---

## Task 2 — CSV-L-4 RecentFilesList deserialize 上限

**目的**: 攻撃 settings.json (10 万件の `RecentFiles`) を投入されても `SettingsStore.Load` が O(MaxItems) で終わる。

### 変更ファイル

- `src/yEdit.Core/Text/RecentFilesList.cs` (const 定義 + Truncate ヘルパ追加)
- `src/yEdit.Core/Settings/SettingsStore.cs` (`Normalize` で Truncate 呼出)
- `src/yEdit.App/FileController.cs` (private const `MaxRecent = 10` を `RecentFilesList.MaxItems` へ差替)
- `tests/yEdit.Core.Tests/Text/RecentFilesListTests.cs`
- `tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs` (存在するか要確認・なければ小規模追加)

### 実装手順

1. **Red**: `RecentFilesListTests` に以下 tests 追加:
   - `Truncate_caps_to_max_items` — 10 万要素の list を `Truncate` すると `MaxItems` 件になる。
   - `Truncate_short_list_is_unchanged` — 5 件は 5 件のまま。
   - `MaxItems_is_10` — 定数値を明文化 (回帰保護)。
2. **Green**:
   - `RecentFilesList` に `public const int MaxItems = 10;` を追加。
   - `public static List<string> Truncate(IEnumerable<string> source)` を追加。実装は `source.Take(MaxItems).ToList()`。
   - `SettingsStore.Normalize` の `s.RecentFiles ??= def.RecentFiles;` の直後に `s.RecentFiles = RecentFilesList.Truncate(s.RecentFiles);` を追加。
   - `FileController.MaxRecent` を削除し `RecentFilesList.MaxItems` を参照へ差替。
3. **境界**: null / 空リスト → 空リストのまま (Truncate ヘルパで LINQ が処理)。既存 Add の max パラメータは残す (別責務 = per-call 上限)。

### 完了条件

- `SettingsStore.Load` に 10 万件の `RecentFiles` を持つ JSON を投入した場合、返却リストが 10 件 (テスト or 手動検証)。
- 既存 `RecentFilesListTests` 全緑。

---

## Task 3 — CSV-L-5 FileController パスプロンプト sanitize

**目的**: `_prompt.Error/Warn` に生の path を載せると RLO (U+202E) 等でスプーフィングされる。全 6 箇所を `SanitizeForDisplay.OneLine(path, 200)` でラップ。

### 変更ファイル

- `src/yEdit.App/FileController.cs`
- `tests/yEdit.App.Tests/FileControllerTests.cs`

### 対象箇所 (Grep で確定)

`FileController.cs` の `_prompt.Error/Warn` を洗い出し (line 番号は今後の編集で変わるため grep 再走査):

1. `LoadInto` 内 `_prompt.Warn` (置換文字警告 — path 参照ない可能性・要検討)
2. `LoadInto` 内 `_prompt.Error($"開けませんでした: {ex.Message}", "エラー")` — `ex.Message` にファイル名混入の可能性
3. `SaveAsDocument` 内 `_prompt.Warn("ファイル名を指定してください。", ...)` — path 参照なし・変更不要
4. `WriteToPath` 内 `_prompt.Error($"保存できませんでした: {ex.Message}", ...)` — `ex.Message` 対応
5. `RestoreFromBackup` 内 `_prompt.Warn("... 元パス: {rec.OriginalPath}", ...)` — 明確に path 混入
6. `TryProbeReachability` 内 `_prompt.Error($"ネットワークパスに到達できません: {path}", ...)` — 明確に path 混入

### 実装手順

1. **Red**: `FileControllerTests` に以下 tests 追加 (Fake `IUserPrompt` で prompt を捕捉):
   - `Restore_sanitizes_original_path_with_rlo` — `OriginalPath` に `"a‮b.txt"` を含めた `BackupRecord` を復元 → `_prompt.Warn` の text に RLO が含まれない。
   - `Reachability_error_sanitizes_path_with_newlines` — CRLF を含む remote path → prompt の text に CRLF が含まれない。
   - `Reachability_error_truncates_long_path` — 500 文字パス → 200 + "…" で切詰め。
   - `LoadInto_and_WriteToPath` のエラー経路については、`ex.Message` に RLO を含むケースの捕捉 (要検討・実装容易なら追加、難しければ scope out)。
2. **Green**: 対象 3 箇所 (5, 6 + LoadInto/WriteToPath の ex.Message) を `SanitizeForDisplay.OneLine(x, 200)` でラップ。既存 helper `SanitizeForDisplay` (Core.Text) を using する。
3. `WriteToPath` / `LoadInto` の `ex.Message` については exception message 全体を sanitize (`ex.Message` は攻撃者制御下ではないが、ファイル名を含みうる = 二次的スプーフィング防止)。
4. **注意**: `RestoreFromBackup` の warning text は「元パス: {rec.OriginalPath}」で改行区切り。`OneLine` は改行を潰すので UI 表示が 1 行連結になる。これは意図的 (SEC>UX)。

### 完了条件

- 新規 3〜4 tests 緑。
- 既存 `FileControllerTests` 全緑 (regression 無し)。

---

## Task 4 — CSV-L-6 EncodingDetector UTF-8 BOM 優先固定 tests

**目的**: 現行の「UTF-8 BOM を UtfUnknown より前で検出」invariant を回帰保護テストで固定する。**UTF-16/32 BOM の追加検出は行わない** — P6 Task 6 の「UTF-16 非対応」決定を尊重 (2026-07-20 セッションでユーザ確認済み)。

### 変更ファイル

- `tests/yEdit.Core.Tests/Text/EncodingDetectorTests.cs`
- `src/yEdit.Core/Text/EncodingDetector.cs` (Comment のみ — invariant の明文化)

### 実装手順

1. **Red/Green** (実装ゼロなので Red は「テスト無い」状態):
   - `Utf8Bom_is_detected_before_utf_unknown_would_pick_ambiguous` — UTF-8 BOM の後に UtfUnknown が誤判定しそうなバイト列を続けた入力 (例: BOM + SJIS 風バイト) が UTF-8 (65001, HasBom=true) を返すことを確認。
   - `Utf8Bom_priority_survives_regardless_of_content` — 極端に短い入力 (BOM の後に 1 バイト) も UTF-8 として確定される。
2. **Comment**: `EncodingDetector.Detect` の `// ① BOM 確定` の直下に「BOM 優先は攻撃者制御バイト列による UtfUnknown 誤判定のスプーフィング防止 (CSV-L-6 / 2026-07-20 v0.11)」旨のコメントを追加。
3. **仕様確認**: 既存 test `Utf16_le_bom_no_longer_detected_as_utf16` / `Utf16_be_bom_no_longer_detected_as_utf16` は UTF-16 非対応をロックする invariant として維持 (削除しない)。

### 完了条件

- 新規 2 tests 緑。既存 UTF-16 no-detection tests は不変。

---

## Task 5 — CSV-L-7 AtomicFile symlink follow 抑止 + ReparsePointCheck 抽出

**目的**: `AtomicFile.Write` (byte[] / Stream 両方) の `File.Replace` は reparse point (symlink / junction) を follow して権限外の実体を上書きする恐れがある。書込直前に dest の reparse point を検出して IOException で拒否。

### 変更ファイル

- `src/yEdit.Core/IO/AtomicFile.cs`
- Create: `src/yEdit.Core/IO/ReparsePointCheck.cs`
- `tests/yEdit.Core.Tests/IO/AtomicFileTests.cs` (or ReparsePointCheckTests.cs)
- Create: `tests/yEdit.Core.Tests/IO/ReparsePointCheckTests.cs` (小さい単体テスト)

### 実装手順

1. **Red**:
   - `ReparsePointCheckTests.Detects_reparse_point_on_junction` — `Directory.CreateDirectory` + junction を tests/temp に作成し `IsReparsePoint(path)` が true を返す。junction 作成は `mklink /J` 相当を `Process.Start("cmd.exe", "/c mklink /J ...")` で作るか (Windows CI OK) or Windows 固有 API。実現困難なら `[Fact(Skip = ...)]` は禁止 → `SkippableFact` 導入 or ダミー化。
   - `AtomicFileTests.Write_throws_when_destination_is_reparse_point` — 同じ手順で dest を junction にした上で `AtomicFile.Write` を呼び IOException を Assert.
   - `Write_still_works_for_non_reparse_point` — 通常ファイルへの書込が回帰しない。
2. **Green**:
   - `ReparsePointCheck.IsReparsePoint(string path)` を実装: `File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0`。missing file は false。
   - `AtomicFile.Write(string, byte[])` 冒頭に `if (ReparsePointCheck.IsReparsePoint(path)) throw new IOException($"reparse point の上書きは許可されていません: {path}");` を追加。同じく `Write(string, Action<Stream>)` にも追加。
3. **注意**: junction 作成テストは Windows 固有・CI runner は Windows なので OK (`.github/workflows/ci.yml` は windows-latest)。junction を作れない環境 (permissions 不足) では test framework の `Skip` は使わない = 前提を CI 側で担保。
4. `OriginalPathValidator` からの参照は Task 5 スコープ外 (design doc は「参照可能に」と言っているが積極的な差替は不要と判断)。

### 完了条件

- 新規 3 tests 緑。既存 `AtomicFileTests` 全緑。SECURITY.md 追記は最終 Task の前で。

---

## Task 6 — CSV-L-8 PathKey 例外時 empty fallback

**目的**: `PathKey.For` (実装名) は `Path.GetFullPath` が例外を投げると生小文字を返す。攻撃者が invalid path で衝突を起こす risk があるので、fallback を `string.Empty` に変える。

### 変更ファイル

- `src/yEdit.Core/Text/PathKey.cs`
- `tests/yEdit.Core.Tests/Text/PathKeyTests.cs`
- (影響調査) `RecentFilesList.Add` の `key` 比較箇所 — 空文字同士の比較は「同一」となり衝突する可能性

### 実装手順

1. **Red**:
   - `Create_returns_empty_on_invalid_path` — invalid path (`":::"` や `"\0"` 混入) → `string.Empty`。
   - `Existing_empty_returns_empty` — 既存の `Empty_returns_empty` は維持。
2. **Green**: `catch { full = path; }` → `catch { return string.Empty; }` に変更。
3. **副作用調査**: 現在の caller は `RecentFilesList.Add` のみ (grep で確認)。invalid path 2 件が同時に RecentFiles に載ると PathKey が両方 empty で衝突 = 片方が dedup される。これは「invalid path 2 件は 1 件にまとめる」= セキュリティ上むしろ望ましい挙動なので許容。既存 `Empty_returns_empty` テストは仕様通り。
4. **設計書との差異**: design doc は「空文字 key = 未指定扱い」と書いているが、実際は「invalid = 未指定にまとめる」で運用上支障なし。この判断を PR description に明記。

### 完了条件

- 新規 1〜2 tests 緑。既存 `PathKeyTests` 全緑。

---

## Task 7 — CSV-L-10 TextBufferBuilder MaxTotalBytes 512 MB

**目的**: `MaxTotalBytes = int.MaxValue` (≈ 2 GB) は現実的な攻撃 file (数百 MB の CSV) を通してしまう。512 MB (= UTF-16 256M chars) に引き下げ、`TextBuffer` 側と統一。

### 変更ファイル

- `src/yEdit.Core/Buffer/TextBufferBuilder.cs`
- `src/yEdit.Core/Buffer/TextBuffer.cs`
- `tests/yEdit.Core.Tests/Buffer/SizeLimitTests.cs` (存在確認・追加)
- `tests/yEdit.Core.Tests/Buffer/TextBufferBuilderTests.cs` (存在確認・追加)

### 実装手順

1. **Red**:
   - `TextBufferBuilderTests.MaxTotalBytes_default_is_512MB` — `new TextBufferBuilder().MaxTotalBytes == 512L * 1024 * 1024` を assert.
   - `TextBufferTests.MaxTotalBytes_default_is_512MB` — 同上 (`TextBuffer` は internal setter だが internal 経路で readable なら確認、無理なら reflection or scope out).
   - `SizeLimitTests` の `_ = _.MaxTotalBytes` を注入している 6 tests は挙動不変 (注入値でテストするため上限変更の影響なし = そのまま緑)。
2. **Green**:
   - `TextBufferBuilder.cs:19` の `int.MaxValue` を `512L * 1024 * 1024` へ。
   - `TextBuffer.cs:26` の `int.MaxValue` を `512L * 1024 * 1024` へ。
   - コメント (`§0-1: 既定 int.MaxValue バイト`) を「既定 512 MB (2026-07-20 v0.11 引下げ)」に更新。
3. **エラーメッセージ更新**: 例外テキスト内の `int.MaxValue バイト` を `512 MB` に。ユーザ視認性優先。

### 完了条件

- 新規 1〜2 tests 緑。既存の Buffer 系 tests は上限を注入しているためすべて緑。
- 起動時ロードが 512 MB 超のファイル → `DocumentTooLargeException` (期待挙動)。

---

## Task 8 — 最終レビュー + `pre-merge-check.ps1`

1. `dotnet csharpier format .` (Husky.NET pre-commit が拾えるが手動でも)。
2. `pwsh -File tools/pre-merge-check.ps1` を実行 → 全 step 緑を確認。
3. `code-reviewer` subagent に PR-E 全体をレビュー依頼 (docs/plans/2026-07-20-security-hardening-v011.md + 全コミット diff)。
4. 指摘があれば fixup コミット → 再 pre-merge-check。

### SECURITY.md への追記

最終コミット (or 別コミット) で以下を追記:

- CSV Writer の apostrophe prefix 対策 (Excel formula injection)
- AtomicFile の reparse point follow 抑止 (symlink 上書き)
- RecentFiles の deserialize 上限 (settings.json DoS)
- パスプロンプトの制御文字/RLO サニタイズ (スプーフィング)
- MaxTotalBytes 512 MB 引下げ (大容量 CSV DoS)

## Task 9 — push + PR 作成

1. `git push -u origin feature/security-pr-e-csv-text`
2. `gh pr create --base main --title "security(v0.11): PR-E CSV/Text 完備 (CSV-L-1/4/5/6/7/8/10)"` — body には Task 1〜7 の概要 + テスト増分 + 「CSV-L-6 は UTF-16/32 非追加 (P6 Task 6 決定尊重)」の明記。
3. PR URL をユーザに返却。

## リスク / 巻き戻し

- **Task 7 (MaxTotalBytes 512 MB)** が唯一挙動を狭める変更 — リリースノート/PR description で「512 MB 超のテキストは開けなくなる」旨を明記。
- **Task 1 (CSV Writer apostrophe)** は保存内容が変わる (先頭に `'` が付く) = ユーザ視点で表示が変わる。SECURITY.md でトレードオフを明記。
- 他 5 件は挙動不変 (エラーメッセージの整形 / seam 追加 / 内部 fallback).

## 参照

- 設計書: [`2026-07-20-security-hardening-v011-design.md`](./2026-07-20-security-hardening-v011-design.md)
- 前史: [`2026-07-19-security-hardening-medium-low.md`](./2026-07-19-security-hardening-medium-low.md)
- テスト戦略: [`2026-07-13-test-strategy-design.md`](./2026-07-13-test-strategy-design.md)
- pre-merge ゲート: `tools/pre-merge-check.ps1`
