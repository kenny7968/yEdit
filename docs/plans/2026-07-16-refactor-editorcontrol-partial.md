# EditorControl partial 分割 (Phase 2) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** EditorControl.cs 3396 行を 5 つの partial(`Ime`/`Caret`/`Input`/`Uia`/`Paint`)+ 本体(コア)に **物理分割** し、Phase 3 の Controller 委譲の下ごしらえを完了する。挙動不変・SR 経路不変・純物理分割で、Editor.Tests / App.Tests / Core.Tests = 1041 全緑を維持する。

**Architecture:** Phase 1 で確立した段階マージ方式([[phase-work-git-flow]] 準拠)。1 サブ Phase = 1 フィーチャーブランチ = 1 no-ff マージ。SDD(Subagent-Driven Development)で Implementer subagent → Spec reviewer → Code quality reviewer (`superpowers:code-reviewer`) の 3 段パス。partial 分割のためフィールドは全て本体側に残置し、method 群だけを対象 partial ファイルへ移動する(field 所有権は Phase 3 で移す)。

**Tech Stack:** C# / .NET 9 / WinForms(EditorControl は System.Windows.Forms.Control 派生)/ xUnit / STA テストヘルパ / `partial class` によるファイル分割。

**上位文書:**

- 設計書: `docs/plans/2026-07-16-refactor-editorcontrol-partial-design.md`
- 上位設計書: `docs/plans/2026-07-16-refactor-separation-of-concerns-design.md`
- Phase 1 実装計画(前例): `docs/plans/2026-07-16-refactor-separation-of-concerns.md`

**テスト数遷移:** 1041 → 1041(全 5 サブで挙動不変・追加テストなし)

---

## Task 0: 事前ゲート+運用ルール確認

**目的:** main から作業を始める準備。

### Step 0.1: 事前ゲート

```powershell
git status         # クリーン (publish/ installer/ は untracked=無視)
git log --oneline -1
# 期待: HEAD=bb49845(Phase 2 設計書コミット)
tools/pre-merge-check.ps1
# 期待: 全緑・Release 0 警告・1041 tests
```

### Step 0.2: 運用ルール確認

- 各サブ Phase(2a〜2e)ごとに独立ブランチを切る: `feature/refactor-2a-editorcontrol-ime` 等
- 各サブ Phase 完了時に `superpowers:requesting-code-review` を起動して別エージェントに review を依頼
- レビュー「マージ可」+ `tools/pre-merge-check.ps1` 緑 → main へ `git merge --no-ff` でマージ
- **並行禁止**: 5 サブは 2a → 2b → 2c → 2d → 2e の順で 1 つずつ完了させてから次
- L5 実機検証は **全 5 サブで不要**(SR 経路不変)
- サブ Phase 完了ごとの MEMORY 更新は不要(全 5 サブ完了時に一括で `refactor-separation-phase2-complete.md` を作成)

### Step 0.3: 挙動不変の確認方法(全サブ共通)

各サブごとに以下 4 点を機械/目視でチェック(Implementer subagent へ伝える標準チェック):

1. **ビルド 0 warnings**: `dotnet build -c Release`
2. **全テスト緑**: `tools/pre-merge-check.ps1` = 1041 tests
3. **git diff 検算**: `git diff --stat main` で `EditorControl.cs` の削減行数 ≒ 新 partial の追加行数(±10 行の誤差は許容)
4. **git diff -U0 spot-check**: 移動先の method 本体が完全一致(削除+追加の 2 hunk が理想)

**Commit:** なし(準備のみ)

---

## Task 2a: `EditorControl.Ime.cs` 抽出

**目的:** IME 関連メソッド群(WM_IME_* handler・composition state 更新・IME 描画補助)を `EditorControl.Ime.cs` へ物理的に切り出す。挙動不変。

**Files:**

- Create: `src/yEdit.Editor/EditorControl.Ime.cs`
- Modify: `src/yEdit.Editor/EditorControl.cs`(該当 method 群を削除)
- Test: 既存 `tests/yEdit.Editor.Tests/EditorControlImeTests.cs`(緑継続)

**L5 実機検証:** 不要

### Step 2a.1: ブランチ作成

