# EditorControl partial 分割(Phase 2) 設計書

- 日付: 2026-07-16
- 前提: Phase 1 完了(main `56a1386`・1041 tests 緑・0 warnings)
- 上位設計: `docs/plans/2026-07-16-refactor-separation-of-concerns-design.md` §3

## A. 全体像・Goals・Non-goals

### 位置付け

Phase 1 完了(1041 tests・0 warnings・main マージ済)を起点。上位設計書 §3 の骨格 (2a〜2e) を採用。
Phase 2 は **EditorControl.cs 3396 行の partial 物理分割**。挙動不変・SR 経路不変・L5 不要。
Phase 3 で Controller に責務移譲するときの「境界を可視化する下ごしらえ」であり、Phase 2 自体はロジック移動を含まない。

### Goals(この Phase で達成する)

1. EditorControl.cs を 5 つの partial(`Ime`/`Caret`/`Input`/`Uia`/`Paint`)+ 本体(コア)に物理分割。
2. **挙動不変**: Editor.Tests 218 + App.Tests 243 + Core.Tests 580 = 1041 全緑継続。0 警告継続。
3. **サブ Phase ごとに独立 branch・別エージェント review・no-ff マージ**([[phase-work-git-flow]] + [[review-by-separate-agent]])。
4. 分割後の本体 EditorControl.cs は **~1000 行**(±100 の誤差許容)。

### Non-goals(Phase 2 では触らない)

- **Controller 抽出**(3a-3d)。partial は同一クラスの分割ファイルであり、フィールド所有権は動かさない。
- **field の可視性変更**(private → internal / readonly 追加等)。Phase 3 で必要な分だけ動かす。
- **API シグネチャ変更**。public/internal の露出面は完全に不変。
- **テスト追加**(既決定: 契約テストは追加しない)。
- **テスト移動**(既存テストは partial 統合後の EditorControl を叩くので透過)。
- **SR/L5 実機検証**。Phase 2 全 5 サブは SR 経路不変で L5 不要。
- **Task 1b の L5 実機検証**(Phase 1 の申し送り)は Phase 2 と並行して user 担当・Phase 2 完了のブロッカーではない。

### 順序

上位設計書通り **2a → 2b → 2c → 2d → 2e**(user 判断で確定)。5 サブは順次実行し並行は禁止。

## B. 各サブ Phase 詳細

上位設計書 §3 の method 割当を実 EditorControl.cs (3396 行) と照合し、行番号込みで確定。行番号は現 EditorControl.cs 基準。

### B.1 Task 2a: `EditorControl.Ime.cs`

**移動対象メソッド**

- `WndProc` 内の WM_IME_* 4 分岐(`:1450-1467`) — WndProc 本体は本体側に残し、`case` の呼び出し先だけ Ime 側で受ける
- `OnImeSetContext(ref Message)` (`:1526`)
- `OnImeStartComposition()` (`:1547`)
- `OnImeComposition(long)` (`:1587`)
- `OnImeEndComposition()` (`:1486`)
- `CancelCompositionAndDefault()` (`:1500`)
- `ApplyResult(string)` (`:1642`)
- `ApplyComposition(string, int, byte[], int[])` (`:1658`)
- `ReadImeString/Bytes/Int` (3 static, `:1676-1720`)
- `NotifyCandidateWindow()` (`:1886`)
- `NotifyCompositionFont()` (`:1917`)
- テストフック `__TestApplyComposition` `__TestImeStart` `__TestIsComposing` `__TestImeText` `__SmokeIsComposing` `__SmokeImeText` (`:1720-1730`)

**残置(本体側)**

- `WndProc(ref Message)` 本体 (`:1424`) — WM_GETOBJECT + WM_GETTEXT[LENGTH] + WM_IME_* switch のディスパッチのみ
- `PositionCaret()` (`:1836`) — システムキャレット位置決定(§C.4)
- `DrawImeOverlay(Graphics)` (`:2729`) — Paint 側で管理(§C.5)

**参照するフィールド(全て本体側で宣言・共有継続)**

