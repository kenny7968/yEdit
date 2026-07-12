# P3: EditorControl 編集・入力 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** P2 の `EditorControl`(読み取り専用ビューア水準)に、キーボード/マウス/クリップボード/Undo・Redo/Overtype/EOL モードを配線し、Scintilla 版と同水準の編集操作を提供する。IME(P4)/UIA(P5)/App 層置換(P6)は次以降。

**Architecture:** 位置移動と単語境界は純ロジック `yEdit.Core.Editing` 名前空間に閉じ込め(xUnit 対象)、EditorControl は表面のみ担当する。選択のためのアンカー概念(`_anchor` と `_caret` の分離)を導入し、shift+左方向の非対称選択を表現できるようにする。`TextBuffer.Insert/Delete/Replace/Undo/Redo` へ配線し、キャレット追従スクロール(`BringCaretIntoView`)で編集後の可視性を保つ。クリップボードは WinForms `Clipboard`、キーストロークは `OnKeyDown`/`OnKeyPress`、マウスは `OnMouseDown/Move/Up/DoubleClick/Wheel` で受ける。設計書: `docs/plans/2026-07-05-custom-editcontrol-design.md` §2-3。

**Tech Stack:** C# / .NET 9 / xUnit(既存 `tests/yEdit.Core.Tests` + 新規 `tests/yEdit.Editor.Tests`)/ WinForms(`Control` 派生)/ ベンチは既存 `tests/yEdit.Core.Bench` の `--typing` サブコマンド追加、smoke は既存 `tests/yEdit.Editor.Smoke` に typing/クリック実操作を追加。

**前提:**
- 作業は worktree `<repo>\.worktrees\custom-editcontrol-design`(ブランチ `feature/custom-editcontrol-design`)
- **main には一切触れない**(全フェーズを本ブランチに閉じ、P7 合格後に一括マージ。設計書§3 運用)
- P3 は **ScintillaHost に変更を加えない**(P6 で一発置換の予定・並行運用しない)
- P1/P2 の公開 API(`TextBuffer`/`TextSnapshot`/`EditorControl` 既存 API)は変更しない=新規追加のみ。**破壊的変更禁止**
- 各タスク完了ごとにコミット。全タスク後に別エージェントレビュー(Task 15)
- 既存テスト 470 件は全タスクで緑を維持

---

## 0. 設計固定事項(全タスク共通の不変条件)

実装全体で守る。レビュー観点でもある。

1. **公開オフセットは UTF-16 コード単位(int)**。P1/P2 と同じ通貨。バイト位置は登場しない
2. **サロゲートペアは分割しない**。1 文字左/右移動・BackSpace/Delete・単語境界すべて code-point 境界で処理。移動中間位置になったら前方(high)へスナップ(P1 §0-3・P2 §0-6 と同方針=`EditorControl.SnapAndClamp` を使う)
3. **アンカー概念を EditorControl 内部に持つ**。`_anchor`(選択の起点)+ `_caret`(現在位置)の 2 変数で状態表現。既存の `_selStart/_selEnd` フィールドは削除し、`GetSelectionCharRange()` は `(Math.Min(anchor, caret), Math.Max(anchor, caret))` を返す。Shift+左方向の選択(キャレット<アンカー)を保持できる
4. **既存 `SetSelectionCharRange(int, int)` は挙動不変**。呼び出し側は今まで通り `(start, end)` を受け、内部で `anchor=start, caret=end` にマップする(=キャレットは末尾)。呼出しコード非互換なし
5. **キャレット追従スクロールは移動系すべての最後に呼ぶ**。`BringCaretIntoView()` が TopLine と ScrollX を必要分だけ調整する(可視ならば no-op)。挿入・削除・Undo/Redo・移動系すべてから同じルーチンを呼ぶ
6. **編集は TextBuffer 経由で単一の Splice 単位に潰す**。BackSpace 連打は `TextBuffer` の coalescing で 1 Undo にまとめる(P1 仕様どおり)。キャレットの純粋移動やマウスクリックで `BreakUndoCoalescing()` を明示的に呼ぶ(§2-5-5=Scintilla の SavePoint 挙動と同じ体験を保つ)
7. **UIA イベント発火は P5**。P3 で `RaiseAutomationEvent` を追加しない(既存 `UiaProbe` の実装が P5 の参照実装。P3 では public event `CaretEnteredEmptyLine` を EditorControl に生やすだけで、購読は P6 の App 層で行う)
8. **本番 App 層は触れない**。ScintillaHost は据え置き、EditorControl はまだどこからも参照されない(smoke 起動器のみが参照)
9. **P4(IME)は WM_IME_* / WM_CHAR を横取り**する予定。P3 の `OnKeyPress` は `WM_CHAR` の高水準ハンドラだが、IME 確定文字も同じ経路で来る(=`ImeMode` に関わらず一旦文字挿入として扱ってよい)。P4 で inline 未確定表示を挿入したら OnKeyPress は「確定文字のみ来る」経路として温存する(=P4 で書き換え禁止=P3 の実装形式が P4 で耐える設計を採る)
10. **クリップボードはテキストのみ**(`TextDataFormat.UnicodeText`)。Scintilla の矩形選択などは v1 スコープ外(YAGNI)

## 1. 公開 API サーフェス(P3 追加分)

P2 で確定済みの API は不変。P3 追加分だけ列挙する。

```csharp
namespace yEdit.Editor;

public sealed partial class EditorControl : Control
{
    // ---- アンカー付き選択(§0-3) ----
    /// <summary>選択のアンカー端(選択起点・UTF-16 char offset)。選択なしのとき = CaretCharOffset。</summary>
    public int SelectionAnchor { get; }

    /// <summary>アンカーを維持したままキャレットを移動する(shift+移動系の共通経路)。</summary>
    public void MoveCaretWithSelection(int newCaret);

    /// <summary>アンカーとキャレットを個別に設定する(P6 の互換 API 用の受け口)。</summary>
    public void SetSelectionAnchored(int anchor, int caret);

    // ---- 編集 API(App 層互換の受け口・P6 で使う) ----
    public void ReplaceCharRange(int start, int length, string replacement);

    // ---- キャレット追従 / 可視化 ----
    /// <summary>指定範囲を最低限可視化する(検索ジャンプ・GoTo 等の呼び出し用)。</summary>
    public void EnsureVisibleCharRange(int start, int length);

    // ---- EOL / 上書きモード ----
    /// <summary>Enter 押下で挿入する改行シーケンス。既定 <see cref="LineEnding.Crlf"/>。</summary>
    public LineEnding EolMode { get; set; }

    /// <summary>上書きモード。true のとき通常文字入力は 1 文字置換(既存 1 文字を消して 1 文字挿入)。</summary>
    public bool Overtype { get; set; }

    /// <summary>読み取り専用(true でキー・マウス編集を no-op、Undo/Redo は false)。</summary>
    public bool ReadOnly { get; set; }

    // ---- 変更フラグ / Undo API(P6 互換) ----
    public bool Modified { get; }              // TextBuffer.Modified の透過
    public bool CanUndo { get; }
    public bool CanRedo { get; }
    public new void Undo();                    // Control.Undo(virtual なし) を隠す=同名で意味を再定義
    public void Redo();
    public void SetSavePoint();                // TextBuffer.MarkSaved の別名
    public void EmptyUndoBuffer();             // TextBuffer.ClearUndo の別名

    // ---- 空行ナビ通知(§2-5-3) ----
    /// <summary>キャレット移動(本文変更なし・選択なし)で空行に着地したとき発火。App 層が能動発声する。</summary>
    public event EventHandler? CaretEnteredEmptyLine;

    // ---- UIA 発火抑止(P6 で CSV モード切替時に使う=P3 では読み取りのみ) ----
    public bool RaiseUiaSelectionEvents { get; set; }

    // ---- Cut/Copy/Paste/SelectAll(Control 基底が持たないため生やす) ----
    public void Cut();
    public void Copy();
    public void Paste();
    public new void SelectAll();               // Control.SelectAll(TextBox 用) と衝突回避

    // ---- 位置照会(P6 で行番号ステータス用) ----
    public int CurrentLine { get; }            // 現在キャレットの論理行(0 始まり)
    public int GetColumn(int offset);          // 論理行内オフセット(0 始まり)
}
```

**P6 互換の維持ポイント**: 既存 `ScintillaHost` の App 層契約(設計書 §2-8)を、P3 で `EditorControl` にも同名同義で載せる。App 層は P6 で単純に `ScintillaHost` を `EditorControl` に差し替えれば動く形にする(=YAGNI に反しない範囲で先出し。設計書 §2-8 の互換 API リストと同じ)。

## 2. 共通コマンド

```powershell
# レイアウト+入力+Buffer 単体(高速ループ)
dotnet test tests/yEdit.Core.Tests -c Release --nologo --filter "FullyQualifiedName~Editing|FullyQualifiedName~Layout|FullyQualifiedName~Buffers"
# EditorControl(WinForms・STA)
dotnet test tests/yEdit.Editor.Tests -c Release --nologo
# 全体回帰(各タスク末尾で実行)
dotnet build yEdit.sln -c Release; dotnet test yEdit.sln -c Release --nologo
```

Expected(全体回帰): build 0 警告 / 既存 470 件 + 新規が全緑。

---

### Task 1: EditorControl テスト基盤 (yEdit.Editor.Tests) を作る

**Files:**
- Create: `tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj`
- Create: `tests/yEdit.Editor.Tests/GlobalUsings.cs`
- Create: `tests/yEdit.Editor.Tests/StaFactAttribute.cs`
- Create: `tests/yEdit.Editor.Tests/CaretAndSelectionSmokeTests.cs`
- Modify: `yEdit.sln`(Editor.Tests プロジェクトを追加)

`yEdit.Editor.Tests` は WinForms(STA スレッド)を要求する xUnit プロジェクト。P2 で「純データ系プロパティテスト(SetCaretCharOffset/SetSelectionCharRange のクランプ・スナップ)は Core.Tests から見えないため未実装」と申し送られた基盤を、Task 1 で建てる。以後の Task で `SetCaretCharOffset`/`Move*`/`Cut/Copy/Paste`/`Undo/Redo` などの契約テストをここに追加する。

**Step 1: プロジェクト作成**

```xml
<!-- tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\yEdit.Core\yEdit.Core.csproj" />
    <ProjectReference Include="..\..\src\yEdit.Editor\yEdit.Editor.csproj" />
  </ItemGroup>
</Project>
```

**Step 2: STA-affined xUnit Fact 属性**

`Control` 生成は STA スレッド必須。xUnit の `[Fact]` は既定で MTA なので `StaFactAttribute` を用意する。