```powershell
git checkout -b feature/refactor-2a-editorcontrol-ime
git log --oneline -1
# 期待: HEAD が main の bb49845
```

### Step 2a.2: 事前計測(方向性チェック用)

```powershell
$before = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (before): $before lines"
# 期待: 3396
```

### Step 2a.3: `EditorControl.Ime.cs` を新規作成

`src/yEdit.Editor/EditorControl.Ime.cs` を新規作成。ファイル冒頭は以下の header:

```csharp
// EditorControl.Ime.cs
// Phase 2 (Task 2a) で切り出した IME 分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3-3a で ImeController へロジック移譲予定。
using System.Runtime.InteropServices;
using yEdit.Core.Editing;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // 以下、対象 method 群を EditorControl.cs から移動
}
```

### Step 2a.4: 対象 method 群を移動

以下を `EditorControl.cs` から `EditorControl.Ime.cs` へ **cut & paste**(コード本体は完全一致・xmldoc/コメントも維持):

- `OnImeEndComposition()` (現行 `EditorControl.cs:1486`)
- `CancelCompositionAndDefault()` (`:1500`)
- `OnImeSetContext(ref Message m)` (`:1526`)
- `OnImeStartComposition()` (`:1547`)
- `OnImeComposition(long gcsFlags)` (`:1587`)
- `ApplyResult(string text)` (`:1642`)
- `ApplyComposition(string text, int cursorPos, byte[] attrs, int[] clauses)` (`:1658`)
- `ReadImeString(nint hIMC, int gcsFlag)` (`:1676`)
- `ReadImeBytes(nint hIMC, int gcsFlag)` (`:1690`)
- `ReadImeInt(nint hIMC, int gcsFlag)` (`:1705`)
- `NotifyCandidateWindow()` (`:1886`)
- `NotifyCompositionFont()` (`:1917`)
- テストフック: `__TestApplyComposition` `__TestImeStart` `__TestIsComposing` `__TestImeText` `__SmokeIsComposing` `__SmokeImeText` (`:1720-1730`)

**残置(EditorControl.cs 側で保持)**:

- `WndProc(ref Message m)` 本体 (`:1424`) — WM_IME_* 分岐は薄い case ラッパのまま残す
- `PositionCaret()` (`:1836`) — システムキャレット位置決定
- `DrawImeOverlay(Graphics g)` (`:2729`) — Paint 側で管理(§C.5)

### Step 2a.5: ビルド確認

```powershell
dotnet build -c Release
# 期待: 0 warnings, 0 errors
```

失敗した場合は typically:
- using 追加漏れ(`System.Runtime.InteropServices` の DllImport 属性など)
- WM_IME_* case からの呼び出し先 method 名の taipo

