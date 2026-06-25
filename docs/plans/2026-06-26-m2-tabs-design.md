# yEdit M2 — タブ / 複数ドキュメント 設計

最終更新: 2026-06-26 / 作業ディレクトリ: `<repo>` / .NET 9 / Windows 11・日本語環境

前提資料: `docs/plans/2026-06-26-yedit-production-architecture-design.md`（§3 ロードマップ M2、§4.1 アクセシビリティの鉄則）、`docs/HANDOFF-scintilla-uia.md`、M1 実装（`MainForm` / `DocumentState` / `ScintillaHost`）。

---

## 0. 位置づけ

M1（v0.1 ウォーキングスケルトン）は完全な単一ドキュメント構成。本ドキュメントは M2「タブ / 複数ドキュメント」の確定設計。レイヤ追加は **App 層のみ**で、`yEdit.Core` / `yEdit.Editor` / `yEdit.Accessibility` は無改変（M1 で実機検証した SR 適応＝HWND 単位の UIA/MSAA・フォーカスイベントをそのまま再利用する）。

### 確定した要件（ブレインストーミングで決定）

| # | 決定 | 内容 |
|---|---|---|
| Q1 | ライフサイクル | 「新規」「開く」は**必ず新規タブ**。空タブ再利用なし。**最後のタブを閉じたらアプリ終了**。 |
| Q2 | キー操作 | 切替 `Ctrl+Tab`/`Ctrl+Shift+Tab`、番号ジャンプ `Ctrl+1`〜`Ctrl+9`（9=最後）、閉じる `Ctrl+W`。 |
| Q3 | SR 読み上げ | 標準 `TabControl` の読み上げに乗る。切替後は編集領域へフォーカス→現在行を読む。**独自読み上げは入れない**（Announcer 系は M6）。 |
| Q4 | 同一ファイル | 既に開いているパスを再度「開く」場合だけ例外で**そのタブへ切替**（二重編集の上書き事故防止）。 |
| Q5 | スコープ | 下記 IN/OUT のとおり。 |

### スコープ

**IN（M2 で実装）**
- `DocumentManager` 新設、`TabControl` ベースのタブ UI。
- タブ毎の独立状態（パス・文字コード・改行・変更フラグ）＋それぞれ独立した Undo/選択。
- 新規／開く／上書き保存／名前を付けて保存／文字コード指定で開き直し を**アクティブタブ対象**に再配線。
- タブを閉じる時の変更確認、アプリ終了時に**全タブの未保存を順に確認**。
- ステータスバー（行・桁・文字コード・改行）とタイトルがアクティブタブを反映。

**OUT（後続マイルストーン送り）**
- セッション復元（再起動時のタブ復元）、タブのドラッグ並べ替え、タブ右クリックメニュー（他を閉じる/全部閉じる）、大量タブ時のオーバーフロー UI 最適化。
- 最近のファイル、検索・置換（M3）、grep（M4）など他 M のもの。

---

## 1. アーキテクチャ方針

採用案＝**`TabControl` ＋ タブ毎に `ScintillaHost` を1つ**（各ドキュメントが独立 HWND の本物のフォーカス可能コントロール）。

採否の根拠:
- Q3「標準 TabControl の読み上げに乗る」に直結。`TabControl` が SR に「タブ N/M・ラベル」をネイティブに読ませる。
- 各エディタが独立 HWND のため、M1 実機検証済みの `ScintillaHost.OnGotFocus`→UIA フォーカス＋選択イベント機構を**無改変で再利用**できる。
- Undo・選択・スナップショット・UIA プロバイダがドキュメント毎に自然に分離。実装リスク最小。

却下案:
- **単一 `ScintillaHost` ＋ Scintilla ドキュメントポインタ切替（`SCI_*DOCPOINTER`）**: メモリ効率は良いが、同一 HWND/フォーカスのまま中身だけ変わるため切替の度に手動でスナップショット更新＋イベント発火が要り、標準 TabControl の読み上げに乗れない。M1 で固めた SR モデルに対しリスク最大。
- **カスタムタブ列**: `TabControl` がタダでくれるアクセシブルなタブを捨てることになり利点が薄い。

