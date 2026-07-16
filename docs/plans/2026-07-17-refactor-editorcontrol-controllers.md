# 責務分離リファクタリング Phase 3 (Controller 委譲) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Phase 2 で partial 分割された EditorControl の各責務を、独立クラス (Controller 4 個) への委譲で完全に責務移譲する。field 所有権を Controller へ完全移動し、EditorControl の public API 契約は薄いラッパで維持する。挙動不変。

**Architecture:** 段階マージ方式 ([[phase-work-git-flow]] 準拠)。1 サブ = 1 フィーチャーブランチ = 1 no-ff マージ = 直列 (並行禁止)。実装順は **3b (CaretController) → 3c (InputRouter) → 3a (ImeController) → 3d (UiaTextHostAdapter)**。3b は基盤 (他 3 個から caret 位置を read/write)。3d は最重 (field 12 個・IUiaTextHost 22 メンバ・_bounds sync) なので最後。L5 実機検証は 3a と 3d の 2 回。各サブ完了時に別エージェント (`superpowers:code-reviewer`) レビュー + `tools/pre-merge-check.ps1` 緑を確認してから main へマージ。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit / STA テストヘルパ (`Sta.Run` + `HostForm.CreateWithDocs`) / Fake 群 (既存 `FakeAnnouncer` 等 + 新規 `FakeImeContext` (3a のみ))。

**上位文書:**
- 設計書: `docs/plans/2026-07-17-refactor-editorcontrol-controllers-design.md`
- 上位設計 (§4 骨格): `docs/plans/2026-07-16-refactor-separation-of-concerns-design.md`
- Phase 2 完了記録: `[[refactor-separation-phase2-complete]]`

**テスト数遷移 (想定):** 1041 → 1048 (3b) → 1054 (3c) → 1064 (3a) → 1070 (3d) = **~1070**

**サイズ目標 (git-truth = `wc -l` 準拠):**
- EditorControl.cs: 1537 → 600〜800
- Editor 層 total: 3470 → ~2650 (-24%)
- **代替 DoD (数値ずれ許容):** 「削減行数 ≒ Controller 追加行数 ±10%」を機械的検証

---

## Task 0: 事前ゲート + 作業ブランチ運用

**目的:** main から作業を始める準備。

### Step 0.1: 事前ゲート

```powershell
git status                     # クリーン (publish/ installer/ は untracked = 無視)
git log --oneline -1
# 期待: HEAD=b0981a1 (Phase 3 設計書コミット)
taskkill /F /IM dotnet.exe     # Phase 2 で認識された flaky test 対策 (残プロセス shutdown)
tools/pre-merge-check.ps1
# 期待: 全緑・Release 0 警告・1041 tests
```

### Step 0.2: 運用ルール確認

- 各サブ (3b→3c→3a→3d) ごとに独立ブランチ:
  - `feature/refactor-3b-caret-controller`
  - `feature/refactor-3c-input-router`
  - `feature/refactor-3a-ime-controller`
  - `feature/refactor-3d-uia-adapter`
- 各サブ完了時に `superpowers:requesting-code-review` を起動して別エージェントに review を依頼。
- レビュー「マージ可」+ `tools/pre-merge-check.ps1` 緑 → main へ `git merge --no-ff` でマージ。
- L5 実機検証は **3a と 3d の 2 回**。3b と 3c は SR 経路不変で不要。
- 各サブ完了ごとに MEMORY.md を更新 (全 4 サブ完了時に一括でも可)。
- 境界事例 (field 帰属・method 移設先) は**移動側に振り reviewer に境界確認依頼**姿勢を継続 (Phase 2 で確立された流儀)。

**Commit:** なし (準備のみ)

---

## Task 3b: CaretController 抽出

**目的:** `_caret`/`_anchor`/`_desiredXpx` の所有権を EditorControl から新規 `CaretController` へ完全移動する。EditorControl.Caret.cs 側の public API 15 個 (SetCaretCharOffset/SelectCharRange/GoToLine 等) は薄いラッパとして残存し、外部契約 (Editor.Tests + MainForm) は不変。基盤 Controller のため他 3 サブより先に抽出する。

**Files:**
- Create: `src/yEdit.Editor/CaretController.cs`
- Modify: `src/yEdit.Editor/EditorControl.cs` (field 削除 `_caret`/`_anchor`/`_desiredXpx` = 3 個・new CaretController を ctor 追加)
- Modify: `src/yEdit.Editor/EditorControl.Caret.cs` (public API を Controller ラッパに書換)
- Modify: `src/yEdit.Editor/EditorControl.Input.cs` / `EditorControl.Ime.cs` / `EditorControl.Paint.cs` / `EditorControl.Uia.cs` (`_caret`/`_anchor`/`_desiredXpx` 参照を `_caretCtrl.Xxx` へ全置換)
- Test: `tests/yEdit.Editor.Tests/CaretControllerTests.cs` (新規)
- Test: `tests/yEdit.Editor.Tests/CaretControllerContractTests.cs` (新規・reflection 契約)

**L5 実機検証:** 不要 (SR 経路不変・内部委譲のみ)

### Step 3b.1: ブランチ作成 + 現状把握

```powershell
taskkill /F /IM dotnet.exe
git checkout main
git checkout -b feature/refactor-3b-caret-controller
git log --oneline -1
# 期待: b0981a1 が HEAD
```

Grep で `_caret`/`_anchor`/`_desiredXpx` の全参照箇所を列挙し、Task 実装後の変更予定行数を PR description に転記する:

- Pattern 1: `\b_caret\b` (EditorControl* 全 partial)
- Pattern 2: `\b_anchor\b`
- Pattern 3: `\b_desiredXpx\b`

**予想箇所数**: EditorControl.cs 側で ~40 箇所、partial 側で ~20 箇所 (全 5 partial 合計)。

### Step 3b.2: 失敗テストを書く (reflection 契約テスト)

`tests/yEdit.Editor.Tests/CaretControllerContractTests.cs` を新規作成:

```csharp
using System.Reflection;
using Xunit;
using yEdit.Editor;

namespace yEdit.Editor.Tests;

public class CaretControllerContractTests
{
    [Fact]
    public void CaretController_Fields_AreOwnedByController()
    {
        // Task 3b: _caret/_anchor/_desiredXpx は EditorControl から CaretController に完全移譲する契約。
        // 戻し忘れを機械固定。
        var editorFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        string[] removed = { "_caret", "_anchor", "_desiredXpx" };
        foreach (var name in removed)
        {
            var f = typeof(EditorControl).GetField(name, editorFlags);
            Assert.Null(f);
        }

        var ctrlFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var ctrlType = typeof(EditorControl).Assembly.GetType("yEdit.Editor.CaretController");
        Assert.NotNull(ctrlType);
        foreach (var name in removed)
        {
            var f = ctrlType!.GetField(name, ctrlFlags);
            Assert.NotNull(f);
        }
    }

    [Fact]
    public void EditorControl_HoldsCaretController_ByField()
    {
        // Task 3b: EditorControl は _caretCtrl フィールドで Controller を保持する。
        var f = typeof(EditorControl).GetField("_caretCtrl",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
    }
}
```

### Step 3b.3: テスト実行 (失敗確認)

```powershell
dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj `
  --filter FullyQualifiedName~CaretControllerContractTests
# 期待: 2 FAIL (CaretController がまだない)
```

### Step 3b.4: CaretController 新規作成 (最小実装)

`src/yEdit.Editor/CaretController.cs`:

```csharp
using yEdit.Core.Buffers;

namespace yEdit.Editor;