`_ime` / `_hasFocus` / `_buffer` / `_caret` / `_metrics` / `_font` / `_topLine` / `_wrapColumns` / `_scrollX`

**行数目安**: ~500

### B.2 Task 2b: `EditorControl.Caret.cs`

**移動対象メソッド**

- `SetCaretCharOffset(int)` (`:907`)
- `SetSelectionCharRange(int, int)` (`:942`)
- `MoveCaretWithSelection(int)` (`:966`)
- `SetSelectionAnchored(int, int)` (`:989`)
- `SnapAndClamp(int)` (`:1011`)
- `SelectCharRange(int, int)` (`:323`)
- `GoToLine(int)` (`:336`)
- `GetColumn(int)` (`:853`)
- `BringCaretIntoView()` (`:2562`)
- `EnsureVisibleCharRange(int, int)` (`:2625`)
- `PointFromCharOffset(int)` (`:1937`)
- **プロパティ**: `CurrentLine` (`:841`) / `CaretCharOffset` (`:834`) / `SelectionAnchor` (`:900`)

**残置(本体側)**

- `_caret` / `_anchor` / `_desiredXpx` の宣言 (`:50-73`) — Phase 3-3b の CaretController で所有権移譲
- `ClampTopLine` / `UpdateVerticalScrollbar` / `UpdateHorizontalScrollbar` (`:1027-1112`) — Scroll 系は本体
- `HighlightCharRange` / `ClearHighlight` (`:709-741`) — 装飾は本体(§C.4)
- `AfterEdit()` (`:1148`) — 編集後同期は本体(§C.4)

**行数目安**: ~450

### B.3 Task 2c: `EditorControl.Input.cs`

**移動対象メソッド**

- `IsInputKey(Keys)` (`:2218`)
- `OnKeyDown(KeyEventArgs)` (`:2251`)
- `OnKeyPress(KeyPressEventArgs)` (`:2477`)
- `InsertConfirmedText(string)` (`:2502`)
- `OnMouseWheel(MouseEventArgs)` (`:1956`)
- `OnMouseDown(MouseEventArgs)` (`:1995`)
- `OnMouseMove(MouseEventArgs)` (`:2024`)
- `OnMouseUp(MouseEventArgs)` (`:2043`)
- `OnMouseDoubleClick(MouseEventArgs)` (`:2063`)
- `OffsetFromClientPoint(int, int)` (`:2089`)
- `SegmentCountAtLine(TextSnapshot, int, int)` (`:2147`)
- `PrevWordBoundary(TextSnapshot, int)` (`:2169`)
- `NextWordBoundary(TextSnapshot, int)` (`:2198`)

**残置(本体側)**

- `_mouseDragging` / `_wheelAccum` の宣言 (`:85-90`) — Phase 3-3c の InputRouter で所有権移譲
- 上位 API(`Undo` / `Redo` / `Copy` / `Cut` / `Paste` / `SelectAll` = `:1195-1345`)は本体

**行数目安**: ~600

### B.4 Task 2d: `EditorControl.Uia.cs`

**移動対象メソッド**

- `IUiaTextHost` explicit 実装一式 (`:3008-3344`):
  - `GetTextRange` / `TextLength` / `GetSelection` / `SetSelection`
  - `NextChar` / `PrevChar` / `LineStartOf` / `LineEnd` / `LineEndNoBreakOf`
  - `WordStart` / `WordEnd` / `NextWordStart` / `PrevWordStart`
  - `BoundingRectangle` / `GetBoundingRectangles` / `OffsetFromScreenPoint`
  - `Handle` / `HasFocus` / `ControlTypeId` / `Name` / `AutomationId` / `SetFocus`
