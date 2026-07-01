# 新CSVモード（グリッド型ナビゲーション）Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `.csv` 自動判定をやめ、メニューで手動有効化する「グリッド型CSVモード」（読取専用＋素キーのセルナビ＋F2オーバーレイ編集）を実装し、main の旧オーバーレイ方式と実機比較できるようにする。

**Architecture:** Scintilla 本体は維持。CSVモード中は `ReadOnly=true` にして本文を編集不可にし、矢印・英字キーを `MainForm.ProcessCmdKey` で横取りして「コマンド」化する。現在セルはインジケータ枠でハイライトし、読み上げは既存 `IAnnouncer.Say` で明示的に行う（PC-Talker実証済みa11yを温存）。F2 のセル編集だけ Scintilla 上にオーバーレイした専用 TextBox で行い、確定時に CSV へ再直列化して本文へ反映する。

**Tech Stack:** C# / .NET 9 / WinForms / ScintillaNET（desjarlais/Scintilla5.NET）/ xUnit。設計詳細は `docs/plans/2026-06-29-csv-grid-mode-design.md`。

**前提:**
- 作業はワークツリー `.worktrees/csv-grid-mode`（ブランチ `feature/csv-grid-mode`）。
- ベースライン: build 0 警告 / Core 238 テスト緑。
- Core 層は TDD（テスト先行）。App/Editor 層（WinForms・SR）は既存方針どおり自動テスト対象外で、**ビルド成功**を各タスクのゲートとし、最後に**手動実機検証**でUX/SRを確認する。
- ビルド: `dotnet build -nologo -clp:ErrorsOnly`（ワークツリー直下）。
- Core テスト: `dotnet test tests/yEdit.Core.Tests -nologo`。

---

## Task 1: Core — CSV フィールドの直列化 `CsvWriter.EscapeField`

F2 確定時に編集後の論理値を CSV フィールド文字列へ戻すための純ロジック。

**Files:**
- Create: `src/yEdit.Core/Csv/CsvWriter.cs`
- Create: `tests/yEdit.Core.Tests/Csv/CsvWriterTests.cs`

**Step 1: Write the failing tests**

`tests/yEdit.Core.Tests/Csv/CsvWriterTests.cs`:
```csharp
using yEdit.Core.Csv;
using Xunit;

namespace yEdit.Core.Tests.Csv;

public class CsvWriterTests
{
    [Fact] public void Plain_value_is_unchanged() => Assert.Equal("abc", CsvWriter.EscapeField("abc"));
    [Fact] public void Empty_value_is_unchanged() => Assert.Equal("", CsvWriter.EscapeField(""));
    [Fact] public void Comma_is_quoted() => Assert.Equal("\"a,b\"", CsvWriter.EscapeField("a,b"));
    [Fact] public void Quote_is_doubled_and_wrapped() => Assert.Equal("\"he \"\"q\"\"\"", CsvWriter.EscapeField("he \"q\""));
    [Fact] public void Lf_is_quoted() => Assert.Equal("\"a\nb\"", CsvWriter.EscapeField("a\nb"));
    [Fact] public void Cr_is_quoted() => Assert.Equal("\"a\rb\"", CsvWriter.EscapeField("a\rb"));
    [Fact] public void Leading_space_is_not_quoted() => Assert.Equal(" a ", CsvWriter.EscapeField(" a "));
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/yEdit.Core.Tests -nologo`
Expected: コンパイルエラー（`CsvWriter` 未定義）。

**Step 3: Write minimal implementation**

`src/yEdit.Core/Csv/CsvWriter.cs`:
```csharp
namespace yEdit.Core.Csv;

/// <summary>CSV フィールドの直列化（RFC 4180・区切りはカンマ固定）。F2 編集確定時に使う。</summary>
public static class CsvWriter
{
    /// <summary>論理値を CSV フィールド文字列へ直列化する。カンマ・二重引用符・CR・LF を含む場合のみ
    /// 二重引用符で囲み、内部の " を "" にエスケープする。それ以外は素通し。</summary>
    public static string EscapeField(string value)
    {
        bool needsQuote =
            value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 ||
            value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0;
        return needsQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/yEdit.Core.Tests -nologo`
Expected: 全テスト PASS（7 件追加）。

**Step 5: Commit**
```bash
git add src/yEdit.Core/Csv/CsvWriter.cs tests/yEdit.Core.Tests/Csv/CsvWriterTests.cs
git commit -m "Core: CSVフィールド直列化 CsvWriter.EscapeField を追加（TDD）"
```

---

## Task 2: Core — `CsvDocument` グリッド移動ヘルパ

新キー（Home/End/PageUp/PageDown/Ctrl+Home/Ctrl+End/G）と列頭読み上げの座標計算。すべて `(int row, int col)?` を返し、データ無し・範囲外は null。

**Files:**
- Modify: `src/yEdit.Core/Csv/CsvDocument.cs`（末尾に追加）
- Create: `tests/yEdit.Core.Tests/Csv/CsvGridNavigationTests.cs`

**Step 1: Write the failing tests**