/// <summary>
/// Task 3b: Caret/Anchor/DesiredXpx を所有し、位置操作と選択管理を担う Controller。
/// EditorControl から state field を完全移譲。EditorControl.Caret.cs の public API は本 Controller への薄いラッパ。
/// </summary>
internal sealed class CaretController
{
    private int _caret;
    private int _anchor;
    private int _desiredXpx = -1;

    public int Caret => _caret;
    public int Anchor => _anchor;
    public int DesiredXpx { get => _desiredXpx; set => _desiredXpx = value; }
    public (int Start, int End) Selection => (Math.Min(_caret, _anchor), Math.Max(_caret, _anchor));
    public bool HasSelection => _caret != _anchor;

    /// <summary>キャレットを指定 offset に移動。アンカーも同時に更新 (選択解除)。</summary>
    public void SetTo(int pos, TextSnapshot snap)
    {
        int c = SnapAndClamp(pos, snap);
        _caret = c;
        _anchor = c;
    }

    /// <summary>キャレット位置のみ更新 (extend=true ならアンカー据置=選択拡大)。</summary>
    public void MoveTo(int newPos, bool extend, TextSnapshot snap)
    {
        int c = SnapAndClamp(newPos, snap);
        _caret = c;
        if (!extend) _anchor = c;
    }

    /// <summary>アンカーとキャレットを個別に設定 (Shift+Click 等)。</summary>
    public void SetSelection(int anchor, int caret, TextSnapshot snap)
    {
        _anchor = SnapAndClamp(anchor, snap);
        _caret = SnapAndClamp(caret, snap);
    }

    /// <summary>選択を解除 (キャレット位置は維持)。</summary>
    public void ClearSelection() => _anchor = _caret;

    /// <summary>offset を [0, snap.CharCount] にクランプし、サロゲート境界に snap する。</summary>
    public int SnapAndClamp(int offset, TextSnapshot snap)
    {
        // EditorControl.Caret.cs:182 の実装を bit-perfect 移設。
        // 移動元の SnapAndClamp method 中身をそのまま貼る (Phase 3 で logic 書換は禁止)。
        // ↓ 実装時に EditorControl.Caret.cs:182〜の内容を確認して貼り付け
        throw new NotImplementedException("Step 3b.5 で移設");
    }
}
```

### Step 3b.5: SnapAndClamp のロジック移設

`src/yEdit.Editor/EditorControl.Caret.cs:182〜` の `SnapAndClamp` の中身を読み、`CaretController.SnapAndClamp` にそのまま貼り付ける。**ロジック書換禁止**。TextSnapshot 参照が partial 側で `_buffer.Current` になっている場合は引数の `snap` に置き換える。

**注意点**: SnapAndClamp が private static ヘルパ関数を呼んでいる場合、それも CaretController に private static として移設 (呼び出し関係を閉じる)。

### Step 3b.6: EditorControl 側の field 削除 + `_caretCtrl` 追加

`src/yEdit.Editor/EditorControl.cs`:

```csharp
// Before
private int _caret;
private int _anchor;
...
private int _desiredXpx = -1;

// After (3 個の field を削除・以下 1 行を追加)
private readonly CaretController _caretCtrl = new();
```

ctor 順序: `_caretCtrl` は field initializer で即生成可 (依存 none)。

### Step 3b.7: partial 側の全参照を Controller 経由に置換

**5 partial + 本体全ての `_caret` を `_caretCtrl.Caret` に、`_anchor` を `_caretCtrl.Anchor` に、`_desiredXpx` を `_caretCtrl.DesiredXpx` に置換。**

書換パターン (代表例):
- `_caret = value;` → `_caretCtrl.SetTo(value, _buffer.Current);` (Buffer 参照が有るコンテキスト) or `_caretCtrl.MoveTo(value, extend: false, _buffer.Current);`
- `_caret` (読み) → `_caretCtrl.Caret`
- `_anchor = _caret;` → `_caretCtrl.ClearSelection();`
- `_anchor = value; _caret = other;` → `_caretCtrl.SetSelection(value, other, _buffer.Current);`
- `_desiredXpx = -1;` → `_caretCtrl.DesiredXpx = -1;`

**重要**: Caret.cs 側の method (`SetCaretCharOffset`/`SetSelectionCharRange`/`MoveCaretWithSelection` 等) が担っていた「buffer null check」「AfterEdit 呼び出し」「Invalidate」は EditorControl 側に残置し、その中で `_caretCtrl.SetTo(...)` などを呼ぶ形に。**Controller は state 操作のみ、副作用 (Invalidate/AfterEdit) は EditorControl 側**。

### Step 3b.8: 全体ゲート + 契約テスト緑確認

```powershell
taskkill /F /IM dotnet.exe
tools/pre-merge-check.ps1
# 期待: 全緑・0 warning・~1043 tests (1041+2 contract test)
```

### Step 3b.9: CaretController pure ロジックテストの追加

`tests/yEdit.Editor.Tests/CaretControllerTests.cs` を新規作成し、以下 6〜8 テストを追加 (既存 `AnchorSelectionTests` の姉妹形):

```csharp
using Xunit;
using yEdit.Core.Buffers;

namespace yEdit.Editor.Tests;

public class CaretControllerTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    [Fact]
    public void SetTo_ClampsBelowZero_ToZero()
    {
        var c = new CaretController();
        c.SetTo(-5, Snap("hello"));
        Assert.Equal(0, c.Caret);
        Assert.Equal(0, c.Anchor);
    }

    [Fact]
    public void SetTo_ClampsAboveLength_ToLength()
    {
        var c = new CaretController();
        c.SetTo(999, Snap("hello"));
        Assert.Equal(5, c.Caret);
    }

    [Fact]
    public void SetTo_ClearsAnchor()
    {
        var c = new CaretController();
        var snap = Snap("hello");
        c.SetSelection(1, 3, snap);
        Assert.True(c.HasSelection);
        c.SetTo(4, snap);
        Assert.False(c.HasSelection);
        Assert.Equal(4, c.Caret);
        Assert.Equal(4, c.Anchor);
    }

    [Fact]
    public void MoveTo_Extend_KeepsAnchor()
    {
        var c = new CaretController();
        var snap = Snap("hello");
        c.SetTo(1, snap);
        c.MoveTo(3, extend: true, snap);
        Assert.Equal(3, c.Caret);
        Assert.Equal(1, c.Anchor);
        Assert.Equal((1, 3), c.Selection);
    }

    [Fact]
    public void SnapAndClamp_SurrogatePair_SnapsToBoundary()
    {
        var c = new CaretController();
        var snap = Snap("a😀b");   // "a😀b" (surrogate pair)
        int mid = c.SnapAndClamp(2, snap);    // low surrogate 位置
        Assert.Equal(1, mid);                  // high surrogate の直前に snap
    }

    // 残 3 テスト:
    //   - ClearSelection_KeepsCaret
    //   - Selection_OrderNormalized (anchor > caret でも Start/End は正順)
    //   - DesiredXpx_RoundTrip
}
```

### Step 3b.10: 全体ゲート再実行

```powershell
taskkill /F /IM dotnet.exe
tools/pre-merge-check.ps1
# 期待: ~1048 tests (1041+2 contract+6~8 pure) 全緑
```

### Step 3b.11: 数値目標検証 (代替 DoD)

```powershell
wc -l src/yEdit.Editor/EditorControl*.cs src/yEdit.Editor/CaretController.cs
# 期待: EditorControl.cs -50〜-80 行 / Caret.cs 微減 / CaretController.cs +100 前後
# 代替 DoD: 「削減行数 ≒ 追加行数 ±10%」の範囲内か確認
```

### Step 3b.12: コミット + review + main マージ

```powershell
git add src/yEdit.Editor/CaretController.cs `
        src/yEdit.Editor/EditorControl.cs `
        src/yEdit.Editor/EditorControl.Caret.cs `
        src/yEdit.Editor/EditorControl.Input.cs `
        src/yEdit.Editor/EditorControl.Ime.cs `
        src/yEdit.Editor/EditorControl.Paint.cs `
        src/yEdit.Editor/EditorControl.Uia.cs `
        tests/yEdit.Editor.Tests/CaretControllerContractTests.cs `
        tests/yEdit.Editor.Tests/CaretControllerTests.cs