### Step 2a.6: 全テスト実行

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・Release 0 warnings・1041 tests(挙動不変)
```

### Step 2a.7: 事後計測

```powershell
$after = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
$ime = (Get-Content src/yEdit.Editor/EditorControl.Ime.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (after): $after lines (target ~2900)"
Write-Host "EditorControl.Ime.cs: $ime lines (target ~500)"
Write-Host "Delta: $($before - $after) lines removed from main, $ime lines added to Ime"
# 期待: 削減分 ≒ Ime 追加分 (±10 の誤差許容)
```

### Step 2a.8: コミット

```powershell
git add src/yEdit.Editor/EditorControl.cs src/yEdit.Editor/EditorControl.Ime.cs
git commit -m "refactor(editor): EditorControl.Ime.cs 抽出 (Task 2a)"
```

### Step 2a.9: 別エージェント review + main マージ

`superpowers:requesting-code-review` を起動:

**Review 観点(subagent へ渡すべき指針)**:

1. 移動した各 method の本体が git diff 上「削除 hunk + 追加 hunk」の対称ペアであること(挙動不変の証跡)
2. `using` が最小限であること(継承しない=各 partial が自己完結)
3. フィールド宣言が partial 側に移動していないこと(§C.1・全 field は本体側残置)
4. WndProc 本体が本体側に残っており、case ラッパが薄いままであること(§C.4)
5. `DrawImeOverlay` が本体側に残っていること(Task 2e で Paint 側へ移動予定)
6. header comment が §C.2 の書式に従っていること

「マージ可」→ `git checkout main && git merge --no-ff feature/refactor-2a-editorcontrol-ime`

---

## Task 2b: `EditorControl.Caret.cs` 抽出

**目的:** キャレット/選択の位置操作 API を `EditorControl.Caret.cs` へ物理的に切り出す。挙動不変。

**Files:**

- Create: `src/yEdit.Editor/EditorControl.Caret.cs`
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Test: 既存 `AnchorSelectionTests` / `CaretAndSelectionSmokeTests` / `CaretScrollTests` / `KeyboardNavigationTests`(緑継続)

**L5 実機検証:** 不要

### Step 2b.1: ブランチ作成

```powershell
git checkout main
git checkout -b feature/refactor-2b-editorcontrol-caret
git log --oneline -1
# 期待: HEAD が 2a マージ後の main
```

### Step 2b.2: 事前計測

```powershell
$before = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (before): $before lines"
# 期待: ~2900(2a マージ後)
```

### Step 2b.3: `EditorControl.Caret.cs` を新規作成

```csharp
// EditorControl.Caret.cs
// Phase 2 (Task 2b) で切り出したキャレット/選択分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3-3b で CaretController へロジック移譲予定(_caret/_anchor/_desiredXpx の所有権も移す)。
using yEdit.Core.Buffers;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // Caret 位置操作 + 選択範囲 API + view-into scroll
}
```

### Step 2b.4: 対象 method 群を移動

- `SetCaretCharOffset(int offset)` (`:907`)
- `SetSelectionCharRange(int start, int end)` (`:942`)
- `MoveCaretWithSelection(int newCaret)` (`:966`)
- `SetSelectionAnchored(int anchor, int caret)` (`:989`)
- `SnapAndClamp(int offset)` (`:1011`)
- `SelectCharRange(int start, int length)` (`:323`)
- `GoToLine(int line)` (`:336`)
- `GetColumn(int offset)` (`:853`)
- `BringCaretIntoView()` (`:2562`)
- `EnsureVisibleCharRange(int start, int length)` (`:2625`)
- `PointFromCharOffset(int offset)` (`:1937`)
- プロパティ: `CurrentLine` (`:841`) / `CaretCharOffset` (`:834`) / `SelectionAnchor` (`:900`)

**残置**:

- `_caret` / `_anchor` / `_desiredXpx` の宣言(§C.1)
- `ClampTopLine` / `UpdateVerticalScrollbar` / `UpdateHorizontalScrollbar`(Scroll 系は本体)
- `HighlightCharRange` / `ClearHighlight`(装飾は本体・§C.4)
- `AfterEdit`(編集後同期は本体・§C.4)

### Step 2b.5: ビルド確認

```powershell
dotnet build -c Release
# 期待: 0 warnings, 0 errors
```

### Step 2b.6: 全テスト実行

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・1041 tests
```

### Step 2b.7: 事後計測

```powershell
$after = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
$caret = (Get-Content src/yEdit.Editor/EditorControl.Caret.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (after): $after lines (target ~2450)"
Write-Host "EditorControl.Caret.cs: $caret lines (target ~450)"
```

### Step 2b.8: コミット + main マージ

```powershell
git add src/yEdit.Editor/EditorControl.cs src/yEdit.Editor/EditorControl.Caret.cs
git commit -m "refactor(editor): EditorControl.Caret.cs 抽出 (Task 2b)"
```

別エージェント review → main へ no-ff マージ。Review 観点は Task 2a.9 の項目を Caret 側へ読み替え。

---

## Task 2c: `EditorControl.Input.cs` 抽出

**目的:** キーボード/マウス入力ハンドラ群を `EditorControl.Input.cs` へ物理的に切り出す。挙動不変。

**Files:**

- Create: `src/yEdit.Editor/EditorControl.Input.cs`
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Test: 既存 `KeyboardNavigationTests` / `MouseInputTests` / `TextInsertionTests`(緑継続)

**L5 実機検証:** 不要

### Step 2c.1: ブランチ作成

```powershell
git checkout main
git checkout -b feature/refactor-2c-editorcontrol-input
```

### Step 2c.2: 事前計測

```powershell
$before = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (before): $before lines"
# 期待: ~2450
```

### Step 2c.3: `EditorControl.Input.cs` を新規作成