`tests/yEdit.Core.Tests/Csv/CsvGridNavigationTests.cs`:
```csharp
using yEdit.Core.Csv;
using Xunit;

namespace yEdit.Core.Tests.Csv;

public class CsvGridNavigationTests
{
    private static CsvDocument Doc(string t) => CsvParser.Parse(t);

    [Fact] public void RowStart_and_RowEnd()
    {
        var d = Doc("a,b,c\nd,e,f");
        Assert.Equal((1, 0), d.RowStart(1));
        Assert.Equal((1, 2), d.RowEnd(1));
    }

    [Fact] public void ColumnTop_and_ColumnBottom_uniform()
    {
        var d = Doc("a,b\nc,d\ne,f");
        Assert.Equal((0, 1), d.ColumnTop(1));
        Assert.Equal((2, 1), d.ColumnBottom(1));
    }

    [Fact] public void ColumnBottom_skips_rows_missing_that_column()
    {
        var d = Doc("a,b,c\nd,e,f\ng");   // 3行目は1列のみ
        Assert.Equal((1, 2), d.ColumnBottom(2));   // 列2を持つ最後の行は2行目
    }

    [Fact] public void ColumnTop_skips_leading_rows_missing_that_column()
    {
        var d = Doc("g\na,b,c\nd,e,f");   // 1行目は1列のみ
        Assert.Equal((1, 2), d.ColumnTop(2));      // 列2を持つ最初の行は2行目
    }

    [Fact] public void TopLeft_and_BottomRight()
    {
        var d = Doc("a,b,c\nd,e");
        Assert.Equal((0, 0), d.TopLeft());
        Assert.Equal((1, 1), d.BottomRight());     // 最終行の最終列
    }

    [Fact] public void GoTo_valid_and_out_of_range()
    {
        var d = Doc("a,b\nc,d");
        Assert.Equal((1, 1), d.GoTo(1, 1));
        Assert.Null(d.GoTo(5, 0));
        Assert.Null(d.GoTo(0, 9));
        Assert.Null(d.GoTo(-1, 0));
    }

    [Fact] public void Helpers_on_empty_doc_return_null()
    {
        var d = Doc("");
        Assert.Null(d.RowStart(0));
        Assert.Null(d.RowEnd(0));
        Assert.Null(d.ColumnTop(0));
        Assert.Null(d.ColumnBottom(0));
        Assert.Null(d.TopLeft());
        Assert.Null(d.BottomRight());
        Assert.Null(d.GoTo(0, 0));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/yEdit.Core.Tests -nologo`
Expected: コンパイルエラー（メソッド未定義）。

**Step 3: Write minimal implementation**

`src/yEdit.Core/Csv/CsvDocument.cs` の `Header` メソッドの直後（クラス内末尾）に追加:
```csharp
    /// <summary>row 行の左端セル (row,0)。行が無い/空ならnull。</summary>
    public (int row, int col)? RowStart(int row)
        => (row >= 0 && row < Rows.Count && Rows[row].Count > 0) ? (row, 0) : null;

    /// <summary>row 行の右端セル。行が無い/空ならnull。</summary>
    public (int row, int col)? RowEnd(int row)
        => (row >= 0 && row < Rows.Count && Rows[row].Count > 0) ? (row, Rows[row].Count - 1) : null;

    /// <summary>col 列を持つ最初の行のセル (r,col)。どの行も持たなければnull。</summary>
    public (int row, int col)? ColumnTop(int col)
    {
        if (col < 0) return null;
        for (int r = 0; r < Rows.Count; r++)
            if (Rows[r].Count > col) return (r, col);
        return null;
    }

    /// <summary>col 列を持つ最後の行のセル (r,col)。どの行も持たなければnull。</summary>
    public (int row, int col)? ColumnBottom(int col)
    {
        if (col < 0) return null;
        for (int r = Rows.Count - 1; r >= 0; r--)
            if (Rows[r].Count > col) return (r, col);
        return null;
    }

    /// <summary>左上セル (0,0)。データ無しならnull。</summary>
    public (int row, int col)? TopLeft()
        => (Rows.Count > 0 && Rows[0].Count > 0) ? (0, 0) : null;

    /// <summary>右下セル（最終行の最終列）。データ無しならnull。</summary>
    public (int row, int col)? BottomRight()
    {
        if (Rows.Count == 0) return null;
        int r = Rows.Count - 1;
        return Rows[r].Count > 0 ? (r, Rows[r].Count - 1) : null;
    }

    /// <summary>(row,col) が有効なら そのまま返す。範囲外はnull。</summary>
    public (int row, int col)? GoTo(int row, int col)
        => (row >= 0 && row < Rows.Count && col >= 0 && col < Rows[row].Count) ? (row, col) : null;
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/yEdit.Core.Tests -nologo`
Expected: 全テスト PASS（7 件追加）。

**Step 5: Commit**
```bash
git add src/yEdit.Core/Csv/CsvDocument.cs tests/yEdit.Core.Tests/Csv/CsvGridNavigationTests.cs
git commit -m "Core: CsvDocument にグリッド移動ヘルパ（行頭末/列頭末/左上右下/GoTo）を追加（TDD）"
```

---

## Task 3: Core — 編集ラウンドトリップのテスト

`EscapeField` → `CsvParser.Parse` で論理値が保たれることを保証（F2 編集の正しさの土台）。

**Files:**
- Modify: `tests/yEdit.Core.Tests/Csv/CsvWriterTests.cs`（テスト追加）

**Step 1: Write the failing test**

`CsvWriterTests` クラスに追加:
```csharp
    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("a,b,c")]
    [InlineData("he said \"hi\"")]
    [InlineData("line1\nline2")]
    [InlineData("comma, and \"quote\"")]
    public void Roundtrip_escape_then_parse_preserves_value(string value)
    {
        // 1行1セルの CSV として直列化→パースし、論理値が戻ることを確認。
        string csvText = CsvWriter.EscapeField(value);
        var doc = CsvParser.Parse(csvText);
        Assert.True(doc.Ok);
        Assert.Equal(value, doc.GetField(0, 0)!.Value);
    }
```

**Step 2: Run test to verify it fails or passes**

Run: `dotnet test tests/yEdit.Core.Tests -nologo`
Expected: PASS（Task 1/2 実装済みなら通る）。万一 `""`（空）で Parse が行を生成せず GetField が null になる場合は、テストを `value==""` 時に `csvText=="\"\""` を直列化する形へ寄せず、空セル1つを期待する形に調整する（空文字は EscapeField で素通し→Parse は空フィールド1つを生成するため (0,0) は空文字で取得できる想定）。

