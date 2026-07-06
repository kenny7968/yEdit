# P4: IME(WM_IME_* 自前処理) 設計書

**位置づけ**: `docs/plans/2026-07-05-custom-editcontrol-design.md` §P4 の詳細設計。P0(SR プローブ・Go)/P1(TextBuffer・DoD 達成)/P2(EditorControl 骨格+描画・DoD 達成)/P3(編集・入力・DoD 達成)の続き。

**方針**: `EditorControl` に IMM32 経路(`WM_IME_*` / `Imm*`)を自前実装し、未確定文字列は **overlay 描画方式**(`TextBuffer` に触れず EditorControl 内部フィールドに保持し、`OnPaint` で inline 合成)で扱う。確定文字列のみ既存の `OnKeyPress` 経路と同じ helper に流し込み、1 変換=1 Splice=1 Undo に潰す。

**運用**: 全フェーズを worktree `feature/custom-editcontrol-design` に閉じ、**P7 合格後に一括で main へ no-ff マージ**(設計書§3 運用)。**P4 でも main には触れない**。

---

## 1. スコープ(v1)

### 1-1. 含む(実装対象)

- `WM_IME_STARTCOMPOSITION` / `WM_IME_ENDCOMPOSITION`:未確定期間の状態遷移
- `WM_IME_COMPOSITION` の `GCS_COMPSTR` / `GCS_CURSORPOS` / `GCS_COMPATTR` / `GCS_COMPCLAUSE` / `GCS_RESULTSTR`:未確定文字列の受信と確定文字列の反映
- `WM_IME_SETCONTEXT`:`ISC_SHOWUICOMPOSITIONWINDOW` を落として既定 UI を抑止(自前描画のため)
- `ImmSetCandidateWindow`:候補窓をキャレット位置に追従(`PositionCaret` 経路に相乗り)
- `ImmSetCompositionFont`:本文フォントを IME へ通知(座標整合)
- 未確定文字列の inline 描画(下線・変換対象節の反転)
- 未確定期間中の縁ケース:選択範囲があるまま IME 開始・ReadOnly・フォーカス喪失・ESC 取消・BackSpace(IME 経由)
- ATOK 実機チェックリスト(P0 と同形式)

### 1-2. 含まない(v1 スコープ外・申し送り)

- **再変換**(`IMR_RECONVERTSTRING` / `IMR_CONFIRMRECONVERTSTRING`):設計書§2-3 で明記
- TSF(TextServices Framework)経路
- IME 特有拡張(ATOK 辞書登録キー連携・パスワードモード等)
- 未確定文字列の UIA プロバイダ露出(P5 で判断・v1 は IME 自身の候補読みに任せる=P0 実機結果と整合)

### 1-3. DoD(Definition of Done)

- ATOK 実機で「文字入力/連文節変換/候補選択/確定/取消(ESC)/未確定中 BackSpace/フォーカス移動時の取消」の 7 項目チェックリストが全 OK
- 既存 663 テスト全緑・build 0 警告維持
- 新規純ロジックテスト `ImeCompositionStateTests` 追加分緑
- Smoke で IME 開始→確定の 1 サイクル手動確認可能
- NG 判定でも App 層は無傷=撤退可能(`git revert` で P4 コミット群を戻せる=設計書§4 リスク表)

---

## 2. アーキテクチャ(overlay 方式)

### 2-1. 基本原則

1. 未確定文字列は **`TextBuffer` に触れない**。EditorControl 内部フィールド `_ime`(型 `ImeCompositionState`)に保持し、`OnPaint` で本文描画後にキャレット位置へ inline 合成
2. `WM_IME_COMPOSITION` の `GCS_RESULTSTR`(確定通知)を受けたら、既存 `OnKeyPress` の文字挿入経路(選択削除→Insert→`AfterEdit`)と **同じ private helper** を再利用=1 変換=1 Undo
3. IME メッセージは `WndProc` 内で横取り(P3 で `WndProc` override は未使用のため新規追加)。処理後 `m.Result = IntPtr.Zero` を返し `DefWindowProc` を呼ばない(自前描画を貫徹する項目=SETCONTEXT のみ base に流す)
4. P3 §0-9 の約束を守る:**`OnKeyPress` は書き換えない**。IME 経路と `OnKeyPress` は同一の `InsertConfirmedText(string)` を通す(=P3 挙動不変=既存 663 テストが緑のまま)