git commit -m "refactor(editor): CaretController 抽出・_caret/_anchor/_desiredXpx 所有権移譲 (Task 3b)"
```

別エージェント review → `git checkout main && git merge --no-ff feature/refactor-3b-caret-controller`。

---

## Task 3c: InputRouter 抽出

**目的:** `EditorControl.Input.cs` (608 行・OnKeyDown 234 行含む) の keymap ロジックを新規 `InputRouter` に外だしする。InputRouter は state を持たない pure dispatcher。EditorControl.Input.cs 側は OnKey*/OnMouse* のオーバーライドを 5 行前後に減らす。

**Files:**
- Create: `src/yEdit.Editor/InputRouter.cs`
- Create: `src/yEdit.Editor/InputContext.cs` (value-record)
- Modify: `src/yEdit.Editor/EditorControl.cs` (`_input` field 追加・ctor で new)
- Modify: `src/yEdit.Editor/EditorControl.Input.cs` (OnKeyDown/OnKeyPress/OnMouseXxx をラッパ化)
- Test: `tests/yEdit.Editor.Tests/InputRouterTests.cs` (新規)
- Test: `tests/yEdit.Editor.Tests/InputRouterContractTests.cs` (新規・reflection 契約)

**L5 実機検証:** 不要 (SR 経路不変・キー入力の意味論は既存 keymap と等価)

### Step 3c.1: ブランチ作成 + 現状把握

```powershell
taskkill /F /IM dotnet.exe
git checkout main
git checkout -b feature/refactor-3c-input-router
```

Grep で現状の keymap 箇所を列挙:
- `src/yEdit.Editor/EditorControl.Input.cs` の `OnKeyDown` (line 番号確認)
- `switch (e.KeyData)` or `if (e.KeyData == Keys.XXX)` の分岐一覧
- 想定分岐数: 30〜40 個 (Ctrl+C/V/X/A/Z/Y/End/Home/PgUp/PgDn + Shift 系 + 矢印系 + F3 系 等)

分岐一覧を PR description に転記して、InputRouter 側の keymap dictionary 化の完全性を担保。

### Step 3c.2: 失敗テストを書く (reflection 契約テスト)

`tests/yEdit.Editor.Tests/InputRouterContractTests.cs`:

```csharp
using System.Reflection;
using Xunit;
using yEdit.Editor;

namespace yEdit.Editor.Tests;

public class InputRouterContractTests
{
    [Fact]
    public void EditorControl_HoldsInputRouter_ByField()
    {
        var f = typeof(EditorControl).GetField("_input",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
    }

    [Fact]
    public void InputRouter_HasNoInstanceStateFields()
    {
        // Task 3c: InputRouter は pure dispatcher。state 保持は禁止 (readonly のみ許容)。
        var routerType = typeof(EditorControl).Assembly.GetType("yEdit.Editor.InputRouter");
        Assert.NotNull(routerType);
        var mutableFields = routerType!.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => !f.IsInitOnly).ToList();
        Assert.Empty(mutableFields);
    }
}
```

### Step 3c.3: テスト実行 (失敗確認)

```powershell
dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj `
  --filter FullyQualifiedName~InputRouterContractTests
# 期待: 2 FAIL
```

### Step 3c.4: InputContext と InputRouter を新規作成

`src/yEdit.Editor/InputContext.cs`:

```csharp
namespace yEdit.Editor;

/// <summary>Task 3c: InputRouter が keymap handler に渡す入力コンテキスト (value-record)。</summary>
internal readonly record struct InputContext(
    EditorControl Host,
    CaretController Caret);
```

`src/yEdit.Editor/InputRouter.cs`:

```csharp
using System.Windows.Forms;

namespace yEdit.Editor;

/// <summary>
/// Task 3c: OnKey*/OnMouse* のディスパッチを担う pure dispatcher。state 保持しない。
/// EditorControl.Input.cs の OnKeyDown 分岐を Keys→Action dictionary に外だし。
/// </summary>
internal sealed class InputRouter
{
    private readonly EditorControl _host;
    private readonly CaretController _caret;
    private readonly IReadOnlyDictionary<Keys, Action<InputContext>> _keyMap;

    public InputRouter(EditorControl host, CaretController caret)
    {
        _host = host;
        _caret = caret;
        _keyMap = BuildKeyMap();
    }

    private static IReadOnlyDictionary<Keys, Action<InputContext>> BuildKeyMap()
    {
        // Step 3c.1 で列挙した分岐一覧を Keys → Action で登録。
        // 各 handler は既存 OnKeyDown 内の block をそのまま関数に切り出したもの。
        return new Dictionary<Keys, Action<InputContext>>
        {
            // 例:
            // [Keys.Home] = ctx => ctx.Host.MoveCaretHome(extend: false),
            // [Keys.End]  = ctx => ctx.Host.MoveCaretEnd(extend: false),
            // [Keys.Shift | Keys.Home] = ctx => ctx.Host.MoveCaretHome(extend: true),
            // ... (Step 3c.1 の全分岐を登録)
        };
    }

    /// <summary>返値=true は handled (base call skip)。false は pass-through。</summary>
    public bool Route(KeyEventArgs e)
    {
        if (_keyMap.TryGetValue(e.KeyData, out var handler))
        {
            handler(new InputContext(_host, _caret));
            e.Handled = true;
            e.SuppressKeyPress = true;    // 既存 OnKeyDown の e.SuppressKeyPress 挙動を保持
            return true;
        }
        return false;
    }

    // マウス dispatch は Task 完了時に実装 (現状は OnMouseDown/Move/Up/DoubleClick を Route(MouseEventArgs, kind) で受ける)
}
```

**方針**: 既存 OnKeyDown 内の各 `case Keys.XXX:` block を private static method or lambda として切り出し、dictionary に登録する。**ロジック中身は書き換え禁止** (`__TestApplyResult` を Ime に振ったのと同じ姿勢=境界事例を移動側に振り reviewer に確認依頼)。

### Step 3c.5: EditorControl 側で InputRouter を配線

`src/yEdit.Editor/EditorControl.cs`:

```csharp
// ctor 内に追加 (_caretCtrl 生成の直後):
_input = new InputRouter(this, _caretCtrl);
```

field 追加:
```csharp
private readonly InputRouter _input;
```

### Step 3c.6: EditorControl.Input.cs のオーバーライドを薄ラッパ化

Before:
```csharp
protected override void OnKeyDown(KeyEventArgs e)
{
    // 200+ 行の switch/if 分岐
    ...
    base.OnKeyDown(e);
}
```

After:
```csharp
protected override void OnKeyDown(KeyEventArgs e)
{
    if (!_input.Route(e))
        base.OnKeyDown(e);
}
```

同様に OnKeyPress/OnMouseDown/OnMouseMove/OnMouseUp/OnMouseDoubleClick も薄ラッパ化。マウス系は現状 handler 内で MoveTo/SetSelection を呼んでいるので、InputRouter 側にも MouseEventKind + Route(MouseEventArgs, kind) を実装 (Step 3c.4 の TODO)。

**MouseEventKind enum**:
```csharp
internal enum MouseEventKind { Down, Move, Up, DoubleClick }
```