注: `CsvParser.Parse("")` は Rows.Count==0 だが、`EscapeField("")` は `""`(空文字) を返し、`Parse("")` 同様になる。空ケースで `GetField(0,0)` が null になるなら、空入力のみ別アサート（`Assert.Equal(0, doc.Rows.Count)`）に分離してよい。

**Step 3: （必要なら）テスト調整**

上記の空ケースで失敗した場合は、空文字 InlineData を削除し、別途:
```csharp
    [Fact] public void Empty_value_parses_to_no_rows()
        => Assert.Equal(0, CsvParser.Parse(CsvWriter.EscapeField("")).Rows.Count);
```
に置き換える。

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/yEdit.Core.Tests -nologo`
Expected: 全テスト PASS。

**Step 5: Commit**
```bash
git add tests/yEdit.Core.Tests/Csv/CsvWriterTests.cs
git commit -m "Core: EscapeField→Parse ラウンドトリップのテストを追加"
```

---

## Task 4: Editor — `ScintillaHost` にセル編集支援 API を追加

F2 オーバーレイ TextBox の配置に必要な座標・行高 API を追加する（UI スレッド専用）。

**Files:**
- Modify: `src/yEdit.Editor/Sci.cs`（定数追加）
- Modify: `src/yEdit.Editor/ScintillaHost.cs`（メソッド追加）

**Step 1: Sci.cs に定数追加**

`SCI_TEXTWIDTH` の行の直後に追加:
```csharp
    public const int SCI_TEXTHEIGHT = 2279;       // (line) → 当該行の表示高さ(px)
```

**Step 2: ScintillaHost.cs にメソッド追加**

「表示折り返し」セクションの直前（`// ==================== IUiaTextHost` の直前）に新セクションを追加:
```csharp
    // ==================== CSV グリッド編集支援（UI スレッド専用） ====================

    /// <summary>文字オフセット(UTF-16)のクライアント座標(px)。F2 セル編集オーバーレイの配置に使う
    /// （SCI_POINTX/YFROMPOSITION。lParam=byte position）。</summary>
    public System.Drawing.Point PointFromCharOffset(int offset)
    {
        if (!IsHandleCreated) return System.Drawing.Point.Empty;
        int bytePos = Utf16ToByte(SnapToCodepoint(Clamp16(offset)));
        int x = DirectMessage(Sci.SCI_POINTXFROMPOSITION, nint.Zero, (nint)bytePos).ToInt32();
        int y = DirectMessage(Sci.SCI_POINTYFROMPOSITION, nint.Zero, (nint)bytePos).ToInt32();
        return new System.Drawing.Point(x, y);
    }

    /// <summary>1 行の表示高さ(px)。セル編集ボックスの高さ決定に使う。</summary>
    public int LineHeightPx
        => IsHandleCreated ? DirectMessage(Sci.SCI_TEXTHEIGHT, (nint)0).ToInt32() : 16;
```

注: `ReadOnly` は ScintillaNET の `Scintilla` 基底が既に公開している（SCI_SETREADONLY/SCI_GETREADONLY）。新規追加は不要で、`ed.ReadOnly = true/false` を直接使う。

**Step 3: Build to verify it compiles**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: ビルド成功・0 警告。

**Step 4: Commit**
```bash
git add src/yEdit.Editor/Sci.cs src/yEdit.Editor/ScintillaHost.cs
git commit -m "Editor: ScintillaHost に PointFromCharOffset / LineHeightPx を追加（F2編集オーバーレイ用）"
```

---

## Task 5: App — `CsvAnnounceFormatter` に読み上げ文言を追加

**Files:**
- Modify: `src/yEdit.Core/Csv/CsvAnnounceFormatter.cs`

**Step 1: 文言定数を追加**

`CannotMove` の直後に追加:
```csharp
    /// <summary>セル指定移動で範囲外を指定したときの読み上げ。</summary>
    public const string OutOfRange = "範囲外です";
    /// <summary>セル指定の書式が不正なときの読み上げ。</summary>
    public const string BadCellFormat = "書式が不正です。行,列 の形式で入力してください";
```

**Step 2: Build**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: 成功・0 警告。

**Step 3: Commit**
```bash
git add src/yEdit.Core/Csv/CsvAnnounceFormatter.cs
git commit -m "Core: CSV 読み上げ文言（範囲外/書式不正）を追加"
```

---

## Task 6: App — `CsvGoToCellDialog`（G キー入力ボックス）

「行,列」（1始まり、例 `2,3`）を1つの入力欄で受ける。IME 無効で全角数字事故を防ぐ。

**Files:**
- Create: `src/yEdit.App/CsvGoToCellDialog.cs`

**Step 1: ダイアログを実装**

`src/yEdit.App/CsvGoToCellDialog.cs`:
```csharp
using System.Globalization;

namespace yEdit.App;

/// <summary>セル指定移動のダイアログ。「行,列」（1始まり・例 2,3）を1欄で受ける。
/// IME を無効化し JIS 環境で全角数字が入る事故を防ぐ。GoToLineDialog と同方式。</summary>
public sealed class CsvGoToCellDialog : Form
{
    private readonly TextBox _input = new() { Width = 140, ImeMode = ImeMode.Disable };

    public CsvGoToCellDialog(int currentRow, int currentCol)
    {
        Text = "セルへ移動";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _input.Text = $"{currentRow},{currentCol}";
        _input.AccessibleName = "行カンマ列。例 2,3";

        BuildLayout();
        _input.Select(0, _input.Text.Length);
    }

    /// <summary>入力を 1 始まりの (row, col) として解釈する。形式不正なら false。</summary>
    public bool TryGetCell(out int row, out int col)
    {
        row = col = 0;
        var parts = _input.Text.Split(',');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out row)) return false;
        if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out col)) return false;
        return row >= 1 && col >= 1;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        root.Controls.Add(new Label { Text = "行,列(&C)（例 2,3）:", AutoSize = true }, 0, 0);
        root.Controls.Add(_input, 1, 0);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        root.Controls.Add(buttons, 0, 1);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
```

