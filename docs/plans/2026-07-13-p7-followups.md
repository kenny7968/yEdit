# P7 チェックリスト申し送り(App 層) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** P7 手動チェックリストで挙がった App 層 5 項目(C-2/G-2/G-3/I-5/F-3・F-4)を修正し、UX/バグ/整理を完了する。

**Architecture:** 各項目は互いに独立。工数昇順(F-3 → G-2 → G-3 → I-5 → C-2)で単一 feature ブランチ(`feature/p7-followups`)に個別コミット。実機 SR 検証は I-5 と C-2 完了後に実施し、全完了→別エージェントレビュー→main へ no-ff マージ。

**Tech Stack:** C# / .NET 9.0 / WinForms / xUnit(Core.Tests、Editor.Tests)

**設計書:** `docs/plans/2026-07-13-p7-followups-design.md`(コミット `9f44a39`)

---

## 前準備: ブランチ作成

**Step 1: main の最新状態を確認**

Run:
```bash
git status
git log --oneline -1
```
Expected: main ブランチ・作業ツリー clean(untracked の `installer/` `publish/` `tools/verify-msaa-client.ps1` は無視)、HEAD は `9f44a39`(設計書コミット)

**Step 2: feature ブランチを切る**

Run:
```bash
git checkout -b feature/p7-followups
```
Expected: `Switched to a new branch 'feature/p7-followups'`

**Step 3: build/test のベースライン確認**

Run:
```bash
dotnet build -c Debug 2>&1 | tail -5
dotnet test -c Debug --nologo 2>&1 | tail -10
```
Expected: 0 warning・全テスト緑(現時点 827 テスト)

---

## Task 1: F-3/F-4 削除(Ctrl+Alt+I 文字情報機能)

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`(3 箇所削除: L222, L292-293, L442-451)
- Delete: `src/yEdit.Core/Reading/CharacterDescriber.cs`
- Delete: `tests/yEdit.Core.Tests/Reading/CharacterDescriberTests.cs`
- Modify: `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\MEMORY.md`(索引行削除)
- Delete: `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\charinfo-cp932-next.md`

### Step 1: 削除前のテスト数を控える

Run:
```bash
dotnet test -c Debug --nologo --filter "FullyQualifiedName~CharacterDescriber" 2>&1 | tail -5
```
Expected: `CharacterDescriberTests` のテスト数を確認(削除後の差分検証用)

### Step 2: MainForm.cs から Ctrl+Alt+I ハンドラを削除

Modify `src/yEdit.App/MainForm.cs` L222:

**削除前:**
```csharp
case Keys.Control | Keys.Alt | Keys.P: AnnouncePosition(); return true;
case Keys.Control | Keys.Alt | Keys.I: AnnounceCharInfo(); return true;
case Keys.Control | Keys.G: GoToLine(); return true;
```

**削除後:**
```csharp
case Keys.Control | Keys.Alt | Keys.P: AnnouncePosition(); return true;
case Keys.Control | Keys.Alt | Keys.G: GoToLine(); return true;
```

(注意: `Keys.Control | Keys.G` は元の記法どおり保持。`Ctrl+Alt+G` に変えない)

### Step 3: MainForm.cs からメニュー項目を削除

Modify `src/yEdit.App/MainForm.cs` L292-293:

**削除前:**
```csharp
read.DropDownItems.Add(new ToolStripMenuItem("現在位置(&P)", null, (_, _) => AnnouncePosition())
{ ShortcutKeyDisplayString = "Ctrl+Alt+P" });
read.DropDownItems.Add(new ToolStripMenuItem("文字情報(&I)", null, (_, _) => AnnounceCharInfo())
{ ShortcutKeyDisplayString = "Ctrl+Alt+I" });
read.DropDownItems.Add(new ToolStripMenuItem("行へ移動(&G)...", null, (_, _) => GoToLine())
{ ShortcutKeyDisplayString = "Ctrl+G" });
```

**削除後:** `"文字情報(&I)"` の 2 行のみ削除、他 2 項目は保持

### Step 4: MainForm.cs から AnnounceCharInfo メソッドを削除

Modify `src/yEdit.App/MainForm.cs` L442-451:

**削除:**
```csharp
/// <summary>キャレット位置の文字情報（全角/半角空白の区別など）を読み上げる。末尾なら案内する。</summary>
private void AnnounceCharInfo()
{
    var ed = _docs.Active?.Editor;
    if (ed is null) return;
    string text = ed.SnapshotText;
    int caret = ed.CaretCharOffset; // 選択端ではなく実キャレット位置の文字を説明する
    if (caret < 0 || caret >= text.Length) { _announcer.Say("文書の末尾"); return; }
    _announcer.Say(CharacterDescriber.DescribeAt(text, caret));
}
```

### Step 5: using 節を確認・整理

Run: `Grep 'CharacterDescriber' src/yEdit.App/MainForm.cs`
Expected: マッチ 0 件

using の `yEdit.Core.Reading` は `PositionFormatter` が同名前空間にあるので**残す**。

### Step 6: Core と Tests からファイルを削除

Run:
```bash
rm src/yEdit.Core/Reading/CharacterDescriber.cs
rm tests/yEdit.Core.Tests/Reading/CharacterDescriberTests.cs
```

### Step 7: ビルド + テスト実行

Run:
```bash
dotnet build -c Debug 2>&1 | tail -10
```
Expected: 0 warning・0 error

Run:
```bash
dotnet test -c Debug --nologo 2>&1 | tail -10
```
Expected: 全緑・削除分だけテスト数が減少(Step 1 で控えた分)

### Step 8: メモリ更新

- `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\charinfo-cp932-next.md` を削除
- `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\MEMORY.md` から `[CharInfo CP932外 次タスク](charinfo-cp932-next.md)` 行を削除

### Step 9: コミット

Run:
```bash
git add -A src/yEdit.App/MainForm.cs
git add src/yEdit.Core/Reading/CharacterDescriber.cs
git add tests/yEdit.Core.Tests/Reading/CharacterDescriberTests.cs
git commit -m "$(cat <<'EOF'
P7 F-3/F-4: Ctrl+Alt+I 文字情報機能を全削除(App/Core/テスト)