```csharp
// StaFactAttribute.cs
using Xunit;
using Xunit.Sdk;

namespace yEdit.Editor.Tests;

[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class StaFactAttribute : FactAttribute { }

// テスト本体では TaskFactory.StartNew(action, TaskCreationOptions.None) で STA スレッドを立てる
// ヘルパを提供する(FactAttribute のスレッド属性は xUnit v2 標準に無いため)。
```

より簡潔には、`[Fact]` のまま実行し、テスト本体を `Thread` で STA として起動して待つラッパを書く(xUnit v2 は STA 属性未提供のため定石):

```csharp
public static class Sta
{
    public static void Run(Action action)
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }
}
```

テストは `[Fact] public void X() => Sta.Run(() => { /* WinForms 操作 */ });` と書く。

**Step 3: 最初のスモーク: SetCaretCharOffset のクランプ**

```csharp
public class CaretAndSelectionSmokeTests
{
    [Fact]
    public void SetCaretCharOffset_ClampsAndSnapsSurrogate() => Sta.Run(() =>
    {
        using var c = new EditorControl();
        // ハンドル生成のため親フォームに載せて表示
        using var f = new Form { Visible = false };
        f.Controls.Add(c);
        var _ = f.Handle; // realize
        c.SetSource(TextBuffer.FromString("abc😀def")); // 絵文字(サロゲート)入り

        c.SetCaretCharOffset(-5);
        Assert.Equal(0, c.CaretCharOffset);
        c.SetCaretCharOffset(9999);
        Assert.Equal(c.CaretCharOffset, c.CaretCharOffset); // = CharLength にクランプ

        // サロゲート low を指したら前方(high)へスナップ
        c.SetCaretCharOffset(4);        // 4 = low surrogate 位置
        Assert.Equal(3, c.CaretCharOffset);
    });
}
```

**Step 4: 実行して緑を確認**

```powershell
dotnet test tests/yEdit.Editor.Tests -c Release --nologo
```

Expected: 1 件緑。

**Step 5: コミット**

```powershell
git add tests/yEdit.Editor.Tests/ yEdit.sln
git commit -m "P3: Task 1 EditorControl テスト基盤(WinForms STA・xUnit)"
```

---

### Task 2: アンカー概念導入(既存 API 契約は不変)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(`_selStart`/`_selEnd` → `_anchor`/`_caret` のみに)
- Test: `tests/yEdit.Editor.Tests/AnchorSelectionTests.cs`

現状の EditorControl は `_caret`/`_selStart`/`_selEnd` の 3 変数持ち。shift+左方向の選択が表現できない(P2 レビュー I 申し送り)。**選択は `[Math.Min(anchor, caret), Math.Max(anchor, caret)]`** に統一する。既存 `SetSelectionCharRange(start, end)` は `anchor=start, caret=end` にマップ=挙動不変。

**Step 1: 失敗するテスト(shift+左方向)**

```csharp
[Fact]
public void SetSelectionAnchored_KeepsCaretAtStart_WhenCaretBeforeAnchor() => Sta.Run(() =>
{
    using var c = MakeControl("abcdef");
    c.SetSelectionAnchored(anchor: 5, caret: 2);
    Assert.Equal(2, c.CaretCharOffset);
    Assert.Equal(5, c.SelectionAnchor);
    Assert.Equal((2, 5), c.GetSelectionCharRange());
});

[Fact]
public void MoveCaretWithSelection_KeepsAnchor() => Sta.Run(() =>
{
    using var c = MakeControl("abcdef");
    c.SetSelectionAnchored(anchor: 3, caret: 3);
    c.MoveCaretWithSelection(1);
    Assert.Equal((1, 3), c.GetSelectionCharRange());
    Assert.Equal(1, c.CaretCharOffset);
    Assert.Equal(3, c.SelectionAnchor);
});
```

**Step 2: 実装**

`EditorControl.cs`:
```csharp
private int _caret;
private int _anchor;                    // 新規(_selStart/_selEnd を削除)

public int SelectionAnchor => _anchor;

public (int Start, int End) GetSelectionCharRange()
    => (Math.Min(_anchor, _caret), Math.Max(_anchor, _caret));

public void SetCaretCharOffset(int offset)
{
    if (_buffer is null) return;
    int snapped = SnapAndClamp(offset);
    if (_caret == snapped && _anchor == snapped) return;
    _caret = snapped;
    _anchor = snapped;   // 単純キャレット移動は選択解除
    PositionCaret();
    Invalidate();
}

public void MoveCaretWithSelection(int newCaret)
{
    if (_buffer is null) return;
    int snapped = SnapAndClamp(newCaret);
    if (_caret == snapped) return;
    _caret = snapped;
    // _anchor は保持
    PositionCaret();
    Invalidate();
}

public void SetSelectionAnchored(int anchor, int caret)
{
    if (_buffer is null) return;
    int a = SnapAndClamp(anchor);
    int c = SnapAndClamp(caret);
    if (_anchor == a && _caret == c) return;
    _anchor = a; _caret = c;
    PositionCaret();
    Invalidate();
}

// 既存 SetSelectionCharRange の意味を維持: anchor=Min, caret=Max
public void SetSelectionCharRange(int start, int end)
{
    if (_buffer is null) return;
    int s = SnapAndClamp(Math.Min(start, end));
    int e = SnapAndClamp(Math.Max(start, end));
    if (_anchor == s && _caret == e) return;
    _anchor = s; _caret = e;
    PositionCaret();
    Invalidate();
}
```

**OnPaint 内の `hasSelection`**: `_selStart != _selEnd` → `_anchor != _caret` に置換。`selection` 変数は `GetSelectionCharRange()` から組む。

**Step 3: 既存 P2 テスト(選択系)が緑のままか確認**

```powershell
dotnet test tests/yEdit.Core.Tests -c Release --nologo --filter FullyQualifiedName~Layout
dotnet test tests/yEdit.Editor.Tests -c Release --nologo
```

**Step 4: 全体回帰**

```powershell
dotnet build yEdit.sln -c Release; dotnet test yEdit.sln -c Release --nologo
```

Expected: 470 件 + Task1〜2 新規全緑。

**Step 5: コミット**

```powershell
git commit -am "P3: Task 2 アンカー概念導入(shift+左方向の選択保持)"
```

---

### Task 3: NavigationCommands 純ロジック(Left/Right/Home/End/SmartHome)

**Files:**
- Create: `src/yEdit.Core/Editing/NavigationCommands.cs`
- Test: `tests/yEdit.Core.Tests/Editing/NavigationCommandsTests.cs`

P2 の `TextSnapshot` 上に立つ位置移動関数群を純ロジックに切り出す(xUnit で決定的にテスト可)。EditorControl は Task 6 でここを呼ぶ。

**Step 1: 失敗するテスト**

```csharp
public class NavigationCommandsTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    [Fact]
    public void MoveLeftChar_SkipsSurrogatePair()
    {
        var s = Snap("a😀b");  // a + 😀 + b (CharLength=4)
        Assert.Equal(3, NavigationCommands.MoveLeftChar(s, 4));  // 'b' の前 → surrogate high
        Assert.Equal(1, NavigationCommands.MoveLeftChar(s, 3));  // surrogate high → 'a' の後
        Assert.Equal(0, NavigationCommands.MoveLeftChar(s, 1));
        Assert.Equal(0, NavigationCommands.MoveLeftChar(s, 0));  // 先頭で no-op
    }

    [Fact]
    public void MoveRightChar_SkipsSurrogatePair()
    {
        var s = Snap("a😀b");
        Assert.Equal(1, NavigationCommands.MoveRightChar(s, 0));
        Assert.Equal(3, NavigationCommands.MoveRightChar(s, 1));  // 'a' の後 → 😀 の後
        Assert.Equal(4, NavigationCommands.MoveRightChar(s, 3));
        Assert.Equal(4, NavigationCommands.MoveRightChar(s, 4));  // 末尾で no-op
    }

    [Fact]
    public void MoveHome_ReturnsLineStart()
    {
        var s = Snap("abc\r\ndef");
        Assert.Equal(0, NavigationCommands.MoveHome(s, 2));
        Assert.Equal(5, NavigationCommands.MoveHome(s, 7));  // "def" の 'e' → 5
    }

    [Fact]
    public void MoveEnd_ReturnsLineEnd_ExcludingBreak()
    {
        var s = Snap("abc\r\ndef");
        Assert.Equal(3, NavigationCommands.MoveEnd(s, 1));  // \r の前
        Assert.Equal(8, NavigationCommands.MoveEnd(s, 6));  // EOF
    }

    [Fact]
    public void MoveHomeSmart_TogglesBetweenFirstNonWsAndLineStart()
    {
        var s = Snap("  hello");
        // キャレットが本文内 → 先頭空白の後(位置2)へ
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 4));
        // すでに 2 にいる → 行頭(0)へ
        Assert.Equal(0, NavigationCommands.MoveHomeSmart(s, 2));
        // 行頭にいる → 先頭空白の後(2)へ
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 0));
    }
}
```

**Step 2: 実装(NavigationCommands.cs)**

```csharp
namespace yEdit.Core.Editing;

using yEdit.Core.Buffers;

/// <summary>
/// TextSnapshot 上のキャレット位置移動(純ロジック・状態を持たない)。
/// 返り値は移動後の UTF-16 char offset。範囲外指定時のスナップは呼び出し側(EditorControl.SnapAndClamp)。
/// </summary>
public static class NavigationCommands
{
    public static int MoveLeftChar(TextSnapshot s, int caret)
    {
        if (caret <= 0) return 0;
        int prev = caret - 1;
        if (prev > 0 && char.IsLowSurrogate(s.GetChar(prev)) && char.IsHighSurrogate(s.GetChar(prev - 1)))
            return prev - 1;
        return prev;
    }

    public static int MoveRightChar(TextSnapshot s, int caret)
    {
        if (caret >= s.CharLength) return s.CharLength;
        char c = s.GetChar(caret);
        if (char.IsHighSurrogate(c) && caret + 1 < s.CharLength && char.IsLowSurrogate(s.GetChar(caret + 1)))
            return caret + 2;
        return caret + 1;
    }

    public static int MoveHome(TextSnapshot s, int caret)
    {
        int line = s.GetLineIndexOfChar(caret);
        return s.GetLineStart(line);
    }

    public static int MoveEnd(TextSnapshot s, int caret)
    {
        int line = s.GetLineIndexOfChar(caret);
        return s.GetLineEnd(line, includeBreak: false);
    }

    /// <summary>Home スマート: 先頭空白の後 ⇔ 行頭 をトグル。</summary>
    public static int MoveHomeSmart(TextSnapshot s, int caret)
    {
        int line = s.GetLineIndexOfChar(caret);
        int lineStart = s.GetLineStart(line);
        int lineEnd = s.GetLineEnd(line, includeBreak: false);
        int firstNonWs = lineStart;
        while (firstNonWs < lineEnd)
        {
            char c = s.GetChar(firstNonWs);
            if (c != ' ' && c != '\t') break;
            firstNonWs++;
        }
        // すでに firstNonWs にいる → lineStart。それ以外 → firstNonWs
        if (caret == firstNonWs) return lineStart;
        return firstNonWs;
    }
}
```