```csharp
// EditorControl.Input.cs
// Phase 2 (Task 2c) で切り出したキーボード/マウス入力分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3-3c で InputRouter へロジック移譲予定(キーマップ Dictionary 化・_mouseDragging/_wheelAccum
// の所有権も移す)。
using yEdit.Core.Buffers;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // OnKeyDown/OnKeyPress/OnMouse* + InsertConfirmedText + Word boundary helpers
}
```

### Step 2c.4: 対象 method 群を移動

- `IsInputKey(Keys keyData)` (`:2218`)
- `OnKeyDown(KeyEventArgs e)` (`:2251`)
- `OnKeyPress(KeyPressEventArgs e)` (`:2477`)
- `InsertConfirmedText(string text)` (`:2502`)
- `OnMouseWheel(MouseEventArgs e)` (`:1956`)
- `OnMouseDown(MouseEventArgs e)` (`:1995`)
- `OnMouseMove(MouseEventArgs e)` (`:2024`)
- `OnMouseUp(MouseEventArgs e)` (`:2043`)
- `OnMouseDoubleClick(MouseEventArgs e)` (`:2063`)
- `OffsetFromClientPoint(int clientX, int clientY)` (`:2089`)
- `SegmentCountAtLine(TextSnapshot snap, int line, int maxWidthPx)` (`:2147`)
- `PrevWordBoundary(TextSnapshot snap, int target)` (`:2169`)
- `NextWordBoundary(TextSnapshot snap, int target)` (`:2198`)

**残置**:

- `_mouseDragging` / `_wheelAccum` の宣言(§C.1)
- `Undo` / `Redo` / `Copy` / `Cut` / `Paste` / `SelectAll` は本体(edit 操作の上位 API)

### Step 2c.5: ビルド確認

```powershell
dotnet build -c Release
# 期待: 0 warnings, 0 errors
```

