# P5: UIA/SR 接続(自作 EditorControl に UIA プロバイダを載せる) 設計書

**位置づけ**: `docs/plans/2026-07-05-custom-editcontrol-design.md` §P5 の詳細設計。P0(SR プローブ・Go)/P1(TextBuffer・DoD 達成)/P2(EditorControl 骨格+描画・DoD 達成)/P3(編集・入力・DoD 達成)/P4(IME・自動 DoD 達成・ATOK 実機は P5 完了時に再検証)の続き。**本フェーズが第 2 実機ゲート**。

**方針**: 設計書§2-4 の `IUiaTextHost` v2(範囲ベース化)を**新設**し、自作 EditorControl に UIA プロバイダを常時提供する。ネイティブ表面原則(§2-7)を貫徹し、SR は UIA 経路のみで読む。**自作 EditorControl は Scintilla に非依存**(現状のコード上、`using ScintillaNET` は無し・`Control` 直接派生)。既存の `IUiaTextHost`(v1)と v1 用 Provider 群(`TextControlProvider`/`TextProviderImpl`/`TextRangeProvider`/`TextNavigation`)は本ブランチで**そのまま温存**(`IUiaTextHostLegacy` にリネームするだけの 1 回 refactor で v2 と並存可能)=ScintillaHost 経由の App 層 SR 対応は本フェーズ中も継続。ScintillaHost 本体+v1 系 UIA 関連コードは**全て P6 で一括撤去**する(=あなたの指示「あとで一括削除・置換」に沿う)。実機中間検証(第 2 ゲート)は本フェーズで smoke 上で実施。P4 で不能だった SR 読み上げ検証と ATOK 7 項目チェックリストをここで併せて再実施する。

**運用**: 全フェーズを worktree `feature/custom-editcontrol-design` に閉じ、**P7 合格後に一括で main へ no-ff マージ**(設計書§3 運用)。**P5 でも main には触れない**。

---

## 1. スコープ(v1)

### 1-1. 含む(実装対象)

- `IUiaTextHost` v2 新設(範囲ベース化=位置 API + 座標 API + `GetTextRange`)。v1 は `IUiaTextHostLegacy` にリネームして温存
- v2 用 Provider 群を新設:`TextControlProviderV2` / `TextProviderImplV2` / `TextRangeProviderV2`(既存 v1 用は温存・両方が並存)
- EditorControl の `IUiaTextHost` v2 実装
- `WM_GETOBJECT` / `UiaRootObjectId` 応答(UiaProbe 参照実装を EditorControl へ移植)
- UIA イベント発火:`TextSelectionChangedEvent` / `TextChangedEvent` / `AutomationFocusChangedEvent`
- 空行イベント本挙動化(`CaretEnteredEmptyLine` は P3 で受け口実装済。App 層能動発声のスタブを smoke に配線)
- `RaiseUiaSelectionEvents` の抑止スイッチ配線(CSV セルナビ等の App 層都合を先出し受け口として温存)
- 座標 API 本実装:`GetBoundingRectangles(s,e)` / `RangeFromPoint(x,y)` を `PixelMapper` / `FrameBuilder` から算出
- 単語境界を Core `WordBoundary` へ委譲(Accessibility は Core 非依存を維持)
- ネイティブ表面原則:`WM_GETTEXT` / `WM_GETTEXTLENGTH` に本文を返さない・MSAA は自前実装せず UIA→MSAA ブリッジ任せ・`WM_GETOBJECT` は `UiaRootObjectId` のみ応答
- `tools/verify-uia.ps1` / `walk-test.ps1` / `word-sim.ps1` を新 EditorControl 向けに調整(SR 非依存回帰)
- Smoke に UIA プロバイダ配線バリアント + 最小 Announcer(空行「空行」/ 単語ナビの単語スパン読み)
- 実機中間検証(P0 と同項目を本物で:NVDA / PC-Talker / ナレーター)+ P4 ATOK 7 項目チェックリスト再実施

### 1-2. 含まない(v1 スコープ外・申し送り)

- **ScintillaHost 本体および v1 系 UIA コードの撤去**:全て P6 で一括撤去(=あなたの指示「あとで一括削除」に沿う):
  - `ScintillaHost.cs` / `Sci.cs` の削除
  - `Scintilla5.NET` PackageReference の削除(=Scintilla / Lexilla ネイティブ DLL の同梱停止)
  - `IUiaTextHostLegacy`(v1)/ v1 用 Provider 群(`TextControlProvider`/`TextProviderImpl`/`TextRangeProvider`/`TextNavigation`)の削除
  - App 層で ScintillaHost / v1 UIA に触っている全コード(`AppMain.cs` の編集エンジン結線 / `RaiseUiaSelectionEvents` / `ServeUiaProvider` / `CaretEnteredEmptyLine` 消費側 / `EditorAppearance.Apply(Scintilla 版)` 等)を新 EditorControl 経路へ書換
- **P5 期間中の ScintillaHost の挙動**:**完全に触らない**。App 層は現行のとおり Scintilla を編集エンジンとして使い続け、SR 対応も現行仕様のまま継続=**P5 期間中に App 層で SR 対応の一時劣化は発生しない**
- App 層(`SrRoute.Nvda` / `CsvFocusSink` / `ServeUiaProvider` / `ApplySrAdaptation`)の**撤去**:切り分け手段温存のため P7 で行う(P5 では新プロバイダは smoke でのみ経路として使い、App 層の切替は P6)
- `IUiaTextHost` v2 実装の Announcer 本挙動(単語ナビの単語スパン読み・IME 読み上げ最適化)の App 層配線:P5 では smoke スタブのみ。**本挙動化は P6 の App 層置換で本物の `Announcer` へ差替**
- 再変換(`IMR_RECONVERTSTRING`)関連の UIA 露出:P4 の申し送りに従い v2 以降
- **`TextChangedEvent` の詳細化**(挿入位置/削除範囲の付与)は UIA 仕様上任意=v1 は「なにか変わった」通知のみ

### 1-3. DoD(Definition of Done)

