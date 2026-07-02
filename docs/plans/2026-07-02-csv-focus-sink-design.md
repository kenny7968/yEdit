# CSVモード フォーカスシンク方式 設計書（2026-07-02）

## 背景 / 問題

CSVモードON直後（およびモード中の各種フォーカス復帰時）に、NVDA が Scintilla の生バッファ
（カンマ区切りの生テキスト行）を読み上げてしまう。

根本原因: NVDA 起動時、本アプリは意図的に UIA プロバイダを引っ込め
（`ConfigureForCurrentScreenReader()` → `ServeUiaProvider=false`、クラス名 "Scintilla" 維持）、
NVDA 自身のネイティブ Scintilla オブジェクトに読み上げを任せている。この NVDA ネイティブ読みは
OS レベルのイベント（フォーカス獲得 winevent・システムキャレット移動・選択変更）に反応して
生バッファを読むため、アプリ側の UIA イベント抑止（`RaiseUiaSelectionEvents=false` 等）や
「キャレットを動かさない」対策では原理的に止められない。
CSVモードトグルはメニュー専用（ショートカット無し）なので、メニュー閉塞→編集領域への
フォーカス復帰が必ず発生し、NVDA がキャレット行（生CSV）を読む。

## 方針（採用案）

**フォーカスシンク方式**: CSVモード中はキーボードフォーカスを Scintilla ではなく、
TabPage 内に置いた 1×1px のフォーカス専用コントロール（シンク）に置く。
NVDA のネイティブ読みはフォーカスのあるコントロールに対して働くため、
フォーカスが Scintilla に無ければ生バッファ読み上げの全経路（フォーカス復帰・キャレット・選択）が遮断される。
読み上げは従来どおり Announcer（UIA通知 / PC-Talker）に一本化。

副次効果:
- 現在 `ProcessCmdKey` で横取りしきれていない修飾付きナビゲーションキー
  （Shift+矢印・Ctrl+矢印・Shift+Home/End 等）が Scintilla に届かなくなり、
  キーリークによる生読み・選択読みも同時に消える。
- ON直後の生行読みが消えるため、f312586 で削った「ON時のセル内容読み上げ」を復活できる。

### 検討した代替案（不採用）

- **クラス名リネームをCSVモード中だけ適用**: ウィンドウクラスは実行時変更不可で
  RecreateHandle が必要。Scintilla の内部状態（アンドゥ履歴等）喪失リスクが大きく不採用。
- **タブストリップへフォーカス退避**: 矢印キーがタブ切替に食われ、
  ProcessCmdKey のCSVナビと競合するため不採用。
- **アプリ側イベント抑止の延長**: NVDA はアプリの UIA を見ていないので原理的に不可（実証済み）。

## UX 決定事項（ユーザ確認待ち・推奨値で仮置き）

1. **シンクの AccessibleName = 「CSV表」**（着地時に SR が読む名前。簡潔さ重視）
2. **ON直後の発話を復活**: 「CSVモード オン」＋初期セル内容＋位置（二重読みの原因が消えるため元仕様に戻す）
3. **本文マウスクリック時**: シンクへ即時フォーカス復帰のみ（クリック位置のセル移動は申し送り）

## コンポーネント設計

### 1. CsvFocusSink（新規: `src/yEdit.App/CsvFocusSink.cs`）

- `Control` 派生の最小クラス（30行程度）。
- `SetStyle(ControlStyles.Selectable, true)` / `TabStop = false` / `Size = (1,1)` /
  `AccessibleName = "CSV表"` / `AccessibleRole = AccessibleRole.Pane`。
- `TabStop=false` なので通常モードの Tab 順に乗らない。フォーカスはコードからのみ与える。
- Document 生成時に TabPage（エディタと同じ親）へ追加。常時 Visible（1×1で視覚影響なし）。

### 2. Document.FocusTarget（`Document.cs` 変更）

```csharp
public Control CsvSink { get; }   // ctor で生成し Page.Controls に追加
public Control FocusTarget => State.CsvMode ? CsvSink : Editor;
```

以降「編集領域へフォーカスを戻す」全箇所を `FocusTarget.Focus()` に統一する。

### 3. フォーカス遷移の差し替え

