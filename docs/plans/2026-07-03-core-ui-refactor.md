# コア/UI リファクタリング（2026-07-03）

## 背景

全マイルストーン（M1〜M7＋CSVモード＋マークダウンプレビュー）マージ後のコードベース精査で、
機能追加の積み重ねによる「重複・責務過多・残骸」を洗い出した。優先度 高・中 の8項目を
`refactor/core-ui-cleanup` ブランチで一括対応する。**読み上げ系コンポーネント
（yEdit.App/Speech・Announcer・yEdit.Accessibility・Core/Speech・CsvAnnounceFormatter・Core/Reading）は
スコープ外**（コア/UI リファクタ完了後に別途対応する合意）。

## 対応項目と設計判断

### 1. MainForm から FileController を抽出（優先度: 高）
- ファイルI/O統括（新規/開く/保存/開き直し/復元/未保存確認/最近のファイル/無題連番）を
  既存のコントローラパターン（Search/Grep/Csv/Backup）に揃えて `FileController` へ。
  MainForm 797行 → 510行。
- 「開く」経路を `TryOpenOrActivate` に一本化（`OpenExistingPath` と `OpenAndSelect` の重複解消）。
  **意図的な挙動統一**: grep ジャンプで既に開いているタブへ飛んだ場合も最近のファイルへ
  繰上げされるようになった（新規に開く場合は従来から繰上げ・不揃いの解消）。
- 設定は `OpenSettings` で参照が差し替わるため `Func<AppSettings>` で都度解決。
- 設定保存の try/catch 3箇所を `SaveSettingsSafe` へ集約。

### 2. CSVコマンドテーブルの一元化（優先度: 高）
- `ProcessCmdKey` の素キー横取り switch（17キー）と CSVメニューの二重定義を解消。
- `CsvCommands` に（キー・メニュー文言・キーヒント・グループ・実行）の単一テーブルを定義し、
  キー横取りは `ByKey` 辞書、メニューは定義順＋Group 境界のセパレータで生成。
- Shift+Tab / Ctrl+G のキー専用別名は `MenuText=null` で表現。キー割当・メニュー構成は不変。

### 3. スナップショット二重更新の解消（優先度: 高）
- 1編集につき TextChanged と UpdateUI(Content) の両方で全文コピーが走っていたのを、
  本文更新は `OnTextChangedEvt`（SCN_MODIFIED 由来）に一本化。
- `OnUpdateUI` 側は O(1) の長さ検証 `EnsureSnapshotLength` に置換（未知経路への安全網）。
  挿入/削除は必ず SCN_MODIFIED を伴うため、スナップショット（UIA応答・検索・CSVの土台）の
  鮮度保証は不変。
- **増分スナップショット化（SCN_MODIFIED デルタ適用）は今回見送り**＝SR読みの土台に触れるため、
  実機SR検証の完了後に別途検討。

### 4. SettingsDialog を AppSettings 返却方式へ（優先度: 中）
- MainForm の項目別9連コピー（項目追加時の写し漏れ源）を廃止。ダイアログが元設定の
  クローン（`AppSettings.Clone()` 新設・テスト付き）を編集し `Result` で返す。
- ダイアログで編集しない項目（ウィンドウサイズ・最近のファイル・バックアップ設定）は
  クローン経由で保持。

### 5. 原子的書き込みを AtomicFile へ抽出（優先度: 中）
- `TextFileService.Save` と `BackupStore.Write` に二重実装されていた
  「tmp ステージング→File.Replace/Move→失敗時掃除」を `Core.IO.AtomicFile` へ一本化（テスト付き）。
- 共有違反/ロック競合時の in-place フォールバックは保存固有の方針のため TextFileService 側に残し、
  判定は `AtomicFile.IsShareOrLockViolation` で共有。