- 既存 683 テスト全緑・build 0 警告維持
- 新規純ロジック(`IUiaTextHost` v2 座標算出・範囲テキスト取得・位置歩きの純関数)追加分緑
- 新規 WinForms 表面テスト(`EditorControlUiaTests`)追加分緑=`WM_GETOBJECT` 応答 / `TextControlProvider` 生成 / IUiaTextHost v2 の RPC スレッド安全性
- SR 非依存スクリプト:`tools/verify-uia.ps1` / `walk-test.ps1` / `word-sim.ps1` を新 EditorControl 向けに調整して全 PASS
- 1GB でのプロバイダ応答が O(log n):`GetTextRange(s,len)` / `GetLineIndexOfChar` / `GetBoundingRectangles` のベンチ追加(µs 台目標)
- Smoke で `WM_GETTEXT` / `WM_GETTEXTLENGTH` に本文が乗らないことを Spy++/`GetWindowText` で目視確認(手順書化)
- 実機中間検証(NVDA / PC-Talker / ナレーター)= P0 と同 8 項目を本物 EditorControl 上で再実施:
  - 単文字ナビ / 行ナビ / SayAll / 選択読み / IME 読み上げ / 空行 / ControlType / 復帰フォーカス
  - **PC-Talker 単語ナビ**: smoke Announcer のスタブが「単語スパンを読む」ことを実機で確認(P0 で確定した唯一の App 層補完項目)
- ATOK 7 項目チェックリスト(P4 checklist を SR 読み上げ検証と統合したもの)を実機で全 OK
- NG 判定でも App 層は無傷=撤退可能(`git revert` で P5 コミット群を戻せば P4 完了状態へ復帰=設計書§4 リスク表)

---

## 2. アーキテクチャ(範囲ベース v2)

### 2-1. 基本原則

1. **位置の通貨は UTF-16 文字オフセット**(v1 と同じ)。SR は文字/単語/行単位で UIA 呼出しをするため、UTF-16 単位の位置歩きが実装コストと性能で最適
2. **全文 string の取得は禁じ手**:`IUiaTextHost.GetText()` 全文は v2 で廃止。テキストアクセスは全て `GetTextRange(start, length)` 経由=大容量(1GB)でも O(len) のみで応答可能
3. **RPC スレッド安全**:IUiaTextHost メンバは UIA の RPC スレッドから呼ばれる。EditorControl 側は「不変スナップショット参照 + キャッシュ値 + 明示ロック」で応答
4. **UI 変更は BeginInvoke**:`SetSelection` / `SetFocus` の書込み系はメインスレッドへマーシャリング
5. **単語境界は Core `WordBoundary` に集約**:Accessibility は Core 非依存を維持=EditorControl 側で委譲する
6. **ネイティブ表面は「Uia のみ」**:WM_GETTEXT に本文非公開、MSAA は自前実装しない=クラス改名 Scintilla 実験で PC-Talker が壊れた教訓(HANDOFF §13.3)を貫徹

### 2-2. 責務分離

```
yEdit.Accessibility/
  # 既存(v1・温存・P6 で削除):
  IUiaTextHost.cs                     → IUiaTextHostLegacy.cs にリネーム(interface 名も変更・ScintillaHost 側の実装追従)
  TextControlProvider.cs              : 変更なし(v1 用として温存)
  TextProviderImpl.cs                 : 変更なし(v1 用として温存)
  TextRangeProvider.cs                : 変更なし(v1 用として温存)
  TextNavigation.cs                   : 変更なし(v1 用として温存)
  # 新設(v2・EditorControl 用):
  IUiaTextHost.cs                     : v2 定義(範囲ベース + 位置歩き + 座標 API)
  TextControlProviderV2.cs            : 新設(v2 ホストを受ける・v1 とほぼ同ロジックだが v2 型)
  TextProviderImplV2.cs               : 新設(RangeFromPoint 本実装)
  TextRangeProviderV2.cs              : 新設(全 API を v2 メンバ経由 / Move スパン保持ロジックは v1 から踏襲)

yEdit.Editor/
  EditorControl.cs                    : IUiaTextHost v2 実装 + WM_GETOBJECT + イベント発火 + 座標 API
  NativeMethods.cs                    : WM_GETOBJECT / UiaRootObjectId 定数(P4 で導入済)+ WM_GETTEXT / WM_GETTEXTLENGTH 追加
  ScintillaHost.cs                    : 【触らない】= P5 期間中も現行のまま IUiaTextHostLegacy を実装。App 層の SR 対応継続
  Sci.cs                              : 【触らない】

tests/yEdit.Editor.Tests/
  EditorControlUiaTests.cs            : 新設(WM_GETOBJECT 応答 / v2 メンバの RPC スレッド安全性 / イベント発火順)

tests/yEdit.Editor.Smoke/
  UiaSmokeAnnouncer.cs                : 新設(P0 で確定した「空行」/「単語ナビ 1 文字読み」補完の smoke 用スタブ)

tools/
  verify-uia.ps1                      : 新 EditorControl 用の派生を新設(verify-uia-editor.ps1)。既存は Sci 用として温存
  walk-test.ps1                       : 同上(walk-test-editor.ps1 新設)
  word-sim.ps1                        : 新 EditorControl 上での再実施(既存を smoke バイナリ向けに引数化)
```

- Accessibility は Core 非依存を維持(WordBoundary の呼び出しは EditorControl 側で吸収)
- v1/v2 並存の実務コスト:v1 用ファイル 5 個は**変更ゼロ**(interface 名リネームの機械的置換のみ)+ v2 用ファイル 4 個新設=クラッターは最小
- P6 で v1 用ファイル 5 個 + ScintillaHost.cs / Sci.cs / Scintilla5.NET PackageReference / App 層関連コードを**一括削除**(=あなたの指示どおり)

### 2-3. `IUiaTextHost` v2 定義