**Step 3: 実行**

```powershell
dotnet test tests/yEdit.Core.Tests -c Release --nologo --filter FullyQualifiedName~Editing
```

Expected: 5 件緑。

**Step 4: 全体回帰**

**Step 5: コミット**

```powershell
git add src/yEdit.Core/Editing/NavigationCommands.cs tests/yEdit.Core.Tests/Editing/NavigationCommandsTests.cs
git commit -m "P3: Task 3 NavigationCommands 純ロジック(Left/Right/Home/End/SmartHome)"
```

---

### Task 4: VerticalNavigation 純ロジック(Up/Down/PageUp/Down + desired column)

**Files:**
- Create: `src/yEdit.Core/Editing/VerticalNavigation.cs`
- Test: `tests/yEdit.Core.Tests/Editing/VerticalNavigationTests.cs`

上下移動は視覚行を跨ぐため、`ViewportLayout`/`LineLayout` に依存する。`ICharMetrics` を注入して pixel ベースで desired X を保つ("列ずれない上下"の実現)。

**Step 1: 失敗するテスト**

```csharp
public class VerticalNavigationTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;
    private static readonly ICharMetrics Metrics = new MonoCharMetrics(halfWidthPx: 8, fullWidthPx: 16, lineHeightPx: 20);

    [Fact]
    public void MoveDownLine_KeepsDesiredColumn_AcrossShorterLine()
    {
        var s = Snap("abcdef\nxy\nlong line here");
        // 行0の 'f'(col=5)から行1へ → 行1は "xy"(len=2) → 末尾に丸め・desired=5 は保存
        var (next1, desired1) = VerticalNavigation.MoveDown(s, caret: 5, currentDesiredPx: -1, wrapColumns: 0, Metrics);
        Assert.Equal(8 + 2, next1);   // 7 = "\n" 後の "xy" 末尾(len 2 = "xy" 全体 → +2)。行0(a..f=6)+1(\n)+2("xy")=9 …厳密検算は実装時
        // 次の行へも desired が保持されて col 5 で戻る
        var (next2, desired2) = VerticalNavigation.MoveDown(s, next1, currentDesiredPx: desired1, wrapColumns: 0, Metrics);
        Assert.Equal(desired1, desired2);
    }

    [Fact]
    public void MoveUpLine_AtTopLine_NoOp() { /* ... */ }

    [Fact]
    public void PageDown_MovesByVisibleRows() { /* ... */ }
}
```

**Step 2: 実装(VerticalNavigation.cs)**

```csharp
namespace yEdit.Core.Editing;

using yEdit.Core.Buffers;
using yEdit.Core.Layout;

public static class VerticalNavigation
{
    /// <summary>
    /// 上下移動の 1 ステップ。<paramref name="currentDesiredPx"/> は前回の移動で保存された希望 X(px)。
    /// 初回(単純ナビ以外の起点)は -1 を渡す=呼び出し側 caret の X を新規計算して使う。
    /// 戻り値: (移動先 caret, 保存する desired X px)。
    /// </summary>
    public static (int caret, int desiredPx) MoveDown(TextSnapshot s, int caret, int currentDesiredPx, int wrapColumns, ICharMetrics metrics)
        => MoveVerticalRelative(s, caret, currentDesiredPx, wrapColumns, metrics, deltaRows: +1);

    public static (int caret, int desiredPx) MoveUp(TextSnapshot s, int caret, int currentDesiredPx, int wrapColumns, ICharMetrics metrics)
        => MoveVerticalRelative(s, caret, currentDesiredPx, wrapColumns, metrics, deltaRows: -1);

    public static (int caret, int desiredPx) PageDown(TextSnapshot s, int caret, int currentDesiredPx, int wrapColumns, int visibleRows, ICharMetrics metrics)
        => MoveVerticalRelative(s, caret, currentDesiredPx, wrapColumns, metrics, deltaRows: Math.Max(1, visibleRows));

    public static (int caret, int desiredPx) PageUp(TextSnapshot s, int caret, int currentDesiredPx, int wrapColumns, int visibleRows, ICharMetrics metrics)
        => MoveVerticalRelative(s, caret, currentDesiredPx, wrapColumns, metrics, deltaRows: -Math.Max(1, visibleRows));

    private static (int caret, int desiredPx) MoveVerticalRelative(
        TextSnapshot s, int caret, int currentDesiredPx, int wrapColumns, ICharMetrics metrics, int deltaRows)
    {
        // 1) 現在位置の(論理行・行内オフセット・視覚セグメント index)を求める
        int logicalLine = s.GetLineIndexOfChar(caret);
        int lineStart = s.GetLineStart(logicalLine);
        int lineEnd = s.GetLineEnd(logicalLine, includeBreak: false);
        string lineText = lineEnd == lineStart ? string.Empty : s.GetText(lineStart, lineEnd - lineStart);
        int maxWidthPx = wrapColumns > 0 ? wrapColumns * metrics.MeasureRun("0") : 0;
        var segs = LineLayout.Wrap(lineText, maxWidthPx, metrics);
        int caretInLine = caret - lineStart;
        int segIdx = FindSegIndex(segs, caretInLine);
        var curSeg = segs[segIdx];
        int localOffset = caretInLine - curSeg.OffsetInLine;
        int desiredPx = currentDesiredPx >= 0
            ? currentDesiredPx
            : PixelMapper.OffsetToPx(lineText.AsSpan(curSeg.OffsetInLine, curSeg.Length), localOffset, metrics);

        // 2) 視覚行を deltaRows だけ移動して target 論理行 + セグメント index を得る
        //    - 折り返し ON なら 1 論理行内の別視覚行に留まることもある
        //    - 折り返し OFF なら segIdx=0 固定・logicalLine を deltaRows だけ動かす
        int targetLogicalLine, targetSegIdx;
        if (wrapColumns > 0)
        {
            (targetLogicalLine, targetSegIdx) = WalkVisualRows(s, logicalLine, segIdx, segs.Count, deltaRows, maxWidthPx, metrics);
        }
        else
        {
            targetLogicalLine = Math.Clamp(logicalLine + deltaRows, 0, s.LineCount - 1);
            targetSegIdx = 0;
        }

        // 3) target 行の該当視覚セグメントで desiredPx に一番近い offset を採る
        int targetLineStart = s.GetLineStart(targetLogicalLine);
        int targetLineEnd = s.GetLineEnd(targetLogicalLine, includeBreak: false);
        string targetLineText = targetLineEnd == targetLineStart ? string.Empty : s.GetText(targetLineStart, targetLineEnd - targetLineStart);
        var targetSegs = LineLayout.Wrap(targetLineText, maxWidthPx, metrics);
        var targetSeg = targetSegs[Math.Min(targetSegIdx, targetSegs.Count - 1)];
        var targetSpan = targetLineText.AsSpan(targetSeg.OffsetInLine, targetSeg.Length);
        int localTarget = PixelMapper.PxToOffsetSnappedRight(targetSpan, desiredPx, metrics);
        int newCaret = targetLineStart + targetSeg.OffsetInLine + localTarget;
        return (newCaret, desiredPx);
    }

    private static int FindSegIndex(IReadOnlyList<VisualSegment> segs, int caretInLine)
    {
        for (int i = 0; i < segs.Count; i++)
        {
            int segEnd = segs[i].OffsetInLine + segs[i].Length;
            if (caretInLine < segEnd || (i == segs.Count - 1 && caretInLine == segEnd)) return i;
        }
        return segs.Count - 1;
    }

    // 視覚行歩き(deltaRows は正=下方向・負=上方向)。境界は論理行 0 / LineCount-1 でクランプ。
    private static (int line, int seg) WalkVisualRows(TextSnapshot s, int startLine, int startSeg, int startSegCount,
        int deltaRows, int maxWidthPx, ICharMetrics metrics)
    {
        int line = startLine, seg = startSeg, count = startSegCount;
        int step = Math.Sign(deltaRows);
        int remain = Math.Abs(deltaRows);
        while (remain > 0)
        {
            if (step > 0)
            {
                if (seg + 1 < count) { seg++; }
                else
                {
                    if (line + 1 >= s.LineCount) return (line, seg);
                    line++; seg = 0;
                    count = SegmentCount(s, line, maxWidthPx, metrics);
                }
            }
            else
            {
                if (seg > 0) { seg--; }
                else
                {
                    if (line == 0) return (line, seg);
                    line--;
                    count = SegmentCount(s, line, maxWidthPx, metrics);
                    seg = count - 1;
                }
            }
            remain--;
        }
        return (line, seg);
    }

    private static int SegmentCount(TextSnapshot s, int line, int maxWidthPx, ICharMetrics metrics)
    {
        int ls = s.GetLineStart(line);
        int le = s.GetLineEnd(line, includeBreak: false);
        string t = le == ls ? string.Empty : s.GetText(ls, le - ls);
        return LineLayout.Wrap(t, maxWidthPx, metrics).Count;
    }
}
```

**Step 3: `PixelMapper.PxToOffsetSnappedRight` の追加(必要なら)**

`PixelMapper.OffsetToPx` は既存。逆方向の `PxToOffset` は Task 12 でマウスクリック用に追加するが、上下移動用に先に定義する。テストは Task 4 内で通過する分だけ書けばよい。

**Step 4: 実行→全体回帰**

Expected: 3〜5 件緑。

**Step 5: コミット**

```powershell
git commit -am "P3: Task 4 VerticalNavigation 純ロジック(Up/Down/Page + desired col)"
```

### Task 4 レビュー申し送り(将来対応)

- **最終行 Down 挙動の実機判定**(S-4): 現行 `VerticalNavigation.MoveDown` は最終行到達で
  同列停留(= vim normal mode 相当・シンプル)。Notepad/VSCode/Word は行末に飛ぶ挙動。
  Task 6 の EditorControl 配線後の実機テストで最終判定する(必要なら WalkVisualRows
  内の「文書末打ち切り」直後に「seg=最終・caret=lineEnd 相当」の分岐を追加)。

