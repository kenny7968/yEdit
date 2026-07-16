# 責務分離リファクタリング Phase 3 設計書 (EditorControl Controller 委譲)

- 日付: 2026-07-17
- 発端: Phase 1 (中小 5 項目) + Phase 2 (EditorControl partial 分割) 完了を受けての本命
- 上位設計: `docs/plans/2026-07-16-refactor-separation-of-concerns-design.md` §4 の骨格を Phase 2 の申し送りで補強した Phase 3 単独の詳細設計
- 前提: main (`5794066` Task 2e マージ + `5f41e36` M-1 fixup 時点) ・1041 tests 全緑・0 warnings
- Phase 2 完了記録: `[[refactor-separation-phase2-complete]]`

## 0. スコープと方針

Phase 2 で partial 分割された EditorControl の各責務を、**独立クラスへの委譲 (delegation)** で完全に責務移譲する。Controller 4 個 (`ImeController` / `CaretController` / `InputRouter` / `UiaTextHostAdapter`) を新規追加し、field 所有権を Controller 側へ完全移動する。partial ファイル 5 個は残置しつつ、その内部は Controller への薄いディスパッチだけになる。

### 対象サブ Phase (4 個)

| サブ | 対象 | 状態所有 field | L5 |
|---|---|---|---|
| 3b | CaretController | `_caret`, `_anchor`, `_desiredXpx` | 不要 |
| 3c | InputRouter | (状態なし・pure dispatcher) | 不要 |
| 3a | ImeController | `_ime` + `IImeContext` seam | 必要 |
| 3d | UiaTextHostAdapter | 12 個の Uia 系 field (§2.4 参照) | 必要 |

### 全体方針

- **挙動不変 (shape-preserving)** を絶対条件。SR 発声文言・キャレット位置・IME 挙動・UIA 通知回数を 1 bit も変えない。
- **段階マージ** ([[phase-work-git-flow]]) + **別エージェント review** ([[review-by-separate-agent]]) を全サブで遵守。
- **ローカルゲート** `tools/pre-merge-check.ps1` を main マージ前必須。
- **L5 実機検証** (NVDA/PC-Talker) は 3a と 3d の 2 回。
- **ロールバック粒度**はサブ Phase 単位 (独立ブランチ)。
- **git-truth 計測 (`wc -l`) を正**とし、PowerShell の `Get-Content | Measure-Object -Line` は使わない (Phase 2 で 411 行差が判明した教訓)。

### 実装順 (依存基盤先行 + L5 分散)

**3b → 3c → 3a → 3d** の順で直列。

- 3b (CaretController) が基盤 = 他 3 個から caret 位置を read/write されるため最初に抽出する。
- 3d (UiaTextHostAdapter) は最重 (field 12 個・IUiaTextHost 22 メンバ・_bounds sync) = 最後に回す。
- L5 実機検証は 3a と 3d の間に間隔が開くので、回帰検知しやすい。

## 1. アーキテクチャ (Phase 3 完了時の Editor 層クラス構成)

```
yEdit.Editor/
├── EditorControl.cs              (600〜800 行想定) Control ライフサイクル + Buffer 配線 + Controller 4 個 ctor 組み立て
├── EditorControl.Ime.cs          (~50 行) WndProc WM_IME_* 分岐 → _ime.OnXxx() ディスパッチ
├── EditorControl.Caret.cs        (~80 行) 外部公開 caret API → _caretCtrl.Xxx() ラッパ
├── EditorControl.Input.cs        (~80 行) OnKey*/OnMouse* オーバーライド → _input.Route() ディスパッチ
├── EditorControl.Uia.cs          (~80 行) WM_GETOBJECT 分岐 + Adapter 生成
├── EditorControl.Paint.cs        (~236 行 = 現状維持) OnPaint + RenderFrame (Phase 3 対象外・§C.5)
├── ImeController.cs              (~500 行) IME 状態機械 + IImeContext 経由の Message pump
├── CaretController.cs            (~400 行) Caret/Anchor/DesiredXpx 所有 + navigation ロジック
├── InputRouter.cs                (~500 行) Keys→Action dictionary + マウス dispatch
├── UiaTextHostAdapter.cs         (~450 行) IUiaTextHost 実装 + snapshot/bounds/lastLineSegs キャッシュ + Automation Event 発火
├── Abstractions/IImeContext.cs   (新規) Imm32 P/Invoke seam (3a のみ)
├── GdiCharMetrics.cs             (現状維持)
└── NativeMethods.cs              (現状維持)
```