```csharp
public interface IUiaTextHost
{
    // ---------- テキスト(範囲ベース) ----------
    /// <summary>[start, start+length) の UTF-16 部分文字列。範囲外は clamp。RPC スレッド安全。</summary>
    string GetTextRange(int start, int length);

    /// <summary>本文長(UTF-16 コード単位)。</summary>
    int TextLength { get; }

    // ---------- 選択 ----------
    (int Start, int End) GetSelection();
    void SetSelection(int start, int end);

    // ---------- 位置歩き(全て純関数=RPC スレッド安全) ----------
    int NextChar(int offset);         // サロゲート考慮
    int PrevChar(int offset);
    int LineStartOf(int offset);
    int LineEndNoBreakOf(int offset); // 改行を含まない行末(空行 len=0 公開)
    int LineEnd(int offset);          // 改行を含む行末(次行の開始)
    int WordStart(int offset);        // 単語の左端(Core WordBoundary 委譲)
    int WordEnd(int offset);          // 単語の右端(同上)
    int NextWordStart(int offset);    // Ctrl+→ 相当
    int PrevWordStart(int offset);    // Ctrl+← 相当

    // ---------- 座標 ----------
    /// <summary>コントロール全体のスクリーン座標矩形(UI スレッドで更新したキャッシュ値)。</summary>
    Rect BoundingRectangle { get; }

    /// <summary>[start, end) の各行スクリーン矩形を UIA 形式 (x,y,w,h, ...) で返す。空なら長さ 0。</summary>
    double[] GetBoundingRectangles(int start, int end);

    /// <summary>スクリーン座標 (x, y) 直下の文字オフセット(HitTest 相当)。範囲外は clamp。</summary>
    int OffsetFromScreenPoint(double x, double y);

    // ---------- 属性 ----------
    nint Handle { get; }
    bool HasFocus { get; }
    int ControlTypeId { get; }     // Document 固定(P0 で確定)
    string Name { get; }           // "本文"
    string AutomationId { get; }   // "editor"
    void SetFocus();
}
```

**変更要点**:
- v1 の `GetText()` を廃止 → `GetTextRange(int, int)` に一本化
- 位置歩き 8 メンバを新設 → `TextRangeProvider` が全文 string を舐める依存を解消
- `OffsetFromScreenPoint` を新設 → `TextProviderImpl.RangeFromPoint` の本実装受け皿(v1 はスタブ)
- `GetBoundingRectangles` の実装本挙動化(v1 は `Array.Empty<double>()`)

### 2-4. スレッド戦略

- `_bufferSnapshot`(volatile `TextSnapshot?`)= EditorControl フィールド。編集経路(`AfterEdit`)で更新した後にキャッシュに載せ替え。`IUiaTextHost` メンバは RPC スレッドからこの参照を単純に読む
- `_caret` / `_anchor` は既存 volatile int 相当(P3 実装済)
- `_bounds`(`System.Windows.Rect`)は既存 `lock` パターン(v1 と同じ)
- `SetSelection` / `SetFocus` は `InvokeRequired ? BeginInvoke(...) : 直接` パターン(v1 と同じ)
- **原則**:RPC スレッドで `_buffer.Current` を「一度だけ」読み、その参照(不変)を関数内で使い切る=編集 API を書き換えるレースがあっても、既に取得した snapshot は不変で自己整合

### 2-5. WM_GETOBJECT / プロバイダ生成

```csharp
protected override void WndProc(ref Message m)
{
    // P4 で追加した IME 経路より前段で処理
    if (m.Msg == NativeMethods.WM_GETOBJECT)
    {
        long objid = m.LParam.ToInt64();
        if (objid == NativeMethods.UiaRootObjectId)
        {
            _provider ??= new TextControlProvider(this);
            m.Result = AutomationInteropProvider.ReturnRawElementProvider(
                Handle, m.WParam, m.LParam, _provider);
            return;
        }
        // それ以外(OBJID_CLIENT 等)は base=DefWindowProc に流す(=MSAA プロキシ生成せず)
    }
    // ネイティブ表面原則:本文非公開
    if (m.Msg == NativeMethods.WM_GETTEXT)      { m.Result = IntPtr.Zero; return; }
    if (m.Msg == NativeMethods.WM_GETTEXTLENGTH){ m.Result = IntPtr.Zero; return; }
    base.WndProc(ref m);
}
```

- `_provider` は 1 度だけ生成(handle 生成後の最初の WM_GETOBJECT で lazy 生成)。Handle 破棄時は null 化しない(WinForms が新 Handle を再生成した場合は次の WM_GETOBJECT で再生成される想定=v1 実装踏襲)
- **`WM_GETOBJECT` の `OBJID_CLIENT` に応答しない**:応答すると WinForms 既定の MSAA プロキシが生成され、`Name`/`Value` 経由で本文が漏れる可能性がある(HANDOFF §13.3 の再発防止)
- **`OBJID_WINDOW`(=0)にも応答しない**:base=DefWindowProc に流す(ウィンドウレベルは自然に処理される)

### 2-6. UIA イベント発火

**発火タイミング**(EditorControl 内部から `RaiseUia(...)` 経由):

| イベント | 発火点 | 抑止条件 |
|---|---|---|
| `TextSelectionChangedEvent` | `AfterEdit` 末尾 / `MoveCaretTo`(P3)末尾 / `OnGotFocus`(明示発火=SR 追従回避) | `RaiseUiaSelectionEvents == false`(App 層フラグ) |
| `TextChangedEvent` | `AfterEdit` 内、TextBuffer 更新直後 | なし(v1 は詳細化しない=「なにか変わった」通知のみ) |
| `AutomationFocusChangedEvent` | `OnGotFocus` | なし |
| `CaretEnteredEmptyLine`(EditorControl イベント) | `RaiseCaretEnteredEmptyLineIfNeeded`(P3 実装済)| `RaiseUiaSelectionEvents == false`(P3 実装済) |

**共通ヘルパ**:

```csharp
private void RaiseUia(AutomationEvent ev)
{
    if (_provider is null || !AutomationInteropProvider.ClientsAreListening) return;
    try { AutomationInteropProvider.RaiseAutomationEvent(ev, _provider, new AutomationEventArgs(ev)); }
    catch { /* UIA サーバ側で失敗しても本体は続行 */ }
}
```

- **フォーカス獲得時の `TextSelectionChanged` 明示発火**(HANDOFF §13.6): PC-Talker が 2 秒ポーリングで選択を追う既知挙動への対策=v1 実装(UiaProbe/ScintillaHost)から踏襲
- **`RaiseUiaSelectionEvents`** は App 層 CSV セルナビ配線用の受け口として先出し(P3 実装済。P5 では抑止条件のみ配線)

### 2-7. 座標 API 本実装