### Step 2c.6: 全テスト実行

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・1041 tests
```

### Step 2c.7: 事後計測

```powershell
$after = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
$input = (Get-Content src/yEdit.Editor/EditorControl.Input.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (after): $after lines (target ~1850)"
Write-Host "EditorControl.Input.cs: $input lines (target ~600)"
```

### Step 2c.8: コミット + main マージ

```powershell
git add src/yEdit.Editor/EditorControl.cs src/yEdit.Editor/EditorControl.Input.cs
git commit -m "refactor(editor): EditorControl.Input.cs 抽出 (Task 2c)"
```

別エージェント review → main へ no-ff マージ。

---

## Task 2d: `EditorControl.Uia.cs` 抽出

**目的:** `IUiaTextHost` 実装群と UIA キャッシュ/イベント発火/座標補助を `EditorControl.Uia.cs` へ物理的に切り出す。挙動不変。

**Files:**

- Create: `src/yEdit.Editor/EditorControl.Uia.cs`
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Test: 既存 `EditorControlUiaHostTests` / `EditorControlUiaEventsTests` / `EditorControlUiaFocusEventTests` / `EditorControlUiaGetObjectTests` / `EditorControlBoundingRectsTests` / `EditorControlOffsetFromPointTests` / `RaiseUiaSelectionEventsTests` / `EditorControlCacheTests`(緑継続)

**L5 実機検証:** 不要(実装は動かず配置のみ・SR 経路の意味論は不変)

### Step 2d.1: ブランチ作成

```powershell
git checkout main
git checkout -b feature/refactor-2d-editorcontrol-uia
```

### Step 2d.2: 事前計測

```powershell
$before = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (before): $before lines"
# 期待: ~1850
```

### Step 2d.3: `EditorControl.Uia.cs` を新規作成

```csharp
// EditorControl.Uia.cs
// Phase 2 (Task 2d) で切り出した UIA (IUiaTextHost) 分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3-3d で UiaTextHostAdapter へロジック移譲予定
// (_bufferSnapshot/_bounds/_boundsSync/_lastLineSegs/_hwnd の所有権も移す)。
using yEdit.Core.Buffers;
using yEdit.Core.Layout;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // IUiaTextHost 実装 + UIA キャッシュ + イベント発火 + 座標補助
}
```

### Step 2d.4: 対象 method 群を移動

**`IUiaTextHost` explicit 実装**:

- `GetTextRange` / `TextLength` / `GetSelection` / `SetSelection`
- `NextChar` / `PrevChar` / `LineStartOf` / `LineEnd` / `LineEndNoBreakOf`
- `WordStart` / `WordEnd` / `NextWordStart` / `PrevWordStart`
- `BoundingRectangle` / `GetBoundingRectangles` / `OffsetFromScreenPoint`
- `Handle` / `HasFocus` / `ControlTypeId` / `Name` / `AutomationId` / `SetFocus`

**UIA キャッシュ + ライフサイクル + 座標 + wrap + Word 補助**:

- `CacheSnapshot()` (`:2967`)
- `UpdateBoundsCache()` (`:2972`)
- `OnHandleCreated(EventArgs e)` (`:2983`) — 純 UIA 用途 (_hwnd キャッシュ + UpdateBoundsCache)
- `OnHandleDestroyed(EventArgs e)` (`:2990`) — 純 UIA 用途 (_hwnd = 0)
- `ComputeBoundingRectangles(int start, int end)` (`:3249`)
- `ComputeOffsetFromScreenPoint(double x, double y)` (`:3303`)
- `TryFindVisualSegment(TextSnapshot snap, int line, int offsetInLine)` (`:3118`)
- `TryFindVisualSegmentCore(...)` (`:3137`)
- `WordBoundary_WordStart(TextSnapshot snap, int pos)` (`:3198`)
- `WordBoundary_WordEnd(TextSnapshot snap, int pos)` (`:3214`)

**UIA event 発火**:

- `RaiseUia(AutomationEvent ev)` (`:3364`)

**Provider 生成 helper**(WM_GETOBJECT 分岐から呼ばれる薄い helper を新設)。分岐そのものは WndProc 本体側に残す(§C.4)ため、`_provider ??= new TextControlProviderV2(this)` の生成呼び出しを本体側の case 内から Uia 側の helper に切り出す。

**テストフック**:

- `TestHook_WndProc` / `TestHook_LastGetObjectServed` / `TestHook_ForceUiaListen` / `TestHook_ResetUiaEventCounts` / `TestHook_UiaEventCounts` / `TestHook_GetLastFrame`
- `TestHook_LastLineSegsHitCount` / `TestHook_LastLineSegsMissCount` / `TestHook_ResetLastLineSegsCounters`(`_lastLineSegs` は UIA 側の TryFindVisualSegment 用キャッシュ)

**残置**:

- `WndProc` 本体の WM_GETOBJECT 分岐(§C.4・薄い case ラッパのまま)
- `OnSizeChanged` / `OnLocationChanged`(Control ライフサイクルは本体・中身は Uia 側 `UpdateBoundsCache()` を呼ぶ)
- 全 UIA 関連フィールドの宣言(`_bufferSnapshot` / `_bounds` / `_boundsSync` / `_hwnd` / `_lastLineSegs` / `_lastFrame` / `_clientToScreenX` / `_clientToScreenY` / `_uiaTextChangedCount` / `_uiaSelectionChangedCount` / `_uiaFocusChangedCount` / `_provider` / `_testHook_LastGetObjectServed`)

### Step 2d.5: ビルド確認

```powershell
dotnet build -c Release
# 期待: 0 warnings, 0 errors
```

### Step 2d.6: 全テスト実行

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・1041 tests
```

**特に注意して確認**:

- `EditorControlUiaGetObjectTests`: WM_GETOBJECT 経路が本体側の switch から Uia 側の provider 生成 helper を呼ぶ形になったので、`TestHook_WndProc` 経由の smoke がまだ動くこと
- `EditorControlCacheTests`: `TestHook_LastLineSegsHitCount/MissCount` フィールドは本体側に残置・観測メソッドは Uia 側に移動

### Step 2d.7: 事後計測

```powershell
$after = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
$uia = (Get-Content src/yEdit.Editor/EditorControl.Uia.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (after): $after lines (target ~1450)"
Write-Host "EditorControl.Uia.cs: $uia lines (target ~400)"
```

### Step 2d.8: コミット + main マージ