- **超長行性能懸念**(S-5・P7 申し送り): `MoveVerticalRelative` は論理行全体を
  `snap.GetText(lineStart, lineEnd - lineStart)` で string 化 → `LineLayout.Wrap` で
  code-point 単位に走査する。単一 1GB 行(CSV/log)で 1 キーストロークが実質フリーズ。
  典型ケース(< 1000 chars/line)は問題なし。P7(ベンチ検証)で「単一超長行」の
  非機能要件項目を追加。将来的な最適化案: `TextSnapshot.GetTextSpan(int, int)` で
  `ReadOnlySpan<char>` を返す API 追加(コピー回避)。PieceTable との相性次第。

---

### Task 5: WordBoundary 純ロジック(Ctrl+←→)

**Files:**
- Create: `src/yEdit.Core/Editing/WordBoundary.cs`
- Test: `tests/yEdit.Core.Tests/Editing/WordBoundaryTests.cs`

Unicode カテゴリで文字種を分類(英数/日本語ひらがな/カタカナ/漢字/記号/空白)し、**「同じ文字種の連続を 1 単語」**とする(現行 M3 の実装と等価)。CRLF は skip。

**Step 1: 失敗するテスト**

```csharp
public class WordBoundaryTests
{
    private static TextSnapshot S(string s) => TextBuffer.FromString(s).Current;

    [Theory]
    [InlineData("hello world", 0, 5)]   // hello の後
    [InlineData("hello world", 5, 6)]   // 空白 → 次単語頭
    [InlineData("hello world", 6, 11)]  // world の後
    [InlineData("aaa bbb ccc", 3, 4)]   // 空白 skip
    [InlineData("abc\r\ndef", 3, 5)]    // CRLF まとめて skip → def 頭
    public void NextWordStart_Ascii(string text, int from, int expected)
        => Assert.Equal(expected, WordBoundary.NextWordStart(S(text), from));

    [Theory]
    [InlineData("hello world", 11, 6)]
    [InlineData("hello world", 6, 0)]
    public void PrevWordStart_Ascii(string text, int from, int expected)
        => Assert.Equal(expected, WordBoundary.PrevWordStart(S(text), from));

    [Fact]
    public void WordBoundary_CJK_ClassSwitch()
    {
        var s = S("あいう漢字abc123");
        // ひらがな→漢字→英字→数字 で境界
        Assert.Equal(3, WordBoundary.NextWordStart(s, 0));   // "あいう" の後 → 漢字頭
        Assert.Equal(5, WordBoundary.NextWordStart(s, 3));   // "漢字" の後 → 英字頭
        Assert.Equal(8, WordBoundary.NextWordStart(s, 5));   // "abc" の後 → 数字頭
        Assert.Equal(11, WordBoundary.NextWordStart(s, 8));  // "123" の後 → EOF
    }
}
```

**Step 2: 実装**

`CharClass` 列挙: Whitespace / LineBreak / Latin / Digit / Hiragana / Katakana / Han / Other。走査は文字ずつ code-point で(サロゲート考慮)。

```csharp
namespace yEdit.Core.Editing;

using System.Globalization;
using yEdit.Core.Buffers;

internal enum CharClass { Whitespace, LineBreak, Latin, Digit, Hiragana, Katakana, Han, Other }

public static class WordBoundary
{
    public static int NextWordStart(TextSnapshot s, int caret)
    {
        int pos = caret;
        if (pos >= s.CharLength) return s.CharLength;
        // 現在の class をスキップ → 次の非 whitespace/linebreak の先頭を返す
        var start = ClassOf(s, pos);
        pos = SkipClass(s, pos, start);
        pos = SkipWhitespaceOrBreak(s, pos);
        return pos;
    }

    public static int PrevWordStart(TextSnapshot s, int caret)
    {
        int pos = caret;
        if (pos <= 0) return 0;
        // 1 左に寄って whitespace/linebreak を後方へスキップ → その class の先頭まで戻る
        pos = MoveLeftCp(s, pos);
        pos = SkipBackwardWhitespaceOrBreak(s, pos);
        var cls = ClassOf(s, pos);
        while (pos > 0)
        {
            int prev = MoveLeftCp(s, pos);
            if (ClassOf(s, prev) != cls) break;
            pos = prev;
        }
        return pos;
    }

    private static CharClass ClassOf(TextSnapshot s, int pos)
    {
        if (pos >= s.CharLength) return CharClass.Other;
        char c = s.GetChar(pos);
        if (c == '\r' || c == '\n') return CharClass.LineBreak;
        if (c == ' ' || c == '\t') return CharClass.Whitespace;
        if (c >= '0' && c <= '9') return CharClass.Digit;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_') return CharClass.Latin;
        if (c >= 0x3040 && c <= 0x309F) return CharClass.Hiragana;
        if (c >= 0x30A0 && c <= 0x30FF) return CharClass.Katakana;
        if (c >= 0x4E00 && c <= 0x9FFF) return CharClass.Han;
        return CharClass.Other;
    }

    // SkipClass / SkipWhitespaceOrBreak / MoveLeftCp などのヘルパは実装時に定義
}
```

**Step 3: 実行→全体回帰**

**Step 4: コミット**

```powershell
git commit -am "P3: Task 5 WordBoundary(Ctrl+←→・CJK 対応)"
```

### Task 5 レビュー申し送り(将来対応)

- **NavigationCommands/WordBoundary の命名統一と重複解消**(S-1/S-2・Task 15 or 将来 refactor 候補):
  - 引数名: `NavigationCommands` は `s`、`WordBoundary` は `snap` で不一致。Task 15 の設計書追記時に
    `NavigationCommands` 側を `snap` に統一する候補として記録。
  - `WordBoundary.MoveLeftCp`/`MoveRightCp` は `NavigationCommands.MoveLeftChar`/`MoveRightChar` と
    事実上同じロジック(サロゲート BMP 判定を含む)。Task 15 で「WordBoundary から
    NavigationCommands の同名関数を呼ぶ」形へ集約する候補として記録。動作等価の pure refactor。

- **CJK 拡張範囲の実機検証**(S-4・P7 申し送り): 現行 `ClassOf` の Han 判定は BMP `U+4E00..U+9FFF`
  のみ。CJK Ext A(U+3400..U+4DBF)/ CJK Compatibility Ideographs(U+F900..U+FAFF)/ Ext B以降
  (サロゲート)は Other 扱い。Scintilla 版 M3 の実装と等価(計画どおり)。P7 実機検証で
  「熟語 Ctrl+←→ が期待どおり単語まとまりで飛ぶか」を確認項目に追加。拡張が必要なら
  `HanExtension` クラスを追加し `0x3400..0x4DBF`, `0xF900..0xFAFF` を含める。

---

### Task 6: キーバインド配線(移動系:Arrow/Home/End/Page/Ctrl-Arrow/Ctrl+A)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(`OnKeyDown` + `IsInputKey` 追加)
- Test: `tests/yEdit.Editor.Tests/KeyboardNavigationTests.cs`

移動系のキーを配線する。文字挿入・削除は Task 7〜9 で別に行う。**Shift 押下時は `MoveCaretWithSelection`**、それ以外は `SetCaretCharOffset`(=選択解除)。Ctrl+A は全選択。

`IsInputKey` オーバーライド: `Keys.Left/Right/Up/Down/Home/End/PageUp/PageDown/Tab` を含めて、フォームレベルのフォーカス遷移に持っていかれないようにする。

**Step 1: 失敗するテスト**

```csharp
[Fact]
public void RightArrow_MovesCaretByOneChar() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc");
    c.SetCaretCharOffset(0);
    SendKey(c, Keys.Right);
    Assert.Equal(1, c.CaretCharOffset);
});

[Fact]
public void ShiftRightArrow_ExtendsSelection() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abcdef");
    c.SetCaretCharOffset(1);
    SendKey(c, Keys.Right | Keys.Shift);
    Assert.Equal((1, 2), c.GetSelectionCharRange());
    Assert.Equal(2, c.CaretCharOffset);
});

[Fact]
public void CtrlRight_MovesToNextWord() => Sta.Run(() =>
{
    using var c = MakeControlFocused("hello world");
    c.SetCaretCharOffset(0);
    SendKey(c, Keys.Right | Keys.Control);
    Assert.Equal(6, c.CaretCharOffset);   // "hello " の後 → 'w'
});

[Fact]
public void CtrlA_SelectsAll() => Sta.Run(() =>
{
    using var c = MakeControlFocused("hello");
    SendKey(c, Keys.A | Keys.Control);
    Assert.Equal((0, 5), c.GetSelectionCharRange());
});
```

`SendKey` ヘルパ: `Control.ProcessCmdKey` の代わりに、OnKeyDown を直接呼ぶメソッドをリフレクションで叩く(protected の内部テスト向けブリッジ)、または `InternalsVisibleTo` で `OnKeyDown` を internal ラップして呼ぶ。

**Step 2: 実装**

```csharp
protected override bool IsInputKey(Keys keyData)
{
    Keys code = keyData & Keys.KeyCode;
    return code switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
        _ => base.IsInputKey(keyData)
    };
}

// 上下移動の desired X を保持(移動系以外の操作でクリア)
private int _desiredXpx = -1;

protected override void OnKeyDown(KeyEventArgs e)
{
    base.OnKeyDown(e);
    if (_buffer is null) return;
    var snap = _buffer.Current;
    bool shift = (e.Modifiers & Keys.Shift) != 0;
    bool ctrl = (e.Modifiers & Keys.Control) != 0;

    int? target = null;
    switch (e.KeyCode)
    {
        case Keys.Left:
            target = ctrl ? WordBoundary.PrevWordStart(snap, _caret)
                          : NavigationCommands.MoveLeftChar(snap, _caret);
            break;
        case Keys.Right:
            target = ctrl ? WordBoundary.NextWordStart(snap, _caret)
                          : NavigationCommands.MoveRightChar(snap, _caret);
            break;
        case Keys.Home:
            target = ctrl ? 0 : NavigationCommands.MoveHomeSmart(snap, _caret);
            break;
        case Keys.End:
            target = ctrl ? snap.CharLength : NavigationCommands.MoveEnd(snap, _caret);
            break;
        case Keys.Up:
        {
            var (t, d) = VerticalNavigation.MoveUp(snap, _caret, _desiredXpx, _wrapColumns, _metrics);
            _desiredXpx = d; target = t; break;
        }
        case Keys.Down:
        {
            var (t, d) = VerticalNavigation.MoveDown(snap, _caret, _desiredXpx, _wrapColumns, _metrics);
            _desiredXpx = d; target = t; break;
        }
        case Keys.PageUp:
        {
            int rows = Math.Max(1, ClientSize.Height / _metrics.LineHeightPx);
            var (t, d) = VerticalNavigation.PageUp(snap, _caret, _desiredXpx, _wrapColumns, rows, _metrics);
            _desiredXpx = d; target = t; break;
        }
        case Keys.PageDown:
        {
            int rows = Math.Max(1, ClientSize.Height / _metrics.LineHeightPx);
            var (t, d) = VerticalNavigation.PageDown(snap, _caret, _desiredXpx, _wrapColumns, rows, _metrics);
            _desiredXpx = d; target = t; break;
        }
        case Keys.A when ctrl:
            SetSelectionAnchored(0, snap.CharLength);
            _buffer.BreakUndoCoalescing();
            e.Handled = true; return;
    }

    if (target is int t2)
    {
        // Left/Right/Home/End は desired をリセット(§0-6の一貫性)
        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End) _desiredXpx = -1;

        if (shift) MoveCaretWithSelection(t2);
        else SetCaretCharOffset(t2);
        _buffer.BreakUndoCoalescing();     // 純キャレット移動は coalescing 破断
        BringCaretIntoView();              // Task 7 で本実装(no-op スタブでよい)
        RaiseCaretEnteredEmptyLineIfNeeded(); // Task 13 で本実装(no-op スタブでよい)
        e.Handled = true;
    }
}

// スタブ(Task 7/13 で本実装)
private void BringCaretIntoView() { }
private void RaiseCaretEnteredEmptyLineIfNeeded() { }
```