**`GetBoundingRectangles(int start, int end)`**(RPC スレッド):

```
snap = _bufferSnapshot;  // 参照コピー(不変)
frame = _lastFrame;      // 参照コピー(不変=P2 の Frame)
if (snap is null || frame is null) return Array.Empty<double>();

// 選択矩形(逐行分解)を PixelMapper で算出
rects = new List<double>(8);
pos = clamp(start, 0, snap.CharLength);
end = clamp(end, 0, snap.CharLength);
while (pos < end) {
    lineEnd = min(end, snap.LineEndNoBreak(pos));
    p1 = PixelMapper.OffsetToPx(frame, pos);        // (x1, y1)
    p2 = PixelMapper.OffsetToPx(frame, lineEnd);    // (x2, y1)
    lineHeightPx = frame.LineHeightPx;
    // client → screen 変換(キャッシュ済み _clientToScreen オフセット)
    (sx1, sy1) = ToScreen(p1.X, p1.Y);
    rects.Add(sx1); rects.Add(sy1); rects.Add(p2.X - p1.X); rects.Add(lineHeightPx);
    pos = snap.LineEnd(pos);
    if (pos <= last_pos) break;  // 安全弁
}
return rects.ToArray();
```

- 参照する Frame は `_lastFrame`(EditorControl が OnPaint で最新化する volatile 参照)
- クライアント→スクリーン座標変換は `RectangleToScreen` を UI スレッドで実行済のオフセットキャッシュ(`_clientToScreenX`/`_clientToScreenY`)で加算=RPC スレッドから呼べる

**`OffsetFromScreenPoint(double x, double y)`**(RPC スレッド):

- スクリーン→クライアント変換(オフセット引き算)→ P3 の `HitTest` 相当(既存 private HitTest を internal に露出 + snap 引数化)を呼ぶ
- 使い方例:PC-Talker の RangeFromPoint(SR がキャレット直下の単語を読むために使う可能性)

### 2-8. `TextRangeProviderV2` の実装(新設)

v1 の `TextRangeProvider` からロジックを踏襲しつつ、テキストアクセスを全て `_owner.Host` の v2 メンバ経由に:

- `ExpandToEnclosingUnit(TextUnit)`:
  - `Character` → `_start = pos; _end = host.NextChar(pos);`
  - `Word` / `Format` → `_start = host.WordStart(pos); _end = host.WordEnd(_start);`(縮退時は `NextChar` で 1 進める=v1 と同挙動)
  - `Line` / `Paragraph` → `_start = host.LineStartOf(pos); _end = host.LineEndNoBreakOf(pos);`(空行 len=0)
  - `Page` / `Document` → `_start = 0; _end = host.TextLength;`
- `Move(TextUnit, int)`:`StepForward`/`StepBackward` を host メンバ経由に(全文 string を捨てる)。**Move スパン保持**(`wasDegenerate` 判定と移動後の再展開)ロジックは v1 から踏襲=PC-Talker の文字歩きが動く条件
- `MoveEndpointByUnit(...)`:同上
- `GetText(int maxLength)`:`host.GetTextRange(_start, e - s)` に一本化(全文 Substring を廃止)
- `FindText(string, bool, bool)`:`host.GetTextRange(_start, _end - _start)` の局所文字列上で `IndexOf`/`LastIndexOf`(v1 と同アルゴリズム。全文取得を範囲取得へ差替)
- `GetBoundingRectangles()`:`host.GetBoundingRectangles(_start, _end)`

**v1 から踏襲(実装ほぼ同一)**:`Clone`/`Compare`/`CompareEndpoints`/`FindAttribute`/`GetAttributeValue`/`GetEnclosingElement`/`MoveEndpointByRange`/`Select`/`AddToSelection`/`RemoveFromSelection`/`ScrollIntoView`/`GetChildren`

### 2-9. `TextProviderImplV2` の実装(新設)

- `GetSelection()`:v1 と同(Range を v2 型で返す)
- `GetVisibleRanges()`:v1 と同(`(0, TextLength)`)
- `RangeFromChild(...)`:v1 と同(空範囲)
- `RangeFromPoint(Point)`:**本実装**=`host.OffsetFromScreenPoint(x, y)` → `TextRangeProviderV2(pos, pos)`(v1 は `(0, 0)` スタブだった)
- `DocumentRange`:v1 と同
- `SupportedTextSelection`:v1 と同(`Single`)

### 2-10. IUiaTextHost の v1/v2 並存戦略

- **v1(`IUiaTextHostLegacy`)は温存**:ScintillaHost が実装し、App 層の SR 対応(NVDA 経路 / PC-Talker 経路 / CsvFocusSink)に使い続ける。**P5 の実装作業では ScintillaHost / v1 用 Provider 群 / v1 用 TextNavigation に一切手を入れない**(interface 名リネームの機械的置換 1 回のみ実施)
- **v2(`IUiaTextHost`)は新設**:EditorControl(自作)が実装し、smoke バイナリ(`--uia`)経由での SR 検証にのみ使う
- **App 層は現行のまま**:編集エンジンは Scintilla(=`ScintillaHost`)を使い続ける=**P5 期間中の App 層 SR 対応に一切劣化なし**
- P6 で v1 系(`IUiaTextHostLegacy` / v1 Provider 群 / `TextNavigation`)と ScintillaHost / `Scintilla5.NET` PackageReference / Sci.cs / App 層関連コードを**一括撤去**(あなたの指示に沿う)
- v1/v2 並存のコスト:v1 用ファイルは変更ゼロ(interface 名リネームの機械的置換のみ)。UI 表面/挙動は v1(App 層本番)と v2(smoke)で完全に独立=お互い干渉しない

---

## 3. データフロー

### 3-1. EditorControl の新設内部 API