| 箇所 | 現状 | 変更後 |
|---|---|---|
| `CsvController.ApplyCell` (236) | `ed.Focus()` | `doc.FocusTarget.Focus()`（CSVモード中=シンク） |
| `CsvController.ToggleMode` ON | （ApplyCell 経由） | シンクへフォーカス＋セル内容込みで Say |
| `CsvController.ToggleMode` OFF | フォーカス操作なし | `CsvMode=false` を先に→キャレット復帰→`Editor.Focus()` を明示 |
| `CsvCellEditor.Teardown` (128) | `_ed?.Focus()` | `Begin` に復帰先 `Control` を追加しそこへ復帰 |
| `DocumentManager` (82, 122) | `doc.Editor.Focus()` | `doc.FocusTarget.Focus()` |
| `MainForm` OnShown(84)/タブ閉じ(769)/MDプレビュー復帰(586) | `Editor.Focus()` | `FocusTarget.Focus()` |

### 4. フォーカス漂着対策（GotFocus リダイレクト）

エディタの `GotFocus` を購読し、`State.CsvMode == true` かつ F2 編集中でなければ
`BeginInvoke` でシンクへフォーカスを戻す（再入回避のため BeginInvoke 必須）。

- マウスクリックで Scintilla がフォーカスを取っても即シンクへ復帰
  → NVDA の生読みはフォーカス変更キャンセルで断片化し実害を最小化。
- ToggleMode OFF は `CsvMode=false` を `Editor.Focus()` より先に実行するため、
  ガード条件が自然に外れて明示除外は不要（実装時に順序を保証する）。
- 配線場所: MainForm がドキュメント生成時に配線（`_csv.IsEditing` を参照できるのが MainForm のため）。

### 5. キー横取りガード（`MainForm.ProcessCmdKey` 178）

`_docs.Active.Editor.ContainsFocus` → `Editor.ContainsFocus || CsvSink.Focused`
（実質シンクがフォーカス保持。エディタ側も残すのは遷移瞬間の取りこぼし防止）。

追加: CSVモード中の `Ctrl+G` は GoToLine ではなく `GoToCell` へ読み替える
（現状 Ctrl+G が素通りしてキャレット移動＋`ed.Focus()` で生読みを誘発するため）。

### 6. 発話と既存対策の整理

- ON時: `Say(ModeOn + Cell(...))` を復活（f312586 で削減した部分）。
- `RaiseUiaSelectionEvents=false` は**維持**: メニュー閉塞→一瞬エディタへフォーカスが
  復帰してからシンクへ移る遷移の間、PC-Talker（UIA経路）が `OnGotFocus` の明示
  TextSelectionChangedEvent に反応し得るための防御。
- `CsvController` 冒頭のドキュメントコメントを新設計（シンク方式）に合わせて全面書き換え。

## 残存する既知の読み（許容）

- **メニュー確定時の一瞬の生行断片（ON時）**: WinForms はクリックハンドラ実行前に
  前フォーカス（エディタ）へ復帰させるため、NVDA が生行を読み始める可能性がある。
  直後の `sink.Focus()` のフォーカスイベントで NVDA は発話をキャンセルするため
  「ごく短い断片」まで軽減される。完全排除はアプリ側から不可。実機で許容可否を確認する。
- **OFF後のフォーカス獲得行読み**: 通常編集への復帰として標準挙動。許容。

## エッジケース

- F3/検索系は CSVモード中も動く（SelectCharRange＋Focus）→ スコープ外・申し送り
  （ブロック or 容認を実機確認後に判断）。
- 整形コマンドは既にブロック済（MainForm:596）。
- タブ切替: `FocusTarget` 経由で自動的に正しい側へ。F2 編集中は既存 `AbortEdit` で破棄。
- Alt+Tab 復帰: Windows が最後のフォーカス（シンク）へ復元するため生読みなし。
- ファイル開き直し: `CsvMode=false` リセット済（MainForm:654）。シンクは残るが無害。

## テスト / 検証

- ビルド 0 警告維持。Core テスト非影響（App 層のみの変更）。
- App 層は UI 依存のため自動テスト対象外（既存方針踏襲）。
- 実機 SR チェックリスト更新: ON時に生行が読まれない（断片の許容可否）、
  セル移動が Announcer のみ、クリック後も素キー有効、F2 往復、OFF後の行読み、
  PC-Talker 側の同項目、Shift+矢印等の修飾キーが無反応であること。

## 実装順

1. `CsvFocusSink` 新規＋`Document` 組み込み＋`FocusTarget`
2. フォーカス呼び出し箇所の差し替え（DocumentManager / MainForm / CsvController / CsvCellEditor）
3. GotFocus リダイレクト＋ProcessCmdKey ガード＋Ctrl+G 読み替え
4. ON発話復元＋CsvController コメント刷新
5. ビルド＋実機チェックリスト更新

規模: 新規1ファイル（約30行）＋既存5ファイルの小変更。