- UIA キャッシュ: `CacheSnapshot()` (`:2967`) / `UpdateBoundsCache()` (`:2972`)
- Handle ライフサイクル hook(純 UIA): `OnHandleCreated` (`:2983`) / `OnHandleDestroyed` (`:2990`)
- 座標補助: `ComputeBoundingRectangles(int, int)` (`:3249`) / `ComputeOffsetFromScreenPoint(double, double)` (`:3303`)
- wrap 探索: `TryFindVisualSegment` (`:3118`) / `TryFindVisualSegmentCore` (`:3137`)
- Word 補助: `WordBoundary_WordStart` (`:3198`) / `WordBoundary_WordEnd` (`:3214`)
- UIA event 発火: `RaiseUia(AutomationEvent)` (`:3364`)
- `_provider` provider 生成 helper(WM_GETOBJECT 分岐用)
- テストフック: `TestHook_WndProc` / `TestHook_LastGetObjectServed` / `TestHook_ForceUiaListen` / `TestHook_ResetUiaEventCounts` / `TestHook_UiaEventCounts` / `TestHook_GetLastFrame` / `TestHook_LastLineSegsHitCount` / `TestHook_LastLineSegsMissCount` / `TestHook_ResetLastLineSegsCounters`

**残置(本体側)**

- `WndProc` 本体の WM_GETOBJECT 分岐 (`:1427-1441`) — Message pump 一貫性のため。`_provider ??= new TextControlProviderV2(this)` の provider 生成 helper のみ Uia 側に置く
- `OnSizeChanged` / `OnLocationChanged` (`:2996-3006`) — Control ライフサイクルは本体側で `UpdateBoundsCache()` (Uia 側) を呼ぶ形

**参照するフィールド(本体側で宣言)**

`_bufferSnapshot` / `_bounds` / `_boundsSync` / `_hwnd` / `_lastLineSegs` / `_lastFrame` / `_clientToScreenX` / `_clientToScreenY` / `_metrics` / `_wrapColumns` / `_caret` / `_anchor` / `_hasFocus` / `_buffer` / `_uiaTextChangedCount` / `_uiaSelectionChangedCount` / `_uiaFocusChangedCount` / `_testHook_LastGetObjectServed`

**行数目安**: ~400(上位設計 350 見積もりから 50 上振れ)

### B.5 Task 2e: `EditorControl.Paint.cs`

**移動対象メソッド**

- `OnPaint(PaintEventArgs)` (`:2653`)
- `RenderFrame(Graphics, Frame)` (`:2787`)
- `MeasureLineNumberWidth(int)` (`:2814`)
- `ToColor(PaintColor)` (`:2820`)
- `DefaultStyle()` (`:2823`)
- `BuildStyle(AppearanceTheme, bool)` (`:2929`)
- `BlendRgb(int, int, double)` (`:2948`)
- `FromRgb(int)` (`:2958`)
- `DrawImeOverlay(Graphics)` (`:2729`) — §C.5

**残置(本体側)**

- `ApplyAppearance(AppSettings)` (`:2850`) — 設定反映の受口は本体(§C.4)
- `_font` / `_underlineFontCache` / `_targetFontCache` / `_metrics` / `_style` の宣言 (`:24-32`)

**行数目安**: ~250

### B.6 分割後の本体 EditorControl.cs に残るもの

- 全 field 宣言(`:24-142` の 20 個以上)
- ctor (`:144-179`)
- `SetSource` / `ReplaceSource` / `SetOrReplaceSource` (`:188-321`)
- `ConvertEols` + helpers (`EmitEol` / `FlushBuf` / `IsEolAlreadyUniform` / `CountNonBreakAndBreaksInSnapshot`) (`:362-590`)
- `ReplaceCharRange` (`:877`)
- `SnapshotText` プロパティ (`:284`) / `LineCount` / `CurrentPosition` / `LineHeightPx` などの基本 read プロパティ
- `TopLine` / `WrapColumns` / `ScrollX` / `ShowLineNumbers` / `ShowWhitespace` / `HighlightCurrentLine` / `Overtype` / `ReadOnly` / `RaiseUiaSelectionEvents` プロパティ (`:598-811`)
- `HighlightCharRange` / `ClearHighlight` (`:709-741`)
- `ClampTopLine` / `UpdateVerticalScrollbar` / `UpdateHorizontalScrollbar` / `HideAndResetHScroll` / `ClampScrollX` (`:1027-1147`)
- `AfterEdit` (`:1148`)
- `Undo` / `Redo` / `SetSavePoint` / `ClearSavePoint` (`:1195-1273`)
- `Copy` / `Cut` / `Paste` / `SelectAll` (`:1275-1350`)
- `GetText` internal (`:1349`)
- `OnResize` / `OnGotFocus` / `OnLostFocus` (`:1351-1413`)
- `WndProc` (`:1424`)
- `OnSizeChanged` / `OnLocationChanged` (`:2996-3006`)
- `ApplyAppearance` (`:2850`)
- `PositionCaret` (`:1836`)
- `Dispose` (`:3384`)