```csharp
// フィールド
private TextControlProvider? _provider;              // WM_GETOBJECT の応答用(lazy)
private volatile TextSnapshot? _bufferSnapshot;      // RPC スレッドが読む不変参照
private volatile Frame? _lastFrame;                  // 座標算出用(OnPaint 完了時に更新)
private int _clientToScreenX, _clientToScreenY;     // UI スレッドで更新するキャッシュ

// UI スレッド側フック
private void CacheSnapshotAndBounds()               // AfterEdit / OnResize / OnLocationChanged / OnPaint 末尾で呼ぶ
private void PublishFrame(Frame frame)              // OnPaint で最新 Frame を _lastFrame へ

// RPC スレッド側ヘルパ(純関数=IUiaTextHost 実装から呼ぶ)
private static int HitTestOffset(TextSnapshot snap, Frame frame, int clientX, int clientY);
private static double[] BuildBoundingRects(TextSnapshot snap, Frame frame, int start, int end, int csx, int csy);
```

### 3-2. WM_GETOBJECT 応答フロー

```
WM_GETOBJECT(lParam=UiaRootObjectId)
  ├─ _provider ??= new TextControlProvider(this)
  ├─ m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, wParam, lParam, _provider)
  └─ return(base 不呼出=OBJID_CLIENT 系の MSAA プロキシ生成を防ぐ)

WM_GETOBJECT(lParam=OBJID_CLIENT / OBJID_WINDOW / その他)
  └─ base.WndProc(=DefWindowProc に流す・自前は応答しない)

WM_GETTEXT / WM_GETTEXTLENGTH
  ├─ m.Result = IntPtr.Zero
  └─ return(本文非公開=SR は UIA 経路のみで読む)
```

### 3-3. UIA イベント発火フロー

```
編集(AfterEdit 内):
  ├─ TextBuffer 更新
  ├─ _bufferSnapshot = _buffer.Current           (RPC スレッドから見える最新化)
  ├─ RaiseUia(TextChangedEvent)
  ├─ 選択再配置 / PositionCaret / BringCaretIntoView
  ├─ if (RaiseUiaSelectionEvents) RaiseUia(TextSelectionChangedEvent)
  └─ Invalidate

キャレット移動(MoveCaretTo / MoveCaretWithSelection):
  ├─ _caret / _anchor 更新
  ├─ PositionCaret
  ├─ if (RaiseUiaSelectionEvents) RaiseUia(TextSelectionChangedEvent)
  ├─ RaiseCaretEnteredEmptyLineIfNeeded    (P3 実装済・App 層 Announcer 経由で「空行」発声)
  └─ Invalidate

OnGotFocus:
  ├─ _hasFocus = true
  ├─ CreateCaret + PositionCaret + ShowCaret
  ├─ RaiseUia(AutomationFocusChangedEvent)
  └─ RaiseUia(TextSelectionChangedEvent)     ← 明示発火(PC-Talker 2秒ポーリング対策・HANDOFF §13.6)
```

### 3-4. スナップショット/座標キャッシュ同期

- `AfterEdit`(P3 導入)末尾で `_bufferSnapshot = _buffer.Current` を実行=編集の**線形化点**
- `OnPaint` 末尾で `_lastFrame = frame` / `CacheClientToScreenOffsets()` を実行=描画完了 = 座標が最新化されたことの**線形化点**
- 選択のみの変更(編集なし)では `_bufferSnapshot` は再代入不要(参照が変わらない)

### 3-5. Announcer スタブ(smoke 用)

`tests/yEdit.Editor.Smoke/UiaSmokeAnnouncer.cs`:

- `RaiseAutomationNotification`(UIA 5.1 以降)+ フォールバックで `PCTKPReadW` 直叩き=既存 App 層 Announcer の最小サブセット
- 配線:
  - `CaretEnteredEmptyLine` → `"空行"` を Notification 発火
  - 新規 `WordNavigatedEvent`(EditorControl P5 で先出し=Ctrl+←→ 移動後に単語スパンを引数として発火) → `snap.GetTextRange(wordStart, wordEnd - wordStart)` を Notification 発火
- **P6 では**この Announcer を捨て、App 層本物 `Announcer` に置き換える=EditorControl のイベント表面(`CaretEnteredEmptyLine` + `WordNavigatedEvent`)は先出しで完成

### 3-6. `WordNavigatedEvent`(P5 で新設・EditorControl イベント)

```csharp
public event EventHandler<WordNavigatedEventArgs>? WordNavigated;

public sealed class WordNavigatedEventArgs : EventArgs
{
    public int WordStart { get; }
    public int WordEnd   { get; }
    public WordNavigatedEventArgs(int s, int e) { WordStart = s; WordEnd = e; }
}
```

- 発火点:P3 `OnKeyDown` の Ctrl+←→ 分岐末尾(選択拡張中でない場合のみ=`!shift` かつ移動が発生した場合)
- 発火時のスパン計算:移動前の `_caret` を A、移動後の `_caret` を B として `[min(A,B), max(A,B))` を単語スパンとして通知
- **抑止条件**:`RaiseUiaSelectionEvents == false` のとき発火しない(App 層 CSV セルナビ配線と同じフラグに相乗り)
- **設計判断**:PC-Talker の Ctrl+←→ 単語ナビは P0 で「クライアント側が Word 単位を呼ばない=プロバイダ側では改善不能」と確定。App 層能動発声で補うのが唯一の解=EditorControl は「単語ナビが発生した」だけ通知し、発声は App 層(または smoke Announcer)に任せる

---

## 4. 縁ケース / エラー処理

### 4-1. Handle 未生成時の UIA 呼出

- `IUiaTextHost.Handle` は `_hwnd`(= `OnHandleCreated` で `Handle` を代入)。Handle 未生成なら `IntPtr.Zero`
- プロバイダ側では `HostRawElementProvider = AutomationInteropProvider.HostProviderFromHandle(_host.Handle)` が `IntPtr.Zero` で null を返す=正常経路

### 4-2. `_bufferSnapshot` 未初期化(SetSource 前の UIA 呼出)

- `IUiaTextHost.TextLength` → 0
- `GetTextRange(s, len)` → `""`
- 位置歩き 8 メンバ → 全て 0 または clamp
- 座標 API → `Array.Empty<double>()` / 0
- =SR には「空文書」に見える(=何もない安全状態)

### 4-3. RPC スレッド中の編集レース

- `IUiaTextHost` メンバの先頭で `var snap = _bufferSnapshot;` を**一度だけ**取得
- 以降は snap の**不変**参照上で計算=編集で `_bufferSnapshot` が差し替わっても取得済み snap は自己整合
- 例外:選択位置は `_caret`/`_anchor`(volatile int)で単体読み=1 UIA 呼出中に少なくとも self-consistent(読み瞬間の値)。UIA 仕様上「今この瞬間の値」を返すのが正解

