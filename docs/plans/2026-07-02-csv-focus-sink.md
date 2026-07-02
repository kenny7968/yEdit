# CSVモード フォーカスシンク方式 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** CSVモード中はキーボードフォーカスを Scintilla から 1×1px のシンクコントロールへ退避し、NVDA のネイティブ Scintilla 読み（生バッファ読み上げ）を全経路で遮断する。

**Architecture:** 設計書 `docs/plans/2026-07-02-csv-focus-sink-design.md` 参照。`Document` に `CsvFocusSink`＋`FocusTarget`（CsvMode ? シンク : エディタ）を追加し、「編集領域へのフォーカス復帰」を全箇所 `FocusTarget` 経由に統一。エディタの GotFocus はCSVモード中シンクへ即時リダイレクトする。

**Tech Stack:** .NET 9 / WinForms / Scintilla5.NET。App/Editor 層は既存方針どおり自動テスト対象外（ビルド 0 警告＝ゲート、最後に実機SR検証）。Core テストは非影響だが最後に回帰確認する。

**ブランチ:** `fix/csv_mode`（作業中の CsvController 変更をTask 0で先にコミットして土台を確定する）

**ビルド:** `dotnet build -nologo -clp:ErrorsOnly`（リポジトリ直下・警告0を確認）
**Coreテスト:** `dotnet test tests/yEdit.Core.Tests -nologo`

---

## Task 0: 作業中変更のコミット（土台の確定）

**Files:**
- Commit: `src/yEdit.App/CsvController.cs`（作業ツリーに未コミット変更あり）
- Commit: `docs/plans/2026-07-02-csv-focus-sink-design.md`（未コミットなら）

**Step 1: 現状確認**

Run: `git status --short`
Expected: `M src/yEdit.App/CsvController.cs`（＋設計書が未コミットならそれも）

**Step 2: コミット**

```bash
git add src/yEdit.App/CsvController.cs docs/plans/2026-07-02-csv-focus-sink-design.md
git commit -m "App: CSVモード中のUIA選択イベント抑止とON時発話の暫定縮退

PC-Talker(UIA経路)の二重読み対策としてRaiseUiaSelectionEvents抑止を導入。
ON時のセル発話縮退はNVDA生読み対策の暫定で、次のフォーカスシンク方式で復元する。"
```

（注: 設計書が既にコミット済みならCsvControllerのみで良い）

---

## Task 1: CsvFocusSink 新規作成＋Document への組み込み

**Files:**
- Create: `src/yEdit.App/CsvFocusSink.cs`
- Modify: `src/yEdit.App/Document.cs`

**Step 1: CsvFocusSink を作成**

`src/yEdit.App/CsvFocusSink.cs` を以下の内容で新規作成:

```csharp
namespace yEdit.App;

/// <summary>
/// CSVモード中にキーボードフォーカスを預かる 1×1px のフォーカスシンク。
/// NVDA はネイティブ Scintilla 統合により、OS イベント（フォーカス獲得・システムキャレット
/// 移動・選択変更）に反応してフォーカスのあるエディタの生バッファを読み上げる。これは
/// アプリ側の UIA イベント抑止では止められないため、CSVモード中はフォーカス自体を本
/// コントロールへ退避して全経路を遮断し、読み上げを Announcer に一本化する。
/// Dock=Fill のエディタより後に親へ追加されることで Z 順の背面に隠れ、視覚影響もない。
/// TabStop=false のため通常モードの Tab 順には乗らず、フォーカスはコードからのみ与える。
/// </summary>
public sealed class CsvFocusSink : Control
{
    public CsvFocusSink()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = false;
        Size = new Size(1, 1);
        Location = new Point(0, 0);
        AccessibleName = "CSV表";           // 着地時に SR が読む名前（設計書の UX 決定事項）
        AccessibleRole = AccessibleRole.Pane;
    }
}
```

**Step 2: Document に CsvSink / FocusTarget を追加**

`src/yEdit.App/Document.cs` のクラス本体を以下へ変更（プロパティ2つとコンストラクタ内の生成を追加）:

```csharp
public sealed class Document
{
    public ScintillaHost Editor { get; }
    public TabPage Page { get; }
    public DocumentState State { get; } = new();

    /// <summary>CSVモード中のフォーカス退避先。生成時に Page へ追加され、
    /// Dock=Fill のエディタの背面に隠れる（視覚影響なし）。</summary>
    public CsvFocusSink CsvSink { get; }

    /// <summary>「編集領域」へフォーカスを戻すときの正しい行き先。CSVモード中はシンク、
    /// 通常時はエディタ。編集領域への Focus() 呼び出しは必ずこれを経由すること。</summary>
    public Control FocusTarget => State.CsvMode ? CsvSink : Editor;

    public Document(ScintillaHost editor, TabPage page)
    {
        Editor = editor;
        Page = page;
        CsvSink = new CsvFocusSink();
        page.Controls.Add(CsvSink);   // editor(Dock=Fill) より後に追加 → Z順で背面
    }

    /// <summary>タブに表示するラベル（ファイル名＋変更マーク）。</summary>
    public string TabLabel => State.DisplayName + (Editor.Modified ? " *" : "");
}
```