**Step 3: 実行→全体回帰**

**Step 4: コミット**

```powershell
git commit -am "P3: Task 6 キーバインド配線(移動系 + Ctrl+A + shift 拡張)"
```

### Task 6 レビュー申し送り(将来対応)

- **Ctrl+A 後の可視化(I-1・Task 7 で要判断)**: Ctrl+A は `_caret = CharLength` にジャンプ
  させるが、現行は `BringCaretIntoView` を呼ばない(選択があるので
  `RaiseCaretEnteredEmptyLineIfNeeded` は意図的にスキップ)。100万行文書でキャレットが
  遥か下端に行くケースで可視化が追従しない。Task 7 の `BringCaretIntoView` 本実装時に、
  Ctrl+A 分岐でも呼ぶかどうかを DoD レビューで判断。多くのエディタ(Notepad/VSCode)は
  Ctrl+A 後スクロールしない慣習。

- **Tab キーの IsInputKey 追加(S-2・Task 8/9 送り)**: Task 6 の `IsInputKey` は
  移動系 8 キーのみ。Task 8/9 で Tab を `\t` 挿入として配線するとき、`IsInputKey` にも
  Tab を追加すること(追加しないとフォーカス移動に持って行かれて `OnKeyPress` で
  `\t` が来ない)。

---

### Task 7: キャレット追従スクロール(BringCaretIntoView + EnsureVisibleCharRange)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(`BringCaretIntoView`/`EnsureVisibleCharRange`)
- Modify: `src/yEdit.Editor/EditorControl.cs`(`ComputeCaretPoint` を横方向可視性つき化・P2 申し送り)
- Test: `tests/yEdit.Editor.Tests/CaretScrollTests.cs`

移動系すべてから呼ぶ。**垂直: caret が可視外なら TopLine 調整。水平: 折り返し OFF で caret X が [0, paintWidth) 外なら ScrollX 調整**。

**Step 1: 失敗するテスト**

```csharp
[Fact]
public void BringCaretIntoView_ScrollsVertically() => Sta.Run(() =>
{
    using var c = MakeControlFocusedSized("a\nb\nc\nd\ne\nf\ng", width: 200, height: 40); // 2 行だけ可視
    c.SetCaretCharOffset(12); // 末尾 'g'
    // 移動系経路経由で呼ばれるので直接呼ぶ検証
    Assert.True(c.TopLine >= 5); // 少なくとも最終行まで見えるように
});

[Fact]
public void EnsureVisibleCharRange_JumpsAndCentersIfOutside() => Sta.Run(() => { /* ... */ });
```

**Step 2: 実装**

```csharp
public void BringCaretIntoView()
{
    if (_buffer is null) return;
    var snap = _buffer.Current;
    int logicalLine = snap.GetLineIndexOfChar(_caret);
    int visibleRows = Math.Max(1, ClientSize.Height / _metrics.LineHeightPx);

    // 垂直: caret 行が [TopLine, TopLine+visibleRows) に入るように TopLine 調整
    if (logicalLine < _topLine)
    {
        TopLine = logicalLine;
    }
    else if (logicalLine >= _topLine + visibleRows)
    {
        TopLine = logicalLine - visibleRows + 1;
    }

    // 水平(折り返し OFF のみ): caret X が [ScrollX, ScrollX+paintWidth) に入るように ScrollX 調整
    if (_wrapColumns == 0 && _hscroll.Visible)
    {
        var (x, _, visible) = ComputeCaretPoint(_caret);
        if (visible)
        {
            int paintWidth = Math.Max(0, ClientSize.Width - _vscroll.Width);
            if (x < _scrollX) ScrollX = x;
            else if (x >= _scrollX + paintWidth) ScrollX = x - paintWidth + _metrics.MeasureRun("0");
        }
    }
}

public void EnsureVisibleCharRange(int start, int length)
{
    if (_buffer is null) return;
    int end = SnapAndClamp(start + Math.Max(0, length));
    // 単純にレンジ末尾を可視化(検索ジャンプ用途で十分)
    var savedCaret = _caret;
    _caret = end;
    BringCaretIntoView();
    _caret = savedCaret;
    Invalidate();
}
```

**Step 3: Task 6 のスタブを本実装で置換**

`BringCaretIntoView()` の本体が実装されたので、Task 6 で仕込んだ空のスタブメソッドは自動的に有効化される(=同名メソッドの中身に本実装が入る)。

**Step 4: 実行→全体回帰**

**Step 5: コミット**

```powershell
git commit -am "P3: Task 7 キャレット追従スクロール(BringCaretIntoView + EnsureVisibleCharRange)"
```

### Task 7 レビュー申し送り(将来対応)

- **編集後処理ヘルパ抽出(S-4・Task 8 冒頭検討)**: Task 8/9 の編集経路では
  `_desiredXpx = -1` / `AfterEdit`(scroll 再計算)/ `BringCaretIntoView` /
  `RaiseCaretEnteredEmptyLineIfNeeded` の後処理を漏れなく呼ぶ必要がある。
  Task 8 冒頭で共通ヘルパ `AfterCaretChange(kind: Edit|PureMove|Jump)` を切り出す
  余地あり。SetCaretCharOffset に組み込むと Ctrl+A の CharLength ジャンプで
  可視化まで走ってしまうため慎重に切り分ける(現行 Ctrl+A は BringCaretIntoView
  を呼ばない=Notepad/VSCode 慣習)。

- **PageUp/PageDown の rows 計算統一(将来 refactor)**: OnKeyDown の Page 分岐は
  `ClientSize.Height / LineHeightPx` を使うが、Task 7 の BringCaretIntoView は
  paintHeight ベース(hscroll 高減算後)を使う。Task 15 の最終整理で統一検討
  (Task 7 レビュー I-1 対応で BringCaretIntoView 側は paintHeight ベースになった)。

---

### Task 8: 文字挿入(OnKeyPress + Overtype)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(`OnKeyPress` + `Overtype` プロパティ + `TextBuffer` 配線)
- Test: `tests/yEdit.Editor.Tests/TextInsertionTests.cs`

`WM_CHAR` は `OnKeyPress` で来る。Ctrl/Alt 修飾付きの制御文字(Ctrl+A の 0x01 等)は 0x20 未満で来るので、**制御文字は無視**(BackSpace=0x08 と Enter=0x0D と Tab=0x09 だけ Task 9 で処理)。

**Step 1: 失敗するテスト**

```csharp
[Fact]
public void KeyPress_InsertsChar_AtCaret() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc");
    c.SetCaretCharOffset(1);
    SendKeyPress(c, 'X');
    Assert.Equal("aXbc", c.GetText());
    Assert.Equal(2, c.CaretCharOffset);
});

[Fact]
public void KeyPress_ReplacesSelection() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abcdef");
    c.SetSelectionCharRange(1, 4);
    SendKeyPress(c, 'X');
    Assert.Equal("aXef", c.GetText());
    Assert.Equal(2, c.CaretCharOffset);
});

[Fact]
public void KeyPress_Overtype_Replaces1Char() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abcdef");
    c.SetCaretCharOffset(2);
    c.Overtype = true;
    SendKeyPress(c, 'X');
    Assert.Equal("abXdef", c.GetText());
    Assert.Equal(3, c.CaretCharOffset);
});

[Fact]
public void KeyPress_Overtype_AtEol_InsertsWithoutReplace() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc\ndef");
    c.SetCaretCharOffset(3);  // \n の直前
    c.Overtype = true;
    SendKeyPress(c, 'X');
    Assert.Equal("abcX\ndef", c.GetText());   // 上書きモードでも改行は消さない
});
```

**Step 2: 実装**

```csharp
public bool Overtype { get; set; }
public bool ReadOnly { get; set; }

// EditorControl.cs に、外部からテキスト全文を取り出す簡易 API を生やす(テスト用途に InternalsVisibleTo で公開)
internal string GetText() => _buffer?.Current.GetText(0, _buffer.Current.CharLength) ?? string.Empty;

protected override void OnKeyPress(KeyPressEventArgs e)
{
    base.OnKeyPress(e);
    if (_buffer is null || ReadOnly) return;
    char ch = e.KeyChar;
    // 制御文字は Enter/Tab/BackSpace 以外は無視(それらは OnKeyDown で処理・Task 9)
    if (ch < 0x20 && ch != '\t') { return; }

    var (s, en) = GetSelectionCharRange();
    string ins = ch.ToString();

    if (s != en)
    {
        // 選択があるときは無条件で置換(Overtype 影響なし)
        _buffer.Replace(s, en - s, ins);
        _caret = _anchor = s + ins.Length;
    }
    else if (Overtype)
    {
        // 上書きモード: 直後 1 文字を潰して置換。ただし改行なら潰さず挿入(Scintilla と同じ挙動)
        var snap = _buffer.Current;
        int overwriteLen = 0;
        if (_caret < snap.CharLength)
        {
            char nc = snap.GetChar(_caret);
            if (nc != '\r' && nc != '\n')
            {
                // サロゲート対
                overwriteLen = (char.IsHighSurrogate(nc) && _caret + 1 < snap.CharLength
                                && char.IsLowSurrogate(snap.GetChar(_caret + 1))) ? 2 : 1;
            }
        }
        _buffer.Replace(_caret, overwriteLen, ins);
        _caret = _anchor = _caret + ins.Length;
    }
    else
    {
        _buffer.Insert(_caret, ins);
        _caret = _anchor = _caret + ins.Length;
    }

    _desiredXpx = -1;
    UpdateVerticalScrollbar();
    UpdateHorizontalScrollbar();
    PositionCaret();
    BringCaretIntoView();
    Invalidate();
    e.Handled = true;
}
```