```powershell
git add src/yEdit.Editor/EditorControl.cs src/yEdit.Editor/EditorControl.Uia.cs
git commit -m "refactor(editor): EditorControl.Uia.cs 抽出 (Task 2d)"
```

別エージェント review → main へ no-ff マージ。

**追加 Review 観点(2d 固有)**:

- WM_GETOBJECT case 内の provider 生成が Uia 側の helper を呼ぶ形になっていること
- `OnHandleCreated` / `OnHandleDestroyed` が Uia 側に移動していること(純 UIA 用途のため §C.4 の例外)
- `TryFindVisualSegment` の Invoke マーシャリング(RPC スレッド → UI スレッド)が改変されていないこと

---

## Task 2e: `EditorControl.Paint.cs` 抽出

**目的:** 描画関連メソッド群を `EditorControl.Paint.cs` へ物理的に切り出す。`DrawImeOverlay` も Paint 側に置く(§C.5)。挙動不変。

**Files:**

- Create: `src/yEdit.Editor/EditorControl.Paint.cs`
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Test: 既存 Editor.Tests 全体(描画は smoke 経由で全 test に効く)

**L5 実機検証:** 不要

### Step 2e.1: ブランチ作成

```powershell
git checkout main
git checkout -b feature/refactor-2e-editorcontrol-paint
```

### Step 2e.2: 事前計測

```powershell
$before = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (before): $before lines"
# 期待: ~1450
```

### Step 2e.3: `EditorControl.Paint.cs` を新規作成

```csharp
// EditorControl.Paint.cs
// Phase 2 (Task 2e) で切り出した描画分割。フィールドは EditorControl.cs 本体で宣言。
// DrawImeOverlay も IME 状態 (_ime) を読むだけの純描画関数のため Paint 側に置く (§C.5)。
using yEdit.Core.Layout;
using yEdit.Core.Settings;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // OnPaint + RenderFrame + IME overlay 描画 + style/color helpers
}
```

### Step 2e.4: 対象 method 群を移動

- `OnPaint(PaintEventArgs e)` (`:2653`)
- `DrawImeOverlay(Graphics g)` (`:2729`) — §C.5(Paint 側で最終管理)
- `RenderFrame(Graphics g, Frame frame)` (`:2787`)
- `MeasureLineNumberWidth(int lineCount)` (`:2814`)
- `ToColor(PaintColor c)` (`:2820`)
- `DefaultStyle()` (`:2823`)
- `BuildStyle(AppearanceTheme theme, bool highlightCurrentLine)` (`:2929`)
- `BlendRgb(int baseRgb, int accentRgb, double ratio)` (`:2948`)
- `FromRgb(int rgb)` (`:2958`)

**残置**:

- `ApplyAppearance(AppSettings settings)`(設定反映の受口は本体・§C.4)
- `_font` / `_underlineFontCache` / `_targetFontCache` / `_metrics` / `_style` の宣言

### Step 2e.5: ビルド確認

```powershell
dotnet build -c Release
# 期待: 0 warnings, 0 errors
```

### Step 2e.6: 全テスト実行

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・1041 tests
```

### Step 2e.7: 事後計測

```powershell
$after = (Get-Content src/yEdit.Editor/EditorControl.cs | Measure-Object -Line).Lines
$paint = (Get-Content src/yEdit.Editor/EditorControl.Paint.cs | Measure-Object -Line).Lines
Write-Host "EditorControl.cs (after): $after lines (target ~1000-1100)"
Write-Host "EditorControl.Paint.cs: $paint lines (target ~250)"
```

### Step 2e.8: 全体 Phase 2 完了確認

```powershell
ls src/yEdit.Editor/EditorControl*.cs
# 期待:
# EditorControl.cs         (~1000-1100 行)
# EditorControl.Caret.cs   (~450 行)
# EditorControl.Ime.cs     (~500 行)
# EditorControl.Input.cs   (~600 行)
# EditorControl.Paint.cs   (~250 行)
# EditorControl.Uia.cs     (~400 行)
$total = (Get-ChildItem src/yEdit.Editor/EditorControl*.cs |
    ForEach-Object { (Get-Content $_.FullName | Measure-Object -Line).Lines } |
    Measure-Object -Sum).Sum