### 依存方向 (単方向・循環禁止)

```
EditorControl (owner)
  ├─ CaretController          [基盤・他 3 個から read/write]
  ├─ InputRouter              → CaretController (caret 移動要求時)
  ├─ ImeController            → CaretController (composition attach 位置 read)
  └─ UiaTextHostAdapter       → CaretController (selection 状態 read)
                              → TextBuffer/TextSnapshot (直接参照)
```

### 所有権原則

- 各 Controller は自身の state field を**完全所有** (EditorControl から field 移動)。
- EditorControl の public API 契約は薄いラッパで維持 (`SetCaretCharOffset` → `_caretCtrl.SetTo(pos, snap)` のような 1 行転送)。
- 外部 (Editor.Tests / MainForm) から見た public 表面は変わらない。

### サイズ目標 (git-truth)

| 対象 | 現状 | Phase 3 完了時目標 |
|---|---:|---:|
| EditorControl.cs | 1537 | 600〜800 |
| .Ime.cs | 325 | ~50 |
| .Caret.cs | 326 | ~80 |
| .Input.cs | 608 | ~80 |
| .Uia.cs | 438 | ~80 |
| .Paint.cs | 236 | 236 |
| ImeController.cs | 新規 | ~500 |
| CaretController.cs | 新規 | ~400 |
| InputRouter.cs | 新規 | ~500 |
| UiaTextHostAdapter.cs | 新規 | ~450 |
| **Editor 層 total** | **3470** | **~2650** (-24%) |

**代替 DoD (数値ずれ許容)**: 「削減行数 ≒ Controller 追加行数 ±10%」を機械的検証。目標値ずれても本 DoD が真なら受理。

## 2. コンポーネント境界 (各 Controller の API と field 移譲リスト)

### 2.1 CaretController (3b・基盤)

**所有 field** (EditorControl → CaretController に移動): `_caret`, `_anchor`, `_desiredXpx`
**残置 field** (Paint 責務): `_caretWidthPx` は描画側で扱うため EditorControl.Paint.cs へ

```csharp
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

    public void SetTo(int pos, TextSnapshot snap);
    public void MoveTo(int newPos, bool extend, TextSnapshot snap);
    public void SetSelection(int anchor, int caret, TextSnapshot snap);
    public int SnapAndClamp(int offset, TextSnapshot snap);
    public void ClearSelection();
}
```

`ICharMetrics` は既存の `GdiCharMetrics` 直参照のまま (Fake seam を作らない・Q4 決定)。座標計算 (`ComputeCaretPoint`) は EditorControl.Caret.cs 側の薄いラッパで `_metrics` を渡す形。

### 2.2 InputRouter (3c・state なし)

**所有 field**: なし (pure dispatcher)
**依存**: CaretController, TextBuffer, EditorControl (clipboard/scroll 等 host callback)

```csharp
internal sealed class InputRouter
{
    private readonly IReadOnlyDictionary<Keys, Action<InputContext>> _keyMap;
    private readonly EditorControl _host;

    public bool Route(KeyEventArgs e);
    public bool Route(MouseEventArgs e, MouseEventKind kind, Point clientPoint);
}
```

`_keyMap` は `CsvCommands.ByKey` パターン踏襲。Modifier (Ctrl/Shift/Alt) は `Keys` の bitwise 組み合わせでキー化。`WordBoundary`/`NavigationCommands` などの純粋関数は既存のまま InputRouter から call。

### 2.3 ImeController (3a・L5 必要)

**所有 field** (EditorControl → ImeController に移動): `_ime` (ImeCompositionState)
**seam**: `IImeContext` (**Phase 3 唯一の新規 seam**)