### 2-2. 責務分離

```
NativeMethods.cs         : Imm* / WM_IME_* 定数と P/Invoke 追加のみ
ImeCompositionState.cs   : 未確定状態値型(text/cursorPos/attrs/clauses)+ pure な描画データ算出
EditorControl.cs         : WndProc 追加・状態管理・描画合成・候補窓位置追従
```

- `ImeCompositionState` は immutable struct + 純関数ヘルパで純ロジック化 → **xUnit で状態遷移テスト可能**(Windows メッセージなしで検証)
- EditorControl 側は「メッセージ→state 更新→Invalidate」だけの薄い層
- P2 の `Frame`/`FrameBuilder` は本文描画専用(不変)。IME overlay は Frame の外側で追加描画=Frame テストは影響を受けない

### 2-3. 状態機械

```
Idle ── WM_IME_STARTCOMPOSITION ──▶ Composing
Composing ── WM_IME_COMPOSITION(GCS_COMPSTR) ──▶ Composing (state 更新)
Composing ── WM_IME_COMPOSITION(GCS_RESULTSTR + 空文字) ──▶ Cancelled
Composing ── WM_IME_COMPOSITION(GCS_RESULTSTR + 非空) ──▶ Confirming ──▶ Idle
Composing ── WM_IME_ENDCOMPOSITION ──▶ Idle (state クリア)
Composing ── LostFocus / ReadOnly=true への変更 / 外部編集 API ──▶ 強制取消 ──▶ Idle
```

- Confirming は `InsertConfirmedText` を呼んで即 Idle へ(短命=フィールド上には存在しない中間段階)
- **`Cancelled` はロールバック不要**(未確定は overlay で TextBuffer に触れていないため `_ime = default` + Invalidate だけ)

---

## 3. データフロー

### 3-1. 新設内部 API(EditorControl 内部・public 露出なし)

```csharp
internal readonly record struct ImeCompositionState(
    int Start,           // 未確定文字列の挿入位置(_caret の凍結値)
    string Text,         // 未確定文字列(GCS_COMPSTR)
    int CursorPos,       // Text 内の IME キャレット位置(GCS_CURSORPOS)
    byte[] Attrs,        // 文字ごとの属性(GCS_COMPATTR):INPUT/TARGET_CONVERTED 等
    int[] Clauses)       // 節境界オフセット(GCS_COMPCLAUSE)
{
    public static ImeCompositionState Empty { get; }
    public bool IsActive => Text.Length > 0;
}

// EditorControl 内部フィールド
private ImeCompositionState _ime = ImeCompositionState.Empty;
private bool IsComposing => _ime.IsActive;

// P3 の OnKeyPress と共有する挿入 helper(§0-9 の温存契約を守るため
// OnKeyPress からもここへ委譲する形にリファクタ=挙動不変)
private void InsertConfirmedText(string text);

// WndProc から呼ばれる薄いハンドラ
private void OnImeStartComposition();
private void OnImeComposition(long gcsFlags);
private void OnImeEndComposition();
private void OnImeSetContext(ref Message m);   // ISC_SHOWUICOMPOSITIONWINDOW を落として base へ

// 強制取消(縁ケース用)
private void CancelCompositionAndDefault();     // ImmNotifyIME(CPS_CANCEL) + _ime = default
```

### 3-2. メッセージ受信フロー