### Step 3c.7: 全体ゲート

```powershell
taskkill /F /IM dotnet.exe
tools/pre-merge-check.ps1
# 期待: 全緑・0 warning・~1050 tests (1048+2 contract test)
```

### Step 3c.8: InputRouter pure テスト追加

`tests/yEdit.Editor.Tests/InputRouterTests.cs` に 4〜6 テスト:

```csharp
[Fact]
public void Route_UnmappedKey_ReturnsFalse()
{
    Sta.Run(() =>
    {
        using var editor = new EditorControl();
        editor.SetSource("hello");
        var e = new KeyEventArgs(Keys.F13);   // 未マップ
        var router = ... // EditorControl から取得
        Assert.False(router.Route(e));
    });
}

[Fact]
public void Route_CtrlA_SelectsAll()
{
    Sta.Run(() =>
    {
        using var editor = new EditorControl();
        editor.SetSource("hello world");
        var e = new KeyEventArgs(Keys.Control | Keys.A);
        // ... InputRouter 経由で Ctrl+A を発火 → selection が全体になることを assert
    });
}

// 残 2〜4 テスト: Ctrl+Home/End・Shift+arrow・Ctrl+C の handler ヒット
```

**注意**: keymap の網羅性は既存の `KeyboardNavigationTests` (218 tests のうち 30 前後) が担う。本 pure テストは「Router 経由で発火する」ことの機械固定に絞る。

### Step 3c.9: 数値目標検証

```powershell
wc -l src/yEdit.Editor/EditorControl.Input.cs src/yEdit.Editor/InputRouter.cs
# 期待: Input.cs 608 → 80〜100 (削減 500+) / InputRouter.cs +500 前後
# 代替 DoD: 「削減行数 ≒ 追加行数 ±10%」
```

### Step 3c.10: コミット + review + main マージ

```powershell
git add src/yEdit.Editor/InputRouter.cs `
        src/yEdit.Editor/InputContext.cs `
        src/yEdit.Editor/EditorControl.cs `
        src/yEdit.Editor/EditorControl.Input.cs `
        tests/yEdit.Editor.Tests/InputRouterContractTests.cs `
        tests/yEdit.Editor.Tests/InputRouterTests.cs
git commit -m "refactor(editor): InputRouter 抽出・keymap dictionary 化 (Task 3c)"
```

別エージェント review → main へ no-ff マージ。

---

## Task 3a: ImeController 抽出

**目的:** `EditorControl.Ime.cs` (325 行) の IME 状態機械を新規 `ImeController` に抽出し、Imm32 P/Invoke を `IImeContext` seam でラップする。`_ime` (ImeCompositionState) の所有権を Controller に完全移譲。FakeImeContext で pure テスト可能にする。**L5 必要**。

**Files:**
- Create: `src/yEdit.Editor/Abstractions/IImeContext.cs`
- Create: `src/yEdit.Editor/WinImeContext.cs` (本番実装)
- Create: `src/yEdit.Editor/ImeController.cs`
- Create: `src/yEdit.Editor/IImeOverlayHost.cs` (Draw 通知用 interface)
- Modify: `src/yEdit.Editor/EditorControl.cs` (`_ime` field 削除・`_imeCtrl` field 追加・ctor で new)
- Modify: `src/yEdit.Editor/EditorControl.Ime.cs` (WndProc 分岐をラッパ化)
- Modify: `src/yEdit.Editor/EditorControl.Paint.cs` (`DrawImeOverlay` を `_imeCtrl.Draw(g)` に変更)
- Test: `tests/yEdit.Editor.Tests/Fakes/FakeImeContext.cs` (新規)
- Test: `tests/yEdit.Editor.Tests/ImeControllerTests.cs` (新規・6〜8 テスト)
- Test: `tests/yEdit.Editor.Tests/ImeControllerContractTests.cs` (新規・reflection 契約)
- Test: `tests/yEdit.Editor.Tests/WinImeContextSmokeTests.cs` (新規・2 smoke)

**L5 実機検証:** **必要**。NVDA/PC-Talker で IME 変換読み・確定読み・overlay 描画位置が不変であることを確認。

### Step 3a.1: ブランチ作成 + 現状把握

```powershell
taskkill /F /IM dotnet.exe
git checkout main
git checkout -b feature/refactor-3a-ime-controller
```

Grep で以下を列挙:
- `EditorControl.Ime.cs` の Imm32 P/Invoke 呼び出し箇所 (`ImmGetContext`/`ImmGetCompositionString*`/`ImmReleaseContext`/`ImmSetCandidateWindow`/`ImmSetCompositionFont` 等)
- `_ime` フィールド参照箇所 (`ImeCompositionState`)
- `DrawImeOverlay` 呼び出し箇所 (EditorControl.Paint.cs から)

### Step 3a.2: IImeContext seam を先に定義

`src/yEdit.Editor/Abstractions/IImeContext.cs`:

```csharp
using System.Drawing;

namespace yEdit.Editor.Abstractions;

/// <summary>
/// Task 3a: Imm32 P/Invoke seam。ImeController を pure テスト可能にする。
/// 本番実装 = WinImeContext (Handle をラップ)。テスト実装 = FakeImeContext。
/// null 返却 = P/Invoke 失敗 or IME 無効 (両ケースを 1 パターンに集約)。
/// </summary>
public interface IImeContext : IDisposable
{
    /// <summary>指定 GCS_* フラグで composition string を取得。P/Invoke 失敗時は null。</summary>
    string? GetCompositionString(long gcsFlags);
    /// <summary>candidate window を client 座標 (x, y) に設定。</summary>
    void SetCandidateWindow(int x, int y);
    /// <summary>composition font を設定。</summary>
    void SetCompositionFont(Font font);
    /// <summary>現在の composition をキャンセル。</summary>
    void CancelComposition();
}
```

`src/yEdit.Editor/IImeOverlayHost.cs`:

```csharp
namespace yEdit.Editor;

/// <summary>Task 3a: ImeController.Draw が overlay 描画時に host から metrics を取得するための seam。</summary>
internal interface IImeOverlayHost
{
    ICharMetrics Metrics { get; }
    Font Font { get; }
    // 必要に応じて追加 (composition 位置計算用の CaretPoint など)
}
```

### Step 3a.3: 失敗テストを書く (reflection 契約テスト)

`tests/yEdit.Editor.Tests/ImeControllerContractTests.cs`:

```csharp
using System.Reflection;
using Xunit;
using yEdit.Editor;
using yEdit.Editor.Abstractions;

namespace yEdit.Editor.Tests;

public class ImeControllerContractTests
{
    [Fact]
    public void ImeController_UsesIImeContext_ByCtor()
    {
        var ctrlType = typeof(EditorControl).Assembly.GetType("yEdit.Editor.ImeController");
        Assert.NotNull(ctrlType);
        var ctors = ctrlType!.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(ctors);
        bool hasImeContextParam = ctors.Any(c =>
            c.GetParameters().Any(p =>
                p.ParameterType == typeof(Func<IImeContext>) ||
                p.ParameterType == typeof(IImeContext)));
        Assert.True(hasImeContextParam, "ImeController must accept IImeContext via ctor");
    }