**Step 2: Build**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: 成功・0 警告。

**Step 3: Commit**
```bash
git add src/yEdit.App/CsvGoToCellDialog.cs
git commit -m "App: セル指定移動ダイアログ CsvGoToCellDialog を追加"
```

---

## Task 7: App — `CsvCellEditor`（F2 オーバーレイ編集）

Scintilla 上に重ねた TextBox でセル値だけを編集する。本文には触れず、確定値（\n 正規化済み）/取消をコールバックで返す。

**Files:**
- Create: `src/yEdit.App/CsvCellEditor.cs`

**Step 1: 実装**

`src/yEdit.App/CsvCellEditor.cs`:
```csharp
using yEdit.Core.Csv;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// F2 セル編集のオーバーレイ TextBox。Scintilla 本文は読取専用のまま、セル値だけを
/// 通常の EDIT コントロールで編集する（カーソルはセル内のみ＝この TextBox 内のみ）。
/// 確定文字列の本文反映は呼び出し元（CsvController）が CSV 直列化して行う。本クラスは
/// TextBox の生成・配置・キー処理（Enter=確定 / Alt+Enter=改行 / Esc=取消）・フォーカス復帰のみ担う。
/// </summary>
public sealed class CsvCellEditor
{
    private TextBox? _box;
    private bool _closing;
    private ScintillaHost? _ed;
    private Action<string>? _onCommit;
    private Action? _onCancel;

    public bool IsEditing => _box is not null;

    /// <summary>セル編集を開始する。onCommit は確定値（改行は \n 正規化済み）、onCancel は取消で呼ぶ。</summary>
    public void Begin(ScintillaHost ed, CsvField field, Action<string> onCommit, Action onCancel)
    {
        if (IsEditing) return;
        _ed = ed; _onCommit = onCommit; _onCancel = onCancel; _closing = false;

        var host = (Control?)ed.Parent ?? ed;                 // 親(TabPage 等)に重ねる
        var clientPt = ed.PointFromCharOffset(field.Start);   // Scintilla クライアント座標
        var local = host.PointToClient(ed.PointToScreen(clientPt));

        _box = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,        // Enter は KeyDown で自前処理
            AcceptsTab = false,
            WordWrap = false,
            ScrollBars = ScrollBars.None,
            BorderStyle = BorderStyle.FixedSingle,
            Text = field.Value,
            Location = local,
            Width = Math.Max(140, ed.ClientSize.Width / 4),
            Height = Math.Max(ed.LineHeightPx + 6, 24),
            AccessibleName = "セル編集",
            ImeMode = ImeMode.NoControl,
        };
        _box.KeyDown += OnKeyDown;
        _box.LostFocus += OnLostFocus;

        host.Controls.Add(_box);
        _box.BringToFront();
        _box.Focus();
        _box.SelectAll();                 // 全選択（即上書きしやすく）
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_box is null) return;
        if (e.KeyCode == Keys.Return && e.Alt)          // Alt+Enter → セル内改行
        {
            int at = _box.SelectionStart;
            _box.Text = _box.Text.Remove(at, _box.SelectionLength).Insert(at, "\r\n");
            _box.SelectionStart = at + 2;
            _box.SelectionLength = 0;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Return)                   // Enter → 確定
        {
            e.SuppressKeyPress = true;
            Commit();
            return;
        }
        if (e.KeyCode == Keys.Escape)                   // Esc → 取消
        {
            e.SuppressKeyPress = true;
            CancelEdit();
        }
    }

    // フォーカス喪失は取消扱い（誤変更を避ける）。確定/取消処理中は無視。
    private void OnLostFocus(object? sender, EventArgs e)
    {
        if (!_closing) CancelEdit();
    }

    private void Commit()
    {
        if (_box is null || _closing) return;
        string text = _box.Text.Replace("\r\n", "\n").Replace("\r", "\n");
        var cb = _onCommit;
        Close();
        cb?.Invoke(text);
    }

    private void CancelEdit()
    {
        if (_box is null || _closing) return;
        var cb = _onCancel;
        Close();
        cb?.Invoke();
    }

    private void Close()
    {
        _closing = true;
        var box = _box; _box = null;
        if (box is not null)
        {
            box.KeyDown -= OnKeyDown;
            box.LostFocus -= OnLostFocus;
            box.Parent?.Controls.Remove(box);
            box.Dispose();
        }
        _ed?.Focus();
    }
}
```

**Step 2: Build**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: 成功・0 警告。

**Step 3: Commit**
```bash
git add src/yEdit.App/CsvCellEditor.cs
git commit -m "App: F2 セル編集オーバーレイ CsvCellEditor を追加"
```

---

## Task 8: App — `CsvController` を新方式へ全面書き換え

読取専用前提のグリッドナビ＋読み上げ＋F2編集の配線。現在セルはキャレットから毎回 `CsvParser.Parse` で導出（ステートレス）。

**Files:**
- Replace: `src/yEdit.App/CsvController.cs`（全置換）

**Step 1: 全文を置き換える**