### 4-4. サロゲート中間への `SetSelection`

- `SetSelection(int, int)` は UI スレッドで `SetCaretCharOffset` 相当(P3 実装済)を経由=既存の `SnapAndClamp` でサロゲート中間はスナップされる
- 範囲外は `[0, TextLength]` に clamp

### 4-5. `Frame` 未確定(OnPaint 前の座標 API 呼出)

- `_lastFrame` が null なら `GetBoundingRectangles` → `Array.Empty<double>()`、`OffsetFromScreenPoint` → 0
- 実運用では `OnHandleCreated` → 初回 `OnPaint` の間に UIA が座標を要求するのは稀。安全側の no-op で十分

### 4-6. `_lastFrame` と `_bufferSnapshot` の版ズレ

- `_bufferSnapshot` は編集で更新、`_lastFrame` は OnPaint で更新=編集直後で `_lastFrame` が古いことがある
- 対応:`GetBoundingRectangles` の中で `frame.SnapshotIdentity == snap.RootIdentity` のような整合性チェックを試みる or 単に「古い frame でも clamp して描画上の妥当な矩形を返す」で許容
- **v1 判断**:後者(単純に clamp)。SR は近似矩形でも SayAll/選択ハイライトが動くため実害少ない。P5 中間検証で表出しなければ確定

### 4-7. UIA 未リッスン時のイベント発火

- `AutomationInteropProvider.ClientsAreListening` が false ならイベント発火をスキップ(v1 実装踏襲)= SR 起動していない環境で無駄なコストを払わない

### 4-8. 大容量(1GB)での UIA 応答

- 位置歩きは全て O(log n)(`TextSnapshot.GetLineIndexOfChar` 等)
- 座標 API は「[start, end) の逐行分解」=SR が要求する範囲は行数分。SayAll 単位は 1 行=O(log n + 1 行の桁数)で応答
- **`GetVisibleRanges` は `(0, TextLength)`** を返す=UIA 仕様上「文書全体が visible」と主張。SR はここから小さい TextUnit で読み進むため実害なし

### 4-9. `RangeFromPoint` の範囲外座標

- スクリーン→クライアント変換後、クライアント矩形の外なら 0 または clamp して行頭を返す(v1 挙動を優先=SR がクラッシュしない安全側)

### 4-10. App 層 `RaiseUiaSelectionEvents=false` での UIA 期待動作

- 現行 App 層(CsvFocusSink 等)が false にする用途:「SR が UIA の TextSelectionChanged を追いすぎるのを一時抑止」
- P5 では受け口だけ配線(P6 の App 層置換で本挙動配線)。抑止時の空行イベントは発火しない(既存 P3 挙動を維持)

---

## 5. テスト戦略

### 5-1. 自動テスト範囲

**`tests/yEdit.Core.Tests/`** に追加(純ロジック):
- `IUiaTextHost.GetBoundingRectangles` の逐行分解ヘルパ(private static → internal 露出)を xUnit で:
  - 単一行選択の矩形長
  - 複数行選択の矩形数と各行長
  - 空選択(start==end)の空配列
  - 範囲外 clamp
- `HitTestOffset` の x,y →オフセット変換:
  - 行頭/行末/中間位置
  - サロゲート境界のスナップ
  - 空行での座標
  - スクロール中のオフセット反映

**`tests/yEdit.Editor.Tests/EditorControlUiaTests.cs` 新設**(WinForms STA):
- `WM_GETOBJECT(UiaRootObjectId)` → 非 null 応答
- `WM_GETOBJECT(OBJID_CLIENT)` → 0 応答(=既定 MSAA プロキシ抑止)
- `WM_GETTEXT` / `WM_GETTEXTLENGTH` → 0 応答
- `TextControlProvider` の各 UIA プロパティ:Name="本文" / AutomationId="editor" / ControlType=Document
- IUiaTextHost v2 の RPC スレッド安全性:別スレッドで `GetTextRange` を叩きながら UI スレッドで編集=クラッシュ/例外なし
- イベント発火順:編集→ TextChanged→ TextSelectionChanged / Focus→ FocusChanged→ TextSelectionChanged
- `_bufferSnapshot` の線形化:編集直後の UIA 読みが**更新後**の内容を返す

**`tests/yEdit.Editor.Tests/EditorControlWordNavEventTests.cs` 新設**:
- Ctrl+→ 発火時 `WordNavigated` イベントの `WordStart`/`WordEnd` が Core `WordBoundary` と一致
- Shift+Ctrl+→(選択拡張)では発火しない
- `RaiseUiaSelectionEvents=false` で発火しない

### 5-2. SR 非依存回帰スクリプト(`tools/*.ps1`)

**`tools/verify-uia.ps1`**(既存)を新 EditorControl 向けに調整:
- 起動対象を `yEdit.Editor.Smoke.exe --uia` に変更
- `FindFirst` の `AutomationId="editor"` に変更
- ControlType=Document / Name="本文" の検証
- `TextPattern` の GetSelection / DocumentRange / RangeFromPoint(有効化)確認

**`tools/walk-test.ps1`**(既存):
- 単文字/行歩き(Character/Line)+選択→ GetText(=範囲テキスト取得)→ 期待値比較
- 空行の len=0 確認(現行 v1 相当を新実装で通す)

**`tools/word-sim.ps1`**(P0 で導入):
- 単語ナビ(NextWord/PrevWord)→ WordStart/WordEnd → GetText の 6 パターン(P0 の word-sim を新 EditorControl 上で PASS)

### 5-3. Smoke 拡張(`tests/yEdit.Editor.Smoke`)

- `--uia` サブコマンド:UiaSmokeAnnouncer を配線+タイトルバーに UIA event ログを表示
- 起動時に「本文」の Name / AutomationId="editor" / ControlType=Document を UIA 側から拾えることを起動直後に確認(自己診断ログを出す)
- `--ime` サブコマンド(P4 で導入)と互換=`--uia --ime` の同時指定で SR + IME 統合検証(ATOK 7 項目)

### 5-4. 応答性ベンチ