    [Fact]
    public void EditorControl_ImeField_Removed()
    {
        var f = typeof(EditorControl).GetField("_ime",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Null(f);
    }

    [Fact]
    public void EditorControl_HoldsImeController_ByField()
    {
        var f = typeof(EditorControl).GetField("_imeCtrl",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
    }
}
```

### Step 3a.4: テスト実行 (失敗確認)

```powershell
dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj `
  --filter FullyQualifiedName~ImeControllerContractTests
# 期待: 3 FAIL (ImeController がまだない)
```

### Step 3a.5: WinImeContext 本番実装

`src/yEdit.Editor/WinImeContext.cs`:

```csharp
using System.Drawing;
using System.Runtime.InteropServices;
using yEdit.Editor.Abstractions;

namespace yEdit.Editor;

/// <summary>Task 3a: 本番用 IImeContext。ImmGetContext で取得した HIMC をラップ。</summary>
internal sealed class WinImeContext : IImeContext
{
    private readonly IntPtr _hwnd;
    private IntPtr _himc;
    private bool _disposed;

    public WinImeContext(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _himc = NativeMethods.ImmGetContext(hwnd);
    }

    public string? GetCompositionString(long gcsFlags)
    {
        if (_himc == IntPtr.Zero) return null;
        // EditorControl.Ime.cs の ReadImeString の実装をここに移設。
        // ImmGetCompositionString で bytes 長さ取得 → buffer 確保 → Encoding.Unicode.GetString。
        // 失敗時 (負値/0) は null 返却。
        ...
    }

    public void SetCandidateWindow(int x, int y)
    {
        if (_himc == IntPtr.Zero) return;
        // EditorControl.Ime.cs の NotifyCandidateWindow の実装を移設。
        ...
    }

    public void SetCompositionFont(Font font)
    {
        if (_himc == IntPtr.Zero) return;
        // EditorControl.Ime.cs の NotifyCompositionFont の実装を移設。
        ...
    }

    public void CancelComposition()
    {
        if (_himc == IntPtr.Zero) return;
        NativeMethods.ImmNotifyIME(_himc, NativeMethods.NI_COMPOSITIONSTR,
            NativeMethods.CPS_CANCEL, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_himc != IntPtr.Zero)
        {
            NativeMethods.ImmReleaseContext(_hwnd, _himc);
            _himc = IntPtr.Zero;
        }
    }
}
```

### Step 3a.6: ImeController 本体

`src/yEdit.Editor/ImeController.cs`:

```csharp
using System.Drawing;
using System.Windows.Forms;
using yEdit.Core.Editing;
using yEdit.Editor.Abstractions;

namespace yEdit.Editor;

/// <summary>
/// Task 3a: IME 状態機械 + Message pump アダプタ。
/// _ime (ImeCompositionState) を所有し、WndProc からのイベントを IImeContext seam で処理する。
/// </summary>
internal sealed class ImeController
{
    private ImeCompositionState _ime = ImeCompositionState.Empty;
    private readonly Func<IImeContext> _contextFactory;
    private readonly CaretController _caret;
    private readonly IImeOverlayHost _overlayHost;
    private readonly Action<string> _insertConfirmedText;    // EditorControl.InsertConfirmedText callback

    public bool IsActive => _ime.IsComposing;

    public ImeController(Func<IImeContext> contextFactory, CaretController caret,
        IImeOverlayHost overlayHost, Action<string> insertConfirmedText)
    {
        _contextFactory = contextFactory;
        _caret = caret;
        _overlayHost = overlayHost;
        _insertConfirmedText = insertConfirmedText;
    }

    public void OnStartComposition()
    {
        // EditorControl.Ime.cs の OnImeStart 実装を移設 (ロジックそのまま)
        _ime = new ImeCompositionState(...);
    }

    public void OnComposition(long gcsFlags)
    {
        using var ctx = _contextFactory();
        if ((gcsFlags & NativeMethods.GCS_RESULTSTR) != 0)
        {
            var result = ctx.GetCompositionString(NativeMethods.GCS_RESULTSTR);
            if (!string.IsNullOrEmpty(result))
                _insertConfirmedText(result);
            _ime = ImeCompositionState.Empty;
        }
        if ((gcsFlags & NativeMethods.GCS_COMPSTR) != 0)
        {
            var comp = ctx.GetCompositionString(NativeMethods.GCS_COMPSTR);
            var cursor = ctx.GetCompositionString(NativeMethods.GCS_CURSORPOS);   // ※CURSORPOS は int 変換必要
            _ime = new ImeCompositionState(...);   // 既存ロジック移設
        }
    }

    public void OnEndComposition()
    {
        _ime = ImeCompositionState.Empty;
    }

    public void OnSetContext(ref Message m)
    {
        // EditorControl.Ime.cs の OnImeSetContext 実装を移設
    }

    public void Cancel()
    {
        using var ctx = _contextFactory();
        ctx.CancelComposition();
        _ime = ImeCompositionState.Empty;
    }

    public void Draw(Graphics g)
    {
        // EditorControl.Paint.cs の DrawImeOverlay 実装を移設。
        // _overlayHost.Metrics/Font と _caret.Caret から描画位置を計算し、_ime の未確定文字列を描画。
        if (!_ime.IsComposing) return;
        ...
    }
}
```

### Step 3a.7: EditorControl 側の配線

`src/yEdit.Editor/EditorControl.cs`:

```csharp
// _ime フィールド削除
// 以下 field 追加:
private readonly ImeController _imeCtrl;
```

ctor 内 (`_input = new InputRouter(...)` の直後):

```csharp
_imeCtrl = new ImeController(
    contextFactory: () => new WinImeContext(Handle),
    caret: _caretCtrl,
    overlayHost: this,       // EditorControl が IImeOverlayHost を実装
    insertConfirmedText: InsertConfirmedText);
```

`EditorControl` に `IImeOverlayHost` を実装 (Metrics/Font プロパティを露出)。

### Step 3a.8: EditorControl.Ime.cs をラッパ化

Before (325 行の WM_IME_* 分岐):
```csharp
case NativeMethods.WM_IME_STARTCOMPOSITION:
    OnImeStart();
    m.Result = IntPtr.Zero;
    return;
```

After (~50 行):
```csharp
case NativeMethods.WM_IME_STARTCOMPOSITION:
    _imeCtrl.OnStartComposition();
    m.Result = IntPtr.Zero;
    return;
case NativeMethods.WM_IME_COMPOSITION:
    _imeCtrl.OnComposition(m.LParam.ToInt64());
    m.Result = IntPtr.Zero;
    return;
case NativeMethods.WM_IME_ENDCOMPOSITION:
    _imeCtrl.OnEndComposition();
    m.Result = IntPtr.Zero;
    return;
case NativeMethods.WM_IME_SETCONTEXT:
    _imeCtrl.OnSetContext(ref m);
    return;
```

`__Test*/__Smoke*` フック 7 個は Ime.cs 側残置 (テスト帰属)。ただし内部で `_imeCtrl` を触れるよう `internal` アクセサを追加。

### Step 3a.9: EditorControl.Paint.cs の DrawImeOverlay を差替

Before:
```csharp
private void DrawImeOverlay(Graphics g) { /* 60 行の描画 */ }
```

After:
```csharp
// DrawImeOverlay method 削除
// OnPaint 内の呼び出しを _imeCtrl.Draw(g) に変更
```

### Step 3a.10: FakeImeContext + ImeController pure テスト

`tests/yEdit.Editor.Tests/Fakes/FakeImeContext.cs`:

```csharp
using System.Drawing;
using yEdit.Editor.Abstractions;

namespace yEdit.Editor.Tests.Fakes;

internal sealed class FakeImeContext : IImeContext
{
    public Dictionary<long, string?> Strings { get; } = new();
    public (int X, int Y)? CandidateWindow { get; private set; }
    public Font? CompositionFont { get; private set; }
    public bool CancelCalled { get; private set; }
    public bool Disposed { get; private set; }

    public string? GetCompositionString(long gcsFlags)
        => Strings.TryGetValue(gcsFlags, out var s) ? s : null;

    public void SetCandidateWindow(int x, int y) => CandidateWindow = (x, y);
    public void SetCompositionFont(Font font) => CompositionFont = font;
    public void CancelComposition() => CancelCalled = true;
    public void Dispose() => Disposed = true;
}
```

`tests/yEdit.Editor.Tests/ImeControllerTests.cs` に 6〜8 テスト:

- `OnComposition_GcsResultStr_CallsInsertConfirmedText`
- `OnComposition_GcsCompStr_UpdatesState`
- `OnComposition_NullFromContext_IsNoOp`
- `Cancel_CallsContextCancel_AndResetsState`
- `Draw_NotComposing_IsNoOp`
- `Dispose_ReleasesContextEachTime` (Func factory 経由で毎回 new/dispose される検証)

### Step 3a.11: WinImeContext smoke テスト (2 個)

`tests/yEdit.Editor.Tests/WinImeContextSmokeTests.cs`:

```csharp
[Fact]
public void WinImeContext_ZeroHwnd_GetCompositionString_ReturnsNull()
{
    using var ctx = new WinImeContext(IntPtr.Zero);
    Assert.Null(ctx.GetCompositionString(NativeMethods.GCS_COMPSTR));
}

[Fact]
public void WinImeContext_Dispose_IsIdempotent()
{
    using var ctx = new WinImeContext(IntPtr.Zero);
    ctx.Dispose();
    ctx.Dispose();   // 例外なし
}
```

### Step 3a.12: 全体ゲート

```powershell
taskkill /F /IM dotnet.exe
tools/pre-merge-check.ps1
# 期待: 全緑・0 warning・~1064 tests (1054+3 contract+6~8 pure+2 smoke)
```

### Step 3a.13: L5 実機検証

- NVDA を起動し、日本語 IME で以下を確認:
  - 変換中の未確定文字列が発声される (「あ」「あい」等の中間状態)
  - 確定時に確定文字列が発声される
  - overlay 描画位置がキャレット直下から変わっていない
- PC-Talker で同じ 3 項目を確認 (yEdit 側 SR プロファイル切替)
- 変化があれば 3a のロジック移設に bit-perfect でない箇所がある = 移動元 EditorControl.Ime.cs の該当実装を再確認して差分を潰す

### Step 3a.14: 数値目標検証

```powershell
wc -l src/yEdit.Editor/EditorControl.Ime.cs src/yEdit.Editor/ImeController.cs `
      src/yEdit.Editor/WinImeContext.cs
# 期待: Ime.cs 325 → 50〜80 / ImeController.cs +500 / WinImeContext.cs +80
```

### Step 3a.15: コミット + review + main マージ

```powershell
git add src/yEdit.Editor/Abstractions/IImeContext.cs `
        src/yEdit.Editor/IImeOverlayHost.cs `
        src/yEdit.Editor/WinImeContext.cs `
        src/yEdit.Editor/ImeController.cs `
        src/yEdit.Editor/EditorControl.cs `
        src/yEdit.Editor/EditorControl.Ime.cs `
        src/yEdit.Editor/EditorControl.Paint.cs `
        tests/yEdit.Editor.Tests/Fakes/FakeImeContext.cs `
        tests/yEdit.Editor.Tests/ImeControllerContractTests.cs `
        tests/yEdit.Editor.Tests/ImeControllerTests.cs `
        tests/yEdit.Editor.Tests/WinImeContextSmokeTests.cs
git commit -m "refactor(editor): ImeController 抽出・IImeContext seam 導入 (Task 3a)"
```

別エージェント review → **L5 実機検証 OK** → main へ no-ff マージ。

---

## Task 3d: UiaTextHostAdapter 抽出

**目的:** `EditorControl.Uia.cs` (438 行) の IUiaTextHost 実装 22 メンバを新規 `UiaTextHostAdapter` に完全移譲し、Uia 系 field 12 個の所有権を Adapter に移す。§C.4 例外 (OnHandle*/On*Changed 4 method の Uia.cs 帰属) を解消し、EditorControl 本体に統一する。**L5 必要・最重サブ**。

**Files:**
- Create: `src/yEdit.Editor/UiaTextHostAdapter.cs`
- Modify: `src/yEdit.Editor/EditorControl.cs` (Uia 系 field 12 個削除・`_uia` field 追加・`OnHandleCreated`/`OnHandleDestroyed` 復帰 + Adapter への通知)
- Modify: `src/yEdit.Editor/EditorControl.Uia.cs` (IUiaTextHost 実装を全削除・Adapter への薄いラッパのみ残す・OnHandleCreated/Destroyed を本体側へ戻す)
- Modify: `src/yEdit.Editor/EditorControl.cs` の `OnSizeChanged`/`OnLocationChanged` (Adapter への `OnBoundsChanged` 呼び出しに変更)
- Modify: `src/yEdit.Editor/EditorControl.cs` の `AfterEdit`/`SetSource`/`ReplaceSource` (`_uia.OnSnapshotChanged` + `_uia.RaiseTextChanged` 呼び出しに変更)
- Modify: `src/yEdit.Accessibility/TextControlProviderV2.cs` (ctor 引数を EditorControl から UiaTextHostAdapter に変更 → **Adapter は IUiaTextHost を実装しているので TextControlProviderV2 の内部依存は既存の IUiaTextHost 経由で変更不要**)
- Test: `tests/yEdit.Editor.Tests/UiaTextHostAdapterTests.cs` (新規)
- Test: `tests/yEdit.Editor.Tests/UiaTextHostAdapterContractTests.cs` (新規・reflection 契約)

**L5 実機検証:** **必要**。NVDA/PC-Talker で以下 3 経路が不変であることを確認:
1. 文書テキストの読み (GetTextRange/TextLength)
2. キャレット移動時の追従読み (SelectionChanged)
3. UIA 通知の発火 (TextChanged/SelectionChanged/FocusChanged がそれぞれ従前と同じタイミング/回数で発火)

### Step 3d.1: ブランチ作成 + 現状把握

```powershell
taskkill /F /IM dotnet.exe
git checkout main
git checkout -b feature/refactor-3d-uia-adapter
```

Grep で以下を列挙:
- `EditorControl.Uia.cs` の IUiaTextHost 実装メンバ (22 個想定)
- `_bufferSnapshot` / `_bounds` / `_boundsSync` / `_clientToScreenX` / `_clientToScreenY` / `_lastLineSegs` / `_hwnd` / `_provider` / `_testHook_LastGetObjectServed` / `_uia{Text,Selection,Focus}ChangedCount` の全参照箇所
- `_lastLineSegs = null;` の破棄箇所 6 個 (Phase 2 完了記録より)
- `RaiseTextChanged`/`RaiseSelectionChanged`/`RaiseFocusChanged` 発火箇所 (現状 EditorControl 側で `_uiaXxxChangedCount++` している箇所)

**予想合計参照箇所**: 60〜80 (Phase 2 の Uia.cs 移設時と同規模)。

### Step 3d.2: 失敗テストを書く (reflection 契約テスト)

`tests/yEdit.Editor.Tests/UiaTextHostAdapterContractTests.cs`:

```csharp
using System.Reflection;
using Xunit;
using yEdit.Editor;

namespace yEdit.Editor.Tests;

public class UiaTextHostAdapterContractTests
{
    [Fact]
    public void UiaTextHostAdapter_Owns12UiaFields()
    {
        // Task 3d: 12 個の Uia 系 field は Adapter に完全移譲される契約。
        string[] fields = {
            "_bufferSnapshot", "_boundsSync", "_bounds",
            "_clientToScreenX", "_clientToScreenY",
            "_lastLineSegs", "_hwnd", "_provider",
            "_testHook_LastGetObjectServed",
            "_uiaTextChangedCount", "_uiaSelectionChangedCount", "_uiaFocusChangedCount"
        };
        var editorFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var name in fields)
        {
            var f = typeof(EditorControl).GetField(name, editorFlags);
            Assert.Null(f);
        }

        var adapterType = typeof(EditorControl).Assembly.GetType("yEdit.Editor.UiaTextHostAdapter");
        Assert.NotNull(adapterType);
        var adapterFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var name in fields)
        {
            var f = adapterType!.GetField(name, adapterFlags);
            Assert.NotNull(f);
        }
    }