```csharp
public interface IImeContext : IDisposable
{
    string? GetCompositionString(long gcsFlags);
    void SetCandidateWindow(int x, int y);
    void SetCompositionFont(Font font);
    void CancelComposition();
    // 実装: WinImeContext (Imm32 P/Invoke) と FakeImeContext (テスト)
}

internal sealed class ImeController
{
    private ImeCompositionState _ime = ImeCompositionState.Empty;
    private readonly Func<IImeContext> _contextFactory;
    private readonly CaretController _caretCtrl;
    private readonly IImeOverlayHost _overlayHost;

    public bool IsActive => _ime.IsComposing;
    public void OnStartComposition();
    public void OnComposition(long gcsFlags);
    public void OnEndComposition();
    public void OnSetContext(ref Message m);
    public void Cancel();
    public void Draw(Graphics g);   // Paint.cs から呼ばれる (DrawImeOverlay の移設)
}
```

`WinImeContext` は現行 `ReadImeString/Bytes/Int` の Imm32 P/Invoke をラップ。`FakeImeContext` は Editor.Tests で状態遷移 (GCS_COMPSTR / GCS_RESULTSTR / GCS_CURSORPOS) を driven。

### 2.4 UiaTextHostAdapter (3d・L5 必要・最重)

**所有 field** (EditorControl → UiaTextHostAdapter に移動・**12 個フル**):

| # | field | 型 | 用途 |
|---:|---|---|---|
| 1 | `_bufferSnapshot` | `volatile TextSnapshot?` | RPC スレッド安全 snapshot |
| 2 | `_boundsSync` | `readonly object` | `_bounds` ロック |
| 3 | `_bounds` | `System.Windows.Rect` | UIA 座標 |
| 4 | `_clientToScreenX` | `int` | client→screen offset |
| 5 | `_clientToScreenY` | `int` | 同上 |
| 6 | `_lastLineSegs` | `(TextSnapshot, int, IReadOnlyList<WrapSegment>)?` | 論理行 segs キャッシュ (P8 Minor-5) |
| 7 | `_hwnd` | `nint` | RPC スレッド安全 hwnd |
| 8 | `_provider` | `TextControlProviderV2?` | Provider インスタンス |
| 9 | `_testHook_LastGetObjectServed` | `bool` | Task 6 test hook |
| 10 | `_uiaTextChangedCount` | `int` | notification カウンタ |
| 11 | `_uiaSelectionChangedCount` | `int` | 同上 |
| 12 | `_uiaFocusChangedCount` | `int` | 同上 |

**EditorControl → Adapter への通知経路** (4 イベント):

```csharp
internal sealed class UiaTextHostAdapter : IUiaTextHost
{
    // 全 22 個の IUiaTextHost 実装 + UpdateBoundsCache/CacheSnapshot

    public void OnSnapshotChanged(TextSnapshot newSnap);     // AfterEdit/SetSource/ReplaceSource から
    public void OnBoundsChanged(nint hwnd, ClientRect r);    // OnSizeChanged/OnLocationChanged/OnHandleCreated から
    public void OnHandleCreated(nint hwnd);                  // OnHandleCreated から
    public void OnHandleDestroyed();                         // OnHandleDestroyed から
    public void RaiseTextChanged();                          // AfterEdit から
    public void RaiseSelectionChanged();                     // Caret 移動時
    public void RaiseFocusChanged(bool focused);             // Focus 変化時
}
```

**EditorControl 側の変化**:
- `OnHandleCreated`/`OnHandleDestroyed`/`OnSizeChanged`/`OnLocationChanged` は EditorControl 本体に統一 (§C.4 例外解消) し、各々の中で `base.OnXxx(e); _uia.OnHandleCreated(Handle);` の 2〜3 行だけ。
- `EnsureUiaProvider()` helper は Adapter 内へ移動 (WM_GETOBJECT 分岐は WndProc 本体側残置・§C.4 準拠)。

### 2.5 境界事例判断 (Phase 2 流儀継続)

以下 3 件は「plan 明示外だが論理帰属で判断・reviewer に境界確認依頼」姿勢を継続:

1. `_caretWidthPx` → 描画属性なので Paint 側残置 (Caret ではない)
2. `_provider` → Adapter 所有 (EnsureUiaProvider も Adapter method 化)
3. `_lastLineSegs` 破棄ポイント (EditorControl.cs 現 6 箇所) → EditorControl 側から `_uia.OnSnapshotChanged()` を呼ぶ形に集約、Adapter が破棄

## 3. データフロー (主要 6 経路)

