# 新CSVモード（グリッド型ナビゲーション）設計

- 日付: 2026-06-29
- ブランチ: `feature/csv-grid-mode`（ワークツリー `.worktrees/csv-grid-mode`）
- 目的: 現状のCSVモード（自動判定＋自由編集オーバーレイ）とは別物の「グリッド型ナビゲーション」を新ワークツリーで実装し、main（旧方式）と実機比較する。

## 比較方式（確定）

ワークツリー単位で比較する。`<repo>`（main）＝旧CSVモード、`.worktrees\csv-grid-mode`（本ブランチ）＝新CSVモード。
同一ワークツリー内でのブランチ切替比較や、1ビルド内での新旧トグルは行わない。本ブランチでは新方式が旧オーバーレイ方式を**完全置換**する。

## 要件（ユーザー提示）

1. `.csv` を読み込んだ時点ではCSVモードを有効にしない（パースもしない）。
2. メニューの「CSVモード」を実行したときに初めて有効化する。
3. 既存エディットボックスへのオーバーレイ方式をやめ、以下の操作感にする。
   - 上下左右矢印: セルを移動して読み上げ
   - Tab: 現在フォーカスのセル内容を読み上げ（移動なし）
   - C: その列の一番上のセル内容を読み上げ（移動なし）
   - R: その行の一番左のセル内容を読み上げ（移動なし）
   - Home: その行の左端セルへ移動
   - End: その行の右端セルへ移動
   - PageUp: その列の一番上のセルへ移動
   - PageDown: その列の一番下のセルへ移動
   - Ctrl+Home: 一番左上のセルへ移動
   - Ctrl+End: 一番右下のセルへ移動
   - G: 指定セルへ移動（入力ボックス、`行,列` 形式・1始まり、例 `2,3`）
   - F2: フォーカスセルを編集モードに。カーソルはセル内のみ。Alt+Enter=改行、Enter=確定して編集モードを抜ける。通常のエディットボックスと同様に編集可能。
     - 編集モードに入らない限り内容は一切変更できない。
   - フォーカスセルはハイライトする。
   - メニューでCSVモードを無効化すると通常エディタへ戻る。
4. メニューからの有効化対象は任意のバッファ（拡張子不問・パース成功が条件）。

## アーキテクチャ方針（確定）

Scintilla本体は維持し、CSVモード中は `ReadOnly = true` で本文を編集不可にする。矢印・英字キーは `MainForm.ProcessCmdKey` で横取りして「コマンド」として解釈し、Scintillaへは渡さない。現在セルはインジケータ枠でハイライトし、キャレットをセル先頭へ置く。読み上げは既存の `IAnnouncer.Say` で明示的に行う（PC-Talker実証済みのa11yをそのまま活かす）。

F2のセル編集は Scintilla 上に**オーバーレイした専用 TextBox**で行い、セル値だけを編集する。確定時にCSVとして再直列化し `ReplaceCharRange` で本文へ反映する（確定の瞬間だけ `ReadOnly` を一時解除）。本物の EDIT コントロールなので SR 読みが確実で、「カーソルはセル内のみ」が自然に成立する。

### F2編集開始時の挙動（確定）
- 編集開始時はセル値を**全選択**（即上書きしやすい）。
- Multiline TextBox。Alt+Enter=改行、Enter=確定、Esc=取消。

### ラギッド行の扱い（確定）
- PageDown（列の最下段）等は「その列を持つ最後の行」へ寄せる（短い末尾行で当該列が欠ける場合はそれより上の最後の保有行）。
- 上下移動で列数不足の行に入る場合は末尾列にクランプ（既存 `MoveCell` の方針を踏襲）。

## 現状との差分（本ブランチで置換する箇所）

- 自動判定の撤廃: `.csv` を開いてもONにしない・パースしない（要件1）。`MainForm.LoadInto` の CSV 自動判定ブロックを削除。
- `RedetectCsvMode`（SaveAs時のパス連動再判定）を撤廃。モードは手動のみ。
- キー体系を `Ctrl+Shift+矢印` から**素のキー**のコマンド体系へ全面変更。
- 自由編集オーバーレイ → **読取専用＋F2オーバーレイ編集**。

## キー割り当て（CSVモードON かつ 非編集中のみ横取り）

| キー | 動作 | 移動 |
|---|---|---|
| ↑ ↓ ← → | セル移動＋読み上げ（端は端メッセージ） | 有 |
| Tab | 現在セルを読み上げ | 無 |
| C | 列の最上段セルを読み上げ | 無 |
| R | 行の左端セルを読み上げ | 無 |
| Home / End | 行の左端 / 右端セルへ移動 | 有 |
| PageUp / PageDown | 列の最上段 / 最下段セルへ移動 | 有 |
| Ctrl+Home / Ctrl+End | 左上 / 右下セルへ移動 | 有 |
| G | セル指定移動（`行,列`・1始まり） | 有 |
| F2 | セル編集（オーバーレイ TextBox） | 無 |

編集中は TextBox にフォーカスがあるため上記コマンドは発火しない。TextBox 内で Enter=確定 / Alt+Enter=改行 / Esc=取消 を処理する。

## コンポーネントと責務