    [Fact]
    public void EditorControl_HoldsAdapter_ByField()
    {
        var f = typeof(EditorControl).GetField("_uia",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
    }
}
```

### Step 3d.3: テスト実行 (失敗確認)

```powershell
dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj `
  --filter FullyQualifiedName~UiaTextHostAdapterContractTests
# 期待: 2 FAIL
```

### Step 3d.4: UiaTextHostAdapter 新規作成 (フル移設)

`src/yEdit.Editor/UiaTextHostAdapter.cs`:

```csharp
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Core.Layout;

namespace yEdit.Editor;

/// <summary>
/// Task 3d: UIA (IUiaTextHost) 実装を EditorControl から完全に切り離した adapter。
/// 12 個の Uia 系 field を所有し、RPC スレッドから呼ばれる 22 メンバを snapshot/lock 越しで応答する。
/// EditorControl からは 4 個の通知 method (OnSnapshotChanged/OnBoundsChanged/OnHandleCreated/OnHandleDestroyed)
/// と 3 個の Raise* method で state を更新される。
/// </summary>
internal sealed class UiaTextHostAdapter : IUiaTextHost
{
    private readonly EditorControl _host;    // RectangleToScreen/PointToScreen 用の callback
    private readonly CaretController _caret;
    private readonly TextBuffer _buffer;     // 直接参照 (Adapter は TextSnapshot と CaretController のみ触る)

    private volatile TextSnapshot? _bufferSnapshot;
    private readonly object _boundsSync = new();
    private Rect _bounds;
    private int _clientToScreenX, _clientToScreenY;
    private (TextSnapshot Snap, int LogicalLine, IReadOnlyList<WrapSegment> Segs)? _lastLineSegs;
    private nint _hwnd;
    private TextControlProviderV2? _provider;
    private bool _testHook_LastGetObjectServed;
    private int _uiaTextChangedCount, _uiaSelectionChangedCount, _uiaFocusChangedCount;

    public UiaTextHostAdapter(EditorControl host, CaretController caret, TextBuffer buffer)
    {
        _host = host;
        _caret = caret;
        _buffer = buffer;
    }

    // ==================== 通知経路 (UI thread 側から呼ばれる) ====================

    public void OnSnapshotChanged(TextSnapshot newSnap)
    {
        _bufferSnapshot = newSnap;
        _lastLineSegs = null;
    }

    public void OnBoundsChanged(nint hwnd, Rectangle clientRect)
    {
        _hwnd = hwnd;
        if (hwnd == IntPtr.Zero) return;
        var screen = _host.RectangleToScreen(clientRect);
        lock (_boundsSync)
            _bounds = new Rect(screen.Left, screen.Top, screen.Width, screen.Height);
        var origin = _host.PointToScreen(Point.Empty);
        _clientToScreenX = origin.X;
        _clientToScreenY = origin.Y;
    }

    public void OnHandleCreated(nint hwnd)
    {
        _hwnd = hwnd;
        // 初期 bounds 計算は host に問い合わせる (OnBoundsChanged 相当)
    }

    public void OnHandleDestroyed()
    {
        _hwnd = IntPtr.Zero;
        _provider = null;
    }

    public void RaiseTextChanged()
    {
        if (_provider is null) return;
        _uiaTextChangedCount++;
        // EditorControl.Uia.cs で行っていた RaiseAutomationEvent の実装を移設
    }

    public void RaiseSelectionChanged()
    {
        if (_provider is null) return;
        _uiaSelectionChangedCount++;
        ...
    }

    public void RaiseFocusChanged(bool focused)
    {
        if (_provider is null) return;
        _uiaFocusChangedCount++;
        ...
    }

    public void EnsureProvider()
    {
        _provider ??= new TextControlProviderV2(this);
    }

    // ==================== IUiaTextHost 22 メンバ実装 (RPC thread から呼ばれる) ====================

    public int TextLength => (_bufferSnapshot?.CharCount) ?? 0;
    public (int Start, int End) GetSelection() => _caret.Selection;

    // ... 残 20 個は EditorControl.Uia.cs 側からロジックそのままコピー ...
    //     _bufferSnapshot / _bounds / _boundsSync / _lastLineSegs / _hwnd 参照を全て「自分の field」に置換

    // ==================== Test hook ====================

    internal long TestHook_LastLineSegsHitCount { get; private set; }
    internal long TestHook_LastLineSegsMissCount { get; private set; }
    internal void TestHook_ResetLastLineSegsCounters()
    {
        TestHook_LastLineSegsHitCount = 0;
        TestHook_LastLineSegsMissCount = 0;
    }
    internal bool TestHook_LastGetObjectServed => _testHook_LastGetObjectServed;
    internal int TextChangedCount => _uiaTextChangedCount;
    internal int SelectionChangedCount => _uiaSelectionChangedCount;
    internal int FocusChangedCount => _uiaFocusChangedCount;
}
```

**重要**: IUiaTextHost 実装 22 メンバは EditorControl.Uia.cs から**ロジックそのまま移設** (bit-perfect)。field 参照だけ「自分の field」に付け替え。

### Step 3d.5: EditorControl 側の変更

`src/yEdit.Editor/EditorControl.cs`:

```csharp
// Uia 系 field 12 個を全て削除
// 以下 1 行を追加:
private readonly UiaTextHostAdapter _uia;
```

ctor 内 (`_imeCtrl = new ImeController(...)` の直後):

```csharp
_uia = new UiaTextHostAdapter(this, _caretCtrl, _buffer);
```

**注意**: `_buffer` は SetSource 時に生成されるので、`_uia` の生成タイミングを SetSource 側に移すか、または `_uia` は lazy 生成する必要がある可能性。既存 `_buffer` のライフサイクルを Grep で確認して調整。

### Step 3d.6: OnHandle*/On*Changed の本体復帰 (§C.4 例外解消)

`src/yEdit.Editor/EditorControl.Uia.cs` から `OnHandleCreated`/`OnHandleDestroyed` を削除。

`src/yEdit.Editor/EditorControl.cs` に統一:

```csharp
protected override void OnHandleCreated(EventArgs e)
{
    base.OnHandleCreated(e);
    _uia.OnHandleCreated(Handle);
    _uia.OnBoundsChanged(Handle, ClientRectangle);
}

protected override void OnHandleDestroyed(EventArgs e)
{
    base.OnHandleDestroyed(e);
    _uia.OnHandleDestroyed();
}

protected override void OnSizeChanged(EventArgs e)
{
    base.OnSizeChanged(e);
    _uia.OnBoundsChanged(Handle, ClientRectangle);
}

protected override void OnLocationChanged(EventArgs e)
{
    base.OnLocationChanged(e);
    _uia.OnBoundsChanged(Handle, ClientRectangle);
}
```

### Step 3d.7: AfterEdit / SetSource / ReplaceSource の通知経路統一

以下 6 箇所の `_lastLineSegs = null;` を削除し、`_uia.OnSnapshotChanged(newSnap)` に置換 (Phase 2 完了記録より):

- `EditorControl.cs:203` (SetSource)
- `EditorControl.cs:257` (ReplaceSource)
- `EditorControl.cs:595` (wrap 値変化)
- `EditorControl.cs:963` (AfterEdit)
- `EditorControl.cs:1504` (metrics/wrap 変化)
- (残 1 箇所は Grep で確認)

同時に AfterEdit で `_uia.RaiseTextChanged();` を呼ぶ (現状 EditorControl 側で `_uiaTextChangedCount++` している箇所を Adapter 経由に変更)。

### Step 3d.8: EditorControl.Uia.cs をラッパ化

Before (438 行):
```csharp
public int TextLength => (_bufferSnapshot?.CharCount) ?? 0;
// ... 22 メンバ + CacheSnapshot/UpdateBoundsCache 等 ...
```

After (~80 行 = WM_GETOBJECT 分岐 + EnsureUiaProvider ラッパ + test hook 転送):
```csharp
public sealed partial class EditorControl
{
    // WM_GETOBJECT 分岐 (§C.4 準拠: WndProc 本体側に置く)
    // 現状 EnsureUiaProvider() は削除 (Adapter に移設済み)
    // WndProc case では _uia.EnsureProvider() 呼び出しに変更

    // Test hook 転送 (Editor.Tests から観測)
    internal long TestHook_LastLineSegsHitCount => _uia.TestHook_LastLineSegsHitCount;
    internal void TestHook_ResetLastLineSegsCounters() => _uia.TestHook_ResetLastLineSegsCounters();
    internal static void TestHook_WndProc(EditorControl c, ref Message m) => c.WndProc(ref m);
    internal static bool TestHook_LastGetObjectServed(EditorControl c) => c._uia.TestHook_LastGetObjectServed;
}
```

### Step 3d.9: 全体ゲート + 契約テスト緑

```powershell
taskkill /F /IM dotnet.exe
tools/pre-merge-check.ps1
# 期待: 全緑・0 warning・~1066 tests (1064+2 contract test)
```

### Step 3d.10: UiaTextHostAdapter pure テスト追加

`tests/yEdit.Editor.Tests/UiaTextHostAdapterTests.cs` に 4〜6 テスト:

- `OnSnapshotChanged_InvalidatesLastLineSegs`
- `GetSelection_ReturnsCaretControllerSelection`
- `TextLength_UsesSnapshotCharCount`
- `RaiseTextChanged_NullProvider_IsNoOp`
- `RaiseTextChanged_WithProvider_IncrementsCount`
- `OnHandleDestroyed_NullifiesProvider`

**注意**: RPC スレッド境界のテストは既存 `EditorControlUiaHostTests` (305 行) が担う。本 pure テストは Adapter の内部契約 (通知経路と field 更新) に絞る。既存 Uia テストは Adapter 経由に自動で切り替わる (EditorControl の public API 契約が同じため)。

### Step 3d.11: L5 実機検証 (最重要)

- NVDA を起動し、以下 3 経路を確認:
  1. **文書テキスト読み**: 本文を上下矢印で移動 → 行内容が読まれる
  2. **キャレット移動追従**: Ctrl+End/Home で移動 → 移動後の位置が読まれる (SelectionChanged 通知)
  3. **編集通知**: 文字入力後に「変更あり」等の再読みが起きる (TextChanged 通知)
- PC-Talker で同じ 3 経路を確認
- **差分検出時**: EditorControl.Uia.cs から移設した 22 メンバの実装差分を diff で確認。ロジック書換の疑いあれば bit-perfect に戻す。

### Step 3d.12: 数値目標検証 + 全体サイズ確認

```powershell
wc -l src/yEdit.Editor/EditorControl*.cs src/yEdit.Editor/*Controller.cs `
      src/yEdit.Editor/UiaTextHostAdapter.cs src/yEdit.Editor/WinImeContext.cs
# 期待:
#   EditorControl.cs 1537 → 600〜800
#   EditorControl.Uia.cs 438 → 80
#   UiaTextHostAdapter.cs +450
# 全体 Editor 層 total ≒ 2650 (-24%)
```

### Step 3d.13: コミット + review + main マージ

```powershell
git add src/yEdit.Editor/UiaTextHostAdapter.cs `
        src/yEdit.Editor/EditorControl.cs `
        src/yEdit.Editor/EditorControl.Uia.cs `
        tests/yEdit.Editor.Tests/UiaTextHostAdapterContractTests.cs `
        tests/yEdit.Editor.Tests/UiaTextHostAdapterTests.cs
git commit -m "refactor(editor): UiaTextHostAdapter 抽出・12 field 所有権移譲・§C.4 例外解消 (Task 3d)"
```

別エージェント review → **L5 実機検証 OK** → main へ no-ff マージ。

---

## Phase 3 完了時のチェックリスト

- [ ] Task 3b/3c/3a/3d 全て main マージ済
- [ ] `tools/pre-merge-check.ps1` 全緑・0 warning・~1070 tests
- [ ] L5 実機検証 (3a と 3d) 完了
- [ ] EditorControl.cs 1537 → 600〜800 (git-truth)
- [ ] Editor 層 total 3470 → ~2650 (代替 DoD: 削減 ≒ 追加 ±10%)
- [ ] Uia 系 field 12 個が完全に Adapter 所有
- [ ] `_caret`/`_anchor`/`_desiredXpx` が完全に CaretController 所有
- [ ] `_ime` が完全に ImeController 所有
- [ ] §C.4 例外解消 (OnHandle*/On*Changed 4 method が本体側)
- [ ] IImeContext seam 導入 (WinImeContext + FakeImeContext)
- [ ] MEMORY.md を更新 (`refactor-separation-phase3-complete.md` 追加)

---

## リスクと緩和策 (全 Phase 共通・再掲)

| リスク | 緩和策 |
|---|---|
| Controller ctor 順序で circular dependency | CaretController を最初に new・Input/Ime/Uia は CaretController を受け取る |
| UiaTextHostAdapter への field 移譲でスレッド安全崩壊 | `_boundsSync` の lock 粒度・volatile 属性を Adapter へそのまま移植・**変更最小** |
| IImeContext seam で P/Invoke タイミング変化 | `Func<IImeContext>` factory で毎回取り直し = 現状 `ImmGetContext(Handle)` の毎回呼びと同等 |
| Phase 2 flaky test に遭遇 | 各サブ着手前 `taskkill /F /IM dotnet.exe` |
| plan の数値目標が現実とずれる | 代替 DoD「削減行数 ≒ 追加行数 ±10%」で機械的検証 |
| 3d 完了時 §C.4 例外解消で Editor.Tests 破壊 | 3d Step 3d.6 で「4 method を本体に戻す」を明示・reflection 契約テストで機械固定 |
| ロジック書換の混入 (bit-perfect でない移設) | 各 Task の Step X.4/X.6 で「実装は移動元からそのまま貼る」明記・L5 実機検証で発見 |

---

## 全体 DoD (Definition of Done・全 Phase 完了時)

- 全テスト緑 (Phase 2 完了時 1041 → 想定 ~1070)
- 0 warning (Release ビルド)
- EditorControl.cs 1537 → 600〜800 (git-truth 準拠)
- Editor 層に Controller 4 個抽出済み (CaretController/InputRouter/ImeController/UiaTextHostAdapter)
- 中小 5 項目 (Phase 1) + partial 5 分割 (Phase 2) + Controller 4 委譲 (Phase 3) = **6 項目全解消**
- MEMORY.md に完了記録追加 (`refactor-separation-phase3-complete.md`)
- Task 1b の L5 実機検証 (Phase 1 の宿題) も併せて完了推奨
- origin/main への push 判断 (ユーザー)