- **意図的な単純化**: 旧実装は差替段階の共有違反のみ救済したが、乱数名 tmp のステージング書込で
  共有違反は事実上起きないため、段階を区別しない（どの段階由来でも in-place は FileMode.Create の
  非切詰め特性により安全）。
- 既存の保存セマンティクステスト（完全ロック時の原本非破壊の回帰ガードを含む）はすべて緑。

### 6. CSVパースキャッシュを Document 単位へ（優先度: 中）
- CsvController 単位のメモ化はタブ横断で直近文書の全文＋パース結果が滞留し、複数CSVタブの
  行き来で毎回再パースになるため、`Document.ParseCsv()`（文書と同寿命）へ移設。
- CSVモード解除・開き直しでは `ClearCsvCache` で明示解放。
- F2 編集の確定/取消コールバックはアクティブ文書の再解決をやめ、開始時の文書を捕捉
  （タブ切替時は AbortEdit が先行するため対象文書は不変）。

### 7. EOL変換の集約（優先度: 中）
- Core に `LineEndingExtensions`（`ToEolString` / `ToDisplayString`）を追加（テスト付き）。
- Editor に `ScintillaHost.ApplyLineEnding(LineEnding)` を追加し、
  LineEnding→ScintillaNET.Eol の対応を一本化。MainForm の変換3箇所を置換。

### 8. 診断系コードの削除（優先度: 中・**分離ではなく削除**＝ユーザー指示）
- ScintillaHost から削除: `SetReportedControlType`・`UseRenamedClass`＋クラスクローン一式
  （破棄済みの NVDA純UIA路線の名残）・`Log`/`LogState`・WndProc の `UiaDiag.Log` 採取・
  `RaiseUiaTextEvents`（本番で常に true）。
- `RaiseUiaSelectionEvents` は CSVモードの本番制御のため存置。
  `ServeUiaProvider`/`SuppressClientMsaa` は SR適応の本番制御のため存置（コメントを本番仕様へ更新）。
- NativeMethods のクラスクローン用 Win32 interop を削除。
- これらに依存する **ScintillaProbe（役目を終えた実験機）は sln から除外**（ソースは残置・
  git 履歴からいつでも復元可能）。UiaProbe は Editor 非依存のため影響なし。
- `UiaDiag` 本体は Accessibility 層（スコープ外）で使用中のため未変更。

## 検証

- 全コミットでビルド0警告・0エラー、Core テスト緑（258 → 270 件に増加）。
- アプリ起動スモーク（起動5秒生存→終了）OK。
- 別エージェントによるコードレビュー済み: **マージ可（Critical/Important 0件・Minor 4件）**。
  Minor のうち3件（フォールバック条件の文書化・CSVモード入場失敗時のキャッシュ解放・
  SettingsDialog.Result の副作用除去）はレビュー後に反映済み。残る1件は ScintillaProbe の
  ソース残置（sln 除外済みだが削除済み API 参照でビルド不可のまま）→ ディレクトリ削除の要否は
  ユーザー判断待ち。
- 実機SR検証への影響: 挙動を変える意図の変更は無し。SR に最も近いのは項目3
  （スナップショット更新経路）で、UIA イベントの発火順・内容は不変だが、
  次回の実機SR検証時に通常編集の読み上げが従来どおりであることの確認を推奨。

## スコープ外への申し送り（変更なし・記録のみ）

- FindReplaceDialog / GrepDialog が各自 `AnnouncerFactory.Create(_status)` で Announcer を
  生成しており通知経路が3系統ある。SR側リファクタ時の一本化候補。
- 増分スナップショット化・`ByteToUtf16` の位置キャッシュ化（大容量ファイル性能）は
  実機SR検証後の候補。
- 未使用コード（`CsvFile.IsCsvPath`・`TextSearcher.ReplaceAll`・`CsvDocument.Header`・
  余剰 `partial` 3箇所）、`DocumentManager.BeforeActiveChange` の二重経路、
  コントローラのテスト可能化（ITextBuffer 化）は優先度・低として未対応。