Write-Host "Total lines across all EditorControl partials: $total"
# 期待: ~3200-3300(元 3396 に近い。increase なら header comment/using 分の膨張=許容)
```

### Step 2e.9: コミット + main マージ

```powershell
git add src/yEdit.Editor/EditorControl.cs src/yEdit.Editor/EditorControl.Paint.cs
git commit -m "refactor(editor): EditorControl.Paint.cs 抽出 (Task 2e・Phase 2 完了)"
```

別エージェント review → main へ no-ff マージ。

---

## Phase 2 完了時のチェックリスト

- [ ] Task 2a〜2e 全て main マージ済
- [ ] `tools/pre-merge-check.ps1` 全緑・0 警告・1041 tests(挙動不変)
- [ ] `src/yEdit.Editor/EditorControl.cs` が **~1000 行**(±100 の誤差許容・目標 ≦ 1100)
- [ ] `src/yEdit.Editor/` に partial 5 ファイルが存在(`Ime`/`Caret`/`Input`/`Uia`/`Paint`)
- [ ] MEMORY.md に `refactor-separation-phase2-complete.md` を追加(全 5 サブ・マージ SHA・行数計測結果を記録)
- [ ] MEMORY.md の Phase 1 完了記録(`refactor-separation-phase1-complete.md`)を Phase 2 完了ステータスに更新(または Phase 2 記録から相互リンク)

## Phase 3 概要: Controller 委譲(未着手)

Phase 2 完了後に Phase 3 実装計画 `docs/plans/2026-XX-XX-refactor-editorcontrol-controllers.md` を `superpowers:writing-plans` skill で新規作成する。

**サブ Phase 目次(上位設計書 §4 参照):**

| サブ | ブランチ名 | L5 | DoD |
|---|---|---|---|
| 3a | `feature/refactor-3a-ime-controller` | 必要 | `ImeController` 抽出・`IImeContext` seam・Editor.Tests 緑 |
| 3b | `feature/refactor-3b-caret-controller` | 不要 | `CaretController` 抽出・`_caret`/`_anchor`/`_desiredXpx` 所有権移譲 |
| 3c | `feature/refactor-3c-input-router` | 不要 | `InputRouter` 抽出・キーマップ dictionary 化 |
| 3d | `feature/refactor-3d-uia-adapter` | 必要 | `UiaTextHostAdapter` 抽出・`_bufferSnapshot`/`_bounds` 所有権移譲 |

---

## リスクと緩和策(全 Phase 2 共通・設計書 §D.4 と対応)

| リスク | 影響 | 緩和策 |
|---|---|---|
| method 移動中に不注意で本体を書き換え | 挙動差異 | 各サブの PR で「method 本体 diff = 0」を reviewer が確認 |
| partial 側で必要な using を書き忘れ | compile error | ローカルビルドで即発覚(Step N.5) |
| WndProc の case が partial 側に漏れた | Message pump 崩壊 | §C.4 で WndProc 本体側規約・`EditorControlImeTests`/`EditorControlUiaGetObjectTests` で守る |
| DrawImeOverlay が Paint 移動時に `_ime` を取り違え | IME overlay 消失 | §C.5 で Paint 帰属確定・§C.1 で field は本体側=同一クラス private として直接見える |
| テストフックの partial 移動で InternalsVisibleTo が壊れる | Editor.Tests 破壊 | `yEdit.Editor.csproj` の assembly attribute は partial ファイル位置と無関係=影響なし |
| サブ間で並行作業してコンフリクト | マージ困難 | 5 サブは順次実行(2a→2b→…)・並行禁止 |
| Phase 2 期間中に別バグ修正が入る | main と同期困難 | 各サブを 1〜2 日で終わらせる(短命ブランチ) |

---

## 全体 DoD(Phase 2 完了時)

- 全テスト緑(1041 tests・Core 580 + Editor 218 + App 243)
- 0 警告(Release ビルド)
- EditorControl.cs 3396 → ~1000 行(±100 誤差許容)
- Editor 層 partial 5 ファイル(`EditorControl.Ime.cs`/`.Caret.cs`/`.Input.cs`/`.Uia.cs`/`.Paint.cs`)存在
- MEMORY.md に Phase 2 完了記録追加
- Phase 3 実装計画の準備が完了(writing-plans skill で新規作成する状態)