**Step 3: ビルド確認**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: エラー・警告 0

**Step 4: コミット**

```bash
git add src/yEdit.App/CsvFocusSink.cs src/yEdit.App/Document.cs
git commit -m "App: CSVモード用フォーカスシンクと Document.FocusTarget を追加"
```

---

## Task 2: CsvController / CsvCellEditor のフォーカス先切り替えと ON 発話復元

**Files:**
- Modify: `src/yEdit.App/CsvCellEditor.cs`
- Modify: `src/yEdit.App/CsvController.cs`

**Step 1: CsvCellEditor の復帰先を注入式にする**

`_ed` フィールド（Teardown の復帰先にしか使っていない）を `_refocus` に置き換える:

1. フィールド `private ScintillaHost? _ed;` → `private Control? _refocus;`
2. `Begin` のシグネチャに復帰先を追加:
   ```csharp
   public void Begin(ScintillaHost ed, CsvField field, Control refocusTarget, Action<string> onCommit, Action onCancel)
   ```
   本体先頭の `_ed = ed; _onCommit = onCommit; ...` → `_refocus = refocusTarget; _onCommit = onCommit; ...`
3. `Teardown` 内 `if (refocus) _ed?.Focus();` → `if (refocus) _refocus?.Focus();`、
   `_ed = null;` → `_refocus = null;`
4. クラスの doc コメント末尾「フォーカス復帰のみ担う」の説明に
   「復帰先は呼び出し元が指定する（CSVモード中はフォーカスシンク）」の旨を追記。

**Step 2: CsvController.ToggleMode を書き換える**

ON 分岐（`if (!doc.State.CsvMode)` 側）を以下へ:

```csharp
var csv = ParseCached(doc.Editor);
if (!csv.Ok) { _announcer.Say(CsvAnnounceFormatter.ParseError); return; } // 解析不可ならモードに入らない
doc.State.CsvMode = true;
doc.Editor.ReadOnly = true;
// PC-Talker（UIA経路）向け防御: シンクへ移る遷移の一瞬にエディタがフォーカスを
// 得た際、OnGotFocus の明示 TextSelectionChangedEvent で行を読まれるのを防ぐ。
doc.Editor.RaiseUiaSelectionEvents = false;
if (csv.Rows.Count == 0)
{
    doc.Editor.ClearHighlight();
    doc.CsvSink.Focus();               // データ無しでもフォーカスはシンクへ退避する
    _announcer.Say(CsvAnnounceFormatter.ModeOn);
    return;
}
// ON 時のみ、その時点のキャレット位置から初期セルを導出する（以降はキャレットではなく状態を真実源にする）。
var (row, col) = csv.FindCell(doc.Editor.CaretCharOffset);
doc.State.CsvRow = row;
doc.State.CsvCol = col;
ApplyCell(doc.Editor, csv, row, col, announce: false);   // ハイライト＋スクロール＋シンクへフォーカス
var f = csv.GetField(row, col);
_announcer.Say(f is null
    ? CsvAnnounceFormatter.ModeOn
    : CsvAnnounceFormatter.ModeOn + " " + CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
```

OFF 分岐を以下へ（**CsvMode=false を先に**して GotFocus リダイレクトのガードを外してから
エディタへフォーカスを返す。順序が重要）:

```csharp
var csv = ParseCached(doc.Editor);
doc.State.CsvMode = false;                 // 先に解除（エディタ GotFocus のシンク退避ガードを外す）
doc.Editor.ReadOnly = false;
doc.Editor.RaiseUiaSelectionEvents = true; // 通常編集の SR 挙動へ復帰
doc.Editor.ClearHighlight();
// モード中に動かなかったキャレットを最終セル位置へ復帰させ、編集領域へフォーカスを返す。
// 以降は通常編集なので、SR がフォーカス獲得で現在行を読むのは標準挙動として許容。
if (csv.Ok && csv.Rows.Count > 0)
{
    var f = csv.GetField(doc.State.CsvRow, doc.State.CsvCol);
    if (f is not null) doc.Editor.MoveCaretCharOffset(f.Start);
}
doc.Editor.Focus();
_announcer.Say(CsvAnnounceFormatter.ModeOff);
```

**Step 3: ApplyCell のフォーカス先をシンクへ**

`ed.Focus();` の行を削除し、doc の null チェック内で `FocusTarget` を使う:

```csharp
var doc = _docs.Active;
if (doc is not null)
{
    doc.State.CsvRow = row;
    doc.State.CsvCol = col;
    doc.FocusTarget.Focus();   // CSVモード中はシンク（Scintilla に SR のフォーカス読みを向けない）
}
if (announce) _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
```

あわせて ApplyCell の doc コメントの「システムキャレットは動かさない…」に
「フォーカスもシンクに置く」旨を追記。

**Step 4: BeginEdit の Begin 呼び出しに復帰先を渡す**

```csharp
_editor.Begin(ed, f, _docs.Active!.CsvSink,   // TryContext 成功時は Active 非 null
    onCommit: text =>
    ...
```

**Step 5: クラス冒頭の doc コメントを刷新**

CsvController 冒頭コメント（`/// 目的: ...` 〜 `/// 初期セルは...` の段落）を新設計に書き換える:

```
/// 目的: NVDA はネイティブ Scintilla 統合（クラス名 "Scintilla"・UIA 非依存）で、OS の
/// フォーカス獲得・キャレット移動・選択変更に反応して生バッファを読み上げる。これは
/// アプリ側では抑止できないため、CSVモード中はフォーカス自体を Document.CsvSink
/// （1×1px のフォーカスシンク）へ退避して全経路を遮断し、読み上げを Announcer に一本化する。
/// システムキャレットも動かさない（可視域スクロールはキャレット無移動の
/// EnsureVisibleCharRange）。RaiseUiaSelectionEvents=false は PC-Talker（UIA 経路）向けの
/// 防御（シンクへ移る遷移の一瞬に OnGotFocus の明示イベントで読まれるのを防ぐ）。
/// F2 は CsvCellEditor に委譲し、終了時の復帰先もシンクにする。
```

**Step 6: ビルド確認**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: エラー・警告 0

**Step 7: コミット**

```bash
git add src/yEdit.App/CsvController.cs src/yEdit.App/CsvCellEditor.cs
git commit -m "App: CSVモードのフォーカスをシンクへ退避しON時セル発話を復元"
```

---

## Task 3: DocumentManager / MainForm のフォーカス配線とキーガード

**Files:**
- Modify: `src/yEdit.App/DocumentManager.cs`
- Modify: `src/yEdit.App/MainForm.cs`

**Step 1: DocumentManager — FocusTarget 化＋GotFocus 通知イベント**

1. イベントを追加（`ActiveCaretChanged` の宣言の下）:
   ```csharp
   /// <summary>アクティブ Document のエディタが Win32 フォーカスを得た（CSVモードの
   /// シンク退避判断は上位 MainForm が行う。_csv.IsEditing を参照できるのが上位のため）。</summary>
   public event Action<Document>? EditorGotFocus;
   ```
2. `CreateNew` 内の `editor.UpdateUI += ...` 購読の直後に追加:
   ```csharp
   editor.GotFocus += (_, _) =>
   {
       if (ReferenceEquals(doc, Active)) EditorGotFocus?.Invoke(doc);
   };
   ```
3. `Activate` の `doc.Editor.Focus();` → `doc.FocusTarget.Focus();`（コメントは維持）
4. `FocusActiveEditor` の本体 `Active?.Editor.Focus();` → `Active?.FocusTarget.Focus();`

**Step 2: MainForm — シンク退避リダイレクトの配線**

コンストラクタの `_csv = new CsvController(_docs, _announcer);` の直後に追加:

```csharp
// CSVモード中に Scintilla がフォーカスを得たら（メニュー閉塞後の復帰・マウスクリック等）
// シンクへ即時退避する。NVDA のネイティブ読み（フォーカス駆動）を Scintilla に向けない。
// BeginInvoke は GotFocus 中の再入 Focus() を避けるため必須。
_docs.EditorGotFocus += doc =>
{
    if (!doc.State.CsvMode || _csv.IsEditing) return;
    BeginInvoke(() => { if (doc.State.CsvMode && !_csv.IsEditing) doc.CsvSink.Focus(); });
};
```

**Step 3: MainForm — ProcessCmdKey のガードと追加キー**

CSVモード横取りの if 条件（現在 `_docs.Active?.State.CsvMode == true && !_csv.IsEditing && _docs.Active.Editor.ContainsFocus && !_menuActive`）を以下へ:

```csharp
var activeDoc = _docs.Active;
if (activeDoc?.State.CsvMode == true && !_csv.IsEditing && !_menuActive &&
    (activeDoc.Editor.ContainsFocus || activeDoc.CsvSink.Focused))
```

（コメントに「通常はシンクがフォーカス保持。エディタ側も残すのは遷移瞬間の取りこぼし防止」を追記）

switch に 2 ケース追加:

```csharp
case Keys.Shift | Keys.Tab: _csv.ReadCurrent(); return true; // Shift+Tab でフォーカスがシンクから逃げるのを防ぐ
case Keys.Control | Keys.G: _csv.GoToCell(); return true;    // 行ジャンプはCSVモード中セル指定に読み替え（素通りするとキャレット移動＋生読みを誘発）
```

**Step 4: MainForm — 編集領域へのフォーカス復帰を FocusTarget 化**

以下 4 箇所の `Editor.Focus()` を `FocusTarget.Focus()` に変更:
- `OnShown`（84行付近）: `_docs.Active?.Editor.Focus();` → `_docs.Active?.FocusTarget.Focus();`
- `OpenAndSelect`（520行付近・grep ジャンプ）: `doc.Editor.Focus();` → `doc.FocusTarget.Focus();`
- Markdownプレビュー復帰（586行付近）: `_docs.Active?.Editor.Focus();` → `_docs.Active?.FocusTarget.Focus();`
- タブクローズ後（769行付近）: `_docs.Active?.Editor.Focus();` → `_docs.Active?.FocusTarget.Focus();`

（GoToLine と整形コマンド内の `ed.Focus()` は変更しない: 前者は Step 3 の読み替えで
CSVモード中は到達不能、後者は既に CSVモード中ブロック済みのため）

**Step 5: ビルド確認**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: エラー・警告 0

**Step 6: コミット**

```bash
git add src/yEdit.App/DocumentManager.cs src/yEdit.App/MainForm.cs
git commit -m "App: 編集領域フォーカスをFocusTarget経由に統一しCSVモードのシンク退避を配線"
```

---

## Task 4: 回帰確認と実機SR検証チェックリスト

**Step 1: 全体ビルド＋Coreテスト**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: エラー・警告 0

Run: `dotnet test tests/yEdit.Core.Tests -nologo`
Expected: 全テスト成功（App層のみの変更なので回帰しないはず。失敗したら原因を調査すること）

**Step 2: 実機SR検証チェックリストを本ファイル末尾に追記してコミット**

```bash
git add docs/plans/2026-07-02-csv-focus-sink.md
git commit -m "docs: フォーカスシンク実装完了・実機SR検証チェックリスト"
```

---

## 実機SR検証チェックリスト（ユーザー実施・NVDA / PC-Talker 各々）

- [ ] CSVモードON（メニュー）: 生のCSV行が読まれない（メニュー閉塞直後の「ごく短い断片」の許容可否も判断）。「CSVモード オン＋セル内容＋位置」が読まれる
- [ ] 矢印キーでのセル移動: Announcer のセル読みのみ（生行・「選択」読みが混ざらない）
- [ ] Shift+矢印 / Ctrl+矢印 / Shift+Home/End: 無反応（生読み・選択読みが発生しない）
- [ ] Tab / Shift+Tab: 現在セル再読。フォーカスがタブ列等へ逃げない
- [ ] 本文をマウスクリック: 生行読みが（断片以上に）発生せず、直後の素キーナビが継続して効く
- [ ] G / Ctrl+G: セル指定ダイアログ。閉じた後に生行読みが出ない
- [ ] F2 編集: TextBox へ移動→Enter確定/Esc取消→復帰後に生行読みが出ず、素キーナビが効く
- [ ] CSVモードOFF: 「CSVモード オフ」＋（標準挙動の）現在行読み。キャレットが最終セル位置にある
- [ ] CSVモードOFF直後にPC-Talkerで二重読みがないか（キャレット復帰のUIAイベントは同期経路のみ抑止のため、遅延配送で読まれる可能性を実機確認）
- [ ] タブ切替（Ctrl+Tab→Enter）: CSVモードのタブへ戻ると素キーナビが効く（フォーカスがシンクに乗る）
- [ ] Alt+Tab で他アプリ→復帰: 生行読みが出ない
- [ ] 通常モードのタブでは従来どおり編集・読み上げができる（回帰なし）
- [ ] 通常モード（CSVモードOFF）のタブで、SRのオブジェクトナビゲーションに「CSV表」ペインが現れても実害がないか（シンクは常時存在するため）

## 申し送り（本計画のスコープ外）

- F3/検索系は CSVモード中も本文選択を動かす（生読みは出ないがセル状態と不整合）。ブロックか容認かは実機確認後に判断
- grep結果ジャンプ（OpenAndSelect）はCSVモード中のタブに対して本文選択を動かす（生読みはFocusTarget化で防止済み・セル状態との不整合はF3系と同族）
- 本文クリックでクリック位置のセルへ現在セルを移動する改善（晴眼者向け）
- メニュー閉塞直後の「ごく短い生行断片」はアプリ側で完全排除不可（NVDA のフォーカス読みが OS イベント駆動のため）。実機で許容可否を確認