P7 手動チェックリスト F-3/F-4 の廃止決定。各 SR(NVDA/PC-Talker/ナレーター)
が文字情報読みコマンドを標準搭載しているため yEdit 独自実装は冗長。

- MainForm.cs: ProcessCmdKey の Ctrl+Alt+I ハンドラ・読み上げメニューの
  「文字情報(&I)」項目・AnnounceCharInfo メソッドを削除
- Core: CharacterDescriber.cs 削除(PositionFormatter は独立=残す)
- テスト: CharacterDescriberTests.cs 削除
- メモ [[charinfo-cp932-next]] は廃止決定でクローズ

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: G-2 検索ダイアログ「次を検索」後の自動閉じ(検索モードのみ)

**Files:**
- Modify: `src/yEdit.App/FindReplaceDialog.cs`

### Step 1: `_isReplaceMode` フィールド追加+SetMode で保持

Modify `src/yEdit.App/FindReplaceDialog.cs`(現状 L11-26 のフィールド定義エリア末尾に追加):

```csharp
private bool _isReplaceMode; // G-2: 検索モードでは「次を検索」後にダイアログを Hide
```

### Step 2: `SetMode` を修正して `_isReplaceMode` を保持

Modify L63-71:

**Before:**
```csharp
public void SetMode(bool replaceMode)
{
    Text = replaceMode ? "置換" : "検索";
    _replacementLabel.Visible = replaceMode;
    _replacement.Visible = replaceMode;
    _replaceOne.Visible = replaceMode;
    _replaceAll.Visible = replaceMode;
    _inSelection.Visible = replaceMode;
}
```

**After:**
```csharp
public void SetMode(bool replaceMode)
{
    _isReplaceMode = replaceMode;
    Text = replaceMode ? "置換" : "検索";
    _replacementLabel.Visible = replaceMode;
    _replacement.Visible = replaceMode;
    _replaceOne.Visible = replaceMode;
    _replaceAll.Visible = replaceMode;
    _inSelection.Visible = replaceMode;
}
```

### Step 3: `_next.Click`/`_prev.Click` を検索モード時 Hide するよう変更

Modify L42-43:

**Before:**
```csharp
_next.Click += (_, _) => _controller.FindNext();
_prev.Click += (_, _) => _controller.FindPrev();
```

**After:**
```csharp
_next.Click += (_, _) => { _controller.FindNext(); if (!_isReplaceMode) Hide(); };
_prev.Click += (_, _) => { _controller.FindPrev(); if (!_isReplaceMode) Hide(); };
```

### Step 4: `ProcessCmdKey` の Enter を検索モード時 Hide

Modify L85:

**Before:**
```csharp
case Keys.Enter when _pattern.Focused: _controller.FindNext(); return true;
```

**After:**
```csharp
case Keys.Enter when _pattern.Focused: _controller.FindNext(); if (!_isReplaceMode) Hide(); return true;
```

### Step 5: F3/Shift+F3 は Hide しないことを確認

L83-84 の `case Keys.F3` / `case Keys.Shift | Keys.F3` は**変更しない**。F3 はダイアログ表示外からも呼ばれるため。

Run: `Grep 'case Keys.F3' src/yEdit.App/FindReplaceDialog.cs`
Expected: 変更前と同じ

### Step 6: ビルド + テスト

Run:
```bash
dotnet build -c Debug 2>&1 | tail -5
dotnet test -c Debug --nologo 2>&1 | tail -5
```
Expected: 0 warning・全緑