**Step 3: 実行→全体回帰**

**Step 4: コミット**

```powershell
git commit -am "P3: Task 8 文字挿入(OnKeyPress + Overtype + 選択置換)"
```

### Task 8 レビュー申し送り(将来対応)

- **Tab(0x09)の扱いの Plan 逸脱**(S-1・Task 9 で決着): Plan §Task 8 Step 2 は
  `if (ch < 0x20 && ch != '\t') { return; }` で Tab を OnKeyPress で `\t` 挿入する経路
  として書いていたが、実装は `ch < 0x20` で Tab も無視。Plan §Task 9 は「Tab は
  OnKeyPress で処理」と書いていて逸脱状態。Task 9 実装冒頭で以下のいずれかを確定させる:
  - (A) 現行実装維持(Tab は Task 9 の OnKeyDown で `Keys.Tab` case 追加+`\t` 挿入+
    `_desiredXpx = -1`+`AfterEdit`)
  - (B) OnKeyPress の制御文字フィルタから `\t` を除外(Plan 原案どおり)
  現行実装のまま(A)が Task 6 申し送り「IsInputKey に Tab を追加+OnKeyDown で処理」
  と整合する。**Task 9 実装者は Keys.Tab の case 追加+IsInputKey 追加をチェックリスト化**。

- **S-3/S-4/S-5 は現状 OK**: AfterEdit の PositionCaret + BringCaretIntoView 重複は
  冪等・許容。`_caret = _anchor` 直接代入の理由付け xmldoc は Task 9〜11 で補強候補。
  `_desiredXpx = -1` の呼び出し側個別設定は Task 9 削除系で書き忘れないよう注意。

---

### Task 9: 削除・Enter・Tab(BackSpace/Delete/Enter/Tab + EolMode)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(OnKeyDown に BackSpace/Delete/Enter/Tab/Insert)
- Test: `tests/yEdit.Editor.Tests/TextEditingTests.cs`

`Enter` は `EolMode` に応じて "\r\n"/"\n"/"\r" を挿入。`Tab` は `\t` を挿入(TabsToSpaces は P6 送り)。`Insert` キーは Overtype トグル。**BackSpace/Delete でサロゲートペアは 1 文字扱い**。

**Step 1: 失敗するテスト**

```csharp
[Fact]
public void Backspace_DeletesPrevChar() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc");
    c.SetCaretCharOffset(2);
    SendKey(c, Keys.Back);
    Assert.Equal("ac", c.GetText());
    Assert.Equal(1, c.CaretCharOffset);
});

[Fact]
public void Backspace_DeletesSurrogatePair() => Sta.Run(() =>
{
    using var c = MakeControlFocused("a😀b");
    c.SetCaretCharOffset(3); // 😀 の直後
    SendKey(c, Keys.Back);
    Assert.Equal("ab", c.GetText());
});

[Fact]
public void Enter_InsertsCrlf_ByEolMode() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc");
    c.EolMode = LineEnding.Crlf;
    c.SetCaretCharOffset(3);
    SendKey(c, Keys.Enter);
    Assert.Equal("abc\r\n", c.GetText());
});

[Fact]
public void Enter_InsertsLf_WhenEolLf() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc");
    c.EolMode = LineEnding.Lf;
    c.SetCaretCharOffset(3);
    SendKey(c, Keys.Enter);
    Assert.Equal("abc\n", c.GetText());
});

[Fact]
public void Insert_TogglesOvertype() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc");
    Assert.False(c.Overtype);
    SendKey(c, Keys.Insert);
    Assert.True(c.Overtype);
});
```

**Step 2: 実装(OnKeyDown への追加)**

```csharp
// EolMode プロパティ
public LineEnding EolMode { get; set; } = LineEnding.Crlf;

// OnKeyDown の switch に追加
case Keys.Back when !ReadOnly:
{
    var (s, en) = GetSelectionCharRange();
    if (s != en) { _buffer.Replace(s, en - s, ""); _caret = _anchor = s; }
    else if (_caret > 0)
    {
        int start = NavigationCommands.MoveLeftChar(_buffer.Current, _caret);
        _buffer.Delete(start, _caret - start);
        _caret = _anchor = start;
    }
    _desiredXpx = -1;
    AfterEdit();
    e.Handled = true; return;
}
case Keys.Delete when !ReadOnly:
{
    var (s, en) = GetSelectionCharRange();
    if (s != en) { _buffer.Replace(s, en - s, ""); _caret = _anchor = s; }
    else if (_caret < _buffer.Current.CharLength)
    {
        int next = NavigationCommands.MoveRightChar(_buffer.Current, _caret);
        _buffer.Delete(_caret, next - _caret);
    }
    _desiredXpx = -1;
    AfterEdit();
    e.Handled = true; return;
}
case Keys.Enter when !ReadOnly:
{
    string eol = EolMode switch { LineEnding.Lf => "\n", LineEnding.Cr => "\r", _ => "\r\n" };
    var (s, en) = GetSelectionCharRange();
    _buffer.Replace(s, en - s, eol);
    _caret = _anchor = s + eol.Length;
    _desiredXpx = -1;
    AfterEdit();
    e.Handled = true; return;
}
case Keys.Insert:
    Overtype = !Overtype;
    e.Handled = true; return;

// Tab は OnKeyPress で '\t' が来るためそちらに任せる(Ctrl+Tab はフォーム側の予約)
```

`AfterEdit()`: `UpdateVerticalScrollbar/HScroll/PositionCaret/BringCaretIntoView/Invalidate` をまとめる(Task 8 の反復排除)。

**Step 3: 実行→全体回帰**

**Step 4: コミット**

```powershell
git commit -am "P3: Task 9 削除/Enter/Insert 配線 + EolMode(サロゲート対応)"
```

### Task 9 レビュー申し送り(将来対応)

- **BackSpace の CRLF ペア削除(S-5・P6 検討)**: `NavigationCommands.MoveLeftChar` は
  1 code-point 削除なので、キャレットが `"\r\n"` の直後にあると LF だけを削除して
  CR が残る(Windows ネイティブ Edit と同挙動)。`Enter → BackSpace` で 1 発戻したい
  直感的 UX に反する。P6 の App 層配線時に「CRLF 境界での BackSpace 一括削除」を
  検討候補として記録(実装は BackSpace 経路で `snap.GetChar(_caret-1) == '\n' &&
  _caret >= 2 && snap.GetChar(_caret-2) == '\r'` 判定で 2 code-units 削除)。

---

### Task 10: Undo/Redo 配線 + Modified/SetSavePoint/EmptyUndoBuffer

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(Undo/Redo/CanUndo/CanRedo/Modified/SetSavePoint/EmptyUndoBuffer + Ctrl+Z/Y)
- Test: `tests/yEdit.Editor.Tests/UndoRedoTests.cs`

**Step 1: 失敗するテスト**

```csharp
[Fact]
public void Undo_RestoresText_AndCaret() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc");
    c.SetCaretCharOffset(3);
    SendKeyPress(c, 'X');
    Assert.Equal("abcX", c.GetText());
    Assert.True(c.CanUndo);
    Assert.True(c.Modified);
    SendKey(c, Keys.Z | Keys.Control);
    Assert.Equal("abc", c.GetText());
    Assert.Equal(3, c.CaretCharOffset);
});

[Fact]
public void Redo_ReappliesEdit() => Sta.Run(() => { /* ... */ });

[Fact]
public void SetSavePoint_ResetsModified() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc");
    SendKeyPress(c, 'X');
    Assert.True(c.Modified);
    c.SetSavePoint();
    Assert.False(c.Modified);
});
```

**Step 2: 実装**

```csharp
public bool Modified => _buffer?.Modified ?? false;
public bool CanUndo => _buffer?.CanUndo ?? false;
public bool CanRedo => _buffer?.CanRedo ?? false;

public new void Undo()
{
    if (_buffer is null) return;
    var r = _buffer.Undo();
    if (r is null) return;
    _caret = _anchor = Math.Clamp(r.Value.CaretPos, 0, _buffer.Current.CharLength);
    AfterEdit();
}
public void Redo()
{
    if (_buffer is null) return;
    var r = _buffer.Redo();
    if (r is null) return;
    _caret = _anchor = Math.Clamp(r.Value.CaretPos, 0, _buffer.Current.CharLength);
    AfterEdit();
}

public void SetSavePoint() => _buffer?.MarkSaved();
public void EmptyUndoBuffer() => _buffer?.ClearUndo();

// OnKeyDown への追加
case Keys.Z when ctrl && !ReadOnly:
    Undo(); e.Handled = true; return;
case Keys.Y when ctrl && !ReadOnly:
    Redo(); e.Handled = true; return;
```

**Step 3: 実行→全体回帰**

**Step 4: コミット**

```powershell
git commit -am "P3: Task 10 Undo/Redo 配線 + Modified/SetSavePoint/EmptyUndoBuffer"
```

---

### Task 11: クリップボード(Cut/Copy/Paste)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(Cut/Copy/Paste + Ctrl+X/C/V + Ctrl+Insert/Shift+Insert/Shift+Del)
- Test: `tests/yEdit.Editor.Tests/ClipboardTests.cs`

`Clipboard.SetText/GetText` は STA 必須(テストは Sta.Run 経由でOK)。Copy 時に**行末改行がない選択でも 1 行選択なら EolMode に応じた改行を末尾に追加する**などの Scintilla 独自仕様は v1 では真似ない(=素直に選択文字列だけ)。

**Step 1: 失敗するテスト**

```csharp
[Fact]
public void Copy_PutsSelectedText_OnClipboard() => Sta.Run(() =>
{
    using var c = MakeControlFocused("hello");
    c.SetSelectionCharRange(1, 4);
    c.Copy();
    Assert.Equal("ell", Clipboard.GetText(TextDataFormat.UnicodeText));
    Assert.Equal("hello", c.GetText());  // Copy は本文不変
});

[Fact]
public void Cut_RemovesSelection() => Sta.Run(() =>
{
    using var c = MakeControlFocused("hello");
    c.SetSelectionCharRange(1, 4);
    c.Cut();
    Assert.Equal("ho", c.GetText());
    Assert.Equal(1, c.CaretCharOffset);
});

[Fact]
public void Paste_InsertsClipboardAtCaret() => Sta.Run(() =>
{
    using var c = MakeControlFocused("hello");
    Clipboard.SetText("XY");
    c.SetCaretCharOffset(2);
    c.Paste();
    Assert.Equal("heXYllo", c.GetText());
    Assert.Equal(4, c.CaretCharOffset);
});
```