`src/yEdit.App/CsvController.cs`:
```csharp
using yEdit.Core.Csv;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 新CSVモード（グリッド型ナビゲーション）の配線。CSVモード中は ScintillaHost.ReadOnly=true で
/// 本文を編集不可にし、素キーのコマンドでセル移動・読み上げを行う。現在セルはキャレットの文字
/// オフセットから毎回導出するため、編集後も状態が陳腐化しない。F2 は CsvCellEditor に委譲する。
/// </summary>
public sealed class CsvController
{
    private readonly DocumentManager _docs;
    private readonly IAnnouncer _announcer;
    private readonly CsvCellEditor _editor = new();

    public CsvController(DocumentManager docs, IAnnouncer announcer)
    {
        _docs = docs;
        _announcer = announcer;
    }

    /// <summary>F2 編集オーバーレイ表示中か（MainForm がキー横取りを抑止するのに使う）。</summary>
    public bool IsEditing => _editor.IsEditing;

    /// <summary>CSVモードを手動でトグルする。ON 時は読取専用化＋現在セルを確定して読み上げ。</summary>
    public void ToggleMode()
    {
        var doc = _docs.Active;
        if (doc is null || _editor.IsEditing) return;

        if (!doc.State.CsvMode)
        {
            var csv = CsvParser.Parse(doc.Editor.SnapshotText);
            if (!csv.Ok) { _announcer.Say(CsvAnnounceFormatter.ParseError); return; } // 解析不可ならモードに入らない
            doc.State.CsvMode = true;
            doc.Editor.ReadOnly = true;
            _announcer.Say(CsvAnnounceFormatter.ModeOn);
            if (csv.Rows.Count == 0) { doc.Editor.ClearHighlight(); return; }
            var (row, col) = csv.FindCell(doc.Editor.CaretCharOffset);
            ApplyCell(doc.Editor, csv, row, col, announce: true);
        }
        else
        {
            doc.State.CsvMode = false;
            doc.Editor.ReadOnly = false;
            doc.Editor.ClearHighlight();
            _announcer.Say(CsvAnnounceFormatter.ModeOff);
        }
    }

    // ---- 移動（読み上げ付き） ----
    public void Move(Direction dir)
    {
        if (!TryContext(out var ed, out var csv, out var row, out var col)) return;
        var t = csv.MoveCell(row, col, dir);
        if (t is null) { _announcer.Say(EdgeMessage(dir)); return; }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    public void MoveRowStart()    { if (TryContext(out var ed, out var csv, out var r, out _)) ApplyTarget(ed, csv, csv.RowStart(r)); }
    public void MoveRowEnd()      { if (TryContext(out var ed, out var csv, out var r, out _)) ApplyTarget(ed, csv, csv.RowEnd(r)); }
    public void MoveColumnTop()   { if (TryContext(out var ed, out var csv, out _, out var c)) ApplyTarget(ed, csv, csv.ColumnTop(c)); }
    public void MoveColumnBottom(){ if (TryContext(out var ed, out var csv, out _, out var c)) ApplyTarget(ed, csv, csv.ColumnBottom(c)); }
    public void MoveTopLeft()     { if (TryContext(out var ed, out var csv, out _, out _))     ApplyTarget(ed, csv, csv.TopLeft()); }
    public void MoveBottomRight() { if (TryContext(out var ed, out var csv, out _, out _))     ApplyTarget(ed, csv, csv.BottomRight()); }

    /// <summary>セル指定移動（G）。「行,列」入力ボックス→範囲検証→移動。</summary>
    public void GoToCell()
    {
        if (!TryContext(out var ed, out var csv, out var row, out var col)) return;
        using var dlg = new CsvGoToCellDialog(row + 1, col + 1);
        if (dlg.ShowDialog(ed.FindForm()) != DialogResult.OK) return;
        if (!dlg.TryGetCell(out int r1, out int c1)) { _announcer.Say(CsvAnnounceFormatter.BadCellFormat); return; }
        var t = csv.GoTo(r1 - 1, c1 - 1);
        if (t is null) { _announcer.Say(CsvAnnounceFormatter.OutOfRange); return; }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    // ---- 読み上げのみ（移動なし） ----
    /// <summary>現在セルを読み上げる（Tab）。</summary>
    public void ReadCurrent()
    {
        if (!TryContext(out _, out var csv, out var row, out var col)) return;
        var f = csv.GetField(row, col);
        if (f is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
    }

    /// <summary>現在列の最上段セルを読み上げる（C）。</summary>
    public void ReadColumnTop()
    {
        if (!TryContext(out _, out var csv, out _, out var col)) return;
        var t = csv.ColumnTop(col);
        var f = t is null ? null : csv.GetField(t.Value.row, t.Value.col);
        _announcer.Say(CsvAnnounceFormatter.Header(f?.Value ?? ""));
    }

    /// <summary>現在行の左端セルを読み上げる（R）。</summary>
    public void ReadRowHead()
    {
        if (!TryContext(out _, out var csv, out var row, out _)) return;
        var f = csv.GetField(row, 0);
        _announcer.Say(CsvAnnounceFormatter.Header(f?.Value ?? ""));
    }

    /// <summary>現在セルを F2 編集する。確定で CSV 直列化→本文反映→再ハイライト＋読み上げ。</summary>
    public void BeginEdit()
    {
        if (_editor.IsEditing) return;
        if (!TryContext(out var ed, out _, out var row, out var col)) return;
        // 開始時点のセル span（直列化対象）を確定。row/col はセル内容変更では不変。
        var startCsv = CsvParser.Parse(ed.SnapshotText);
        var f = startCsv.GetField(row, col);
        if (f is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        int start = f.Start, length = f.Length;

        _editor.Begin(ed, f,
            onCommit: text =>
            {
                string serialized = CsvWriter.EscapeField(text);
                bool wasRo = ed.ReadOnly;
                ed.ReadOnly = false;
                ed.ReplaceCharRange(start, length, serialized);
                ed.ReadOnly = wasRo;
                var csv2 = CsvParser.Parse(ed.SnapshotText);
                if (csv2.Ok) ApplyCell(ed, csv2, row, col, announce: true);
            },
            onCancel: () =>
            {
                var csv2 = CsvParser.Parse(ed.SnapshotText);
                if (csv2.Ok && csv2.Rows.Count > 0) ApplyCell(ed, csv2, row, col, announce: false);
            });
    }

    // ==================== 内部 ====================

    /// <summary>アクティブが CSV モードならそのエディタ、でなければ null。</summary>
    private ScintillaHost? ActiveCsvEditor()
    {
        var doc = _docs.Active;
        return (doc is not null && doc.State.CsvMode) ? doc.Editor : null;
    }

    /// <summary>パースして現在 (row,col) を得る。CSVでない/解析不可/データ無しは読み上げて false。</summary>
    private bool TryContext(out ScintillaHost ed, out CsvDocument csv, out int row, out int col)
    {
        ed = null!; csv = null!; row = 0; col = 0;
        var e = ActiveCsvEditor();
        if (e is null) return false;
        ed = e;
        csv = CsvParser.Parse(ed.SnapshotText);
        if (!csv.Ok) { ed.ClearHighlight(); _announcer.Say(CsvAnnounceFormatter.ParseError); return false; }
        if (csv.Rows.Count == 0) { ed.ClearHighlight(); _announcer.Say(CsvAnnounceFormatter.NoData); return false; }
        (row, col) = csv.FindCell(ed.CaretCharOffset);
        return true;
    }

    private void ApplyTarget(ScintillaHost ed, CsvDocument csv, (int row, int col)? t)
    {
        if (t is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    /// <summary>(row,col) のセルへ ハイライト＋キャレット移動（選択は作らない）＋必要なら読み上げ。</summary>
    private void ApplyCell(ScintillaHost ed, CsvDocument csv, int row, int col, bool announce)
    {
        var f = csv.GetField(row, col);
        if (f is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        ed.HighlightCharRange(f.Start, f.Length);
        ed.MoveCaretCharOffset(f.Start);
        ed.Focus();
        if (announce) _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
    }

    private static string EdgeMessage(Direction dir) => dir switch
    {
        Direction.Left => CsvAnnounceFormatter.LeftEdge,
        Direction.Right => CsvAnnounceFormatter.RightEdge,
        Direction.Up => CsvAnnounceFormatter.TopEdge,
        _ => CsvAnnounceFormatter.BottomEdge,
    };
}
```