### Core（テスト対象・UI 非依存）
- `CsvDocument` に移動ヘルパを追加:
  - `RowStart(row)` / `RowEnd(row)`（行の左端 / 右端列）
  - `ColumnTop(col)` / `ColumnBottom(col)`（列の最上段 / その列を持つ最後の行）
  - `TopLeft()` / `BottomRight()`
  - `GoTo(row, col)`（範囲検証つき。範囲外は null）
  - いずれも `(int row, int col)?` を返し、範囲外・データ無しは null。
- `CsvWriter.EscapeField(string value)` 新設: 値にカンマ・`"`・CR・LF を含むとき `"` で囲み内部 `"` を `""` に。F2 確定時の再直列化に使う。
- `CsvAnnounceFormatter`: 既存（`Cell`/`Header`/各メッセージ）を流用。必要なら行頭読み上げ用の薄い追加。

### App
- `CsvController`（全面書き換え）:
  - `ToggleMode()`: `ReadOnly` 切替・ハイライト/クリア・モード読み上げ・ON時に現在セルを確定して読み上げ。
  - `Move(Direction)` / `MoveRowStart` / `MoveRowEnd` / `MoveColumnTop` / `MoveColumnBottom` / `MoveTopLeft` / `MoveBottomRight` / `GoToCell`。
  - `ReadColumnTop`（C）/ `ReadRowHead`（R）: 読み上げのみ。
  - `BeginEdit`（F2）: オーバーレイ編集を起動。
  - 現在セルはキャレットオフセットから毎回 `CsvParser.Parse` で導出（ステートレス。編集後も陳腐化しない既存方針を踏襲）。
  - 共通ヘルパ `GoTo(row, col)`: 検証→ハイライト→キャレット移動→読み上げ。
- `CsvCellEditor`（新規）: オーバーレイ TextBox（Multiline）。セル矩形に配置、論理値をプリフィルし全選択。Enter=確定（再直列化→ `ReadOnly` 一時解除→ `ReplaceCharRange` →復帰→再ハイライト→読み上げ→Scintilla へフォーカス復帰）、Alt+Enter=改行、Esc=取消。
- `ScintillaHost` に最小 API 追加:
  - `ReadOnly`（ScintillaNET 既存プロパティ）の利用、確定時の一時解除。
  - `PointFromCharOffset(int offset)`: セル先頭のクライアント座標（SCI_POINTX/YFROMPOSITION）。
  - 行高の取得（SCI_TEXTHEIGHT）— 編集ボックスのサイズ決定用。
  - 既存の `HighlightCharRange` / `ClearHighlight` / `MoveCaretCharOffset` / `ReplaceCharRange` を流用。
- `MainForm`:
  - `LoadInto` の CSV 自動判定削除、`RedetectCsvMode` 撤廃。
  - `ProcessCmdKey` の CSV ブロック差し替え（CSVモードON かつ 非編集中で新キー集合を横取り）。
  - CSV メニュー刷新（トグル＋表示用ショートカット文字列、ON時のみ移動系を活性）。

## データフロー

- 移動: キー → `CsvController` → `CsvParser.Parse(snapshot)` → 現在 (row,col) 導出 → 目標算出 → `HighlightCharRange` ＋ `MoveCaretCharOffset` → `Announcer.Say(Cell(...))`。
- 編集: F2 → 現在セルの `CsvField`(Start/Length/Value) 取得 → TextBox 表示（全選択）→ 確定: `EscapeField(newValue)` → `ReadOnly` 一時解除 → `ReplaceCharRange(Start,Length,escaped)` → `ReadOnly` 復帰 → 再パースし新スパンを再ハイライト＋読み上げ → Scintilla へフォーカス復帰。

## エラー処理・エッジ

- パース不可（引用符未終端）/ データ無し: `ParseError` / `NoData` を読み上げ、ハイライト消去。
- 端での移動: 既存の端メッセージ（左端/右端/先頭行/最終行）。
- G 入力の不正（範囲外・書式違反）: 読み上げで通知し移動しない。
- 空セル: 「空」読み（既存 `Cell` 準拠）。
- 編集確定で本文長が変わってもスナップショット再パースで状態は陳腐化しない。

## テスト（Core に集約）

- `CsvWriter.EscapeField`: 素通し / カンマ含み / 引用符含み / 改行(CR/LF/CRLF)含み / 空。
- `CsvDocument` 新ヘルパ: RowStart/RowEnd/ColumnTop/ColumnBottom/TopLeft/BottomRight/GoTo の境界・ラギッド行・範囲外。
- 編集ラウンドトリップ: 値編集→ `EscapeField` →再パースで論理値一致（カンマ/引用符/改行を含む値）。
- App 層（オーバーレイ・キー横取り・SR）は既存方針どおり手動実機検証（自動テスト対象外）。

## 非目標 / 申し送り

- 区切り文字はカンマ固定（既存パーサ仕様）。
- 列・行の挿入/削除や並べ替えは対象外（セル値編集のみ）。
- スクロール追従中の編集ボックス位置ずれは初版で許容（F2 は事実上モーダル運用）。
- 実機 SR 検証（PC-Talker / NVDA）は実装後に別途実施。