`tests/yEdit.Core.Bench/` に `--uia` モード追加:
- `GetTextRange(random_pos, 200)` × 10,000 回:目標 <100µs/回
- `NextChar/PrevChar/LineStartOf/LineEndNoBreakOf` × 10,000 回:目標 <10µs/回
- `WordStart/WordEnd/NextWordStart/PrevWordStart` × 10,000 回:目標 <100µs/回
- `GetBoundingRectangles(random_range)` × 10,000 回:目標 <1ms/回
- 1GB データ(P1/P2 と同じ生成器)で全項目 PASS が DoD

### 5-5. Spy++ / GetWindowText 目視検証

- `smoke --uia` 起動状態で Spy++ の Text 欄が空であること(=`WM_GETTEXT` に本文を返していない)
- `Send Message` で `WM_GETTEXTLENGTH` を送って 0 が返ること
- 手順書は`docs/plans/2026-07-06-p5-uia-checklist.md`(=下記 5-6 と同居)

### 5-6. 実機中間検証チェックリスト(=P5 の最終ゲート・ユーザー実施)

**`docs/plans/2026-07-06-p5-uia-checklist.md`** 新設(P0 のフォーマットを踏襲):

| # | 検証項目 | 期待挙動 |
|---|---|---|
| 1 | Windows キー + Ctrl+Enter で SR 起動 → smoke --uia を起動 | フォーカスで「本文」と発声 |
| 2 | 単文字ナビ(← → ↑ ↓) | 移動先の 1 文字を発声 |
| 3 | 行ナビ(Home / End / 上下矢印) | 行内容を発声(空行は「空行」または SR 既定) |
| 4 | SayAll(NVDA: NVDA+↓ / PC-Talker: 全文読み) | 先頭から末尾まで途切れず読む |
| 5 | 選択読み(Shift+矢印) | 選択スパンを発声 |
| 6 | Ctrl+←→ 単語ナビ | 単語スパンを発声(PC-Talker は smoke Announcer 経由) |
| 7 | 空行に着地 | 「空行」または SR 既定発声 |
| 8 | 他ウィンドウ→ smoke に復帰 | フォーカス時に位置と本文の一部を発声(TextSelectionChanged 明示発火の確認) |
| 9 | Spy++ で Text 欄空・WM_GETTEXTLENGTH=0 | ネイティブ表面原則の確認 |
| 10-16 | ATOK 7 項目(P4 checklist)+ SR 読み上げ検証 | 全項目 SR が未確定/確定/変換対象節を読む |

- ログは既存 `SrDiagLog` 方式流用(P5 UIA フック名で分離)。TracingUia(P0 導入)を smoke に組み込み可能にしておく(既定 OFF・環境変数で ON)
- **NG 判定 → 撤退**:`git revert` で P5 コミット群を戻せば P4 完了状態(683 テスト緑・build 0 警告)に戻る=設計書§4 リスク表

---

## 6. Task 分解(15 Task 案)