### 3.1 編集フロー (AfterEdit・全 Controller の同期点)

```
User edit (Input/Ime) → TextBuffer.Edit(...)
                     → EditorControl.AfterEdit(newSnap)
                          ├─ _caretCtrl.SetTo(clampedPos, newSnap)        // caret 位置を新 snap に再射影
                          ├─ _uia.OnSnapshotChanged(newSnap)               // Adapter 側で _bufferSnapshot 差替 + _lastLineSegs 破棄
                          ├─ _uia.RaiseTextChanged()                       // UIA notification
                          └─ Invalidate()                                   // 再描画要求
```

`AfterEdit` は EditorControl 本体側に残す (4 個の Controller を同期する唯一の join point)。

### 3.2 キー入力フロー (3c 主導)

```
WinForms → EditorControl.OnKeyDown(e)   [EditorControl.Input.cs の 5 行ラッパ]
        → _input.Route(e)
             ├─ ByKey.TryGetValue(e.KeyData, out var handler) ? handler(ctx) : false
             ├─ Ctrl+End → ctx.MoveCaret(text.Length, extend: false)
             ├─ Shift+Right → ctx.MoveCaret(ctx.Caret+1, extend: true)
             └─ Ctrl+X → ctx.CutSelection()
        (all handlers) → _caretCtrl.MoveTo/SetSelection or TextBuffer.Edit → AfterEdit
```

`InputContext` は `{ CaretController Caret; TextBuffer Buffer; EditorControl Host }` の value-record。keymap は静的 dictionary。

### 3.3 IME 入力フロー (3a 主導・L5)

```
Win32 → EditorControl.WndProc(ref m)   [EditorControl.Ime.cs 側の分岐]
     ├─ WM_IME_STARTCOMPOSITION → _ime.OnStartComposition()
     ├─ WM_IME_COMPOSITION      → _ime.OnComposition(m.LParam.ToInt64())
     ├─ WM_IME_ENDCOMPOSITION   → _ime.OnEndComposition()
     └─ WM_IME_SETCONTEXT       → _ime.OnSetContext(ref m)

_ime.OnComposition:
     ├─ IImeContext.GetCompositionString(GCS_RESULTSTR)   // 確定 → InsertConfirmedText
     │       → _host.InsertConfirmedText(str) → TextBuffer.Edit → AfterEdit
     └─ IImeContext.GetCompositionString(GCS_COMPSTR)     // 未確定 → _ime = new ImeCompositionState(...)
             → Invalidate() (overlay 再描画)
```

`_host.InsertConfirmedText` は EditorControl 側の internal method。Controller が host callback 経由で TextBuffer を触るのはこの一点のみ。

### 3.4 UIA read フロー (3d・RPC スレッド)

```
UIA RPC thread → TextControlProviderV2.GetSelection() etc.
              → UiaTextHostAdapter.GetSelection()
                   → volatile read _bufferSnapshot (RPC 安全)
                   → lock (_boundsSync) read _bounds
                   → CaretController.Selection を read       [!] 現状挙動と同じ torn read リスク・§5.10 で backlog
                   → 応答
```

**Caret read の thread-safety**: 現状 EditorControl 側で `_caret`/`_anchor` を直接 read しており、UIA RPC スレッドから見た torn read リスクは既に存在。Phase 3 で **CaretController.Selection の read パスを既存挙動不変**に留める (Interlocked 化などは非目標・§5.10)。将来課題として backlog。

### 3.5 UIA notification フロー

```
UI thread → _uia.RaiseTextChanged/SelectionChanged/FocusChanged()
         → Adapter が RaiseAutomationEvent(_provider, ...) を UI スレッドで直接 call
         → _uiaXxxChangedCount++   (test hook)
```

`_provider` が null (まだ WM_GETOBJECT が来ていない) なら no-op。既存挙動維持。

### 3.6 Bounds/Handle ライフサイクルフロー (§C.4 例外解消)

```
EditorControl 本体側に統一:
    OnHandleCreated  → base; _uia.OnHandleCreated(Handle);
    OnHandleDestroyed → base; _uia.OnHandleDestroyed();
    OnSizeChanged    → base; _uia.OnBoundsChanged(Handle, ClientRectangle);
    OnLocationChanged → base; _uia.OnBoundsChanged(Handle, ClientRectangle);
```