**Step 2: Build**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: 成功・0 警告（MainForm は次タスクで合わせる。ここで MainForm がまだ旧 API を呼びコンパイルエラーになる場合は Task 9 まで一括で進めてから build する）。

注: Task 8 と Task 9 はビルド上相互依存する（MainForm が `_csv.Move(Direction)` 等の旧シグネチャを参照）。本タスクのコミットはコンパイル不能になり得るため、**Task 8 と Task 9 を続けて実施し、Task 9 のビルド成功後にまとめて 2 コミットする**運用とする（各ファイルごとに `git add`→`commit`）。

**Step 3: Commit（Task 9 のビルド成功後）**
```bash
git add src/yEdit.App/CsvController.cs
git commit -m "App: CsvController を新CSVモード（グリッドナビ＋F2編集）へ全面書き換え"
```

---

## Task 9: App — `MainForm` 配線（自動判定撤廃・キー横取り・メニュー刷新・保存時の読取専用解除）

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`

**Step 1: 自動判定の撤廃（LoadInto）**

`MainForm.cs` の以下（読み込み時の CSV 自動判定）:
```csharp
            doc.State.CsvMode = false;
            if (CsvFile.IsCsvPath(path))
            {
                var csv = CsvParser.Parse(loaded.Text);
                if (csv.Ok) { doc.State.CsvMode = true; _announcer.Say(CsvAnnounceFormatter.ModeOn); }
                else { _announcer.Say(CsvAnnounceFormatter.OpenParseFailed); }
            }
```
を次へ置換:
```csharp
            // CSV モードは自動判定しない（メニューから手動で有効化する）。
            // 既存タブへロードし直す場合に備え、読取専用とハイライトを解除しておく。
            doc.State.CsvMode = false;
            doc.Editor.ReadOnly = false;
            doc.Editor.ClearHighlight();
```

**Step 2: `RedetectCsvMode` の撤廃**

`RedetectCsvMode(Document doc)` メソッド定義（`/// <summary>パスと本文から CSV モードを再判定…` のブロック全体）を削除し、`SaveAsDocument` 内の呼び出し行:
```csharp
        RedetectCsvMode(doc);         // パス変更に追従して CSV モードを再判定
```
を削除する。

**Step 3: 保存時に読取専用を一時解除（WriteToPath）**

`WriteToPath` の:
```csharp
            ApplyEol(doc);
            doc.Editor.ConvertEols(doc.Editor.EolMode);
```
を次へ置換（ConvertEols は読取専用だと無効化されるため）:
```csharp
            ApplyEol(doc);
            bool wasReadOnly = doc.Editor.ReadOnly;
            if (wasReadOnly) doc.Editor.ReadOnly = false;
            doc.Editor.ConvertEols(doc.Editor.EolMode);
            if (wasReadOnly) doc.Editor.ReadOnly = true;
```

**Step 4: ProcessCmdKey の CSV ブロック差し替え**

既存の CSV ブロック:
```csharp
        if (_docs.Active?.State.CsvMode == true)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Shift | Keys.Up: _csv.Move(Direction.Up); return true;
                case Keys.Control | Keys.Shift | Keys.Down: _csv.Move(Direction.Down); return true;
                case Keys.Control | Keys.Shift | Keys.Left: _csv.Move(Direction.Left); return true;
                case Keys.Control | Keys.Shift | Keys.Right: _csv.Move(Direction.Right); return true;
                case Keys.Control | Keys.Shift | Keys.C: _csv.ReadColumnHeader(); return true;
            }
        }
```
を次へ置換:
```csharp
        // CSVモードのアクティブタブのみ、素のキーをグリッドナビ用に横取りする。
        // F2 編集オーバーレイ表示中（_csv.IsEditing）は素通しし、TextBox に通常編集させる。
        if (_docs.Active?.State.CsvMode == true && !_csv.IsEditing)
        {
            switch (keyData)
            {
                case Keys.Up: _csv.Move(Direction.Up); return true;
                case Keys.Down: _csv.Move(Direction.Down); return true;
                case Keys.Left: _csv.Move(Direction.Left); return true;
                case Keys.Right: _csv.Move(Direction.Right); return true;
                case Keys.Tab: _csv.ReadCurrent(); return true;
                case Keys.C: _csv.ReadColumnTop(); return true;
                case Keys.R: _csv.ReadRowHead(); return true;
                case Keys.Home: _csv.MoveRowStart(); return true;
                case Keys.End: _csv.MoveRowEnd(); return true;
                case Keys.PageUp: _csv.MoveColumnTop(); return true;
                case Keys.PageDown: _csv.MoveColumnBottom(); return true;
                case Keys.Control | Keys.Home: _csv.MoveTopLeft(); return true;
                case Keys.Control | Keys.End: _csv.MoveBottomRight(); return true;
                case Keys.G: _csv.GoToCell(); return true;
                case Keys.F2: _csv.BeginEdit(); return true;
            }
        }
```