```
WM_IME_STARTCOMPOSITION
  ├─ 選択があれば削除(_buffer.Replace(s, en-s, ""))→_caret = s, _anchor = s
  │  (=Scintilla 互換。選択中に IME 起動したら選択を先に置換して開始)
  ├─ _ime = new ImeCompositionState(Start: _caret, Text: "", ...)
  ├─ ImmSetCandidateWindow(caret のスクリーン座標)
  └─ Invalidate

WM_IME_COMPOSITION (lParam = GCS_* フラグ束)
  ├─ GCS_COMPSTR bit あり:
  │   ImmGetCompositionString(GCS_COMPSTR)      → Text 更新
  │   ImmGetCompositionString(GCS_COMPATTR)     → Attrs 更新
  │   ImmGetCompositionString(GCS_COMPCLAUSE)   → Clauses 更新
  │   ImmGetCompositionString(GCS_CURSORPOS)    → CursorPos 更新
  │   ImmSetCandidateWindow(現時点の描画キャレット位置)
  │   Invalidate
  ├─ GCS_RESULTSTR bit あり:
  │   ImmGetCompositionString(GCS_RESULTSTR) → resultText
  │   _ime = default    (先にクリア=下記 Insert が overlay と競合しない)
  │   if resultText.Length > 0: InsertConfirmedText(resultText)
  │   (AfterEdit 経由でスクロールバー再計算/キャレット再配置/Invalidate)
  └─ m.Result = 0

WM_IME_ENDCOMPOSITION
  ├─ _ime = default   (取消/確定後どちらでも安全に呼ばれる)
  └─ Invalidate

WM_IME_SETCONTEXT
  ├─ lParam &= ~ISC_SHOWUICOMPOSITIONWINDOW  (既定 UI 抑止)
  └─ base.WndProc(ref m)                     (残りは既定処理へ)
```

### 3-3. 描画合成(`OnPaint` 内・本文描画の直後)

```
if (IsComposing) {
  1. キャレット位置 (_ime.Start) の client 座標を PixelMapper で算出
  2. 未確定文字列を GdiCharMetrics で 1 度に描画
     - 基本: 下線 + 通常前景色
     - Clauses[i] .. Clauses[i+1] の節が「変換対象節(TARGET_CONVERTED 属性)」なら
       背景反転(SelectionBack 相当)+ 下線太
  3. システムキャレットを (_ime.Start + _ime.CursorPos) の位置に SetCaretPos
     (=IME 内 caret 追従)
  4. 未確定文字列が右端を超えたら折り返し表示は行わない(現行 Scintilla と同挙動)
}
```

### 3-4. 候補窓位置追従

- `ImmSetCandidateWindow` は「未確定開始時 + Composition 更新時 + `PositionCaret` 呼出時」の 3 タイミングで発火
- スクロール(`TopLine`/`ScrollX`)変更時は既存の `PositionCaret` 経路に相乗り(=専用配線不要)

### 3-5. フォント通知

- `SetSource` 完了時 + `ApplyAppearance` 内で `ImmSetCompositionFont(hIMC, LOGFONT)` を 1 度呼ぶ(Font 変更後に反映)

---

## 4. 縁ケース / エラー処理

### 4-1. 選択範囲があるまま IME 開始

- `WM_IME_STARTCOMPOSITION` の先頭で `GetSelectionCharRange` を確認
- 選択があれば `_buffer.Replace(s, en-s, "")` で削除して `_caret = _anchor = s` にしてから `_ime.Start = _caret` を設定
- =Scintilla 互換。選択削除は 1 Splice(Undo 1 単位)、以後の未確定は overlay=Undo に影響なし

### 4-2. ReadOnly

- `ReadOnly = true` のとき `WM_IME_STARTCOMPOSITION` を無視(`m.Result = 0` して return・base 不呼出)
- IME 側は「開始拒否」として扱い、以後 COMPSTR も来ない(標準挙動)
- 未確定中に `ReadOnly = true` に切り替えられた場合は `ImmNotifyIME(hIMC, NI_COMPOSITIONSTR, CPS_CANCEL, 0)` で強制取消 → `_ime = default`
- `ReadOnly` setter に上記強制取消フックを埋め込む(既存 setter を最小拡張)

### 4-3. フォーカス喪失