Adapter 内部で `RectangleToScreen`/`PointToScreen` を呼ぶために EditorControl 参照が必要 = `_uia = new UiaTextHostAdapter(this)` (EditorControl を渡す)。Adapter は host callback で screen 変換を要求。

## 4. エラー処理と挙動不変原則

### 4.1 挙動不変の絶対条件 (Phase 1/2 継続)

以下 observable behavior は Phase 3 通じて **1 bit も変えない**:

- SR 発声文言 (NVDA/PC-Talker が読む文字列・音声)
- キャレット位置 (edit 後・キー移動後・IME 確定後の全経路)
- IME 変換中/確定時の overlay 描画位置と文言
- UIA notification の発火回数 (TextChanged/SelectionChanged/FocusChanged)
- ファイル保存挙動 (BOM/エンコーディング/EOL)
- Undo/Redo スタック内容と操作粒度

**担保方法**:
- 既存 `Editor.Tests` 218 テスト全緑継続 (各サブで pre-merge-check 通過)
- Phase 2 で確立された「移動元 method と移動先 method は bit-perfect 対称」の姿勢を委譲でも継続 (**メソッドロジックの中身を書き換えない**・field 参照を Controller 経由に付け替えるだけ)
- L5 実機検証 (3a と 3d の 2 回) で NVDA/PC-Talker の発声・フォーカス追従が変わらないことを確認

### 4.2 各 Controller のエラー処理

**CaretController (3b)**:
- `SetTo/MoveTo/SetSelection` は不正 offset を受けたら `SnapAndClamp` で `[0, snap.CharCount]` に clamp (現状 `SnapAndClamp` の挙動をそのまま移設)
- ctor は非 null 引数を Assert (`ArgumentNullException.ThrowIfNull`)

**InputRouter (3c)**:
- keymap miss は `return false` (親クラス `Control` へ pass-through) = 既存 `OnKeyDown` 末尾の `base.OnKeyDown(e)` 挙動維持
- host callback が例外時は catch せず伝播 (現状も catch していない)

**ImeController (3a)**:
- `IImeContext.GetCompositionString` が null 返却 = composition 破棄・no-op (現状 `ReadImeString` が空文字返す挙動を IImeContext 実装内に閉じる)
- `WinImeContext.Dispose()` で `ImmReleaseContext` を必ず呼ぶ (現状 try/finally の契約を維持)
- P/Invoke 失敗時の Marshal.GetLastWin32Error は既存挙動と同じく無視 (現状 `ReadImeString` に error check なし)

**UiaTextHostAdapter (3d)**:
- RPC スレッドから呼ばれる 22 メンバは**全て snapshot/lock 越し**で応答 (現状の契約継続)
- `_hwnd == IntPtr.Zero` (未生成) なら座標 API は `RectangleF.Empty` 返却 (現状挙動)
- `_provider == null` (WM_GETOBJECT 未受信) なら RaiseXxx 系は no-op (現状挙動)
- `OnSnapshotChanged` は volatile write のみ = ロック不要 (現状 `CacheSnapshot` の契約継続)

### 4.3 IImeContext seam のエラー処理契約

**WinImeContext** (本番):
- `ImmGetContext(_hwnd)` が 0 = IME 無効 → `GetCompositionString` は null を返す (no-op)
- `ImmReleaseContext` は `Dispose` で必ず呼ぶ

**FakeImeContext** (テスト):
- テスト側が事前設定した compStr/resultStr/cursorPos をそのまま返す
- P/Invoke 失敗の再現は「null 返却」で表現 (実装差異を吸収)

seam 契約は「null = P/Invoke 失敗 or IME 無効」の 1 パターンに集約。ImeController 側は null チェックだけで両ケースを吸収 = 既存の防御ロジックと等価。

### 4.4 挙動不変を機械固定する追加テスト (各サブで最小 1 件)

Phase 1 の reflection 契約テスト流儀を継続:

| サブ | 追加テスト | 目的 |
|---|---|---|
| 3b | `CaretController_Fields_AreOwned` = `_caret`/`_anchor`/`_desiredXpx` が EditorControl 側にないことを reflection で assert | 所有権完全移譲を機械固定 |
| 3c | `EditorControl_OnKeyDown_DelegatesToInputRouter` = OnKeyDown が InputRouter を経由することを reflection で assert (`_input` field の存在で足りる) | ラッパ化を機械固定 |
| 3a | `ImeController_UsesIImeContext` = IImeContext を ctor で受けることを reflection で assert + FakeImeContext による GCS_RESULTSTR/GCS_COMPSTR 分岐テスト 4 件 | seam 経由を機械固定 |
| 3d | `UiaTextHostAdapter_OwnsAllUiaFields` = 12 個の field が EditorControl 側にないことを reflection で assert | 所有権完全移譲を機械固定 |

これらは「戻し忘れ」検出用の低コストゲート。実際のロジックテストは既存 218 テストで被覆する。

## 5. テスト方針・運用・DoD

### 5.1 SDD プロセス (Phase 1/2 継続・4 段)

各サブ = 1 独立ブランチ = 1 no-ff マージ = 直列 (並行禁止):

```
1. Implementer subagent (TDD 厳守: 失敗テスト → 実装 → 緑 → refactor)
2. Spec reviewer subagent (仕様一致・plan step 明示外の境界事例判断は「移動側に振り境界確認依頼」姿勢)
3. Code quality reviewer subagent (superpowers:code-reviewer)
4. Fixup が必要なら SendMessage で implementer に再指示 (--amend 禁止・新規コミット)
```

境界事例 (field 帰属・method 移設先) は**移動側に振り reviewer に境界確認依頼**姿勢を継続 (Phase 2 で全 5 サブ fixup 不要だった前例)。

### 5.2 Fake seam (1 個のみ新規)

- `IImeContext` (3a のみ) = `WinImeContext` (本番) + `FakeImeContext` (テスト)
- 他 3 サブは既存 API (TextSnapshot / KeyEventArgs / IUiaTextHost) で Fake 化

### 5.3 flaky test 対策 (Phase 2 で認識)

`MouseInputTests.MouseDown_LeftButton_ClearsSelection` と `ClipboardTests.Copy_NoSelection_NoOp` は dotnet 残プロセス蓄積で fail 再現。Phase 3 でも遭遇の可能性あり。**回復手段**: `taskkill /F /IM dotnet.exe` を各サブ着手前に実施 (implementer への指示に含める)。Phase 3 で根治しない (既存問題・Phase 3 スコープ外)。

### 5.4 数値目標 (git-truth = `wc -l` 準拠)

Phase 2 で PS 計測差 411 行が判明した教訓を反映し、**PowerShell の Get-Content 計測は使わない**。全数値目標は `wc -l` で計測。詳細な数値目標は §1 の表を参照。

### 5.5 L5 実機検証 (2 回)

| サブ | L5 タイミング | 確認項目 |
|---|---|---|
| 3a | main マージ前 | NVDA/PC-Talker で IME 変換読み・確定読み・overlay 描画位置が不変 |
| 3d | main マージ前 | NVDA/PC-Talker で読み・キャレット移動読み・UIA 通知の 3 経路が不変 |

3b/3c は SR 経路不変 (内部委譲のみ) = L5 不要。

### 5.6 ローカルゲート

各サブ main マージ前必須:
- `tools/pre-merge-check.ps1` 緑
- Release 0 warning
- 該当サブの reflection 契約テスト (§4.4) 緑
- Editor.Tests 218 個緑継続

### 5.7 ブランチ/マージ運用 (既存流儀)

- `feature/refactor-3b-caret-controller`
- `feature/refactor-3c-input-router`
- `feature/refactor-3a-ime-controller`
- `feature/refactor-3d-uia-adapter`

の順で 4 ブランチ・直列 no-ff マージ。各サブ完了時に MEMORY.md 更新 (全 4 サブ完了時に一括でも可)。

### 5.8 テスト数遷移想定

Phase 2 完了時点 = **1041** (Core 580 + Editor 218 + App 243)

| サブ | 追加テスト | 累計 |
|---:|---|---:|
| 3b | reflection 契約 +1 + CaretController pure test 6〜8 | ~1048 |
| 3c | reflection 契約 +1 + InputRouter keymap 4〜6 | ~1054 |
| 3a | reflection 契約 +1 + FakeImeContext driven 6〜8 + WinImeContext smoke 2 | ~1064 |
| 3d | reflection 契約 +1 + UiaTextHostAdapter test 4〜6 | ~1070 |