**予想行数**: 3396 → **~1000**(上位設計目安・±100 の誤差許容)。

## C. 分割規約

### C.1 field 所有権

**規約: 全フィールドは EditorControl.cs 本体で宣言し、partial 側から直接参照する**

- Phase 2 では field の物理位置を動かさない。partial 間でフィールドが所有権を持つ錯覚を作らない。
- Phase 3 の Controller 抽出で field ごとに所有権を移す(例: `_desiredXpx` → CaretController、`_ime` → ImeController、`_bufferSnapshot`/`_bounds` → UiaTextHostAdapter)。Phase 2 で field を動かすと Phase 3 の差分が二重になる。
- テストフック用の `_testHook_*` / カウンタ `_uiaTextChangedCount` 等も本体側で宣言(例外なし)。

**将来 Controller への移譲予定を示す 1 行コメント**をフィールド宣言に添える。例:

```csharp
// [Phase 3-3a: ImeController へ移譲予定] IME 未確定文字列の状態。
private ImeCompositionState _ime = ImeCompositionState.Empty;
```

### C.2 partial ファイル命名

**規約: `EditorControl.<Aspect>.cs`** — Aspect は Pascal case・上位設計書表記に合わせて `Ime` / `Caret` / `Input` / `Uia` / `Paint` の 5 種。