- `OnLostFocus` で未確定中なら `ImmNotifyIME(hIMC, NI_COMPOSITIONSTR, CPS_COMPLETE, 0)` を試みる(=確定してから閉じる=Scintilla 互換)
- IME が CPS_COMPLETE 未対応でも `WM_IME_ENDCOMPOSITION` は必ず来るので `_ime = default` は既存の END ハンドラで自動処理

### 4-4. ESC 取消

- IME 側が `WM_IME_COMPOSITION` を `GCS_RESULTSTR=空` で送る(または直接 END)=既存パスで自然に `_ime = default`+`InsertConfirmedText` を呼ばずに終了

### 4-5. 未確定中 BackSpace

- `WM_KEYDOWN` は IME に横取りされ `WM_IME_COMPOSITION` で短くなった `GCS_COMPSTR` として届く=既存パスでそのまま
- **IME を介さない直接 BackSpace(=IME OFF 時)** は現行の P3 OnKeyDown 経路で処理済み(不変)

### 4-6. 未確定中に本文編集 API が外から呼ばれた場合

- 対象:`ReplaceCharRange`/`SetSelectionCharRange`/`SetCaretCharOffset`/`MoveCaretWithSelection`/`SetSelectionAnchored`
- **v1 は「編集 API 側で強制取消してから実行」の防御を入れる**:各編集 API の先頭に `if (IsComposing) CancelCompositionAndDefault();` の 1 行ガード
- `CancelCompositionAndDefault` = `ImmNotifyIME(CPS_CANCEL)` + `_ime = default`(END は IME から後追いで来る)
- **ScintillaHost は同種の外部編集で IME 状態にダーティな挙動を残さない**(内部で ImmNotifyIME を呼ぶ)=同水準を狙う

### 4-7. 未確定中フォーカスなし状態への `Focus()` 呼出

- `OnGotFocus` は `_ime = default` を仮定してよい(前回セッションの残骸はあり得ない=`OnLostFocus` で必ずクリアされる契約)

### 4-8. ImmGetContext 失敗(hIMC = NULL)

- IME 無効環境(サーバ Core・古いターミナル等)。全 Imm* 呼出は `if (hIMC == IntPtr.Zero) return;` でスキップ → 描画も no-op
- Imm* 呼出後は必ず `ImmReleaseContext` を try/finally で呼ぶ(P/Invoke ハンドルリーク防止)

### 4-9. 多重メッセージ(WM_IME_COMPOSITION が STARTCOMPOSITION より先に届く稀ケース)

- 実質観測されないが、`_ime.Start == default(未初期化)` のとき暗黙で `_ime.Start = _caret` する自己防衛を入れる

### 4-10. 明示的な非対応

- 未確定中のマウス位置クリック(選択変更): 現行 Scintilla も未確定を確定してからクリック処理=同挙動を選ぶ。実装は `OnMouseDown` の先頭に `if (IsComposing) CancelCompositionAndDefault();`(=強制取消)。**取消ではなく確定**にする案(`CPS_COMPLETE`)もあるが、v1 は「取消」で統一・申し送りに残す

---

## 5. テスト戦略

### 5-1. 自動テスト範囲(Windows メッセージなしで再現できる部分)

**`tests/yEdit.Core.Tests/Editing/ImeCompositionStateTests.cs` を新設**(純ロジック):
- 状態遷移: `Empty` → `Update(text, cursor, attrs, clauses)` → `Empty`
- Clauses/Attrs のパース(バイト列→int[]/byte[] 分解の境界=長さ 0・要素数不一致・末尾番兵)
- サロゲート境界: 未確定文字列がサロゲートペア途中を含むケースの `CursorPos` スナップ

**`tests/yEdit.Editor.Tests/EditorControlImeTests.cs` を新設**(WinForms STA・既存基盤に相乗り):
- `WndProc` を internal 露出したテスト用ヘルパで直叩き=Windows の IME サービスに依存しない
- 選択削除→未確定開始→未確定更新→確定 のフル 1 サイクルで:
  - TextBuffer に確定文字のみ 1 Splice(=Undo 1 単位)積まれる
  - `_ime` が最終的に `Empty` に戻る
  - キャレット位置が `(Start + resultText.Length)` に着地