**Phase 3 完了時想定 = ~1070** (+30 前後・幅は境界事例吸収)。実測は各サブで確認・plan 側の数値誤記の轍を踏まない。

### 5.9 DoD (Phase 3 完了時)

**サブごと**:
- Editor.Tests 全緑 + 該当 reflection 契約テスト緑
- Release 0 warning
- 別エージェント review「マージ可」
- L5 該当サブ (3a/3d) のみ実機検証 OK
- pre-merge-check.ps1 緑

**Phase 3 全体**:
- 全 4 サブ main マージ済 (直列 no-ff)
- EditorControl.cs 1537 → 600〜800 (git-truth)
- Editor 層に Controller 4 個抽出 (Ime/Caret/Input/UiaAdapter)
- 12 個の Uia 系 field が完全に Adapter 所有
- §C.4 例外 (OnHandle*/On*Changed 4 method の Uia.cs 帰属) 解消
- MEMORY.md に完了記録追加 (`refactor-separation-phase3-complete.md` 想定)

### 5.10 非目標 (スコープ外・将来課題)

- **UIA RPC read パスの torn read 対策** (Interlocked 化) = 既存挙動不変が絶対条件のため Phase 3 では触らない
- **UIA v2 内部リファクタ** (TextControlProviderV2 側) = 3d は Adapter への委譲まで
- **ICharMetrics interface 化** = Fake seam 最小化決定 (Q4) により見送り
- **キーマップの外部化 (設定ファイル化)** = InputRouter のリファクタは dictionary 化まで
- **Undo/Redo コマンドパターン化** = Phase 3 対象外
- **App 層のさらなる Controller 分割** = Stage 8 で判断済み・別トピック
- **性能最適化** = Phase 3 完了後にプロファイル駆動で別 Phase 判断

### 5.11 リスクと緩和策

| リスク | 影響 | 緩和策 |
|---|---|---|
| Controller ctor 順序で circular dependency | ctor throw | CaretController を最初に new・Input/Ime/Uia は CaretController を受け取る |
| UiaTextHostAdapter への field 移譲でスレッド安全崩壊 | UIA RPC race | `_boundsSync` の lock 粒度・volatile 属性を Adapter へそのまま移植・**変更最小** |
| IImeContext seam で P/Invoke タイミング変化 | IME 発火不安定 | `Func<IImeContext>` factory で毎回取り直し = 現状 `ImmGetContext(Handle)` の毎回呼びと同等 |
| Phase 2 flaky test に遭遇 | 誤 fail 判定 | 各サブ着手前 `taskkill /F /IM dotnet.exe` |
| plan の数値目標が現実とずれる | サブごとの DoD 検証困難 | 代替 DoD「削減行数 ≒ Controller 追加行数 ±10%」で機械的検証 |
| 3d 完了時 §C.4 例外解消で Editor.Tests 破壊 | OnHandle* 系のテスト fail | 3d 実装 Step で「4 method を本体に戻す」を明示・reflection 契約テストで機械固定 |

## 6. 次アクション

本設計書承認後、`writing-plans` skill を起動して段階別実装計画に落とし込む (全 4 サブを 1 ファイル・Q1 決定通り)。各サブの具体的な Task 分解・レビュー観点・テスト追加項目は実装計画側に持たせる。

**本設計書はコード変更を含まない**。承認 → 設計書コミット → writing-plans skill 起動 → 実装計画作成、までを本セッションで行う。実装は別セッション。

## 7. 関連

- 上位設計: `docs/plans/2026-07-16-refactor-separation-of-concerns-design.md` §4
- Phase 1 実装計画: `docs/plans/2026-07-16-refactor-separation-of-concerns.md`
- Phase 2 実装計画: `docs/plans/2026-07-16-refactor-editorcontrol-partial.md`
- Phase 2 設計補足: `docs/plans/2026-07-16-refactor-editorcontrol-partial-design.md` §C.1〜C.5
- Phase 1 完了記録: `[[refactor-separation-phase1-complete]]`
- Phase 2 完了記録: `[[refactor-separation-phase2-complete]]`