| # | Task | 主な成果物 |
|---|---|---|
| 1 | v1 の `IUiaTextHost` → `IUiaTextHostLegacy` へリネーム(interface + ScintillaHost 実装 + Provider 型引数の機械的置換) | v1 系ファイル 5 個に対し interface 名リネームのみ / 既存 v1 テスト全通過 / build 0 警告 |
| 2 | `IUiaTextHost` v2 定義新設(範囲ベース + 位置歩き + 座標 API) | `src/yEdit.Accessibility/IUiaTextHost.cs` 新設 / 純 interface / xUnit のスタブ実装で契約テスト追加 |
| 3 | `TextRangeProviderV2` 新設 | v1 のロジック(Move スパン保持等)を踏襲 / 全 API を v2 メンバ経由へ / xUnit 追加(既存 v1 テストと並存) |
| 4 | `TextProviderImplV2` + `TextControlProviderV2` 新設 | RangeFromPoint 本実装受け皿 / v1 と挙動一致の契約テスト |
| 5 | EditorControl に `IUiaTextHost` v2 実装 | `_bufferSnapshot`(volatile) / 位置歩き 8 メンバ委譲(Core `WordBoundary` へ) / GetTextRange / 選択の RPC スレッド安全性 |
| 6 | `WM_GETOBJECT` / `UiaRootObjectId` 応答 + プロバイダ生成 | WndProc 追加(P4 の IME 経路より前段) / `_provider` lazy 生成 / TextControlProviderV2 生成配線 |
| 7 | ネイティブ表面原則:`WM_GETTEXT` / `WM_GETTEXTLENGTH` 抑止 | m.Result=0 応答 / `OBJID_CLIENT` は base に流さない仕上げ |
| 8 | UIA イベント発火配線:`TextChangedEvent` / `TextSelectionChangedEvent` | `AfterEdit`(P3)末尾 / `MoveCaretTo` 末尾 / `RaiseUiaSelectionEvents` 抑止条件 |
| 9 | `AutomationFocusChangedEvent` + フォーカス時 TextSelectionChanged 明示発火 | `OnGotFocus` 内で 2 イベント発火(PC-Talker 2 秒ポーリング対策) |
| 10 | 座標 API 本実装:`GetBoundingRectangles(s,e)` | `_lastFrame` / `_clientToScreen*` キャッシュ / 逐行分解ヘルパ / xUnit 純ロジック追加 |
| 11 | 座標 API 本実装:`OffsetFromScreenPoint` + `TextProviderImplV2.RangeFromPoint` | HitTest 相当を internal 露出 / RPC スレッド安全化 / xUnit 追加 |
| 12 | `WordNavigatedEvent` 先出し(EditorControl)+ Core `WordBoundary` 委譲 | Ctrl+←→ 末尾で発火 / `RaiseUiaSelectionEvents` 抑止 / `EditorControlWordNavEventTests` |
| 13 | tools/*.ps1 の新 EditorControl 向け派生 | verify-uia-editor.ps1 / walk-test-editor.ps1 新設 / word-sim を smoke --uia 上で PASS / v1 用スクリプトは温存 |
| 14 | Smoke 拡張:`--uia` + UiaSmokeAnnouncer + 実機チェックリスト作成 + 別エージェント最終レビュー + 設計書§3 追記 | UIA 配線 / Announcer スタブ / `docs/plans/2026-07-06-p5-uia-checklist.md`(16 項目) / Critical/Important 潰し → 設計書追記 → メモリ更新 → 実機ゲート判定 |

### 6-1. 運用ルール(P0〜P4 と同じ)

- 各タスク完了時に 1 コミット(要点=Task 番号 + 変更概要)
- **main には触れない**(§運用・P7 まで worktree に閉じる)
- 別エージェント subagent-driven-development で実装(P4 と同じ流儀を採用予定)
- **ScintillaHost / App 層は完全無変更**(=Task 1 の interface 名リネームの機械的置換のみが Scintilla 系ファイルへの touch)

### 6-2. 規模見積り

- 設計書§5 の「SR 接続約 300 行」+`IUiaTextHost` v2 新設(v1 は温存で新規追加分のみ)
- 純ロジック(座標算出/HitTest)+ WndProc 拡張+ v2 用 Provider 群新設+ イベント配線で 概ね 500〜700 行(v1 温存で v1 側は書き換えない一方、v2 側は新規実装 4 ファイル)
- テスト側の追加は 25〜35 件想定(WinForms STA 経由 + Core 側純ロジック + v2 Provider 契約)

---

## 7. リスクと撤退基準

| リスク | 判明時期 | 撤退時の損失 |
|---|---|---|
| PC-Talker が新プロバイダを認識しない(WM_GETOBJECT 応答が届いていない) | Task 6〜8 実装後の smoke 目視 | 該当 Task の再設計(=WndProc 順序/UiaProbe との差分検証) |
| NVDA が SayAll で途切れる | Task 14 実機中間 | 該当 Task 内で MSAA プロキシ生成の可能性を調査(OBJID_CLIENT 応答有無・BoundingRectangle キャッシュ整合) |
| 1GB での UIA 応答が目標 µs 台に届かない | Task 5/10 ベンチ | 位置歩きのキャッシュ導入(TextSnapshot 側の既存 O(log n) を再利用) |
| ATOK 実機で SR が未確定文字列を全く読まない | Task 14 実機ゲート | 撤退可能(App 層は Scintilla 経路のまま無変更なので影響ゼロ=`git revert` で P5 群を戻せば P4 完了状態) |
| PC-Talker 単語ナビが smoke Announcer 経由でも読まれない | Task 14 実機ゲート | Announcer の Notification 種別/優先度を調整・NG なら v1 は「1 文字読み」で許容(P0 で確定した既知課題として明記) |

**撤退時の安全性**: **App 層 / ScintillaHost / v1 系 UIA コードは P5 で一切触らない**ため、P5 の 14 Task 分のコミットを `git revert` すれば P4 完了状態(683 テスト緑・build 0 警告)に戻る。撤退時のリスクは v1 系より低い(P5 の変更は追加ファイル+EditorControl 拡張のみで既存の削除を含まない)。**設計書§3 運用「途中フェーズで main に触れない」が撤退安全性の担保**。

---

## 8. 次フェーズへの申し送り

- **P6(App 層一発置き換え + 一括撤去)**:
  - App 層の編集エンジン結線を Scintilla → EditorControl に差替
  - `SrRoute.Nvda` / `CsvFocusSink` / `ServeUiaProvider` / `ApplySrAdaptation` は**無効化のみで残す**(切り分け用)
  - EditorControl の `WordNavigatedEvent` / `CaretEnteredEmptyLine` を App 層本物 Announcer に配線
  - smoke `UiaSmokeAnnouncer` は削除
  - **一括撤去**(P5 で温存した Scintilla / v1 系を全部消す):
    - `ScintillaHost.cs` / `Sci.cs` / `Scintilla5.NET` PackageReference 削除(Scintilla / Lexilla ネイティブ DLL の同梱停止)
    - `IUiaTextHostLegacy` / v1 用 Provider 群(`TextControlProvider` / `TextProviderImpl` / `TextRangeProvider` / `TextNavigation`)削除
    - App 層で ScintillaHost / v1 UIA に触っている全コード削除・書換
    - `tools/verify-uia.ps1` / `walk-test.ps1`(v1 用)削除、`-editor` サフィックス版を正本に
- **P7(実機総合検証)**:
  - 3 SR × 主要全機能(検索/置換/CSV/Markdown/バックアップ/Grep/行ジャンプ/文字情報)+復帰フォーカス+空行+タブ切替
  - `RaiseUiaSelectionEvents` / `CsvFocusSink` の**完全撤去**(P5 で無効化のみで残していたもの)
- **v1 スコープ外の申し送り**:
  - `TextChangedEvent` の詳細化(挿入位置/削除範囲を含めた `TextEditType`):UIA 5.1 以降で対応可・v1 は「なにか変わった」通知のみ
  - 再変換(`IMR_RECONVERTSTRING`)の UIA 露出:v2 以降
  - 大容量二層化時の `GetVisibleRanges` 精密化(可視行のみ返す):App 層置換で二層化フラグを受けてから

---

**関連**:
- 親設計書: `docs/plans/2026-07-05-custom-editcontrol-design.md`(§2-4 IUiaTextHost v2 / §2-7 ネイティブ表面原則 / §3-P5 スコープ)
- P0 実機結果: `docs/plans/2026-07-05-p0-sr-probe-checklist.md`(SR 呼出しトレース分析・PC-Talker 単語ナビの App 層補完必要性)
- P3 実装計画: `docs/plans/2026-07-05-p3-editor-input.md`(`CaretEnteredEmptyLine` / `RaiseUiaSelectionEvents` / `AfterEdit` の実装済み受け口)
- P4 実装計画: `docs/plans/2026-07-06-p4-ime.md`(WndProc 追加地点=P5 の WM_GETOBJECT 挿入位置)
- P4 チェックリスト: `docs/plans/2026-07-06-p4-ime-checklist.md`(P5 で SR 読み上げ検証と統合して再実施)
- 参照実装(P5 で自作 EditorControl へ移植する元ネタ・移植後は元は削除 or 用済み): `src/yEdit.UiaProbe/UiaTextControl.cs`(WM_GETOBJECT / IUiaTextHost v1 / UIA イベント発火 / Bounds キャッシュパターン / RaiseUia ヘルパ)