**Step 5: CSV メニューの刷新**

既存の CSV メニュー構築（`var csv = new ToolStripMenuItem("CSV(&C)");` から `DropDownOpening` ハンドラ閉じまで）を次へ置換:
```csharp
        var csv = new ToolStripMenuItem("CSV(&C)");
        var csvToggle = new ToolStripMenuItem("CSVモード(&M)", null, (_, _) => _csv.ToggleMode());
        var navItems = new List<ToolStripMenuItem>();
        ToolStripMenuItem Nav(string text, string keyHint, Action act)
        {
            var mi = new ToolStripMenuItem(text, null, (_, _) => act()) { ShortcutKeyDisplayString = keyHint };
            navItems.Add(mi);
            return mi;
        }
        csv.DropDownItems.Add(csvToggle);
        csv.DropDownItems.Add(new ToolStripSeparator());
        csv.DropDownItems.Add(Nav("上のセル(&U)", "↑", () => _csv.Move(Direction.Up)));
        csv.DropDownItems.Add(Nav("下のセル(&D)", "↓", () => _csv.Move(Direction.Down)));
        csv.DropDownItems.Add(Nav("左のセル(&L)", "←", () => _csv.Move(Direction.Left)));
        csv.DropDownItems.Add(Nav("右のセル(&R)", "→", () => _csv.Move(Direction.Right)));
        csv.DropDownItems.Add(new ToolStripSeparator());
        csv.DropDownItems.Add(Nav("現在セルを読み上げ(&E)", "Tab", () => _csv.ReadCurrent()));
        csv.DropDownItems.Add(Nav("列の見出しを読み上げ(&C)", "C", () => _csv.ReadColumnTop()));
        csv.DropDownItems.Add(Nav("行の見出しを読み上げ(&H)", "R", () => _csv.ReadRowHead()));
        csv.DropDownItems.Add(new ToolStripSeparator());
        csv.DropDownItems.Add(Nav("行頭へ(&S)", "Home", () => _csv.MoveRowStart()));
        csv.DropDownItems.Add(Nav("行末へ(&N)", "End", () => _csv.MoveRowEnd()));
        csv.DropDownItems.Add(Nav("列頭へ(&T)", "PageUp", () => _csv.MoveColumnTop()));
        csv.DropDownItems.Add(Nav("列末へ(&B)", "PageDown", () => _csv.MoveColumnBottom()));
        csv.DropDownItems.Add(Nav("左上へ(&1)", "Ctrl+Home", () => _csv.MoveTopLeft()));
        csv.DropDownItems.Add(Nav("右下へ(&9)", "Ctrl+End", () => _csv.MoveBottomRight()));
        csv.DropDownItems.Add(new ToolStripSeparator());
        csv.DropDownItems.Add(Nav("セルへ移動(&G)...", "G", () => _csv.GoToCell()));
        csv.DropDownItems.Add(Nav("セルを編集(&F)", "F2", () => _csv.BeginEdit()));
        csv.DropDownOpening += (_, _) =>
        {
            bool on = _docs.Active?.State.CsvMode == true;
            csvToggle.Checked = on;
            foreach (var mi in navItems) mi.Enabled = on;
        };
```

注: `List<ToolStripMenuItem>` のため `using System.Collections.Generic;` が必要（既存 using を確認。WinForms 既定で含まれることが多いが、無ければ追加）。

**Step 6: 不要 using の整理（任意）**

`CsvParser` / `CsvFile` の参照が MainForm から消えるが、`using yEdit.Core.Csv;` は `CsvAnnounceFormatter`（既存メッセージ）でまだ使う場合は残す。ビルド警告が出る未使用 using のみ削除。

**Step 7: Build（Task 8 と合わせて）**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: 成功・0 警告。

**Step 8: Commit**
```bash
git add src/yEdit.App/MainForm.cs
git commit -m "App: 新CSVモード配線（自動判定撤廃・素キー横取り・メニュー刷新・保存時読取専用解除）"
```

---

## Task 10: 全体ビルド・テスト・手動実機検証

**Step 1: フルビルド**

Run: `dotnet build -nologo -clp:ErrorsOnly`
Expected: 成功・**0 警告**。

**Step 2: Core テスト**

Run: `dotnet test tests/yEdit.Core.Tests -nologo`
Expected: 全 PASS（238 + 新規分）・失敗 0。

**Step 3: 手動実機検証チェックリスト（要 SR: PC-Talker / NVDA いずれか）**

アプリ起動: `dotnet run --project src/yEdit.App`（または Release ビルドの exe）。