### Step 7: コミット

```bash
git add src/yEdit.App/FindReplaceDialog.cs
git commit -m "$(cat <<'EOF'
P7 G-2: 検索モードで「次を検索」後にダイアログ自動閉じ

Ctrl+F 検索ダイアログの「次を検索」/「前を検索」/Enter 押下後に Hide()。
置換モード(Ctrl+H)では操作継続のため現状維持=分岐。

- _isReplaceMode フィールドを SetMode で保持
- _next/_prev Click ハンドラ・ProcessCmdKey の Enter で検索モード時のみ Hide
- F3/Shift+F3 はダイアログ表示外からも使うため変更なし

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: G-3 「置換して次へ」1 回目押下で即置換(VSCode 準拠)

**Files:**
- Modify: `src/yEdit.App/SearchController.cs`(ReplaceOne メソッド L130-165)

### Step 1: 現状ロジックを確認

Read `src/yEdit.App/SearchController.cs` L129-165 で `ReplaceOne` の分岐を再確認する。「現在の選択がヒットでなければ `Find(次を検索)` して return」の箇所が変更ポイント。

### Step 2: ReplaceOne をリファクタ

Modify L129-165:

**Before(該当箇所):**
```csharp
/// <summary>現在の選択が今のヒットなら置換し次へ。違えばまず次を検索（標準の置換動作）。</summary>
public void ReplaceOne()
{
    var ed = ActiveEditor;
    var opts = CurrentOptions();
    var d = _dialog;
    if (ed is null || opts is null || d is null) return;
    if (IsCsvModeActive) { Announce("CSVモードでは置換できません"); return; }

    try
    {
        var snap = ed.CurrentBuffer.Current;
        var searcher = new SnapshotSearcher(opts);
        var (selStart, selEnd) = ed.GetSelectionCharRange();
        MatchSpan? span = null;
        string? repl = null;
        if (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
        {
            span = h;
            repl = searcher.TryReplacementAt(snap, h);
        }

        if (repl is null) { Find(forward: true); return; } // まだヒット未選択 → 次を検索
        // ... 以降 置換 + 次を検索
```

**After(該当箇所):**
```csharp
/// <summary>現ヒット未選択なら次を検索して即置換、選択済なら置換して次へ(VSCode 準拠)。</summary>
public void ReplaceOne()
{
    var ed = ActiveEditor;
    var opts = CurrentOptions();
    var d = _dialog;
    if (ed is null || opts is null || d is null) return;
    if (IsCsvModeActive) { Announce("CSVモードでは置換できません"); return; }

    try
    {
        var snap = ed.CurrentBuffer.Current;
        var searcher = new SnapshotSearcher(opts);
        var (selStart, selEnd) = ed.GetSelectionCharRange();
        MatchSpan? span = null;
        string? repl = null;
        if (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
        {
            span = h;
            repl = searcher.TryReplacementAt(snap, h);
        }

        // G-3 修正: 現ヒット未選択なら次を検索してそのまま即置換する(VSCode 準拠)。
        // 未ヒットで見つからない場合のみ「見つかりません」で終了する。
        if (repl is null)
        {
            int from = selEnd;
            var next = searcher.FindNext(snap, from);
            if (next is null) { Announce("見つかりません"); return; }
            span = next;
            repl = searcher.TryReplacementAt(snap, next.Value);
            if (repl is null) { Announce("見つかりません"); return; }
        }

        ed.ReplaceCharRange(span.Value.Start, span.Value.Length, repl);
        // ... 以降 現状の 次を検索 ロジック そのまま
```

**注意:** L150 の `ed.ReplaceCharRange(span.Start, span.Length, repl);` は `span.Value.Start`/`span.Value.Length` にする必要あり(span を `MatchSpan?` として扱うため)。または `span!.Value.Start` としても可。既存のコード形式に合わせる。

### Step 3: 変数の型調整

`span` を `MatchSpan?` から確定 `MatchSpan` に絞る位置を整理:
- if (repl is null) ブロック内で `span = next; ` した後、次の `ed.ReplaceCharRange` 前で `span!.Value` にアクセス

または簡潔に「repl 判定 → span を non-null に確定」する形にする:

```csharp
if (repl is null)
{
    var next = searcher.FindNext(snap, selEnd);
    if (next is null) { Announce("見つかりません"); return; }
    var replCand = searcher.TryReplacementAt(snap, next.Value);
    if (replCand is null) { Announce("見つかりません"); return; }
    span = next.Value;
    repl = replCand;
}

// 以降 span は non-null の MatchSpan として扱う
ed.ReplaceCharRange(span.Value.Start, span.Value.Length, repl);
```

### Step 4: 挙動記述コメントの更新

L129 の 3 行 docstring を新しい挙動に置換(Step 2 の After に含む)。

### Step 5: ビルド確認

Run: `dotnet build -c Debug 2>&1 | tail -5`
Expected: 0 warning・0 error

### Step 6: 実機で手動確認(テスト作成不可のため)

App 層 UI 統括ロジックのため単体テスト困難。手動シナリオ:

1. `dotnet run --project src/yEdit.App` でアプリ起動
2. 新規タブに `foo bar foo baz foo` を入力
3. `Ctrl+H` で置換ダイアログを開き、検索=`foo`、置換=`XXX`
4. **1 回目**「置換して次を検索(&L)」押下 → **`XXX bar foo baz foo` になり 2 つ目の foo が選択される**ことを確認(現状仕様なら選択のみで置換されない)
5. **2 回目**押下 → `XXX bar XXX baz foo` になる
6. **3 回目**押下 → `XXX bar XXX baz XXX` になる
7. **4 回目**押下 → 「置換しました。これ以上見つかりません」音声通知

### Step 7: 既存テスト実行

Run: `dotnet test -c Debug --nologo 2>&1 | tail -5`
Expected: 全緑(既存の SnapshotSearcher/TextSearcher テストは無影響)

### Step 8: コミット

```bash
git add src/yEdit.App/SearchController.cs
git commit -m "$(cat <<'EOF'
P7 G-3: 「置換して次へ」1 回目押下で即置換(VSCode 準拠)

SearchController.ReplaceOne の分岐を変更。現ヒット未選択でも次を検索して
そのまま置換するようにし、VSCode/秀丸/サクラ等と同じ 1 段動作にする。

Before: 現ヒット未選択 → Find(次を検索) して return(置換されない)
After:  現ヒット未選択 → FindNext → 即置換 → 次を検索

未ヒットケース(見つからない)は「見つかりません」通知で終了。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: I-5 Ctrl+Tab / Ctrl+1〜9 で編集エリア直接フォーカス+SR タブ名能動発声

**Files:**
- Modify: `src/yEdit.App/DocumentManager.cs`(SelectNext L115-123, SelectAt L126-132, フィールド追加)
- Modify: `src/yEdit.App/MainForm.cs`(タブ名発声のハンドラ配線)

### Step 1: DocumentManager にキー切替イベントを追加

Modify `src/yEdit.App/DocumentManager.cs`(フィールド定義部・現状 L14 付近):

追加:
```csharp
/// <summary>キー起因(Ctrl+Tab/Ctrl+1..9)のタブ切替時に発火。MainForm がタブ名を SR に読ませる。</summary>
public event Action<Document>? KeyBasedSwitch;
```

### Step 2: SelectNext のフォーカス+発声を変更

Modify L114-123:

**Before:**
```csharp
/// <summary>タブを相対移動し、フォーカスをタブ列へ移す（SR が選択タブ＝ファイル名を読むため）。</summary>
public void SelectNext(int dir)
{
    int n = _tabs.TabPages.Count;
    if (n == 0) return;
    int i = _tabs.SelectedIndex;
    BeforeActiveChange?.Invoke();   // 切替前に F2 編集等を後始末（キーボード経路）
    _tabs.SelectedIndex = ((i + dir) % n + n) % n; // 端は巡回
    FocusTabStrip(); // タブ列にフォーカスを留め、SR が選択タブ（ファイル名＋位置）を読む
}
```

**After:**
```csharp
/// <summary>タブを相対移動し、直接エディタへフォーカス。SR には KeyBasedSwitch でタブ名を読ませる(I-5)。</summary>
public void SelectNext(int dir)
{
    int n = _tabs.TabPages.Count;
    if (n == 0) return;
    int i = _tabs.SelectedIndex;
    BeforeActiveChange?.Invoke();   // 切替前に F2 編集等を後始末（キーボード経路）
    _tabs.SelectedIndex = ((i + dir) % n + n) % n; // 端は巡回
    FocusActiveEditor();            // I-5: タブ列を経由せず直接エディタへ
    if (Active is { } d) KeyBasedSwitch?.Invoke(d); // SR にタブ名を能動発声させる
}
```

### Step 3: SelectAt のフォーカス+発声を変更

Modify L125-132:

**Before:**
```csharp
/// <summary>指定位置のタブを選択し、フォーカスをタブ列へ移す（SR が選択タブ＝ファイル名を読むため）。</summary>
public void SelectAt(int index)
{
    if (index < 0 || index >= _tabs.TabPages.Count) return;
    BeforeActiveChange?.Invoke();   // 切替前に F2 編集等を後始末（キーボード経路）
    _tabs.SelectedIndex = index;
    FocusTabStrip();
}
```

**After:**
```csharp
/// <summary>指定位置のタブを選択し、直接エディタへフォーカス。SR には KeyBasedSwitch でタブ名を読ませる(I-5)。</summary>
public void SelectAt(int index)
{
    if (index < 0 || index >= _tabs.TabPages.Count) return;
    BeforeActiveChange?.Invoke();   // 切替前に F2 編集等を後始末（キーボード経路）
    _tabs.SelectedIndex = index;
    FocusActiveEditor();            // I-5: タブ列を経由せず直接エディタへ
    if (Active is { } d) KeyBasedSwitch?.Invoke(d); // SR にタブ名を能動発声させる
}
```

### Step 4: OnTabKeyDown コメントを更新

Modify `src/yEdit.App/DocumentManager.cs` L144-159:

L147 のコメント「Ctrl+Tab で SR がファイル名を読む→Enter で本文へ、という流れ」を新仕様に合わせて更新:

```csharp
// タブ列にフォーカスがある状態で Enter を押したらエディタへ移って編集を開始する
// (I-5 以降は Ctrl+Tab/Ctrl+1..9 で直接エディタへ遷移するため、この救済路は Alt+Tab
// 等で直接タブ列にフォーカスが渡った場合のフォールバック)。
```

### Step 5: MainForm に KeyBasedSwitch ハンドラを配線

Modify `src/yEdit.App/MainForm.cs`(DocumentManager インスタンス化直後・`_docs.ActiveDocumentChanged += ...` 付近):

追加:
```csharp
_docs.KeyBasedSwitch += doc => _announcer.Say(doc.TabLabel);
```

**配線場所の確認方法:** `Grep 'ActiveDocumentChanged' src/yEdit.App/MainForm.cs` で `_docs.ActiveDocumentChanged +=` の直後を配線位置とする。

### Step 6: ビルド + テスト

Run:
```bash
dotnet build -c Debug 2>&1 | tail -5
dotnet test -c Debug --nologo 2>&1 | tail -5
```
Expected: 0 warning・全緑

### Step 7: 実機で手動確認

1. `dotnet run --project src/yEdit.App` でアプリ起動
2. 3 つ以上のタブを開く(異なるファイル名で)
3. **Ctrl+Tab** 押下 → **編集エリアに直接キャレットが入る**+SR が新タブのファイル名を読むことを確認
4. **Ctrl+Shift+Tab** で逆方向も同じ確認
5. **Ctrl+1/2/3** で番号指定タブ切替も同じ確認
6. 起動時に開いたタブでは能動発声が**しない**ことを確認(BeforeActiveChange 経由のみ)

### Step 8: コミット

```bash
git add src/yEdit.App/DocumentManager.cs src/yEdit.App/MainForm.cs
git commit -m "$(cat <<'EOF'
P7 I-5: Ctrl+Tab / Ctrl+1..9 で編集エリア直接フォーカス+SR タブ名能動発声

DocumentManager.SelectNext/SelectAt のフォーカス先を FocusTabStrip から
FocusActiveEditor に変更。タブ列を経由せず直接エディタへ入るため Enter
不要=UX 改善。SR がタブ名を読む機会は KeyBasedSwitch イベント経由の
能動発声で補償。

- SelectNext/SelectAt 末尾で KeyBasedSwitch?.Invoke(Active)
- MainForm で _docs.KeyBasedSwitch += doc => _announcer.Say(doc.TabLabel)
- OnTabKeyDown(Enter でエディタ復帰)は Alt+Tab 起因の救済路として温存
- 起動時タブ生成・新規タブ・タブクローズでは発声しない(キー起因のみ)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: C-2 SaveAs 新ダイアログ(パス+エンコード+改行) + Ctrl+Shift+S 配線

**Files:**
- Create: `src/yEdit.App/SaveAsDialog.cs`
- Modify: `src/yEdit.App/MainForm.cs`(ProcessCmdKey に Ctrl+Shift+S 追加、メニューショートカット表示)
- Modify: `src/yEdit.App/FileController.cs`(SaveAsDocument を新ダイアログ経由、WriteToPath オーバーロード追加)

### Step 1: LineEnding の選択肢を確認

Read `src/yEdit.Core/Text/LineEnding.cs`(existing) — 列挙値 CRLF/LF/CR とその表示名変換を確認。既存の変換ヘルパがあれば流用。

### Step 2: SaveAsDialog を新規作成

Create `src/yEdit.App/SaveAsDialog.cs`:

```csharp
using System.Text;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// 名前を付けて保存ダイアログ。パス・文字コード・改行コードを 1 画面で収集する。
/// 参照ボタン内部で SaveFileDialog を呼びパスを取得する(拡張子フィルタは従来どおり)。
/// アクセシビリティ: TabIndex は パス→参照→エンコード→改行→OK→キャンセル の順。
/// </summary>
public sealed class SaveAsDialog : Form
{
    private readonly TextBox _path = new() { Width = 320, AccessibleName = "ファイル名" };
    private readonly ComboBox _encoding = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "文字コード",
    };
    private readonly ComboBox _lineEnding = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 120,
        AccessibleName = "改行コード",
    };

    private static readonly IReadOnlyList<EncodingCatalog.EncodingOption> EncodingChoices
        = EncodingCatalog.SelectableEncodings;

    // 改行の選択肢。表示名/値のペア。
    private static readonly (string Label, LineEnding Value)[] LineEndingChoices = new[]
    {
        ("CRLF (Windows)", LineEnding.Crlf),
        ("LF (Unix)",      LineEnding.Lf),
        ("CR (Old Mac)",   LineEnding.Cr),
    };

    public string SelectedPath => _path.Text;
    public int SelectedCodePage => EncodingChoices[_encoding.SelectedIndex].CodePage;
    public LineEnding SelectedLineEnding => LineEndingChoices[_lineEnding.SelectedIndex].Value;

    public SaveAsDialog(string? initialPath, int currentCodePage, LineEnding currentLineEnding)
    {
        Text = "名前を付けて保存";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _path.Text = initialPath ?? "";

        // エンコード選択肢構築
        int encSel = 0;
        for (int i = 0; i < EncodingChoices.Count; i++)
        {
            _encoding.Items.Add(EncodingChoices[i].DisplayName);
            if (EncodingChoices[i].CodePage == currentCodePage) encSel = i;
        }
        _encoding.SelectedIndex = encSel;

        // 改行選択肢構築
        int leSel = 0;
        for (int i = 0; i < LineEndingChoices.Length; i++)
        {
            _lineEnding.Items.Add(LineEndingChoices[i].Label);
            if (LineEndingChoices[i].Value == currentLineEnding) leSel = i;
        }
        _lineEnding.SelectedIndex = leSel;

        BuildLayout();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Padding = new Padding(12) };

        var pathLabel = new Label { Text = "ファイル名(&F):", AutoSize = true, TabIndex = 0 };
        var browseButton = new Button { Text = "参照(&B)...", AutoSize = true, TabIndex = 2 };
        _path.TabIndex = 1;
        root.Controls.Add(pathLabel, 0, 0);
        root.Controls.Add(_path, 1, 0);
        root.Controls.Add(browseButton, 2, 0);

        var encLabel = new Label { Text = "文字コード(&E):", AutoSize = true, TabIndex = 3 };
        _encoding.TabIndex = 4;
        root.Controls.Add(encLabel, 0, 1);
        root.Controls.Add(_encoding, 1, 1);
        root.SetColumnSpan(_encoding, 2);

        var leLabel = new Label { Text = "改行コード(&L):", AutoSize = true, TabIndex = 5 };
        _lineEnding.TabIndex = 6;
        root.Controls.Add(leLabel, 0, 2);
        root.Controls.Add(_lineEnding, 1, 2);
        root.SetColumnSpan(_lineEnding, 2);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, TabIndex = 7 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, TabIndex = 8 };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        root.Controls.Add(buttons, 0, 3);
        root.SetColumnSpan(buttons, 3);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        browseButton.Click += (_, _) => OnBrowseClicked();
    }

    private void OnBrowseClicked()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "テキスト ファイル (*.txt)|*.txt|マークダウン ファイル (*.md)|*.md|CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
        };
        if (!string.IsNullOrEmpty(_path.Text)) dlg.FileName = System.IO.Path.GetFileName(_path.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.FileName;
    }
}
```

### Step 3: LineEnding 型名の確認

Run: `Grep 'enum LineEnding' src/yEdit.Core/Text/LineEnding.cs`
Expected: 列挙値名(Crlf/Lf/Cr など)を確認。Step 2 のコードと合致するか確認、齟齬あれば修正。

### Step 4: FileController の SaveAsDocument を新ダイアログ経由に

Modify `src/yEdit.App/FileController.cs` L190-201:

**Before:**
```csharp
/// <summary>指定ドキュメントを名前を付けて保存。成功で State.Path とラベルを更新する。</summary>
private bool SaveAsDocument(Document doc)
{
    using var dlg = new SaveFileDialog { Filter = "テキスト ファイル (*.txt)|*.txt|マークダウン ファイル (*.md)|*.md|CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*" };
    if (doc.State.Path is not null) dlg.FileName = System.IO.Path.GetFileName(doc.State.Path);
    if (dlg.ShowDialog(_owner) != DialogResult.OK) return false;
    if (!WriteToPath(doc, dlg.FileName)) return false;
    doc.State.Path = dlg.FileName;
    _docs.UpdateLabel(doc);
    _metaChanged();
    RegisterRecent(dlg.FileName); // 保存先も最近のファイルへ
    return true;
}
```

**After:**
```csharp
/// <summary>指定ドキュメントを名前を付けて保存。成功で State.Path/Encoding/LineEnding とラベルを更新する。</summary>
private bool SaveAsDocument(Document doc)
{
    using var dlg = new SaveAsDialog(doc.State.Path, doc.State.Encoding.CodePage, doc.State.LineEnding);
    if (dlg.ShowDialog(_owner) != DialogResult.OK) return false;
    if (string.IsNullOrWhiteSpace(dlg.SelectedPath))
    {
        MessageBox.Show("ファイル名を指定してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    // 新エンコード/改行を State に反映してから WriteToPath へ(既存 WriteToPath は State を参照する)。
    doc.State.Encoding = EncodingCatalog.Get(dlg.SelectedCodePage);
    doc.State.LineEnding = dlg.SelectedLineEnding;

    if (!WriteToPath(doc, dlg.SelectedPath)) return false;
    doc.State.Path = dlg.SelectedPath;
    _docs.UpdateLabel(doc);
    _metaChanged();
    RegisterRecent(dlg.SelectedPath); // 保存先も最近のファイルへ
    return true;
}
```

**設計の要点:**
- `WriteToPath` 側の変更は最小限に留める(既に `doc.State.Encoding`/`ApplyEol` 経由の `EolMode` を使っている)
- `doc.State.Encoding`/`LineEnding` を先に更新してから `WriteToPath` を呼ぶ=**State を経由**するため副作用の管理が明確
- WriteToPath が失敗した場合の State ロールバック:失敗時は既に State は書き換わっているので、警告メッセージ表示のみ(既存挙動と同じで OK・後続保存で使う)

### Step 5: `HasBom` の扱いを確認

`WriteToPath` は `doc.State.HasBom` も使う(L218)。エンコード変更時に BOM をどうするか?

**方針:** UTF-8/UTF-16 系エンコードでは元の HasBom を継続、それ以外(SJIS/EUC-JP 等)では HasBom=false 固定。ただし単純化のため**現状の HasBom を維持**(ユーザーが BOM を明示指定するダイアログ項目がないため)。将来的に BOM 指定 UI を追加する場合は別途対応。

### Step 6: MainForm に Ctrl+Shift+S を配線

Modify `src/yEdit.App/MainForm.cs` L217-224(ProcessCmdKey スイッチ内):

**Before:**
```csharp
case Keys.Control | Keys.Tab: _docs.SelectNext(+1); return true;
case Keys.Control | Keys.Shift | Keys.Tab: _docs.SelectNext(-1); return true;
case Keys.F3: _search.FindNext(); return true;
case Keys.Shift | Keys.F3: _search.FindPrev(); return true;
```

**After:**
```csharp
case Keys.Control | Keys.Tab: _docs.SelectNext(+1); return true;
case Keys.Control | Keys.Shift | Keys.Tab: _docs.SelectNext(-1); return true;
case Keys.Control | Keys.Shift | Keys.S: _file.SaveAs(); return true; // C-2: Ctrl+Shift+S
case Keys.F3: _search.FindNext(); return true;
case Keys.Shift | Keys.F3: _search.FindPrev(); return true;
```

### Step 7: MainForm メニューの「名前を付けて保存」にショートカットキー表示追加

Modify `src/yEdit.App/MainForm.cs` L256:

**Before:**
```csharp
AddMenuItem(file, "名前を付けて保存(&A)...", (_, _) => _file.SaveAs());
```

**After:**
```csharp
AddMenuItem(file, "名前を付けて保存(&A)...", (_, _) => _file.SaveAs(), Keys.Control | Keys.Shift | Keys.S);
```

**注意:** `AddMenuItem` のシグネチャが `Keys` オプション引数を受けるか確認。Read `src/yEdit.App/MainForm.cs` で `AddMenuItem` の定義を探し、既存の Ctrl+S などと同じパターンで追加。

### Step 8: ビルド + テスト

Run:
```bash
dotnet build -c Debug 2>&1 | tail -10
dotnet test -c Debug --nologo 2>&1 | tail -10
```
Expected: 0 warning・全緑

### Step 9: 実機で手動確認

1. `dotnet run --project src/yEdit.App` でアプリ起動
2. 新規タブに日本語+ASCII 混在テキストを入力
3. **Ctrl+Shift+S** 押下 → 新ダイアログが開くことを確認
4. ファイル名を入力、エンコードを `Shift-JIS` に変更、改行を `LF` に変更、OK
5. ファイル名の指定なしで OK した時に警告が出ることを確認
6. 保存されたファイルを別プロセスで開き、エンコード/改行が指定どおりであることを確認
7. **メニュー**「ファイル → 名前を付けて保存(&A)...」にショートカット `Ctrl+Shift+S` が表示されていることを確認

### Step 10: 実機 SR 確認

- NVDA/PC-Talker/ナレーターで SaveAsDialog を開き、Tab キーで各コントロールが読まれることを確認
- パスコンボが「ファイル名 編集」と読まれる
- エンコード/改行コンボがラベル+値で読まれる
- 参照ボタン、OK/キャンセルが読まれる

### Step 11: コミット

```bash
git add src/yEdit.App/SaveAsDialog.cs src/yEdit.App/FileController.cs src/yEdit.App/MainForm.cs
git commit -m "$(cat <<'EOF'
P7 C-2: SaveAs 新ダイアログ+Ctrl+Shift+S 配線

パス・文字コード・改行コードを 1 画面で収集する自作ダイアログを追加し、
Ctrl+Shift+S でも呼び出せるように配線する。

- 新規: src/yEdit.App/SaveAsDialog.cs
  - パス TextBox + 参照ボタン(内部 SaveFileDialog で拡張子フィルタ維持)
  - EncodingCatalog.SelectableEncodings の ComboBox(現在エンコードをプリセレクト)
  - CRLF/LF/CR の ComboBox(現在改行をプリセレクト)
  - TabIndex 順=パス→参照→エンコード→改行→OK→キャンセル
- FileController.SaveAsDocument: 新ダイアログ経由に改修
  - OK 後に doc.State.Encoding/LineEnding を更新してから WriteToPath 呼び出し
  - パス未指定時は警告
- MainForm.ProcessCmdKey: Ctrl+Shift+S ケース追加
- MainForm メニュー: 「名前を付けて保存(&A)...」にショートカットキー表示

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: 全項目完了後の総合確認

### Step 1: 全テスト実行(build + test)

Run:
```bash
dotnet build -c Release 2>&1 | tail -5
dotnet test -c Release --nologo 2>&1 | tail -10
```
Expected: Release ビルドも 0 warning・全緑

### Step 2: 実機 SR 3 種で総合検証

各 SR(NVDA/PC-Talker/ナレーター)で以下シナリオを実施:

1. **C-2**: Ctrl+Shift+S 押下 → 新ダイアログの全コントロールが読まれる
2. **G-2**: Ctrl+F → 検索文字入力 → Enter で「見つかった」+ダイアログ自動閉じ+F3 で次
3. **G-3**: Ctrl+H → 検索/置換文字入力 → 「置換して次を検索」1 回目で即置換されて「置換しました。N 件中 M 件目」
4. **I-5**: 3 タブ以上開いた状態で Ctrl+Tab → タブ名読み+編集エリアに即キャレット
5. **F-3**: Ctrl+Alt+I 押下 → 無反応(SR 独自の文字情報コマンドで代用)

### Step 3: 別エージェントによるコードレビュー依頼

Agent tool で code-reviewer を起動:

```
description: P7 followups 5 項目のコードレビュー
subagent_type: superpowers:code-reviewer
prompt: feature/p7-followups ブランチの main からの diff をレビュー。
特に (1) SearchController.ReplaceOne の VSCode 準拠改修による副作用、
(2) DocumentManager の KeyBasedSwitch イベントの発火漏れ/多重発火、
(3) SaveAsDialog の State 更新順序と WriteToPath 失敗時の巻き戻し、
(4) F-3 削除で残った参照や dead code、を重点的に確認。
```

### Step 4: レビュー指摘事項の対応 → 再テスト

Critical/Important 指摘があれば修正コミット。Minor は要否判断。

### Step 5: main へマージ

Run:
```bash
git checkout main
git merge --no-ff feature/p7-followups -m "$(cat <<'EOF'
P7 チェックリスト申し送り 5 項目対応(C-2/G-2/G-3/I-5/F-3・F-4)一括マージ

- F-3/F-4: Ctrl+Alt+I 文字情報機能を全削除(App/Core/テスト)
- G-2: 検索モードで「次を検索」後にダイアログ自動閉じ
- G-3: 「置換して次へ」1 回目押下で即置換(VSCode 準拠)
- I-5: Ctrl+Tab/Ctrl+1..9 で編集エリア直接フォーカス+SR タブ名能動発声
- C-2: SaveAs 新ダイアログ(パス+エンコード+改行)+Ctrl+Shift+S 配線

設計書: docs/plans/2026-07-13-p7-followups-design.md
実装計画: docs/plans/2026-07-13-p7-followups.md
実機 SR 3 種検証 OK、別エージェントレビュー OK、全テスト緑、0 警告。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

### Step 6: メモリ更新

- `MEMORY.md` の [[p7-post-checklist-followups]] 行を「対応済み」に更新 or ファイル自体を書き換えて完了フラグを立てる
- 新規メモリ `p7-followups-completed.md` を書いて完了を記録(必要に応じて)

### Step 7: ブランチクリーンアップ

```bash
git branch -d feature/p7-followups
```

---

## 進捗管理

各タスク完了時に `verification-before-completion` スキル(あれば)で確認:
- ビルド 0 warning
- テスト全緑
- 該当項目の手動シナリオ通過

タスク間で問題発見時は前タスクへ戻って修正 → 再テスト。
