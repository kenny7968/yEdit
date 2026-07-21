# セキュリティ強化 v0.11 設計書 — 2026-07-20

## 改定履歴 (2026-07-21)

実装途中で「妥当/過剰」の再分類レビューを実施した。判断基準は yEdit の実際の信頼境界:

1. **他人が作れる入力** (開くファイルの中身・ファイル名) への対策 = 本物のセキュリティ → 維持
2. **同一ユーザの別プロセス** (%APPDATA% への細工 JSON 配置・junction 作成等) への対策 = 攻撃者が既にユーザ権限でコード実行できている前提で Windows 上に境界はない → 「安価で無害な場合のみ」の防御的多層化に限定。正常運用を壊すリスクと引き換えにしない

この基準で以下を改定した:

- **PR-E**: 実装後に **CSV-L-1** (formula prefix・エディタ用途でデータ破壊) と **CSV-L-7** (AtomicFile reparse 拒否・OneDrive placeholder 誤検知リスク) を revert (コミット `26eccd0` / `68c6ed8`・PR #14)。最終スコープ 5 件
- **PR-F**: **BK-L-3** (DPAPI 暗号化) をスコープ除外 → 5 件に縮小。初期監査の「over-engineering」判定を復元
- **PR-G**: **UIA-L-4** (機密検索モード) を削除、**UIA-M-3** は設定 3 択をやめ設定なしの HomeRelative 既定化のみ (UX 改善扱い)、**UIA-M-1** から 80 文字切詰めを削除 (sanitize のみ)、**UIA-M-4** から dedupe を削除 (throttle + trailing timer のみ) → 6 件・新規設定項目ゼロ
- **GHSA**: 起票を「信頼境界を跨ぐ修正」に限定し 5 件 → 2 件 (MD-M-2 / MD-M-4)

詳細な根拠は各 PR 節と「対象外」節を参照。

## 背景

`v0.2.0-sec` (HIGH 6 件・PR #2〜#6) と `v0.3.0-sec` (P0 MEDIUM 6 件・PR #7〜#11) で SECURITY.md 対象範囲の主要な穴は塞いだ。残りの MEDIUM/LOW は [`2026-07-19-security-hardening-medium-low.md`](./2026-07-19-security-hardening-medium-low.md) で再監査済みで、次リリース候補 (P1 MEDIUM 5)・対応推奨 LOW (3)・様子見 LOW (多数) に分類されていた。

本設計書は、それらの中から **コード修正可能な全 26 件** を `v0.11` として一括で塞ぐ計画をまとめる (2026-07-21 改定で 22 件に縮小 — 冒頭「改定履歴」参照)。実装は別セッションで SDD (Subagent-Driven Development) により 4 PR に分けて実施する。

## Goal

`v0.11` として、audit doc で「コード対応可」と判定された項目のうち **改定後 22 件** (当初 26 件 − 2026-07-21 改定での除外 4 件 = CSV-L-1 / CSV-L-7 / BK-L-3 / UIA-L-4) を 4 PR に分割して塞ぎ、GitHub Releases へ zip + SHA256 を配布する。

## 対象外

以下は本 v0.11 スコープに含めない (audit doc で「仕様上不可避」判定、または 2026-07-21 改定で過剰と判定):

- **UIA-M-2**: `TextPattern.DocumentRange.GetText(-1)` による本文露出 — UIA の仕様
- **UIA-L-5**: WebView2 Chromium 由来 UIA プロバイダ — Edge/Chrome 同等仕様、抑止不能
- **UIA-L-6**: `TextChangedEvent` 経由 keylogger 粒度 — IME 未確定は既に隠蔽済み・Commit 単位が上限
- **CSV-L-2**: LineEnding 検出 4KB 窓 — Windows テキストエディタとして意図的挙動
- **CSV-L-11**: CSV F2 commit の perf — セキュリティ問題ではない
- **MD-L-6**: `NavigateToString` が `about:blank` オリジンとなる仕様 — 参考情報
- **CSV-L-9**: UtfUnknown detector timeout なし — 64KB prefix で入力上限あり (既に緩和)
- **CSV-L-3**: `AtomicFile` の tmp 残骸 — 起動時 sweep で対応可 (v0.12 候補)

2026-07-21 改定での除外 (過剰判定):

- **CSV-L-1** (実装後 revert `26eccd0`): CsvWriter の apostrophe 前置は「Web アプリが信頼できないデータを Excel 向けにエクスポートする」場面の OWASP 対策で、ユーザが自分のデータを編集するエディタに常時 on で適用するとサイレントなデータ破壊になる (負数 `-5` が F2 確定で `'-5` に化ける)。formula injection は本来、表計算ソフト (開く側) の責務
- **CSV-L-7** (実装後 revert `68c6ed8`): AtomicFile の reparse point 拒否は全保存経路に効くため、OneDrive Files On-Demand placeholder (`IO_REPARSE_TAG_CLOUD` 系) の誤検知で「保存できない」実害リスクがあり、symlink 経由編集の正規ワークフローも壊す。防いでいる攻撃は同一ユーザ書込権限が前提で境界がなく、利得が理論値。復元経路限定の BK-M-1 (OriginalPathValidator) は Untitled フォールバックで実害がないため維持
- **BK-L-3** (DPAPI 暗号化): 初期監査の「テキストエディタとして over-engineering」判定を復元。`%APPDATA%` は既にユーザ ACL で保護済みで、DPAPI CurrentUser は同一ユーザのマルウェアには無力 (Unprotect を呼べる)。得られるのは「BitLocker なしディスク盗難」程度に対し、スキーマバージョニング + 移行 + プロファイル移動不可 + **プロファイル破損時にバックアップ全損**という新しい故障モードを持ち込み、クラッシュ復元の本来目的 (可用性) と相反する
- **UIA-L-4** (grep 機密検索モード): UIA-M-2 の通り任意の UIA クライアントがエディタ本文を丸ごと読める仕様である以上、grep プレビューだけ隠しても防御にならない。設定肥大に見合わない

これらは SECURITY.md に「仕様上の限界」節として明記して対応する。

## Architecture

系統別 4 PR を SDD で個別実装 → `code-reviewer` subagent レビュー → `main` へ no-ff マージ。各 PR は「攻撃入力を白リスト/上限/専用例外/サニタイズで受け止め、正常運用の挙動は一切変えない」原則で最小差分。

**PR 順序** (相互依存を最小化):

1. **PR-D**: Markdown 防御強化 (7 件 → 6 件・MD-L-5 は実装後 revert・PR #13 マージ済)
2. **PR-E**: CSV・Text 完備 (7 件 → 5 件・CSV-L-1/L-7 は実装後 revert・PR #14)
3. **PR-F**: Backup 堅牢化 (5 件・2026-07-21 改定で BK-L-3 DPAPI を除外)
4. **PR-G**: UIA 保護 (6 件・2026-07-21 改定で UIA-L-4 を除外・announcer 変更で L5 実機検証が濃い・最後にマージ)

**再利用する seam / helper**:

- `SanitizeForDisplay` (Core.Text) — 全 PR で流用 (制御文字・BiDi・改行の除去)
- `IReachabilityProbe` (App.Abstractions) — PR-E で流用の可能性 (実装で判断)
- `PreviewNavigationPolicy` (App) — PR-D 拡張時に参照

## Tech Stack

- .NET 9 / C# / Windows Forms
- Markdig / WebView2 (Preview 系)
- xUnit + FluentAssertions
- CSharpier / Husky.NET pre-commit hook
- `tools/pre-merge-check.ps1` ローカルゲート

---

## PR-D: Markdown 防御強化 (7 件 → 6 件・PR #13 マージ済)

**対象**: MD-M-2 / MD-M-4 / MD-L-1 / MD-L-2 / MD-L-3 / MD-L-4 / ~~MD-L-5~~

> **注記**: MD-L-5 (拡張子ガード・下記 (5)) は PR #13 内で実装後に revert 済み (`0f0364e`・LOW 深刻度 + 「.txt でメモ→プレビュー」workflow を壊す UX コスト)。以下の設計記述は当初計画の snapshot として残す。

### 新規/変更ファイル

- Modify: `src/yEdit.Core/Text/MarkdownRenderer.cs`
- Modify: `src/yEdit.App/MarkdownPreviewForm.cs`
- Create: `src/yEdit.App/PreviewCspHeaderInjector.cs`
- Create: `src/yEdit.App/PreviewUserDataFolder.cs`
- Modify: `src/yEdit.App/MainForm.cs` などの caller で `ShowMarkdownPreview` 拡張子ガード
- Modify: `src/yEdit.App/Program.cs` (依存バージョン log 出力)
- Modify: `.github/workflows/ci.yml` (依存 list step 追加)
- Test: `tests/yEdit.Core.Tests/Text/MarkdownRendererTests.cs` に追記
- Test: `tests/yEdit.App.Tests/PreviewCspHeaderInjectorTests.cs` 新規

### 主要な設計判断

#### (1) MD-M-2 + MD-L-1: CSP を HTTP ヘッダで配信 + `img-src data:` 削除

- 新クラス `PreviewCspHeaderInjector` を `CoreWebView2.WebResourceRequested` に登録し、preview リソース (`https://yedit.preview/**` および NavigateToString 起点の `data:text/html`) の応答 header に CSP を差し込む
- `<meta http-equiv="CSP">` は fallback として残す (HTTP header 側が優先。WebResourceRequested が未サポートの環境向け)
- 追加ディレクティブ: `base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'; worker-src 'none'; manifest-src 'none'; connect-src 'none'`
- `img-src` から `data:` を除去。`data:` 画像を含む `.md` は表示崩れになるが「Windows テキストエディタのプレビュー」用途で許容
- `style-src` は `'self' https://yedit.preview` のみ (inline 撤去)。既存 `<style>{{Css}}</style>` は virtual host 経由の `styles.css` として供給

#### (2) MD-M-4: WebView2 UserDataFolder を per-form 化

- 新クラス `PreviewUserDataFolder` が `IDisposable` として一時ディレクトリ (`%LOCALAPPDATA%\yEdit\WebView2\preview-{guid}\`) を作り、`MarkdownPreviewForm.Dispose` で削除
- 削除失敗は Trace 警告のみ (次回起動時 sweep で拾う)
- 副次効果: 複数 preview 同時起動時のプロファイルロック競合が解消

#### (3) MD-L-3: レンダー入力サイズ上限

- `MarkdownRenderer.Render` 冒頭で `markdown.Length > MaxMarkdownChars` (既定 4M chars = 8 MB UTF-16) なら `DocumentTooLargeException` を throw
- `MarkdownPreviewForm` 起動側 (MainForm) が catch し `_prompt.Error` を出す
- ネスト深度/テーブルサイズの pre-scan は入れない (入力サイズ 4 MB で実質封じられる・保守負担を優先)

#### (4) MD-L-4: `baseHref` 検証

- `MarkdownRenderer.Render(md, baseHref)` で `baseHref != string.Empty && baseHref != PreviewBaseHref` なら `ArgumentException`
- 単一 caller の防御的ガード。callers 側も `PreviewBaseHref` 定数を直接参照するよう改める

#### (5) MD-L-5: 拡張子ガード

- `ShowMarkdownPreview` 呼び出し前に `.md/.markdown/.mkd/.mkdn` チェック
- 設定タブ「マークダウン拡張子」を可視な allow list として実装 (将来ユーザ追加可能)

#### (6) MD-L-2: 依存バージョン確認

- `Program.cs` 起動時に `typeof(Markdig.Markdown).Assembly.GetName().Version` を Trace ログに出力
- CI step `dotnet list package --include-transitive` を `.github/workflows/ci.yml` に 1 step 追加 (依存更新 PR で発火)

### テスト方針

- `MarkdownRendererTests`: `Render_Throws_DocumentTooLarge_WhenExceedingCap`, `Render_Throws_OnUntrustedBaseHref`, 既存の `Render_EscapesRaw*` を維持
- `PreviewCspHeaderInjectorTests`: 分類 tests (WebResourceResponse mock で CSP header が付いていることを検証)、`PreviewNavigationPolicyTests` の設計に倣う
- 統合テストは App.Tests 側で難しいので L5 実機ドライブで確認

### L5 実機検証

- 悪意ネスト/巨大テーブルの `.md` → エラーダイアログ・yEdit 生存
- data URI 画像を含む `.md` → 画像非表示・text は正常表示
- 外部リンク `http://example.com` クリック → 既定ブラウザで開く (既存挙動)
- 2 プレビュー同時起動 → ロック競合エラーなし
- 拡張子 `.txt` で開いた文書に対して preview 実行不可

### 想定テスト増分

+20〜25 tests

---

## PR-E: CSV・Text 完備 (7 件 → 改定後 5 件)

**対象**: ~~CSV-L-1~~ / CSV-L-4 / CSV-L-5 / CSV-L-6 / ~~CSV-L-7~~ / CSV-L-8 / CSV-L-10

> **改定 (2026-07-21)**: 実装完了後のレビューで CSV-L-1 (下記 (1)) と CSV-L-7 (下記 (5)) を revert した (コミット `26eccd0` / `68c6ed8`・理由は「対象外」節)。以下の設計記述は当初計画の snapshot として残す。

### 新規/変更ファイル

- Modify: `src/yEdit.Core/Csv/CsvWriter.cs`
- Modify: `src/yEdit.Core/Text/RecentFilesList.cs`
- Modify: `src/yEdit.Core/Text/EncodingDetector.cs`
- Modify: `src/yEdit.Core/Text/PathKey.cs`
- Modify: `src/yEdit.Core/Buffer/TextBufferBuilder.cs` (および `TextBuffer.cs`)
- Modify: `src/yEdit.Core/IO/AtomicFile.cs`
- Create: `src/yEdit.Core/IO/ReparsePointCheck.cs` (BK-M-1 のロジック抽出・OriginalPathValidator も参照)
- Modify: `src/yEdit.App/FileController.cs` (SanitizeForDisplay 呼び出し追加)
- Test: 各 Core.Tests / App.Tests に追記

### 主要な設計判断

#### (1) CSV-L-1: CsvWriter formula injection

- 対象文字を先頭に持つセル (`=`, `+`, `-`, `@`, `\t`, `\r`) には `'` (apostrophe) を先頭付加してエスケープ (OWASP / 業界標準対策)
- 常時 on。合法な数式頭を持つデータ (稀) は SECURITY.md で「Excel 互換のため apostrophe prefix する」旨を明記
- `internal const string FormulaPrefixChars = "=+-@\t\r"` として一元管理

#### (2) CSV-L-4: RecentFilesList デシリアライズ上限

- 既存の `MaxItems` (10 想定) を deserialize 直後にも適用 — `List.Take(MaxItems)` で切詰め
- 攻撃 settings.json (10 万件) を投入されても O(MaxItems) で終わる

#### (3) CSV-L-5: パスプロンプトの制御文字/RLO サニタイズ

- `FileController.cs` の `_prompt.Error/Warn` で path を含む全 6 箇所を `SanitizeForDisplay.OneLine(path, maxLength: 200)` でラップ
- 対象箇所は grep で機械抽出 (FC.cs:158 / 191 / 197 / 431 + Save 系エラー)
- `_prompt.Error($"開けませんでした: {path}", ...)` パターンを `_prompt.Error($"開けませんでした: {SanitizeForDisplay.OneLine(path, 200)}", ...)` へ機械置換

#### (4) CSV-L-6: UTF-16/32 BOM 優先検出

- `EncodingDetector` の BOM 分岐を UTF-Unknown より **前** に実行 (現状は逆順で BOM が拾えないケースあり)
- 対応 BOM: UTF-8 (EF BB BF), UTF-16 LE (FF FE), UTF-16 BE (FE FF), UTF-32 LE (FF FE 00 00), UTF-32 BE (00 00 FE FF)
- BOM 検出時は confidence=1.0 で確定・UTF-Unknown は skip

#### (5) CSV-L-7: AtomicFile.Replace の symlink follow 抑止

- `AtomicFile.Write` の直前で `File.GetAttributes(destPath)` を確認し `FileAttributes.ReparsePoint` があれば `IOException` を throw
- BK-M-1 と同じロジックだが Core 側の I/O 汎用処理として `IO/ReparsePointCheck.cs` に切り出し (`OriginalPathValidator` からも参照可能に)
- 破壊的変更 (既存で symlink 経由の書込が動いていたケース) は 0 と想定・SECURITY.md に「symlink 経由の atomic write は禁止」旨明記

#### (6) CSV-L-8: PathKey 例外時フォールバック

- `PathKey.Create` の catch 節で生小文字を返す代わりに空文字を返す
- 影響: `_map[key]` 系の caller は空文字 key を扱わない invariant で統一
- 挙動変化を最小化するため既存 caller は「空文字 key = 「未指定」扱い」で解釈

#### (7) CSV-L-10: MaxTotalBytes 引き下げ

- `TextBufferBuilder.MaxTotalBytes` を `int.MaxValue` から `512L * 1024 * 1024` (512 MB) に引き下げ
- 併せて `TextBuffer.MaxTotalBytes` も同値に統一 (現状はビルダーと編集後で異なるとバグ源)
- 512 MB = UTF-16 で 256M chars ≈ CSV MaxTotalChars と揃う
- 引き下げにより開けないファイルは実運用では稀 (512 MB のテキストファイル = 数億行) だが v0.11 のリリースノートで明記

### テスト方針

- `CsvWriterTests.Write_PrefixesApostrophe_ForFormulaChars` (6 chars × 2 位置)
- `RecentFilesListTests.Deserialize_TruncatesTo_MaxItems`
- `EncodingDetectorTests.Detect_PrefersUtf16Bom_OverUtfUnknown` (5 BOM × 各 1)
- `PathKeyTests.Create_ReturnsEmpty_OnInvalidPath`
- `AtomicFileTests.Write_ThrowsIOException_OnSymlinkDest` (junction を tests/temp に作成)
- `TextBufferBuilderTests.MaxTotalBytes_Is512MB` (定数確認)
- `FileControllerTests`: 制御文字/RLO を含むパスで Error 呼び出し → prompt が sanitize 済み

### L5 実機検証

不要 (SR 経路変更なし・全て IO / パーサ層の変更)

### 想定テスト増分

+25〜30 tests

---

## PR-F: Backup 堅牢化 (5 件)

**対象**: BK-M-2 / BK-M-3 / BK-L-5 / BK-L-6 / BK-L-8

> **改定 (2026-07-21)**: BK-L-3 (DPAPI 暗号化) をスコープから除外 (理由は「対象外」節)。これに伴い `IBackupEncryption` seam / `DpapiBackupEncryption` / `BackupRecord` のスキーマ変更 (SchemaVersion + EncryptedContent) / Program.cs の DI 登録はすべて不要になり、PR-F は挙動保存的な堅牢化のみの低リスク PR になった。

### 新規/変更ファイル

- Modify: `src/yEdit.Core/Backup/BackupStore.cs` (session subdir + trace)
- Modify: `src/yEdit.Core/Backup/BackupIdValidator.cs` (lowercase 厳格化)
- Modify: `src/yEdit.App/BackupCoordinator.cs` (session id + size cap + SanitizeForDisplay 統合)
- Test: Core.Tests / App.Tests に追記

### 主要な設計判断

#### (1) BK-M-2: セッション別サブディレクトリ

- `%APPDATA%\yEdit\backups\` 直下ではなく `%APPDATA%\yEdit\backups\session-{Guid.N}\` に書く
- `BackupStore.LoadAll` は **全 subdirectory を列挙** して復元候補を収集 (他インスタンス/前回クラッシュ由来も含めユーザに全復元機会を与える)
- `BackupStore.DeleteAll` は **自セッションの subdirectory のみ削除** (他インスタンスのライブは無傷)
- 起動時 sweep で 30 日以上古い session-* ディレクトリを削除 (孤児掃除)
- `BackupCoordinator` ctor で session guid を生成し `BackupStore.Write/Delete/DeleteAll` に渡す (既存 `dir` 引数を `sessionDir` に差替え)

#### (2) BK-M-3: バックアップサイズ上限 + path-only fallback

- `BackupCoordinator.Reconcile` 冒頭で `doc.Editor.CurrentBuffer.Length > MaxBackupChars` (定数 32M chars = 64 MB UTF-16) を判定
- 超過時は `BackupRecord.Content = null` (パス保存のみ) にフォールバック + `_trace.Warn("backup-content-skipped", pathKey, sizeChars)`
- ユーザ視点: 復元ダイアログには「(サイズ超過のため本文は保存されていません — 元ファイルから開き直してください)」と表示
- 32 MB は「実運用で日常編集される最大 CSV」を大きく超える設定

#### ~~(3) BK-L-3: DPAPI 暗号化~~ — 2026-07-21 改定で除外

初期監査の「over-engineering」判定を復元 (詳細は「対象外」節)。バックアップは従来通り `%APPDATA%` に平文保存 (ユーザ ACL による保護) とし、SECURITY.md の「仕様上の限界」節に明記する。

#### (4) BK-L-5: Trace CRLF injection

- `BackupCoordinator._trace.Warn` の呼び出しを全 grep し、attacker-controlled 文字列 (path / id / message) は `SanitizeForDisplay.OneLine(x, maxLength: 200)` でラップ
- 既存 `SafeIdForLog` は `SanitizeForDisplay.OneLine` に置換して単一化 (dedupe)

#### (5) BK-L-6: LoadAll per-file catch 沈黙解消

- 現在: `catch { /* 無視 */ }`
- 変更: `catch (Exception ex) { _trace?.Warn("backup-load-failed", SanitizeForDisplay.OneLine(file, 260), ex.GetType().Name); }`
- ただし `BackupStore` は Core 層 (App 層の trace sink を直接持たない)。**設計判断**: `LoadAll(string dir, Action<string, string>? traceSink = null)` として optional callback を追加 (シンプル)。BackupCoordinator が渡す
- 破損ファイルの警告は「情報」レベルなので落ちない (既存挙動維持)

#### (6) BK-L-8: BackupIdValidator lowercase 厳格化

- `IsValid`: `Guid.TryParseExact(id, "N", out _) && id == id.ToLowerInvariant()`
- `Guid.NewGuid().ToString("N")` は既に lowercase を返す仕様なので既存書込コードは無変更
- 過去の大文字 GUID N (実質存在しないはず) は load 時に skip される (BK-L-6 の trace で可視化される)

### テスト方針

- `BackupStoreTests.LoadAll_EnumeratesAllSessionSubdirs`
- `BackupStoreTests.DeleteAll_OnlyDeletesOwnSession`
- `BackupStoreTests.LoadAll_TriggersTraceSink_OnCorruptFile`
- `BackupIdValidatorTests.IsValid_False_ForUppercaseHex`
- `BackupCoordinatorTests.Reconcile_FallsBackToPathOnly_WhenExceedsMaxSize`
- Session-cleanup test は integration 寄りなので DateTime seam を fake で

### L5 実機検証

- 3 インスタンス同時起動 → 各セッション独立バックアップ確認 → 1 つで「すべて破棄」→ 他 2 つのライブ影響なし
- 100 MB CSV を開く → path-only fallback がトレースに出て復元ダイアログでも「本文なし」表示
- 復元ダイアログ: 旧 flat 配置 (v0.3.0-sec 由来) と session subdir 混在の backup をロード可能

### 移行/リリースノート要点

- v0.3.0-sec ユーザの既存 flat 配置バックアップは後方互換ロードされる (書込は session subdir へ移行)

### 想定テスト増分

+18〜24 tests

---

## PR-G: UIA 保護 (6 件)

**対象**: UIA-M-1 / UIA-M-3 / UIA-M-4 / UIA-L-1 / UIA-L-2 / UIA-L-3 (~~UIA-L-4~~ は 2026-07-21 改定で除外)

> **改定 (2026-07-21)**: SR の中核 UX (「SR で全文が読めること」は yEdit の存在意義) を毀損しないよう縮小。UIA-M-1 の 80 文字切詰めと UIA-M-4 の dedupe を削除、UIA-M-3 は設定 3 択をやめ設定なしの HomeRelative 既定化 (UX 改善扱い)、UIA-L-4 は削除。**新規設定項目はゼロ**になり、SettingsDialog / SettingsData への変更は不要。

### 新規/変更ファイル

- Modify: `src/yEdit.Core/Csv/CsvAnnounceFormatter.cs`
- Modify: `src/yEdit.App/Speech/UiaAnnouncer.cs`
- Modify: `src/yEdit.App/RestoreDialog.cs`
- Modify: `src/yEdit.Accessibility/TextRangeProviderV2.cs` (FindText チャンク検索)
- Modify: `src/yEdit.Editor/UiaTextHostAdapter.cs` (trace sink 挿入)
- Test: 各テスト追記

### 主要な設計判断

#### (1) UIA-M-1: CSV セル発話のサニタイズ (切詰めなし)

- `CsvAnnounceFormatter.Cell(f)` 内で `f.Value` に `SanitizeForDisplay` の制御文字/RLO 除去を適用
- **長さ切詰めは行わない** (2026-07-21 改定): 長いセル (住所・備考欄) を全文読むのは SR ユーザの正当な操作であり、現行の発話長挙動を維持する
- 制御文字を検出した場合は「制御文字を含みます: {先頭 60 文字}」の形式で発話 (完全な dedupe より攻撃気付きを優先)
- **PR-F と共通の SanitizeForDisplay 拡張は不要** — 既存の `OneLine` で C0/C1/RLO/BOM は全て除去済み。切詰めなしのため `maxLength` は十分大きい値または切詰めなしオーバーロードで呼ぶ (実装で判断)
- テスト: `Cell_PreservesFullLength_ForLongValues`, `Cell_ReplacesControlCharsWithMarker`

#### (2) UIA-M-3: 復元ダイアログのパス表示縮退 (設定なし・UX 改善扱い)

- **設定項目は追加しない** (2026-07-21 改定): UIA-M-2 の通り UIA クライアントは本文を丸ごと読めるため、パス表示の縮退にセキュリティ利得はない。位置づけは「SR 発話が短くなり、スピーカー越しの周囲漏れも減る」純粋な UX/プライバシー改善
- `RestoreDialog.Describe` は常に HomeRelative (`~\Documents\...`) で表示。SR 発話・視認バランス最良
- `%USERPROFILE%` 置換は `Environment.GetFolderPath(SpecialFolder.UserProfile)` + StartsWith ケース非依存で `~` に差替。ユーザプロファイル外のパスはフルパスのまま
- `AccessibleDescription` は必ず表示と同じ文字列 (視覚 / SR の整合)

#### (3) UIA-M-4: UiaAnnouncer rate limit (throttle + trailing・dedupe なし)

- `UiaAnnouncer.Say(message)` に throttle ガード: `_lastSaidUtc` から 50 ms 未満なら Label 更新のみ (`RaiseAutomationNotification` skip)
- **trailing timer 必須** (2026-07-21 改定): skip した場合は 50 ms 後に最後の 1 件を必ず Raise する。連打の最終メッセージが失われると「今どこにいるか」が SR に伝わらない
- **dedupe (直前と同一メッセージなら skip) は行わない** (2026-07-21 改定): 同じセルを意図的に読み直すのは SR の基本操作であり、沈黙させてはならない
- 内部フィールド: `DateTime _lastSaidUtc` + trailing 用 timer。`IClock` seam 導入で単体テスト可能 (既存 seam があれば流用・なければ新規)
- テスト: `Say_ThrottlesRaise_When50msWithinPrevious`, `Say_RaisesTrailingMessage_AfterThrottleWindow`, `Say_RepeatsIdenticalMessage_WhenOutsideThrottleWindow`

#### (4) UIA-L-1: FindText チャンク検索

- `TextRangeProviderV2.FindText` を全範囲 string 展開ではなく 64 KB chunks with 1 KB overlap で走査
- overlap は「検索語 max 512 chars 想定」の 2 倍余裕
- ヒット時は該当 chunk 内 offset から global offset を計算
- テスト: 3 chunks にまたがる短い検索語 / chunk 境界丁度のヒット / 1 GB 相当の fake buffer で OOM 発生しないこと

#### (5) UIA-L-2: UIA イベント発火の catch を可観測化

- `UiaTextHostAdapter.cs:216-233` と `UiaAnnouncer.cs:38-40` の `catch { }` を `catch (Exception ex) { _trace?.Warn(...) }` に差替
- `_trace` は既存の `ITraceSink` をコンストラクタで受け取る
- DI 追加: `UiaTextHostAdapter` / `UiaAnnouncer` の ctor に `ITraceSink` を optional で追加 (既存 caller は無変更で `null` になり silent 継続)
- テスト: `Fake` trace sink を注入して「UIA raise が例外投げても Trace に落ちる」を検証

#### (6) UIA-L-3: GetRuntimeId 一意性の機械固定

- コード変更ゼロ (hwnd で通常一意化される仕様に依存)
- 追加テスト: `RuntimeId_IsUnique_PerHwnd` — mock hwnd を 2 種与えて runtime id が異なることを確認
- 追加 comment in provider file で「hwnd 単位で一意」の invariant を明記
- 回帰保護のみ (現状で問題なし)

#### ~~(7) UIA-L-4: GrepResultsWindow 機密検索モード~~ — 2026-07-21 改定で除外

UIA-M-2 (本文は UIA 経由で丸ごと読める仕様) により防御価値がなく、設定肥大に見合わない (詳細は「対象外」節)。

### テスト方針

- Core: `CsvAnnounceFormatterTests` (新規 sanitize 系・切詰めなし invariant)
- App: `UiaAnnouncerTests` (throttle + trailing + trace sink)、`RestoreDialogTests` (HomeRelative 表示・AccessibleDescription 一致)
- Accessibility: `TextRangeProviderV2Tests` (chunk search 境界)

### L5 実機検証 (必須 — SR 経路変更が濃い)

- NVDA + 悪意 CSV セル: 制御文字を含む CSV でセル移動 → 「制御文字を含みます: ...」発話
- NVDA + 長文セル (数百文字): 全文が切詰めなしで発話される
- 復元ダイアログ: HomeRelative 表示 (`~\Documents\...`) と SR 発話が一致
- 大量 grep ヒット (10k件) で `Say` 連呼 → SR 応答が遅延しない (throttle 効果) かつ**最後の発話が必ず届く** (trailing 確認)
- 同一セルへ移動し直し → 同じ内容が再発話される (dedupe を入れていないことの確認)
- FindText で 100 MB テキスト → 検索完走・OOM 出ず

### 想定テスト増分

+20〜28 tests

---

## リスク / 巻き戻し方針

- **BK-L-3 (DPAPI) の除外により、ロールバック困難な項目は消滅** (2026-07-21 改定)。全 PR が独立 revert 可能
- **Session subdirectory (BK-M-2)** は既存 flat subdir を後方互換ロードすれば移行済ユーザも継続動作
- **MD-M-2 の CSP HTTP ヘッダ配信** は WebResourceRequested 依存 → WebView2 未インストール環境では meta CSP fallback で機能維持
- **CSV-L-10 (MaxTotalBytes 512 MB)** は超過ファイルを開けなくする変更 → リリースノートで明示・実運用影響は極稀

## リリース (全 4 PR マージ後)

### Step R.1: `main` を origin に push

```bash
git checkout main
git log --oneline origin/main..HEAD  # 何が push されるか確認
git push origin main
```

### Step R.2: タグを打つ

```bash
git tag v0.11
git push origin v0.11
```

release.yml が発火 → zip + SHA256 → GitHub Releases 公開。

### Step R.3: GitHub Security Advisory 起票 (2 件)

> **改定 (2026-07-21)**: advisory は「信頼境界を跨ぐ修正」(悪意ファイルを開くだけで成立する攻撃面) に限定する。BK-M-2 (攻撃者経路なしの friendly-fire)・BK-M-3 (ユーザ自身の巨大ファイルが前提)・UIA-M-4 (弱シナリオの DoS 緩和) は advisory ノイズになるため起票せず、リリースノート記載に留める。

GitHub UI から Security → Advisories → New draft security advisory を 2 回。各 advisory は:

- タイトル: 対応した MEDIUM の要約
- CWE 参照:
  - MD-M-2 (CSP 単層) = CWE-693 (Protection Mechanism Failure)
  - MD-M-4 (WebView2 UserDataFolder) = CWE-668 (Exposure of Resource to Wrong Sphere)
- 影響バージョン: `< v0.11`
- 修正版: `v0.11`
- コミット SHA を各 advisory に貼る

LOW は advisory 起票せず SECURITY.md 記載で対応。

### Step R.4: SECURITY.md 更新

```markdown
## 対応方針

+ ### v0.11 (2026-07-XX) で修正済
+ - Markdown プレビューの CSP 複層化 (HTTP ヘッダ配信・data: 画像禁止)
+ - WebView2 UserDataFolder の per-form 化 (Cookie / IndexedDB / Service Worker の共有解消)
+ - バックアップのセッション別サブディレクトリ + サイズ上限
+ - UIA アナウンサーの rate limit (trailing 保証) + CSV 攻撃者制御コンテンツのサニタイズ
+ - その他 LOW (詳細は docs/plans/2026-07-20-security-hardening-v011-design.md)

### 仕様上の限界 (コード修正不可 or 意図的に対応しない — 設計上の制約として明記)

- UIA-M-2: UIA `TextPattern.DocumentRange.GetText(-1)` は同一ユーザ他プロセスから読める仕様 (Windows のアクセシビリティ設計)
- UIA-L-5: WebView2 の Chromium 由来 UIA プロバイダはプレビュー DOM を露出する (Edge / Chrome と同等仕様・抑止不能)
- UIA-L-6: `TextChangedEvent` 経由の keylogger 粒度 (IME 未確定は隠蔽済・Commit 単位が上限)
- CSV-L-2: LineEnding 検出の 4 KB 窓 (Windows テキストエディタとして意図的挙動)
- BK-L-3: バックアップは `%APPDATA%` に平文保存 (ユーザ ACL による保護。DPAPI は同一ユーザのマルウェアに無力で、プロファイル破損時のバックアップ全損リスクに見合わないため見送り)
- CSV-L-1: CSV formula injection (先頭 `=` 等のセル) への apostrophe 前置は行わない (エディタがユーザデータを書き換えるのは有害。表計算ソフト側の責務)
- CSV-L-7: symlink / junction / OneDrive placeholder 越しの保存は許容 (reparse point 拒否は OneDrive 誤検知・正規 symlink ワークフロー破壊のリスクが利得を上回る)
+ 詳細は GitHub Security Advisory を参照
```

### Step R.5: メモリ更新

- `memory/security-hardening-v011-complete.md` を新規作成 (改定後 22 件 = P1 MEDIUM 5 + LOW 17 解消・見送り 4 件の理由・PR/コミット SHA・実装期間・L5 結果・GHSA 番号)
- `MEMORY.md` の index に 1 行追加

## 完了条件

- [ ] PR-D 〜 PR-G 全 4 PR が `main` にマージ済 (PR-D #13 済・PR-E #14)
- [ ] `tools/pre-merge-check.ps1` が最終状態で全緑・0 warnings
- [ ] テスト数: PR-E 時点 1357 (Core 778 / Editor 262 / App 317) から PR-F/PR-G で +38〜52
- [ ] `v0.11` タグ push・release CI 完走・zip + SHA256 が Releases に公開
- [ ] GitHub Security Advisory 2 件公開 (MD-M-2 / MD-M-4)
- [ ] SECURITY.md 更新 (対応済節 + 仕様上の限界節・2026-07-21 改定の見送り 4 件を含む)
- [ ] メモリ更新

## 実施しない事項 (明示的除外)

- 本設計書作成に伴うコード変更 (design のみ・実装は別セッション)
- 実装計画書 (`2026-07-20-security-hardening-v011.md` の step-by-step TDD 手順) — 別セッションで SDD 担当者が作成
- 対象外の項目 (UIA-M-2 / UIA-L-5/L-6 / CSV-L-2 / CSV-L-11 / MD-L-6 / CSV-L-9 / CSV-L-3) — 上記「対象外」節参照

## 参照

- [SECURITY.md](../../SECURITY.md) — 対象攻撃面の定義
- [2026-07-19-security-hardening-medium-low.md](./2026-07-19-security-hardening-medium-low.md) — 本設計書の起点 (再監査結果)
- [2026-07-19-security-hardening-high-design.md](./2026-07-19-security-hardening-high-design.md) — HIGH 6 件の設計書 (前史)
- [2026-07-19-security-hardening-high.md](./2026-07-19-security-hardening-high.md) — HIGH 6 件の実装計画
- PR #2〜#6 (v0.2.0-sec)・PR #7〜#11 (v0.3.0-sec) — 過去のセキュリティ強化
- ローカルゲート: `tools/pre-merge-check.ps1`