- ReadOnly 中の START 無視・未確定中 ReadOnly 化での強制取消
- LostFocus での `CPS_COMPLETE` 発火(モックで観測)
- 外部 `ReplaceCharRange` が未確定中に呼ばれたときのガード動作

### 5-2. Smoke 拡張(`tests/yEdit.Editor.Smoke`)

- 起動時タイトルバーに `IME: composing` 表示ラベルを追加(ATOK で手動確認しやすくする)
- `--ime` サブコマンドで「未確定色/下線」がわかる長文サンプルを開いた状態で起動

### 5-3. UiaProbe 拡張(任意・P4 レビュー後)

- 現行 UiaProbe は Scintilla ではなく素の `UiaTextControl` のため、P4 の overlay と直接関係しない
- ただし **P0 IME 実機チェックリスト(§P0-3-8)を新 EditorControl でも実施できるよう**、smoke へ IME 用検証項目を移植=P0 と同じ 7 項目を新コントロール上で再実施(**これが実質の DoD 検証**)

### 5-4. ATOK 実機チェックリスト(=P4 の最終ゲート・ユーザー実施)

| # | 検証項目 | 期待挙動 |
|---|---|---|
| 1 | 「にほんご」タイプ→変換なし確定 | 未確定表示・下線・確定で「にほんご」挿入 |
| 2 | 「かんじ」タイプ→スペースで変換→Enter | 変換対象節が反転・確定で選択候補が挿入 |
| 3 | 「わたしはにほんじん」タイプ→連文節変換 | 節境界が視認できる・矢印で節移動 |
| 4 | 候補ウィンドウが表示される | キャレット直下(=行送り位置)に候補窓が出る |
| 5 | ESC 取消 | 未確定文字列が消える・TextBuffer に何も残らない |
| 6 | 未確定中に BackSpace | 未確定文字列が 1 文字短くなる(TextBuffer 不変) |
| 7 | 未確定中に他ウィンドウへフォーカス移動 | 未確定が確定される・戻ってきても残骸なし |

- ログは既存 `SrDiagLog` 方式を流用(P4 でのみ有効化=P5 のトレース基盤とは別・IME フック名で分離)
- **NG 判定 → 撤退**: `git revert` で P4 コミット群を戻せば App 層無傷(=設計書§4 リスク表)

---

## 6. Task 分解(15 Task 案)

| # | Task | 主な成果物 |
|---|---|---|
| 1 | NativeMethods 拡張 | `WM_IME_*` 定数・`GCS_*`・`NI_*`/`CPS_*`・`ImmGetContext`/`ImmReleaseContext`/`ImmGetCompositionString`/`ImmSetCandidateWindow`/`ImmSetCompositionFont`/`ImmNotifyIME` P/Invoke |
| 2 | `ImeCompositionState` 純ロジック | struct + Attrs/Clauses パース + 状態遷移。**xUnit `ImeCompositionStateTests`** 新設 |
| 3 | `InsertConfirmedText` 共通化リファクタ | 既存 `OnKeyPress` を private helper に委譲(挙動不変=既存 663 テスト緑を維持)。IME/直接入力の共通経路化 |
| 4 | `WndProc` 追加 + `WM_IME_SETCONTEXT` | `ISC_SHOWUICOMPOSITIONWINDOW` 抑止・他 IME メッセージは以後の Task で埋める |
| 5 | `WM_IME_STARTCOMPOSITION` 受信 | 選択削除・`_ime` 初期化・`ImmSetCandidateWindow` 初期位置 |
| 6 | `WM_IME_COMPOSITION` GCS_COMPSTR 受信 | 未確定文字列取得・`_ime` 更新・Invalidate |
| 7 | `WM_IME_COMPOSITION` GCS_RESULTSTR 受信 | `_ime = default` → `InsertConfirmedText` = 1 Splice/1 Undo |
| 8 | `WM_IME_ENDCOMPOSITION` + フォーカス/ReadOnly 縁ケース | `OnLostFocus`/`ReadOnly` setter に `CPS_COMPLETE`/`CPS_CANCEL` フック |
| 9 | OnPaint への未確定 overlay 描画 | 下線・inline 合成・`OnPaint` テスト(Bitmap 差分) |
| 10 | GCS_COMPATTR / GCS_COMPCLAUSE 節ハイライト | 変換対象節の反転・下線太化 |
| 11 | GCS_CURSORPOS 反映 | IME 内キャレット位置に `SetCaretPos` |
| 12 | 候補窓 / フォント追従 | `PositionCaret` 経路に `ImmSetCandidateWindow`・`ApplyAppearance` / `SetSource` に `ImmSetCompositionFont` |
| 13 | 外部編集 API ガード | `ReplaceCharRange`/`SetSelectionCharRange`/`OnMouseDown` の先頭に `if (IsComposing) CancelCompositionAndDefault();` |
| 14 | Smoke 拡張 + ATOK チェックリスト作成 | `--ime` サブコマンド・`docs/plans/2026-07-06-p4-ime-checklist.md`(P0 と同形式) |
| 15 | 別エージェント最終レビュー → 対応 → 設計書§3 に P4 結果表追記 | Critical/Important 潰し → 設計書追記 → メモリ更新 |