- [ ] `.csv` を開いても CSVモードにならない（自動判定なし・自由編集できる通常エディタ）。
- [ ] メニュー CSV →「CSVモード」で ON（「CSVモード オン」読み上げ＋現在セルがハイライト＆読み上げ）。本文が読取専用になる（文字入力しても変化しない）。
- [ ] ↑↓←→ でセル移動＋読み上げ。端で「左端です／右端です／先頭行です／最終行です」。
- [ ] Tab で現在セルを読み上げ（移動しない）。
- [ ] C で列の最上段セル、R で行の左端セルを読み上げ（移動しない）。
- [ ] Home/End で行の左端/右端、PageUp/PageDown で列の最上段/最下段へ移動。
- [ ] Ctrl+Home/Ctrl+End で左上/右下へ移動。
- [ ] G で「行,列」入力ボックス。`2,3` で該当セルへ移動。範囲外は「範囲外です」、書式不正は書式メッセージ。
- [ ] F2 でセル上に編集ボックス（値が全選択）。通常の編集ができ、カーソルはセル内のみ。Alt+Enter で改行、Enter で確定（CSVへ反映・カンマ/引用符/改行を含む値が壊れない）、Esc で取消。
- [ ] 編集確定後、対象セルが再ハイライト＆読み上げ。タブに変更マーク `*`。
- [ ] 保存（Ctrl+S）すると CSV が正しく書き出される（編集が反映され、EOL も正規化される）。
- [ ] メニュー CSV →「CSVモード」で OFF（「CSVモード オフ」・ハイライト消去・通常エディタへ復帰・編集可能）。
- [ ] main ビルド（旧オーバーレイ方式）と並走し、操作感を比較。

**Step 4: 検証結果の記録**

`docs/plans/2026-06-29-csv-grid-mode.md` の本タスク下に、実機検証の結果（OK/要修正項目）を追記してコミット。

```bash
git add docs/plans/2026-06-29-csv-grid-mode.md
git commit -m "docs: 新CSVモード 実機検証結果を記録"
```

---

## Task 10 実施状況（2026-06-29）

### 自動検証（完了）
- フルビルド: 成功・**0 警告 / 0 エラー**。
- Core テスト: **258 件すべて緑**（baseline 238 + 新規 20）。
- 実装は subagent-driven-development で Task 1→9 を順に実施し、各タスクで「spec準拠レビュー → コード品質レビュー」を実施。レビュー指摘は都度修正済み（Task7: 複数行スクロール表示/IMEガード/参照解放、Task8+9: 横取りをエディタフォーカスに限定/ON時アナウンス統合/Parseメモ化/F2・保存の安全化）。

### 最終統合レビューの指摘と対応（ユーザー承認のうえ実施）
全タスク完了後の統合レビューで検出した Important 3件を修正済み:
1. **F2編集中のタブ閉じで `IsEditing` が固着しグリッドナビが停止**する不具合 → タブ閉じ/タブ切替で確実に編集中断（`Abort` 経路追加・`a8e6128`）。
2. **F2編集中のタブ切替でフォーカスが奪い返され編集が無言破棄** → 同コミットで解消。
3. **CSVモード（読取専用）中に置換/折り返し整形が無変更のまま「成功」を読み上げる誤通知** → CSVモード中は抑止し「CSVモード中は実行できません」と通知（`7d82bce`）。

### 手動実機SR検証（未実施・ユーザー対応）
下記を PC-Talker / NVDA 実機で確認し、結果（OK/要修正）を本節に追記する。**Step 3 のチェックリスト** に加え、レビューで挙がった追加確認項目:
- [ ] F2編集中にタブを閉じる/切り替えると編集が中断し、グリッドナビが復帰する（#1/#2 実機確認。特にマウスでのタブクリック）。
- [ ] CSVモード中に Ctrl+H 置換 / Ctrl+Shift+J 整形を試すと「CSVモード中は実行できません」と読み上げる（誤成功なし）。
- [ ] IME変換確定の Enter が F2編集で誤確定しない。
- [ ] 複数行セル / Alt+Enter 追加行が編集ボックスで視認できる（縦スクロール）。
- [ ] モードON時アナウンス「CSVモード オン {値} {行}行{列}列」が自然に読み上げられる。
- [ ] F2取消（Esc/フォーカス喪失）が無言で分かりにくくないか（必要なら取消アナウンス追加を検討）。
- [ ] NVDA でのキャレット移動の二重読み（ネイティブ＋UIA）。
- [ ] main（旧オーバーレイ方式）と並走して操作感を比較。

### 残申し送り（非ブロッカー・v1許容/後続）
- 空セルはハイライト枠が出ない（長さ0）。晴眼/弱視向けにデリミタ位置の細枠など検討余地。
- Tab を ReadCurrent に消費（前方フォーカス移動不可、Shift+Tab は可）。
- デッドコード: `CsvAnnounceFormatter.OpenParseFailed`、`CsvFile.IsCsvPath`（自動判定撤廃で未使用）。
- F2確定の ReadOnly トグルは try/finally 未包（保存側は包済み）。
- `BeforeActiveChange` は単一購読・`AbortEdit` の冪等性が前提（将来コメント補強の余地）。

---

## 完了後（別途）

- 別エージェントによるコードレビュー（requesting-code-review）→ 指摘対応。
- finishing-a-development-branch で `feature/csv-grid-mode` を main へ no-ff マージするか、比較継続のためブランチ保持かを判断。
- メモリ更新（`csv-mode-followups` に新方式の所在と比較結果を追記）。

## リスク・申し送り

- **ネイティブ Scintilla 子コントロールの描画**: オーバーレイ TextBox は `ed.Parent` に重ねる方式。万一描画/フォーカスに難があれば、`ed` 直下への追加や、セル矩形へ正確配置（複数行セルは先頭行基準）へ調整。
- **読取専用と他機能の干渉**: 検索ハイライト・折り返し整形・grep 反映等が CSVモード中に走る経路は本計画の対象外。必要なら CSVモード中は該当メニューを抑止する追加対応を検討。
- **IME**: 読取専用 Scintilla では素キーがコマンドとして届く前提。編集は専用 TextBox（IME 可）。CSVモード中のナビで IME 変換が絡む環境は実機で要確認。