トレードオフ（許容）: 各 Scintilla が本文＋UTF-16 スナップショットを保持するため、多数の巨大ファイルではメモリ増。通常の作業セットでは問題なく、巨大ファイル最適化はロードマップ §4.1 で後続 M のテーマ。

---

## 2. 新規・変更コンポーネント（すべて yEdit.App）

### 2.1 `Document`（新規）— 開いている1ドキュメントを束ねる
- `ScintillaHost Editor` … 独立した編集コントロール。
- `TabPage Page` … TabControl 上の面（`Editor` を `Dock=Fill` で内包）。
- `DocumentState State` … 既存クラスを流用（Path / Encoding / HasBom / LineEnding / DisplayName）。
- 変更フラグは保持せず `Editor.Modified`（Scintilla のセーブポイント）を唯一の真実とする。
- `string TabLabel => State.DisplayName + (Editor.Modified ? " *" : "")`。

生成手順（M1 の SR 作法と同一）: `new ScintillaHost { Dock=Fill }` → `ConfigureForCurrentScreenReader()`（ハンドル生成前）→ フォント適用 → `Page.Controls.Add(Editor)`。

### 2.2 `DocumentManager`（新規）— タブとドキュメント群の管理
- 所有: `TabControl TabControl`（`Dock=Fill`、`MainForm.Controls` に配置）。
- 状態: `Document? Active`、`IReadOnlyList<Document> Documents`。
- メソッド:
  - `Document CreateNew(AppSettings)` … 無題タブ生成＋アクティブ化。
  - `Document? FindByPath(string path)` … 正規化パスで既出検索（§4 エッジケース）。
  - `void Activate(Document)` … タブ選択＋`Editor.Focus()`。
  - `bool TryClose(Document, Func<Document,bool> confirmDiscard)` … 変更確認→`TabControl` から除去→`Editor.Dispose()`。最後の1つを閉じたら呼び出し側でアプリ終了。
  - `void SelectNext(int dir)`（端は巡回）、`void SelectAt(int index)`。
- イベント（アクティブ由来のみ転送）:
  - `ActiveDocumentChanged` … タブ切替時。
  - `ActiveDirtyChanged` … アクティブの変更状態（タイトル更新用）。
  - `ActiveCaretChanged` … アクティブの `UpdateUI`（行・桁更新用）。
- 内部配線: 各 `Editor` の `SavePointLeft/Reached` を購読し、**どのタブでも**そのタブのラベル（`Page.Text = doc.TabLabel`）を更新。アクティブ由来のときのみ上記イベントへ転送。`UpdateUI` はアクティブの分を `ActiveCaretChanged` へ。`TabControl.Selected` で `Active` 更新＋フォーカス移動＋`ActiveDocumentChanged`。

### 2.3 `MainForm`（変更）
- `_editor` / `_doc` 直参照を廃止し `DocumentManager _docs` 経由に。`Controls` には単一エディタの代わりに `_docs.TabControl` を配置（status/menu はそのまま、追加順は M1 の docking を踏襲）。
- `DocumentManager` の3イベントを購読してタイトル・ステータスバーを更新。
- ファイル操作（新規/開く/開き直し/保存/名前を付けて保存）を**アクティブ `Document`**（`.Editor` と `.State`）対象に再配線。
- `ProcessCmdKey` で `Ctrl+Tab`/`Ctrl+Shift+Tab`/`Ctrl+1..9`/`Ctrl+W` を横取り（子の Scintilla に食われないように）。
- ファイルメニューに「タブを閉じる(&W)」（`Ctrl+W`）を追加。

### 2.4 `PathKey`（新規・yEdit.Core.Text）— 同一ファイル判定の純ロジック
- `static string For(string path)` … `Path.GetFullPath` で正規化し、Windows 前提で小文字化したキーを返す。`FindByPath` がこれで比較。M2 唯一の純ロジック追加（xUnit 対象）。