- `.partial.cs` サフィックスは付けない(C# 言語仕様上 `partial` 修飾子で十分)。
- 各 partial ファイル冒頭に責務と Phase 3 の後継を書いた header コメント:

```csharp
// EditorControl.Ime.cs
// Phase 2 (Task 2a) で切り出した IME 分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3-3a で ImeController へロジック移譲予定。
namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // WM_IME_* 受信からの状態機械 + IME 描画補助
    ...
}
```

- クラス宣言は `public sealed partial class EditorControl` に統一(5 partial 全てで `sealed` 明示)。
- using は per-file スコープ。本体の using を継承しないので、各 partial に必要な using だけ書く。

### C.3 partial 間で参照される private helper の帰属

**規約: helper は「呼び出し元が最も多い partial」に置く。cross-partial 呼び出しは同一クラスのため private でも見える。**

具体判断:

- `PositionCaret()` — IME・Caret 両方から呼ばれる。**本体側**(§C.4)
- `TryFindVisualSegment` / `TryFindVisualSegmentCore` — UIA からのみ。**Uia 側**
- `WordBoundary_WordStart` / `WordBoundary_WordEnd` — UIA からのみ。**Uia 側**
- `PrevWordBoundary` / `NextWordBoundary` — Input からのみ。**Input 側**
- `SegmentCountAtLine` — Input からのみ。**Input 側**
- `ReadImeString/Bytes/Int` — IME からのみ。**Ime 側**
- `EmitEol` / `FlushBuf` / `IsEolAlreadyUniform` / `CountNonBreakAndBreaksInSnapshot` — `ConvertEols` からのみ。ConvertEols は本体残置なので **本体側**

### C.4 跨り関数の判断ルール

**規約: 「複数の Aspect から呼ばれる」+「編集/状態同期の中核」は本体側に残す。**

該当:

- `AfterEdit()` — 編集後同期(Undo/Redo/insert/delete 全経路の末尾)。UIA / Scroll / IME / Modified / Caret を全部触る。**本体側**
- `WndProc` — Message pump そのもの。case は薄いラッパで partial 側の実装を呼ぶ。**本体側**
- `OnGotFocus` / `OnLostFocus` / `OnResize` / `OnSizeChanged` / `OnLocationChanged` / `OnHandleCreated` / `OnHandleDestroyed` — Control ライフサイクルは全て本体側。中身の実装は partial 側の helper を呼ぶ(例: `OnSizeChanged` は `UpdateBoundsCache()` を呼ぶだけ・実装は Uia 側)
- `PositionCaret` — Caret 位置決定は Caret 側の候補だが、IME からも呼ばれる。**本体側**
- `ApplyAppearance` — Font / metrics / style / caret width を横断で再構築。**本体側**
- `HighlightCharRange` / `ClearHighlight` — セル装飾は編集/選択の枠外。**本体側**

### C.5 DrawImeOverlay の所属

**判断: `DrawImeOverlay(Graphics)` は `EditorControl.Paint.cs` (2e) に置く。**

理由:

- 呼び出し元は `OnPaint` → `RenderFrame` の描画チェーン(Paint の一部)。IME 状態(`_ime`)を読むだけの純描画関数。
- IME の状態機械(2a)から呼ばれない = 出力側の描画コード。
- 上位設計書 §3 は「2a で暫定・2e で最終位置決定」としていたが、実コードでは呼び出し関係が Paint 一方向のため、**最初から Paint に置く**方針で確定。
- 2a IME 側は `_ime` を更新するだけで描画は Paint 側に完全に委ねる。この責務分離は Phase 3 でも維持される(ImeController は state を提供・Paint は render するだけ)。

### C.6 テストフックの扱い

**規約: internal テストフックはそれが観測する状態と同じ partial に置く。**

- `TestHook_WndProc` / `TestHook_LastGetObjectServed` / `TestHook_ForceUiaListen` / `TestHook_ResetUiaEventCounts` / `TestHook_UiaEventCounts` / `TestHook_GetLastFrame` → **Uia**
- `TestHook_LastLineSegsHitCount` / `TestHook_LastLineSegsMissCount` / `TestHook_ResetLastLineSegsCounters` → **Uia**(`_lastLineSegs` は UIA 側の TryFindVisualSegment 用キャッシュ)
- `__TestApplyComposition` / `__TestImeStart` / `__TestIsComposing` / `__TestImeText` / `__SmokeIsComposing` / `__SmokeImeText` → **Ime**

これらのフィールド宣言(`_uiaTextChangedCount` 等)は §C.1 に従い本体側に残置。partial 側は method のみ移動。

## D. テスト戦略・運用・リスク・DoD

### D.1 テスト戦略

**規約: 追加テストなし・既存テスト移動なし。partial 統合後の 1041 tests 緑継続のみが DoD。**

- 契約テストは追加しない(user 決定・§Non-goals)。
- 既存テストファイル(`EditorControlImeTests.cs` など)は EditorControl(統合後の同一クラス)を叩くので、partial 分割は透過。
- 各サブ完了時のテスト実行 = `tools/pre-merge-check.ps1`(Release build 0 warnings + 全 1041 tests 緑)。サブごとの局所実行(例: `dotnet test --filter Ime`)は開発中の速度優先の補助のみ。

### D.2 挙動不変の確認方法

各サブごとに以下 4 点を機械/目視でチェック:

1. **ビルド 0 warnings**: `dotnet build -c Release`
2. **全テスト緑**: `tools/pre-merge-check.ps1` = 1041 tests (Core 580 + Editor 218 + App 243)
3. **git diff 検算**: `git diff --stat main` で `EditorControl.cs` の削減行数 ≒ 新 partial の追加行数(±10 行の誤差は許容)
4. **git diff -U0 spot-check**: 移動先の method 本体が完全一致することを差分で目視確認(reviewer 用)

### D.3 運用

| 項目 | 方針 |
|---|---|
| ブランチ | `feature/refactor-2a-editorcontrol-ime` 等(サブごと・上位設計書 §6 準拠) |
| 実装単位 | 1 サブ = 1 branch = 1 no-ff マージ。5 サブ = 5 マージコミット |
| 実装方式 | Subagent-Driven Development(SDD)。Implementer subagent → Spec reviewer → Code quality reviewer (`superpowers:code-reviewer`) → 必要なら fixup コミット |
| レビュー観点 | 「移動前後で method 本体が完全一致」+「using が最小」+「フィールド所有権を動かしていない」+「跨り関数の帰属が §C.4 準拠」 |
| ローカルゲート | `tools/pre-merge-check.ps1` を main マージ前必須 |
| L5 実機検証 | Phase 2 全 5 サブは **不要**(SR 経路不変) |
| Task 1b L5 | Phase 2 と並行して user 担当。Phase 2 の完了/マージのブロッカーではない |
| MEMORY 更新 | Phase 2 全 5 サブ完了時に一括で `refactor-separation-phase2-complete.md` を新規作成 |
| origin/main push | 現在 398 commits 先行。Phase 2 完了後のまとめて push を推奨(user 判断) |

### D.4 リスクと緩和策

| リスク | 影響 | 緩和策 |
|---|---|---|
| method 移動中に不注意で本体を書き換え | 挙動差異 | 各サブの PR で「method 本体 diff = 0」を reviewer が確認。git 上は「削除+追加」の 2 hunk が期待通り |
| partial 側で必要な using を書き忘れ | compile error | ローカルビルドで即発覚。SDD の implementer が catch |
| WndProc の case が partial 側に漏れた | Message pump 崩壊 = IME/UIA 崩壊 | §C.4 で「WndProc は本体側」と規約化。`EditorControlImeTests` / `EditorControlUiaGetObjectTests` が守る |
| DrawImeOverlay が Paint 移動時に `_ime` フィールドを取り違え | IME overlay 消失 | §C.5 で Paint 帰属を確定。§C.1 で field は本体側に残置=partial 側からは同一クラス private として直接見える |
| テストフックの partial 移動で InternalsVisibleTo が壊れる | Editor.Tests 破壊 | InternalsVisibleTo は `yEdit.Editor.csproj` の assembly attribute で partial ファイル位置とは無関係=影響なし |
| サブ間で作業並行(2 サブ同時進行)によるコンフリクト | マージ困難 | 5 サブは順次実行(2a→2b→…)。並行禁止 |
| Phase 2 期間中に別バグ修正が入る | main と同期困難 | 各サブを 1〜2 日で終わらせる(短命ブランチ) |

### D.5 サブ Phase ごとの DoD

**各サブ (2a〜2e) 共通:**

- [ ] `EditorControl.<Aspect>.cs` を新規作成し、規約 §C.2 の header コメントあり
- [ ] 対象 method 群が本体から新 partial へ移動(削除+追加)
- [ ] 本体側 `EditorControl.cs` の行数が対象 method 分だけ減少
- [ ] `dotnet build -c Release` 0 warnings
- [ ] `tools/pre-merge-check.ps1` 全 1041 tests 緑
- [ ] 別エージェント review「マージ可」
- [ ] main へ `git merge --no-ff` でマージ

**Phase 2 全体完了時:**

- [ ] Task 2a〜2e 全 5 サブ main マージ済
- [ ] `EditorControl.cs` 3396 → **~1000 行**(±100 の誤差許容)
- [ ] `src/yEdit.Editor/` に partial 5 ファイルが存在
- [ ] `tools/pre-merge-check.ps1` 全緑・0 警告・1041 tests(挙動不変)
- [ ] MEMORY.md に `refactor-separation-phase2-complete.md` を追加

### D.6 次アクション

本設計書承認後:

1. `docs/plans/2026-07-16-refactor-editorcontrol-partial-design.md` として保存(本ドキュメント)
2. 設計書を git commit(単独コミット・main 直コミット= Phase 1 と同形)
3. `superpowers:writing-plans` skill を起動 → 実装計画 `docs/plans/2026-07-16-refactor-editorcontrol-partial.md` を作成
4. 実装は別セッションで(SDD で 2a〜2e を順次)

## E. 参照

- 上位設計書: `docs/plans/2026-07-16-refactor-separation-of-concerns-design.md`
- Phase 1 実装計画: `docs/plans/2026-07-16-refactor-separation-of-concerns.md`
- Phase 1 完了記録: [[refactor-separation-phase1-complete]]
- git flow: [[phase-work-git-flow]]
- レビュー運用: [[review-by-separate-agent]]