**Step 2: 実装**

```csharp
public void Copy()
{
    if (_buffer is null) return;
    var (s, en) = GetSelectionCharRange();
    if (s == en) return;
    string text = _buffer.Current.GetText(s, en - s);
    // TextDataFormat.UnicodeText 固定(§0-10)
    Clipboard.SetText(text, TextDataFormat.UnicodeText);
}
public void Cut()
{
    if (_buffer is null || ReadOnly) return;
    var (s, en) = GetSelectionCharRange();
    if (s == en) return;
    Copy();
    _buffer.Replace(s, en - s, "");
    _caret = _anchor = s;
    _desiredXpx = -1;
    AfterEdit();
}
public void Paste()
{
    if (_buffer is null || ReadOnly) return;
    if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) return;
    string text = Clipboard.GetText(TextDataFormat.UnicodeText);
    if (string.IsNullOrEmpty(text)) return;
    var (s, en) = GetSelectionCharRange();
    _buffer.Replace(s, en - s, text);
    _caret = _anchor = s + text.Length;
    _desiredXpx = -1;
    AfterEdit();
}
public new void SelectAll() => SetSelectionAnchored(0, _buffer?.Current.CharLength ?? 0);

// OnKeyDown への追加
case Keys.X when ctrl && !ReadOnly: Cut(); e.Handled = true; return;
case Keys.C when ctrl: Copy(); e.Handled = true; return;
case Keys.V when ctrl && !ReadOnly: Paste(); e.Handled = true; return;
// レガシー: Ctrl+Insert=Copy, Shift+Insert=Paste, Shift+Delete=Cut
case Keys.Insert when ctrl: Copy(); e.Handled = true; return;
case Keys.Insert when shift && !ReadOnly: Paste(); e.Handled = true; return;
case Keys.Delete when shift && !ReadOnly: Cut(); e.Handled = true; return;
```

**注意**: Keys.Insert が Task 9 の Overtype トグルとぶつかる。`Keys.Insert` は **修飾なしで Overtype、Ctrl/Shift 付きで Copy/Paste** に条件分岐する。

**Step 3: 実行→全体回帰**

**Step 4: コミット**

```powershell
git commit -am "P3: Task 11 クリップボード(Cut/Copy/Paste + レガシーキー)"
```

---

### Task 12: マウス(Down/Move/Up/DoubleClick/Wheel精度改善) + PxToOffset

**Files:**
- Modify: `src/yEdit.Core/Layout/PixelMapper.cs`(`PxToOffsetSnappedRight` 本実装 + `PxToOffsetInLine` 追加)
- Modify: `src/yEdit.Editor/EditorControl.cs`(OnMouseDown/Move/Up/DoubleClick + Wheel 改善)
- Test: `tests/yEdit.Core.Tests/Layout/PixelMapperTests.cs`(px → offset の逆写像)
- Test: `tests/yEdit.Editor.Tests/MouseInputTests.cs`

**Step 1: 失敗するテスト**

`PixelMapper.PxToOffsetSnappedRight`: 与えられた px より大きい offset の中で最小(=px 位置を右にスナップ)。文字境界は必ず code-point 境界。

```csharp
[Theory]
[InlineData("abc", 0, 0)]
[InlineData("abc", 4, 1)]     // 'a' の中間 → 右で 'b' の頭
[InlineData("abc", 8, 1)]     // 'b' の頭ちょうど
[InlineData("abc", 20, 3)]    // 幅超過 → 末尾
public void PxToOffsetSnappedRight_Ascii_HalfWidth8(string text, int px, int expected)
    => Assert.Equal(expected, PixelMapper.PxToOffsetSnappedRight(text.AsSpan(), px, new MonoCharMetrics(8, 16, 20)));
```

Mouse テスト:
```csharp
[Fact]
public void MouseDown_MovesCaret_ToClickedChar() => Sta.Run(() => { /* ... */ });
[Fact]
public void MouseDrag_SelectsRange() => Sta.Run(() => { /* ... */ });
[Fact]
public void DoubleClick_SelectsWord() => Sta.Run(() => { /* ... */ });
```

**Step 2: 実装**

`PixelMapper.PxToOffsetSnappedRight`: 累積幅を左から積んで、px を超えた最初の offset を返す。code-point 境界に丸める(サロゲート pair なら 2 単位進める)。

`OnMouseDown`: クライアント座標 → 論理行 → 行内 seg → seg 内 offset → char offset。**Shift 押下ならアンカー保持で `MoveCaretWithSelection`、それ以外は選択解除**。

`OnMouseMove(_, e)`: `e.Button == Left && MouseDown 起点` のときドラッグ選択(`MoveCaretWithSelection`)。

`OnMouseDoubleClick`: クリック位置の word を `WordBoundary.PrevWordStart` / `NextWordStart` で決めて `SetSelectionAnchored`。

`OnMouseWheel` の改善(P2 申し送り M-3): `SystemInformation.MouseWheelScrollLines` を使う。累積(1 tick で 3 行に満たない場合の残余を持ち越し)する `_wheelAccum` を持つ。

```csharp
private int _wheelAccum;
protected override void OnMouseWheel(MouseEventArgs e)
{
    base.OnMouseWheel(e);
    if (_buffer is null) return;
    _wheelAccum += e.Delta;
    int wheelDelta = SystemInformation.MouseWheelScrollLines <= 0
        ? 3
        : SystemInformation.MouseWheelScrollLines;
    while (_wheelAccum >= 120) { TopLine = _topLine - wheelDelta; _wheelAccum -= 120; }
    while (_wheelAccum <= -120) { TopLine = _topLine + wheelDelta; _wheelAccum += 120; }
}
```

**Step 3: 実行→全体回帰**

**Step 4: コミット**

```powershell
git commit -am "P3: Task 12 マウス配線(Down/Move/Up/DoubleClick/Wheel精度)+ PxToOffset"
```

### Task 12 レビュー申し送り(将来対応)

- **極端 Wheel Delta の while ループ最適化**(S-3・Task 14 ベンチ観測):
  `delta=24000` で while ループ 200 回、TopLine setter の早期 return で境界後は
  Invalidate/PositionCaret が抑止されるが、境界到達までの反復(例: TopLine=100→0
  で 34 回)は 1 WM_MOUSEWHEEL 内で走る。Task 14 のベンチで顕在化するか要観測。
  緩和策: 境界クランプ後に while 早期 break(残余捨てる)を追加。ただし direction
  reversal 時の即応性が落ちるトレードオフ。

- **ドラッグ選択末端の空行イベント**(S-4・Task 13 実装時に判断):
  現行は `OnMouseDown` でのみ `RaiseCaretEnteredEmptyLineIfNeeded` を呼び、ドラッグで
  空行に着地しても Task 13 のイベントは発火しない。Task 13 で純キャレット移動時の
  仕様が確定した後、ドラッグ選択末端(=マウス経路)での SR 通知要否を再検討。

- **空白ダブルクリック挙動の実機評価**(I-1・Task 14 smoke / P7):
  現状は「前単語頭+target 位置までの空白 run」を選択(Notepad 近似・非対称仕様=
  空白 run のどこをクリックするかで選択長が変わる)。実装は `NextWordBoundary` の
  xmldoc に明文化・現状仕様回帰保護テストは `MouseInputTests.DoubleClick_OnWhitespace_
  SelectsPrevWordPlusWhitespaceRun`。Task 14 smoke で実機体感を確認し、VS Code 挙動
  (空白 run 単独選択)への変更要否を P7 実機検証で最終判断。

---

### Task 13: CaretEnteredEmptyLine + RaiseUiaSelectionEvents(受け口のみ)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(空行遷移検知 + イベント発火 + RaiseUiaSelectionEvents プロパティ)
- Test: `tests/yEdit.Editor.Tests/EmptyLineNavigationTests.cs`

継承 SR 対策§2-5-3 の受け口。**純キャレット移動で行が変わり、着地行が空行(len=0)のとき**発火。App 層は P6 でこれを購読して能動発声「空行」する。**UIA 発火自体は P5**なので、`RaiseUiaSelectionEvents` はプロパティ受け口のみ(P3 では読み書きするだけで挙動なし)。

**Step 1: 失敗するテスト**

```csharp
[Fact]
public void CaretEnteredEmptyLine_FiresOnEmptyRow() => Sta.Run(() =>
{
    using var c = MakeControlFocused("abc\n\nxyz");
    c.SetCaretCharOffset(0);
    int fired = 0; c.CaretEnteredEmptyLine += (_, _) => fired++;
    // "abc\n" → 行1(空)へ移動
    c.SetCaretCharOffset(4);
    Assert.Equal(1, fired);
    // 同じ行のままなら発火しない
    c.SetCaretCharOffset(4);
    Assert.Equal(1, fired);
});

[Fact]
public void CaretEnteredEmptyLine_DoesNotFire_OnEdit() => Sta.Run(() =>
{
    // 編集(insert/delete)後は着地が空行でも発火しない(SR は編集通知経路で読む)
});
```

**Step 2: 実装**

```csharp
public event EventHandler? CaretEnteredEmptyLine;
public bool RaiseUiaSelectionEvents { get; set; } = true;  // P5 で本挙動

private int _lastCaretLine;

private void RaiseCaretEnteredEmptyLineIfNeeded()
{
    if (_buffer is null) return;
    var snap = _buffer.Current;
    int line = snap.GetLineIndexOfChar(_caret);
    if (line == _lastCaretLine) return;
    _lastCaretLine = line;
    int lineLen = snap.GetLineEnd(line, includeBreak: false) - snap.GetLineStart(line);
    if (lineLen == 0) CaretEnteredEmptyLine?.Invoke(this, EventArgs.Empty);
}
```

**Task 6 のスタブ `RaiseCaretEnteredEmptyLineIfNeeded()` を本体で置換**。**編集経路(Task 8/9/11)からは呼ばない**(仕様どおり:純キャレット移動時のみ)。

**Step 3: 実行→全体回帰**

**Step 4: コミット**

```powershell
git commit -am "P3: Task 13 CaretEnteredEmptyLine + RaiseUiaSelectionEvents 受け口"
```