---

## 3. データフロー（M1 と同一・タブ単位になるだけ）

- **開く**: App がバイト読込 → Core で復号（`TextFileService.Load`）→ 対象タブの `Editor.Text` へセット → `ApplyEol` / `EmptyUndoBuffer` / `SetSavePoint`。
- **保存**: 対象タブの `Editor` 本文を `_doc.LineEnding` に正規化 → Core で符号化（`TextFileService.Save`）→ 原子的保存 → `SetSavePoint`。
- Core I/O 層（`TextFileService` / `EncodingCatalog` / `EncodingDetector`）は無改変。

**タブ切替時の SR**: `TabControl.Selected` → `Active.Editor.Focus()` → `OnGotFocus` が UIA フォーカス＋選択変更イベントを発火 → SR がタブ（ラベル＝ファイル名＋`*`）/位置を読み、続けて現在行を読む。

---

## 4. エラー処理・エッジケース

**エラー処理（M1 踏襲）**
- ファイル I/O 失敗は `MessageBox` で明示、握り潰さない。捕捉は想定内例外（`IOException`/`UnauthorizedAccessException`/`SecurityException`/`NotSupportedException`）のみ、ロジックバグは伝播。
- 置換文字検出（`HadReplacementChar`）警告は対象タブに対し表示。
- **読込失敗時の新タブ後始末**: 「開く」で新タブ生成後に `LoadPath` が失敗した場合、作りかけの空タブを残さず破棄し、直前のアクティブに戻す（余計なタブを残さない）。

**エッジケース**
- アプリ終了（`OnFormClosing`）: 全 `Document` を順に走査し、変更ありタブだけ 保存/破棄/キャンセル を確認。どれかでキャンセル→終了中止。全通過後にウィンドウサイズを settings 保存（M1 の `RestoreBounds` 方式を維持）。
- 同一ファイル判定: `PathKey.For` で比較。未保存（Path=null）タブは対象外。
- 「文字コード指定で開き直し」を Path 未確定タブで実行 → M1 同様に案内表示して中止。
- 起動時: 無題タブ1つでスタート（Q1=B、空タブ再利用なし）。
- `Ctrl+Tab` が `ProcessCmdKey` に届くかは環境差があり得るため、実機 SR 検証で確認し、必要なら `TabControl` 側フォールバックを併用。

**タブラベル / タイトル**
- タブラベル: `ファイル名` ＋ 変更時に末尾 ` *`（例 `memo.txt *`）。
- ウィンドウタイトル: M1 の慣習どおり先頭 `* `（`* memo.txt - yEdit`）を維持。

---

## 5. テスト戦略（§4.3 準拠）

- **Core 単体（xUnit・新規）**: `PathKey.For` を大文字小文字差・相対/絶対・区切り差で同値になることをテスト。
- **DocumentManager**: WinForms/ネイティブ依存のため自動テストは限定。`PathKey` 以外はビルド0警告＋実機 SR 検証で担保。
- **実機 SR 受け入れ（DoD の核 / PC-Talker・NVDA）**:
  1. 複数ファイルを開く。
  2. `Ctrl+Tab`/`Ctrl+Shift+Tab`/`Ctrl+1..9` で切替 → タブ名と現在行が読まれる。
  3. 編集すると変更マーク `*` が付く。
  4. `Ctrl+W` で変更確認 → 閉じる。
  5. 最後のタブを閉じると終了。
  6. 終了時に複数タブの未保存を順に確認。

---

## 6. git 運用

フィーチャーブランチ `feature/m2-tabs`（同ディレクトリ）→ 別エージェントでコードレビュー → main へ **no-ff マージ**（メモリ `phase-work-git-flow` / `review-by-separate-agent` 準拠）。ビルド0警告を維持。

---

## 7. 次のステップ

本ドキュメントをコミット後、**writing-plans スキルで M2 の実装計画**を作成する。