### 6-1. 運用ルール(P0〜P3 と同じ)

- 各タスク完了時に 1 コミット
- **main には触れない**(§運用・P7 まで worktree に閉じる)
- 別エージェント subagent-driven-development で実装(P3 と同じ流儀)を採用予定
- ScintillaHost / App 層は無変更

### 6-2. 規模見積り

- 設計書§5 の「IME 300〜600 行」に沿う想定
- 純ロジック(`ImeCompositionState`)+ WndProc 追加+ overlay 描画+ 縁ケースガードで概ね該当範囲

---

## 7. リスクと撤退基準

| リスク | 判明時期 | 撤退時の損失 |
|---|---|---|
| ATOK 実機で連文節変換が動作しない | Task 14 | P4 の 15 Task 分(`git revert` で戻す) |
| overlay 描画が特定フォントで座標ズレ | Task 9〜11 | 該当 Task の再設計(=フォント通知の追加)/`ImmSetCompositionFont` で解消の見込み |
| 未確定中の縁ケースで crash / 状態残骸 | Task 8/13 | 該当 Task 内で修正(強制取消 API に集約する設計) |

**撤退時の安全性**: App 層(`ScintillaHost`)は無変更のため、P4 の 15 コミットを `git revert` すれば P3 完了状態(663 テスト緑・build 0 警告)に戻る。**設計書§3 運用「途中フェーズで main に触れない」が撤退安全性の担保**。

---

## 8. 次フェーズへの申し送り

- **P5(UIA/SR 接続)**: 未確定文字列を UIA プロバイダから露出するか判断。P0 実機結果(PC-Talker は `Expand(Character)+GetText(1)` しかしない)より、v1 は IME 自身の候補読みに任せる方針。P5 で本 EditorControl 上に UIA プロバイダを載せてから ATOK 再実機で最終判断
- **P6(App 層一発置き換え)**: `ReadOnly`/`ReplaceCharRange`/`SetSelectionCharRange` の外部呼出しは、P6 の App 層置換で頻度が上がる。§4-6 のガードが実効的に効くかは P6 実装で観測
- **v1 スコープ外の再変換(`IMR_RECONVERTSTRING`)**: ATOK では実装しないと辞書登録・カーソル位置文字の逆変換が動かない。v2 以降で対応判断

---

**関連**:
- 親設計書: `docs/plans/2026-07-05-custom-editcontrol-design.md`
- P3 実装計画: `docs/plans/2026-07-05-p3-editor-input.md`(§0-9 = OnKeyPress 温存契約)
- P0 実機結果: `docs/plans/2026-07-05-p0-sr-probe-checklist.md`(IME 実機ゲート項目の由来)