---

### Task 14: 応答性ベンチ + ReplaceCharRange/CurrentLine/GetColumn 補完 + O(N²) 検証

**Files:**
- Modify: `tests/yEdit.Core.Bench/Program.cs`(`--typing` サブコマンド追加=1M 文字を 1 文字ずつ挿入 + Undo 全 1M 回)
- Modify: `src/yEdit.Editor/EditorControl.cs`(`ReplaceCharRange`/`CurrentLine`/`GetColumn`)
- Test: `tests/yEdit.Editor.Tests/EditorControlContractTests.cs`(残り互換 API のスモーク)
- Modify: `src/yEdit.Core/Layout/FrameBuilder.cs`(P2 申し送り: EmitWhitespaceGlyphs の O(N²) を単走査へ)- 顕在化する場合のみ

**Step 1: `--typing` ベンチを走らせ、1 文字挿入 1M 回が合計 5 秒以内(=1 挿入 5µs 以内)であることを確認**

```csharp
// Program.cs の switch 分岐
case "--typing":
    var b = new TextBufferBuilder();
    b.Add(new byte[0]);
    var buf = b.Build();
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < 1_000_000; i++) buf.Insert(buf.Current.CharLength, "a");
    sw.Stop();
    Console.WriteLine($"typing 1M: {sw.Elapsed.TotalSeconds:F3}s ({sw.Elapsed.TotalMicroseconds / 1_000_000.0:F2}µs/insert)");
    // 目標: 5s 以内(=5µs/insert 以下)
    return sw.Elapsed.TotalSeconds < 5.0 ? 0 : 1;
```

**Step 2: P2 申し送りの EmitWhitespaceGlyphs O(N²) を長行で計測**

`--layout --whitespace --linewidth 10000` などのオプションで 1 行 10,000 字+空白多め文字列に対する Frame ビルド時間を計測。閾値超なら FrameBuilder を単走査型へ書き換え(Task 15 レビューが指摘したら対応)。

**Step 3: 残り互換 API を EditorControl に追加**

```csharp
public int CurrentLine => _buffer is null ? 0 : _buffer.Current.GetLineIndexOfChar(_caret);

public int GetColumn(int offset)
{
    if (_buffer is null) return 0;
    int snapped = SnapAndClamp(offset);
    var snap = _buffer.Current;
    int line = snap.GetLineIndexOfChar(snapped);
    return snapped - snap.GetLineStart(line);
}

public void ReplaceCharRange(int start, int length, string replacement)
{
    if (_buffer is null || ReadOnly) return;
    ArgumentNullException.ThrowIfNull(replacement);
    int s = SnapAndClamp(start);
    int e = SnapAndClamp(start + Math.Max(0, length));
    _buffer.Replace(s, e - s, replacement);
    _caret = _anchor = s + replacement.Length;
    _desiredXpx = -1;
    AfterEdit();
}
```

**Step 4: 全体回帰 + ベンチ**

```powershell
dotnet build yEdit.sln -c Release; dotnet test yEdit.sln -c Release --nologo
dotnet run --project tests/yEdit.Core.Bench -c Release -- --typing
```

**Step 5: コミット**

```powershell
git commit -am "P3: Task 14 応答性ベンチ + ReplaceCharRange/CurrentLine/GetColumn + O(N²)検証"
```

---

### Task 15: Smoke 拡張 + 別エージェント最終レビュー + 設計書追記

**Files:**
- Modify: `tests/yEdit.Editor.Smoke/Program.cs`(タイピング/マウスクリック/選択の目視デモを追加)
- Modify: `docs/plans/2026-07-05-custom-editcontrol-design.md`(§3 P3 結果表を追記)

**Step 1: Smoke 拡張(手動確認用)**

`yEdit.Editor.Smoke` に「開いたファイルにタイピング・BackSpace・矢印・マウスクリック・ドラッグ選択・Cut/Copy/Paste・Undo/Redo が効くか」目視確認する画面を作る(=既存 File>Open の画面にキー入力ハンドラが繋がるので実は追加コード不要な可能性が高い。動作確認スクリプトのみ)。

**Step 2: 別エージェントレビュー依頼**

`superpowers:code-reviewer` エージェントに次のコンテキストで依頼:
- 対象コミット範囲: Task 1〜14(commit 範囲)
- 期待: P3 の DoD(Scintilla 版との操作比較チェックリスト一致・編集後再レイアウトの局所性)
- 焦点:
  - EditorControl の状態遷移(_anchor/_caret の invariant・_desiredXpx リセットタイミング・_lastCaretLine の初期化)
  - TextBuffer.BreakUndoCoalescing の呼び忘れ経路
  - OnKeyDown/OnKeyPress の Handled=true 抜け(移動系で Tab フォーカス移動が起きないか)
  - サロゲートペアの取り扱い漏れ(BackSpace/Delete/Overtype)
  - キャレット追従スクロールが呼ばれない編集経路がないか
  - Clipboard 例外(ロック中エラー)の握り

**Step 3: レビュー指摘の Critical/Important を Task 15 内で修正**

```powershell
git commit -am "P3: Task 15 レビュー対応(...)"
```

**Step 4: 設計書 §3 に P3 結果表を追記**

P2 結果表(§3 P2 結果)の直下に、P3 結果を同フォーマットで書く:
- テスト数 470 → N 全緑
- ベンチ(--typing): 5µs 以内目標に対する実測
- 公開 API(P3 追加分)一覧
- 設計判断のポイント(アンカー化・純ロジック層追加・BringCaretIntoView 共通化)
- 別エージェントレビュー結果
- P4/P5/P6 への申し送り

**Step 5: コミット**

```powershell
git commit -am "P3: 設計書 §3 に P3 結果を追記(DoD 達成)"
```

---

## DoD(P3 完了条件)

1. **build 0 警告 / 全テスト緑**(既存 470 件 + P3 新規)
2. **Scintilla 版との操作比較チェックリスト一致**:
   - 移動系: 矢印(単/Shift)/Ctrl+矢印(単語)/Home/End(Ctrl 付含む)/PageUp/PageDown/Ctrl+A
   - 編集系: 文字挿入(Overtype 含)/BackSpace/Delete/Enter(EolMode 反映)/Tab
   - 履歴系: Undo/Redo/SetSavePoint 相当/Modified 判定
   - クリップボード: Cut/Copy/Paste(Ctrl+X/C/V + Ctrl/Shift+Insert/Delete)
   - マウス: クリック(移動)/Shift+クリック(選択拡張)/ドラッグ選択/ダブルクリック(単語選択)/ホイール(精度改善済)
3. **キャレット追従スクロール**が全経路(キー移動・マウス・編集・Undo/Redo)で有効
4. **応答性ベンチ**(`--typing` 1M 挿入)= 5µs/挿入以下
5. **アンカー概念導入**で shift+左方向の選択保持が可能
6. **CaretEnteredEmptyLine イベント**が純キャレット移動時のみ発火(編集経路では発火しない)
7. **既存 API 契約**(P1/P2 の公開 API)を破壊しない
8. **別エージェントレビュー**の Critical=0 / Important=0(または Task 15 で消化)

## 申し送り(P4 以降で対応)

- **IME(WM_IME_*)**: P4 全体で対応。P3 の OnKeyPress は確定文字のみ来る前提のまま。
- **UIA イベント発火**(TextSelectionChangedEvent 等): P5 で `RaiseUiaSelectionEvents` の本挙動化。P3 は受け口のみ。
- **word ナビ時の能動発声**(P0 で確定した PC-Talker 対応・設計書 P0 結果): P5 で App 層に載る(=`CaretEnteredEmptyLine` と同じ経路で単語スパン発話)。
- **TabsToSpaces / TabWidth**: P6 で反映(App 層 EditorAppearance からの伝播)。
- **単語選択のダブルクリックドラッグ拡張**(1 単語→2 単語→…): v1 スコープ外(申し送り)。
- **矩形選択 / 列モード**: v1 スコープ外(設計書 §0-10)。
- **クリップボード形式**: `TextDataFormat.UnicodeText` 固定(§0-10)。RTF/HTML は将来。
- **Tab キーのフォーム間フォーカス移動**: P3 では `IsInputKey` で Tab を EditorControl 側に取り込む。フォーカス移動は App 層(P6)でメニューショートカット等で代替。

## Task 15 最終レビュー申し送り(2026-07-06・P3 全体レビュー結果)

別エージェント最終レビュー結果: **Critical 0 / Important 0 / DoD 全達成**でマージ可判定。以下 3 件は P5/P6 送りの Suggestion として記録。

- **`_lastCaretLine` の setter 同期は audit trail 化**(Task 13 I-1 fix の残骸): 4 setter で `_lastCaretLine` を書き込むが、`RaiseCaretEnteredEmptyLineIfNeeded(fromLine)` が引数版になったため実際の発火判定には `_lastCaretLine` 読み出しは使われない=デッドコード気味だが不変維持の記録として害なし。P5 で UIA イベント発火設計時に redundant なら削除検討。
- **`Keys.Enter` を IsInputKey に含めていない**(Task 6 申し送り S-2 の Enter 版・P6 embedding 時対応): 現状 `IsInputKey` は Arrow/Home/End/Page/Tab のみ。EditorControl を `AcceptButton` を持つ Form に埋め込む場合、Form.ProcessDialogKey が Enter を先に食う可能性。yEdit の `MainForm` は AcceptButton なしで実害なし=P6 で App 層に組み込む際に `Keys.Enter` を IsInputKey へ追加する。
- **ReadOnly 時のキー消費**: `Ctrl+X/V/Z/Y`/`Shift+Delete`/`Back`/`Delete`/`Enter`/`Tab` は `when !ReadOnly` ガードで case マッチ失敗=`e.Handled=true` を設定せず親コントロールへバブルする。実挙動として問題は観測されていないが consistency gap として記録=P6 で `default:` fallback or `Handled=true` 設定の検討候補。

## Task 6/7 の追加申し送り決着(P3 完了時)

- **Ctrl+A 後の可視化**(Task 6 申し送り I-1): Notepad/VSCode 慣習に合わせて Ctrl+A では BringCaretIntoView を呼ばない選択で Task 7 で決着済み。P7 実機評価で最終確認。
- **PageUp/PageDown の rows 計算統一**(Task 7 申し送り): OnKeyDown の Page 分岐は `ClientSize.Height / LineHeightPx`、BringCaretIntoView は paintHeight ベース(hscroll 高減算後)。Task 15 で統一検討としていたが、両者は目的が異なる(1 ページ移動 vs 可視性判定)ため現状のまま=P5/P6 で必要になったら整理。
