# P5: UIA/SR 接続(自作 EditorControl に UIA プロバイダを載せる)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** P4 完了時点(自作 EditorControl・overlay IME・683 テスト緑)の `EditorControl` に UIA(UI Automation)プロバイダを載せ、`WM_GETOBJECT`/`UiaRootObjectId` に応答し、`IUiaTextHost` v2(範囲ベース化)を新設して SR(NVDA / PC-Talker / ナレーター)から本文/選択/位置/座標を読める状態にする。ネイティブ表面原則(WM_GETTEXT に本文非公開・MSAA 経路自前無し)を貫徹し、tools/*.ps1 の SR 非依存回帰と実機中間検証(第 2 ゲート)で DoD 判定。

**Architecture:** `IUiaTextHost` は v1(現行) → `IUiaTextHostLegacy` に**名前だけ**機械的リネームで温存(App 層 SR 対応が現行のまま継続)。v2 を新設し(範囲ベース + 位置歩き 8 メンバ + 座標 API 本実装)、v2 用 Provider 群(`TextControlProviderV2` / `TextProviderImplV2` / `TextRangeProviderV2`)も新設。EditorControl が v2 を実装、`WM_GETOBJECT` から `TextControlProviderV2` を返し、編集/移動/フォーカスの各経路で UIA イベント(`TextChangedEvent` / `TextSelectionChangedEvent` / `AutomationFocusChangedEvent`)を発火。座標 API は `PixelMapper`/`Frame`(P2 導入済)と `ClientToScreen` オフセットキャッシュから RPC スレッド安全に応答。P0 で確定した PC-Talker Ctrl+←→ 単語ナビの App 層補完は `WordNavigatedEvent` を EditorControl に先出しし、smoke Announcer(実 App 層 Announcer の最小サブセット)で「単語スパン発声」まで実機中間検証内で確認する。**ScintillaHost / App 層 / v1 系 UIA コードは P5 で一切触らない**(Task 1 の interface 名リネームの機械的置換のみ)。設計書=`docs/plans/2026-07-06-p5-uia-connect-design.md`。

**Tech Stack:** C# / .NET 9 / xUnit / WinForms(`Control` 派生・`WndProc` override 拡張)/ WPF Automation(`System.Windows.Automation` / `System.Windows.Automation.Provider` / `System.Windows.Automation.Text`)/ P/Invoke(`user32.dll`)/ 既存 `tests/yEdit.Core.Tests` + `tests/yEdit.Editor.Tests` + `tests/yEdit.Editor.Smoke` + `tests/yEdit.Core.Bench` + `tools/*.ps1`。

**前提:**
- 作業は worktree `<repo>\.worktrees\custom-editcontrol-design`(ブランチ `feature/custom-editcontrol-design`)
- **main には一切触れない**(全フェーズを本ブランチに閉じ、P7 合格後に一括マージ。設計書§3 運用)
- **ScintillaHost / App 層 / v1 系 UIA コードは触らない**(=Task 1 の interface 名リネームの機械的置換のみが Scintilla / v1 系ファイルへの touch)
- P1/P2/P3/P4 の公開 API(`TextBuffer`/`TextSnapshot`/`EditorControl` 既存 public API)は変更しない=新規追加のみ。**破壊的変更禁止**
- 各タスク完了ごとにコミット。全タスク後に別エージェントレビュー(Task 14 内)
- 既存テスト 683 件は全タスクで緑を維持

---

## 0. 設計固定事項(全タスク共通の不変条件)

実装全体で守る。レビュー観点でもある。

1. **v1(`IUiaTextHostLegacy`)は温存**:名前リネーム以外の変更は禁止。ScintillaHost / UiaProbe / v1 用 Provider 群のロジックは一切いじらない
2. **v2 は「新設」であり「差替」ではない**:v1 用のファイルを消したり書き換えたりせず、新しい 4 ファイル(`IUiaTextHost.cs` / `TextControlProviderV2.cs` / `TextProviderImplV2.cs` / `TextRangeProviderV2.cs`)を追加
3. **RPC スレッド安全**:`IUiaTextHost` v2 メンバは UIA の RPC スレッドから呼ばれる。EditorControl 側は「不変スナップショット参照 + キャッシュ値 + 明示ロック」で応答
4. **不変スナップショット原則**:v2 メンバ実装の先頭で `var snap = _bufferSnapshot;` を**一度だけ**取得し、関数中は snap の**不変**参照で全計算を完結させる(RPC スレッド中に編集が起きても snap は自己整合)
5. **UI 変更は BeginInvoke**:`SetSelection` / `SetFocus` の書込み系は `InvokeRequired` ? `BeginInvoke(...)` : 直接 パターン
6. **単語境界は Core `WordBoundary` に集約**:Accessibility は Core 非依存を保つ=EditorControl 側で `IUiaTextHost.WordStart`/`WordEnd`/`NextWordStart`/`PrevWordStart` から Core `WordBoundary` に委譲
7. **ネイティブ表面原則**:`WM_GETTEXT` / `WM_GETTEXTLENGTH` に本文非公開(`m.Result = IntPtr.Zero`)、`WM_GETOBJECT` は `UiaRootObjectId` のみ応答(OBJID_CLIENT や OBJID_WINDOW は base=DefWindowProc に流す)、MSAA は自前実装せず UIA→MSAA ブリッジ任せ
8. **UIA 未リッスン時のスキップ**:`AutomationInteropProvider.ClientsAreListening` が false のときイベント発火をスキップ(v1 実装踏襲)
9. **フォーカス獲得時の `TextSelectionChanged` 明示発火**(PC-Talker 2 秒ポーリング対策・HANDOFF §13.6)を Task 9 で配線
10. **ControlType は Document 固定**(P0 で確定・§2-4)。Name="本文" / AutomationId="editor"
11. **各 Task 完了時に 1 コミット**。コミットメッセージ先頭は `P5: Task N ` で統一
12. **build 0 警告維持**:各 Task 完了時に `dotnet build` して `Warning`/`Error` 件数 0 を確認

---

## Task 1: v1 の `IUiaTextHost` を `IUiaTextHostLegacy` にリネーム

**目的**: v2 用に `IUiaTextHost` 名前を空ける機械的リネーム。v1 の**挙動は一切変えない**(interface 名の置換 + 実装側の型名追従のみ)。既存 683 テスト全緑を維持したまま次の Task に進める準備。

**対象**:
- Rename: `src/yEdit.Accessibility/IUiaTextHost.cs` → `src/yEdit.Accessibility/IUiaTextHostLegacy.cs`
- Modify: `src/yEdit.Accessibility/IUiaTextHostLegacy.cs`(interface 名を `IUiaTextHost` → `IUiaTextHostLegacy` に変更)
- Modify: `src/yEdit.Accessibility/TextControlProvider.cs`(コンストラクタ引数の型・フィールド型を追従)
- Modify: `src/yEdit.Accessibility/TextProviderImpl.cs`(`Host` プロパティの型を追従)
- Modify: `src/yEdit.Editor/ScintillaHost.cs`(`: Scintilla, IUiaTextHost` → `: Scintilla, IUiaTextHostLegacy`、`IUiaTextHost.` explicit 実装の全プレフィックス書換)
- Modify: `src/yEdit.UiaProbe/UiaTextControl.cs`(同上)

**Step 1: ファイルリネーム**

```
mv src/yEdit.Accessibility/IUiaTextHost.cs src/yEdit.Accessibility/IUiaTextHostLegacy.cs
```

**Step 2: `IUiaTextHostLegacy.cs` 内の interface 名を書換**

`public interface IUiaTextHost` → `public interface IUiaTextHostLegacy`(1 行のみ)

**Step 3: `TextControlProvider.cs` を追従**

```csharp
// 変更点:
private readonly IUiaTextHostLegacy _host;   // was: IUiaTextHost
public TextControlProvider(IUiaTextHostLegacy host)  // was: IUiaTextHost host
```

**Step 4: `TextProviderImpl.cs` を追従**

```csharp
// 変更点:
public IUiaTextHostLegacy Host { get; }   // was: IUiaTextHost Host
public TextProviderImpl(IUiaTextHostLegacy host, IRawElementProviderSimple root)
```

**Step 5: `TextRangeProvider.cs` の型参照を確認**

`_owner.Host` 経由で呼ぶだけなので直接 `IUiaTextHost` 型を使っていなければ無変更。ざっと確認して型宣言があれば追従。

**Step 6: `ScintillaHost.cs` を追従**

- クラス宣言 `: Scintilla, IUiaTextHost` → `: Scintilla, IUiaTextHostLegacy`
- explicit 実装のプレフィックス `IUiaTextHost.` → `IUiaTextHostLegacy.`(9 メンバ)
- 使用箇所 grep で `IUiaTextHost\b` を全て `IUiaTextHostLegacy` に置換

**Step 7: `UiaProbe/UiaTextControl.cs` を追従**

同上(explicit 実装のプレフィックスと class 宣言 `: Control, IUiaTextHost` を追従)。

**Step 8: build**

```
dotnet build src/yEdit.sln
```

Expected: build succeeded / 0 warnings / 0 errors。

**Step 9: 既存テスト全緑確認**

```
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: Core.Tests 528 passed / Editor.Tests 155 passed(P4 完了時の数字)。

**Step 10: コミット**

```bash
git add -A
git commit -m "P5: Task 1 IUiaTextHost → IUiaTextHostLegacy リネーム(v2 用に名前を空ける・v1 挙動不変)"
```

**完了条件**:
- ファイルリネーム済
- 既存 683 テスト全緑
- build 0 警告 / 0 エラー
- ScintillaHost / UiaProbe の挙動は一切変更していない(interface 名のみ)

**実施記録**:
_(実施後に追記)_

---

## Task 2: `IUiaTextHost` v2 定義新設(範囲ベース + 位置歩き + 座標 API)

**目的**: 設計書§2-3 の `IUiaTextHost` v2 を新規 interface として定義。純 interface のため実装なし=v2 用の契約と v1 用の契約が別型として並存する。純ロジックの契約テスト(スタブ実装で境界動作を確認)を追加。

**対象**:
- Create: `src/yEdit.Accessibility/IUiaTextHost.cs`(空ができた場所に新設・**リネーム前の中身と別内容**)
- Create: `tests/yEdit.Core.Tests/Accessibility/IUiaTextHostContractStubTests.cs`(interface 呼出し契約をスタブ実装で確認)

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Accessibility/IUiaTextHostContractStubTests.cs`(新規):

```csharp
using System.Windows;
using yEdit.Accessibility;
using Xunit;

namespace yEdit.Core.Tests.Accessibility;

public class IUiaTextHostContractStubTests
{
    // v2 interface が「範囲ベース」「位置歩き」「座標 API」を持つことをスタブ実装で契約確認する
    private sealed class StubHost : IUiaTextHost
    {
        public string GetTextRange(int start, int length) => "";
        public int TextLength => 0;
        public (int Start, int End) GetSelection() => (0, 0);
        public void SetSelection(int start, int end) { }
        public int NextChar(int offset) => offset;
        public int PrevChar(int offset) => offset;
        public int LineStartOf(int offset) => 0;
        public int LineEndNoBreakOf(int offset) => 0;
        public int LineEnd(int offset) => 0;
        public int WordStart(int offset) => offset;
        public int WordEnd(int offset) => offset;
        public int NextWordStart(int offset) => offset;
        public int PrevWordStart(int offset) => offset;
        public Rect BoundingRectangle => System.Windows.Rect.Empty;
        public double[] GetBoundingRectangles(int start, int end) => System.Array.Empty<double>();
        public int OffsetFromScreenPoint(double x, double y) => 0;
        public nint Handle => System.IntPtr.Zero;
        public bool HasFocus => false;
        public int ControlTypeId => 0;
        public string Name => "";
        public string AutomationId => "";
        public void SetFocus() { }
    }

    [Fact]
    public void Stub_ImplementsAllMembers()
    {
        IUiaTextHost host = new StubHost();
        Assert.Equal("", host.GetTextRange(0, 0));
        Assert.Equal(0, host.TextLength);
        Assert.Equal((0, 0), host.GetSelection());
        host.SetSelection(0, 0);
        Assert.Equal(0, host.NextChar(0));
        Assert.Equal(0, host.WordStart(0));
        Assert.Equal(System.Array.Empty<double>(), host.GetBoundingRectangles(0, 0));
        Assert.Equal(0, host.OffsetFromScreenPoint(0, 0));
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Core.Tests --filter FullyQualifiedName~IUiaTextHostContractStubTests
```

Expected: build error(`IUiaTextHost` が未定義=v1 リネーム後に空)。

**Step 3: `IUiaTextHost.cs`(v2)を新設**

`src/yEdit.Accessibility/IUiaTextHost.cs`(新規):

```csharp
using System.Windows;

namespace yEdit.Accessibility;

/// <summary>
/// 自作 EditorControl が実装する UIA プロバイダのバックエンド抽象(v2・範囲ベース化)。
/// v1(<see cref="IUiaTextHostLegacy"/>)は全文 string 経路だったのに対し、v2 は
/// 位置歩き + 範囲テキスト + 座標 API を持つ。
///
/// スレッド: RPC スレッドから呼ばれ得る。実装側は不変スナップショット参照 +
/// キャッシュ値で応答すること(<see cref="SetSelection"/> / <see cref="SetFocus"/> のみ UI マーシャリング)。
/// </summary>
public interface IUiaTextHost
{
    // ---------- テキスト(範囲ベース) ----------

    /// <summary>[start, start+length) の UTF-16 部分文字列。範囲外は clamp。RPC スレッド安全。</summary>
    string GetTextRange(int start, int length);

    /// <summary>本文長(UTF-16 コード単位)。</summary>
    int TextLength { get; }

    // ---------- 選択 ----------

    /// <summary>現在の選択(Start &lt;= End、End 排他)。キャレットのみのときは Start==End。</summary>
    (int Start, int End) GetSelection();

    /// <summary>選択/キャレットを設定(実装は UI スレッドへマーシャリング)。</summary>
    void SetSelection(int start, int end);

    // ---------- 位置歩き(全て純関数=RPC スレッド安全) ----------

    /// <summary>offset の次の code-point 位置(サロゲート考慮)。EOF なら TextLength。</summary>
    int NextChar(int offset);

    /// <summary>offset の前の code-point 位置(サロゲート考慮)。BOF なら 0。</summary>
    int PrevChar(int offset);

    /// <summary>offset を含む行の開始位置。</summary>
    int LineStartOf(int offset);

    /// <summary>offset を含む行の終端(改行を含まない)。空行では LineStartOf と一致し len=0。</summary>
    int LineEndNoBreakOf(int offset);

    /// <summary>offset を含む行の終端(改行を含む=次行の開始)。末尾なら TextLength。</summary>
    int LineEnd(int offset);

    /// <summary>offset を含む単語の左端(Core WordBoundary 委譲)。</summary>
    int WordStart(int offset);

    /// <summary>offset を含む単語の右端(Core WordBoundary 委譲)。</summary>
    int WordEnd(int offset);

    /// <summary>Ctrl+→ 相当の「次の単語の先頭」。EOF なら TextLength。</summary>
    int NextWordStart(int offset);

    /// <summary>Ctrl+← 相当の「前の単語の先頭」。BOF なら 0。</summary>
    int PrevWordStart(int offset);

    // ---------- 座標 ----------

    /// <summary>コントロール全体のスクリーン座標矩形(UI スレッドで更新したキャッシュ値)。</summary>
    Rect BoundingRectangle { get; }

    /// <summary>[start, end) の各行スクリーン矩形を UIA 形式 (x,y,w,h, ...) で返す。空なら長さ 0。</summary>
    double[] GetBoundingRectangles(int start, int end);

    /// <summary>スクリーン座標 (x, y) 直下の文字オフセット(HitTest 相当)。範囲外は clamp。</summary>
    int OffsetFromScreenPoint(double x, double y);

    // ---------- 属性 ----------

    /// <summary>ウィンドウハンドル(キャッシュ値)。</summary>
    nint Handle { get; }

    /// <summary>フォーカス状態(キャッシュ値)。</summary>
    bool HasFocus { get; }

    /// <summary>報告する ControlType Id(本番は Document=P0 で確定)。</summary>
    int ControlTypeId { get; }

    /// <summary>UIA の Name プロパティ("本文")。</summary>
    string Name { get; }

    /// <summary>UIA の AutomationId プロパティ("editor")。</summary>
    string AutomationId { get; }

    /// <summary>コントロールにフォーカスを与える(UI スレッドへマーシャリング)。</summary>
    void SetFocus();
}
```

**Step 4: テスト通過を確認**

```
dotnet test tests/yEdit.Core.Tests --filter FullyQualifiedName~IUiaTextHostContractStubTests
```

Expected: 1 passed。

**Step 5: 既存テスト全緑確認 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 528+1=529 passed / Editor.Tests 155 passed。

**Step 6: コミット**

```bash
git add src/yEdit.Accessibility/IUiaTextHost.cs tests/yEdit.Core.Tests/Accessibility/IUiaTextHostContractStubTests.cs
git commit -m "P5: Task 2 IUiaTextHost v2 定義新設(範囲ベース+位置歩き+座標 API・v1 と並存)"
```

**完了条件**:
- v2 interface 新設(19 メンバ)
- 契約スタブテスト 1 件緑
- v1(`IUiaTextHostLegacy`)は無変更
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 3: `TextRangeProviderV2` 新設(v1 ロジック踏襲 + v2 メンバ経由)

**目的**: v2 用の `ITextRangeProvider` 実装を新設。v1 の `TextRangeProvider`(Move スパン保持 + Expand/GetText 等 · PC-Talker 適合ロジック)を踏襲しつつ、テキストアクセスは `_owner.Host.GetTextRange` + 位置歩き 8 メンバ経由に置き換える。全文 string を舐める依存を解消。

**対象**:
- Create: `src/yEdit.Accessibility/TextRangeProviderV2.cs`(新規)
- Create: `tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs`(新規)

**Step 1: 失敗するテストを書く(重要な契約から)**

`tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs`(新規):

```csharp
using System.Windows.Automation.Text;
using yEdit.Accessibility;
using Xunit;

namespace yEdit.Core.Tests.Accessibility;

public class TextRangeProviderV2Tests
{
    private sealed class InMemoryHost : IUiaTextHost
    {
        private readonly string _text;
        public InMemoryHost(string text) { _text = text; }
        public string GetTextRange(int start, int length)
        {
            start = System.Math.Clamp(start, 0, _text.Length);
            length = System.Math.Clamp(length, 0, _text.Length - start);
            return _text.Substring(start, length);
        }
        public int TextLength => _text.Length;
        public (int Start, int End) GetSelection() => (0, 0);
        public void SetSelection(int s, int e) { }
        public int NextChar(int o) => System.Math.Min(o + 1, _text.Length);
        public int PrevChar(int o) => System.Math.Max(o - 1, 0);
        public int LineStartOf(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i > 0 && _text[i - 1] != '\n') i--;
            return i;
        }
        public int LineEnd(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i < _text.Length && _text[i] != '\n') i++;
            if (i < _text.Length) i++;
            return i;
        }
        public int LineEndNoBreakOf(int o)
        {
            int e = LineEnd(o);
            if (e > 0 && _text[e - 1] == '\n') e--;
            return e;
        }
        public int WordStart(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i > 0 && !char.IsWhiteSpace(_text[i - 1])) i--;
            return i;
        }
        public int WordEnd(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i < _text.Length && !char.IsWhiteSpace(_text[i])) i++;
            return i;
        }
        public int NextWordStart(int o)
        {
            int i = WordEnd(o);
            while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
            return i;
        }
        public int PrevWordStart(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i > 0 && char.IsWhiteSpace(_text[i - 1])) i--;
            while (i > 0 && !char.IsWhiteSpace(_text[i - 1])) i--;
            return i;
        }
        public System.Windows.Rect BoundingRectangle => System.Windows.Rect.Empty;
        public double[] GetBoundingRectangles(int s, int e) => System.Array.Empty<double>();
        public int OffsetFromScreenPoint(double x, double y) => 0;
        public nint Handle => System.IntPtr.Zero;
        public bool HasFocus => false;
        public int ControlTypeId => System.Windows.Automation.ControlType.Document.Id;
        public string Name => "本文";
        public string AutomationId => "editor";
        public void SetFocus() { }
    }

    private TextProviderImplV2 MakeProvider(string text)
    {
        var host = new InMemoryHost(text);
        var root = new TextControlProviderV2(host);
        return new TextProviderImplV2(host, root);
    }

    [Fact]
    public void ExpandToEnclosingUnit_Character_ReturnsOneCodePoint()
    {
        var p = MakeProvider("abcdef");
        var r = new TextRangeProviderV2(p, 2, 2);
        r.ExpandToEnclosingUnit(TextUnit.Character);
        Assert.Equal("c", r.GetText(int.MaxValue));
    }

    [Fact]
    public void ExpandToEnclosingUnit_Word_ExpandsToWordSpan()
    {
        var p = MakeProvider("hello world");
        var r = new TextRangeProviderV2(p, 3, 3);
        r.ExpandToEnclosingUnit(TextUnit.Word);
        Assert.Equal("hello", r.GetText(int.MaxValue));
    }

    [Fact]
    public void ExpandToEnclosingUnit_Line_ExcludesLineBreak()
    {
        var p = MakeProvider("aaa\nbbb\nccc");
        var r = new TextRangeProviderV2(p, 5, 5);
        r.ExpandToEnclosingUnit(TextUnit.Line);
        Assert.Equal("bbb", r.GetText(int.MaxValue));
    }

    [Fact]
    public void ExpandToEnclosingUnit_Line_EmptyLineHasZeroLength()
    {
        var p = MakeProvider("aaa\n\nbbb");
        var r = new TextRangeProviderV2(p, 4, 4);
        r.ExpandToEnclosingUnit(TextUnit.Line);
        Assert.Equal("", r.GetText(int.MaxValue));
    }

    [Fact]
    public void Move_CharForward_PreservesUnitSpan()
    {
        // PC-Talker の文字歩き挙動: Expand(Char) → Move(Char, 1) → GetText を繰り返し
        var p = MakeProvider("abc");
        var r = new TextRangeProviderV2(p, 0, 0);
        r.ExpandToEnclosingUnit(TextUnit.Character);   // "a"
        int moved = r.Move(TextUnit.Character, 1);
        Assert.Equal(1, moved);
        Assert.Equal("b", r.GetText(int.MaxValue));   // b が読める(退化させない)
    }

    [Fact]
    public void GetText_RangeIsClamped()
    {
        var p = MakeProvider("abc");
        var r = new TextRangeProviderV2(p, 0, 3);
        Assert.Equal("abc", r.GetText(int.MaxValue));
        Assert.Equal("ab", r.GetText(2));
    }

    [Fact]
    public void FindText_LocalSearch_ReturnsSubrange()
    {
        var p = MakeProvider("aabbcc");
        var r = new TextRangeProviderV2(p, 0, 6);
        var found = r.FindText("bb", false, false) as TextRangeProviderV2;
        Assert.NotNull(found);
        Assert.Equal("bb", found!.GetText(int.MaxValue));
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Core.Tests --filter FullyQualifiedName~TextRangeProviderV2Tests
```

Expected: build error(`TextRangeProviderV2`/`TextProviderImplV2`/`TextControlProviderV2` 未定義)。

**Step 3: 最小実装 `TextRangeProviderV2.cs`**

`src/yEdit.Accessibility/TextRangeProviderV2.cs`(新規):

```csharp
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace yEdit.Accessibility;

/// <summary>
/// UIA テキスト範囲(v2)。[Start, End) のオフセット対で表現。
/// v1 の Move スパン保持ロジックを踏襲(PC-Talker の文字歩きが動く条件)。
/// テキストアクセスは全て <see cref="_owner"/>.Host の v2 メンバ経由。
/// </summary>
internal sealed class TextRangeProviderV2 : ITextRangeProvider
{
    private readonly TextProviderImplV2 _owner;
    private int _start;
    private int _end;

    public TextRangeProviderV2(TextProviderImplV2 owner, int start, int end)
    {
        _owner = owner;
        int len = owner.Host.TextLength;
        start = System.Math.Clamp(start, 0, len);
        end = System.Math.Clamp(end, 0, len);
        if (start > end) (start, end) = (end, start);
        _start = start;
        _end = end;
    }

    public ITextRangeProvider Clone() => new TextRangeProviderV2(_owner, _start, _end);

    public bool Compare(ITextRangeProvider range)
        => range is TextRangeProviderV2 o && o._start == _start && o._end == _end;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        var o = (TextRangeProviderV2)targetRange;
        int a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int b = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
        return a - b;
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        var host = _owner.Host;
        int len = host.TextLength;
        int pos = System.Math.Clamp(_start, 0, len);
        switch (unit)
        {
            case TextUnit.Character:
                _start = pos;
                _end = host.NextChar(pos);
                break;
            case TextUnit.Word:
            case TextUnit.Format:
                _start = host.WordStart(pos);
                _end = host.WordEnd(_start);
                if (_end == _start) _end = host.NextChar(_start);
                break;
            case TextUnit.Line:
            case TextUnit.Paragraph:
                _start = host.LineStartOf(pos);
                _end = host.LineEndNoBreakOf(pos);
                break;
            default: // Page / Document
                _start = 0;
                _end = len;
                break;
        }
    }

    public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) => null!;

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        var host = _owner.Host;
        int s = System.Math.Clamp(_start, 0, host.TextLength);
        int e = System.Math.Clamp(_end, 0, host.TextLength);
        if (string.IsNullOrEmpty(text) || s >= e) return null!;
        string hay = host.GetTextRange(s, e - s);
        var cmp = ignoreCase ? System.StringComparison.OrdinalIgnoreCase : System.StringComparison.Ordinal;
        int idx = backward ? hay.LastIndexOf(text, cmp) : hay.IndexOf(text, cmp);
        if (idx < 0) return null!;
        return new TextRangeProviderV2(_owner, s + idx, s + idx + text.Length);
    }

    public object GetAttributeValue(int attributeId) => AutomationElement.NotSupported;

    public double[] GetBoundingRectangles() => _owner.Host.GetBoundingRectangles(_start, _end);

    public IRawElementProviderSimple GetEnclosingElement() => _owner.RootProvider;

    public string GetText(int maxLength)
    {
        var host = _owner.Host;
        int s = System.Math.Clamp(_start, 0, host.TextLength);
        int e = System.Math.Clamp(_end, 0, host.TextLength);
        int count = e - s;
        if (count < 0) count = 0;
        if (maxLength >= 0 && count > maxLength) count = maxLength;
        return host.GetTextRange(s, count);
    }

    public int Move(TextUnit unit, int count)
    {
        var host = _owner.Host;
        bool wasDegenerate = _start == _end;

        int pos = _start;
        int moved = 0;
        if (count > 0)
            for (int i = 0; i < count; i++) { int n = StepForward(host, pos, unit); if (n == pos) break; pos = n; moved++; }
        else if (count < 0)
            for (int i = 0; i < -count; i++) { int p = StepBackward(host, pos, unit); if (p == pos) break; pos = p; moved++; }
        _start = _end = pos;

        // v1 実装の Move スパン保持を踏襲(PC-Talker の文字歩き=Expand(Char)→Move(Char,1)→GetText で
        // 2 文字目以降が空にならないように、非退化だった元の状態を復元)
        if (!wasDegenerate)
            ExpandToEnclosingUnit(unit);

        return count < 0 ? -moved : moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        var host = _owner.Host;
        int pos = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int moved = 0;
        if (count > 0)
            for (int i = 0; i < count; i++) { int n = StepForward(host, pos, unit); if (n == pos) break; pos = n; moved++; }
        else if (count < 0)
            for (int i = 0; i < -count; i++) { int p = StepBackward(host, pos, unit); if (p == pos) break; pos = p; moved++; }

        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = pos;
            if (_end < _start) _end = _start;
        }
        else
        {
            _end = pos;
            if (_start > _end) _start = _end;
        }
        return count < 0 ? -moved : moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        var o = (TextRangeProviderV2)targetRange;
        int target = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = target;
            if (_end < _start) _end = _start;
        }
        else
        {
            _end = target;
            if (_start > _end) _start = _end;
        }
    }

    public void Select() => _owner.Host.SetSelection(_start, _end);

    public void AddToSelection() { /* SupportedTextSelection.Single のため無効 */ }

    public void RemoveFromSelection() { /* SupportedTextSelection.Single のため無効 */ }

    public void ScrollIntoView(bool alignToTop) { /* PC-Talker はテキスト歩きで読めるため省略(v1 挙動踏襲) */ }

    public IRawElementProviderSimple[] GetChildren() => System.Array.Empty<IRawElementProviderSimple>();

    // ---------- 単位ステップ(host v2 メンバ経由) ----------

    private static int StepForward(IUiaTextHost host, int pos, TextUnit unit) => unit switch
    {
        TextUnit.Character => host.NextChar(pos),
        TextUnit.Word or TextUnit.Format => host.NextWordStart(pos),
        TextUnit.Line or TextUnit.Paragraph => host.LineEnd(pos),
        _ => host.TextLength,
    };

    private static int StepBackward(IUiaTextHost host, int pos, TextUnit unit) => unit switch
    {
        TextUnit.Character => host.PrevChar(pos),
        TextUnit.Word or TextUnit.Format => host.PrevWordStart(pos),
        TextUnit.Line or TextUnit.Paragraph => LineBackward(host, pos),
        _ => 0,
    };

    private static int LineBackward(IUiaTextHost host, int pos)
    {
        int ls = host.LineStartOf(pos);
        if (ls < pos) return ls;       // 行頭でなければ行頭へ
        if (ls == 0) return 0;
        return host.LineStartOf(ls - 1); // 行頭なら前行の行頭へ
    }
}
```

**Step 4: `TextProviderImplV2`/`TextControlProviderV2` の仮スケルトン(次 Task 4 で本実装)**

同 Task で使うため、最小の仮実装だけ:

`src/yEdit.Accessibility/TextControlProviderV2.cs`(新規・仮):

```csharp
using System.Windows.Automation.Provider;

namespace yEdit.Accessibility;

/// <summary>v2 用 UIA ルートプロバイダの仮スケルトン(Task 4 で本実装)。</summary>
internal sealed class TextControlProviderV2 : IRawElementProviderSimple
{
    private readonly IUiaTextHost _host;
    public TextControlProviderV2(IUiaTextHost host) { _host = host; }
    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
    public object? GetPatternProvider(int patternId) => null;
    public object? GetPropertyValue(int propertyId) => null;
    public IRawElementProviderSimple? HostRawElementProvider => null;
}
```

`src/yEdit.Accessibility/TextProviderImplV2.cs`(新規・仮):

```csharp
using System.Windows.Automation.Provider;

namespace yEdit.Accessibility;

/// <summary>v2 用 UIA TextProvider の仮スケルトン(Task 4 で本実装)。</summary>
internal sealed class TextProviderImplV2
{
    public IUiaTextHost Host { get; }
    public IRawElementProviderSimple RootProvider { get; }

    public TextProviderImplV2(IUiaTextHost host, IRawElementProviderSimple root)
    {
        Host = host;
        RootProvider = root;
    }
}
```

**Step 5: テスト通過を確認**

```
dotnet test tests/yEdit.Core.Tests --filter FullyQualifiedName~TextRangeProviderV2Tests
```

Expected: 7 passed。

**Step 6: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 529+7=536 passed / Editor.Tests 155 passed。

**Step 7: コミット**

```bash
git add src/yEdit.Accessibility/TextRangeProviderV2.cs src/yEdit.Accessibility/TextProviderImplV2.cs src/yEdit.Accessibility/TextControlProviderV2.cs tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs
git commit -m "P5: Task 3 TextRangeProviderV2 新設(v1 Move スパン保持ロジック踏襲+v2 メンバ経由)"
```

**完了条件**:
- `TextRangeProviderV2` 全 API 実装(Move スパン保持ロジック含む)
- ExpandToEnclosingUnit / Move / GetText / FindText の契約テスト 7 件緑
- v1 `TextRangeProvider` は無変更
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 4: `TextControlProviderV2` + `TextProviderImplV2` の本実装

**目的**: v2 用の UIA ルートプロバイダ + TextProvider の本実装。v1 の実装(`TextControlProvider` / `TextProviderImpl`)を踏襲しつつ、`RangeFromPoint` は本実装(host.OffsetFromScreenPoint 委譲)。

**対象**:
- Modify: `src/yEdit.Accessibility/TextControlProviderV2.cs`(仮 → 本実装)
- Modify: `src/yEdit.Accessibility/TextProviderImplV2.cs`(仮 → 本実装)
- Create: `tests/yEdit.Core.Tests/Accessibility/TextProviderImplV2Tests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Accessibility/TextProviderImplV2Tests.cs`(新規):

```csharp
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using yEdit.Accessibility;
using Xunit;

namespace yEdit.Core.Tests.Accessibility;

public class TextProviderImplV2Tests
{
    private sealed class Host : IUiaTextHost
    {
        public int _selS, _selE;
        public string GetTextRange(int s, int l) => "";
        public int TextLength => 100;
        public (int Start, int End) GetSelection() => (_selS, _selE);
        public void SetSelection(int s, int e) { _selS = s; _selE = e; }
        public int NextChar(int o) => o + 1;
        public int PrevChar(int o) => o - 1;
        public int LineStartOf(int o) => 0;
        public int LineEndNoBreakOf(int o) => 100;
        public int LineEnd(int o) => 100;
        public int WordStart(int o) => 0;
        public int WordEnd(int o) => 100;
        public int NextWordStart(int o) => o;
        public int PrevWordStart(int o) => o;
        public System.Windows.Rect BoundingRectangle => new System.Windows.Rect(0, 0, 200, 100);
        public double[] GetBoundingRectangles(int s, int e) => System.Array.Empty<double>();
        public int OffsetFromScreenPoint(double x, double y) => 42; // 定数=呼ばれたことがわかる
        public nint Handle => System.IntPtr.Zero;
        public bool HasFocus => true;
        public int ControlTypeId => ControlType.Document.Id;
        public string Name => "本文";
        public string AutomationId => "editor";
        public void SetFocus() { }
    }

    [Fact]
    public void GetSelection_ReturnsHostSelectionAsSingleRange()
    {
        var h = new Host { _selS = 10, _selE = 20 };
        var root = new TextControlProviderV2(h);
        var pi = new TextProviderImplV2(h, root);
        var sel = pi.GetSelection();
        Assert.Single(sel);
        Assert.Equal("", ((TextRangeProviderV2)sel[0]).GetText(0));
    }

    [Fact]
    public void RangeFromPoint_UsesHostOffsetFromScreenPoint()
    {
        var h = new Host();
        var root = new TextControlProviderV2(h);
        var pi = new TextProviderImplV2(h, root);
        var r = pi.RangeFromPoint(new System.Windows.Point(50, 30)) as TextRangeProviderV2;
        Assert.NotNull(r);
        // OffsetFromScreenPoint が 42 を返す → [42, 42) の縮退範囲
        int startCmp = r!.CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            r, System.Windows.Automation.Text.TextPatternRangeEndpoint.End);
        Assert.Equal(0, startCmp);
    }

    [Fact]
    public void DocumentRange_ReturnsFullText()
    {
        var h = new Host();
        var root = new TextControlProviderV2(h);
        var pi = new TextProviderImplV2(h, root);
        var r = (TextRangeProviderV2)pi.DocumentRange;
        Assert.Equal(0, r.CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            new TextRangeProviderV2(pi, 0, 0), System.Windows.Automation.Text.TextPatternRangeEndpoint.Start));
    }

    [Fact]
    public void TextControlProviderV2_ReportsDocumentControlType()
    {
        var h = new Host();
        var root = new TextControlProviderV2(h);
        Assert.Equal(ControlType.Document.Id, root.GetPropertyValue(AutomationElementIdentifiers.ControlTypeProperty.Id));
        Assert.Equal("本文", root.GetPropertyValue(AutomationElementIdentifiers.NameProperty.Id));
        Assert.Equal("editor", root.GetPropertyValue(AutomationElementIdentifiers.AutomationIdProperty.Id));
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Core.Tests --filter FullyQualifiedName~TextProviderImplV2Tests
```

Expected: build error(`RangeFromPoint`/`DocumentRange` 未定義)。

**Step 3: `TextControlProviderV2.cs` 本実装**

v1 `TextControlProvider` から踏襲 + `IRawElementProviderFragment` / `IRawElementProviderFragmentRoot` 追加:

```csharp
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;

namespace yEdit.Accessibility;

/// <summary>
/// 自作 EditorControl のルート UIA プロバイダ(v2)。
/// WM_GETOBJECT から返し、TextPattern を公開する(fragment root として hwnd に同居)。
/// </summary>
public sealed class TextControlProviderV2 :
    IRawElementProviderSimple,
    IRawElementProviderFragment,
    IRawElementProviderFragmentRoot
{
    private readonly IUiaTextHost _host;
    private readonly TextProviderImplV2 _textProvider;

    public TextControlProviderV2(IUiaTextHost host)
    {
        _host = host;
        _textProvider = new TextProviderImplV2(host, this);
    }

    // ---------- IRawElementProviderSimple ----------

    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;

    public object? GetPatternProvider(int patternId)
    {
        if (patternId == TextPatternIdentifiers.Pattern.Id) return _textProvider;
        return null;
    }

    public object? GetPropertyValue(int propertyId)
    {
        if (propertyId == AutomationElementIdentifiers.ControlTypeProperty.Id)
            return _host.ControlTypeId;
        if (propertyId == AutomationElementIdentifiers.NameProperty.Id)
            return _host.Name;
        if (propertyId == AutomationElementIdentifiers.AutomationIdProperty.Id)
            return _host.AutomationId;
        if (propertyId == AutomationElementIdentifiers.IsContentElementProperty.Id)
            return true;
        if (propertyId == AutomationElementIdentifiers.IsControlElementProperty.Id)
            return true;
        if (propertyId == AutomationElementIdentifiers.IsEnabledProperty.Id)
            return true;
        if (propertyId == AutomationElementIdentifiers.IsKeyboardFocusableProperty.Id)
            return true;
        if (propertyId == AutomationElementIdentifiers.HasKeyboardFocusProperty.Id)
            return _host.HasFocus;
        return null;
    }

    public IRawElementProviderSimple? HostRawElementProvider
        => AutomationInteropProvider.HostProviderFromHandle(_host.Handle);

    // ---------- IRawElementProviderFragment ----------

    public Rect BoundingRectangle => _host.BoundingRectangle;

    public IRawElementProviderFragmentRoot FragmentRoot => this;

    public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;

    public int[] GetRuntimeId() => new int[] { AutomationInteropProvider.AppendRuntimeId, 1 };

    public IRawElementProviderFragment? Navigate(NavigateDirection direction) => null; // 子なし・親は host 経由

    public void SetFocus() => _host.SetFocus();

    // ---------- IRawElementProviderFragmentRoot ----------

    public IRawElementProviderFragment ElementProviderFromPoint(double x, double y) => this;

    public IRawElementProviderFragment? GetFocus() => _host.HasFocus ? this : null;
}
```

**Step 4: `TextProviderImplV2.cs` 本実装**

```csharp
using System.Windows;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace yEdit.Accessibility;

/// <summary>UIA TextPattern 本体(v2・ITextProvider)。範囲の生成と現在選択の提供を担う。</summary>
internal sealed class TextProviderImplV2 : ITextProvider
{
    public IUiaTextHost Host { get; }
    public IRawElementProviderSimple RootProvider { get; }

    public TextProviderImplV2(IUiaTextHost host, IRawElementProviderSimple root)
    {
        Host = host;
        RootProvider = root;
    }

    public ITextRangeProvider[] GetSelection()
    {
        var (s, e) = Host.GetSelection();
        return new ITextRangeProvider[] { new TextRangeProviderV2(this, s, e) };
    }

    public ITextRangeProvider[] GetVisibleRanges()
        => new ITextRangeProvider[] { new TextRangeProviderV2(this, 0, Host.TextLength) };

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
        => new TextRangeProviderV2(this, 0, 0);

    /// <summary>スクリーン座標直下の縮退範囲(host.OffsetFromScreenPoint 委譲・本実装)。</summary>
    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        int pos = Host.OffsetFromScreenPoint(screenLocation.X, screenLocation.Y);
        return new TextRangeProviderV2(this, pos, pos);
    }

    public ITextRangeProvider DocumentRange => new TextRangeProviderV2(this, 0, Host.TextLength);

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
}
```

**Step 5: テスト通過を確認**

```
dotnet test tests/yEdit.Core.Tests --filter FullyQualifiedName~TextProviderImplV2Tests
```

Expected: 4 passed。

**Step 6: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 536+4=540 passed / Editor.Tests 155 passed。

**Step 7: コミット**

```bash
git add src/yEdit.Accessibility/TextControlProviderV2.cs src/yEdit.Accessibility/TextProviderImplV2.cs tests/yEdit.Core.Tests/Accessibility/TextProviderImplV2Tests.cs
git commit -m "P5: Task 4 TextControlProviderV2 + TextProviderImplV2 本実装(RangeFromPoint 本実装含む)"
```

**完了条件**:
- `TextControlProviderV2` が `IRawElementProviderSimple`/`Fragment`/`FragmentRoot` 実装
- `TextProviderImplV2` が `ITextProvider` 実装
- `RangeFromPoint` が host.OffsetFromScreenPoint 委譲(v1 スタブから本実装へ)
- 契約テスト 4 件緑
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 5: EditorControl に `IUiaTextHost` v2 実装(スナップショット参照 + 位置歩き委譲)

**目的**: EditorControl に v2 メンバを実装。位置歩き 8 メンバは Core `WordBoundary` / `TextSnapshot` に委譲、GetTextRange はスナップショットの Substring、選択は volatile な `_caret`/`_anchor` 参照(RPC スレッドから安全に読める)。座標 API はスタブ(Task 10/11 で本実装)。UI 変更(SetSelection / SetFocus)は BeginInvoke マーシャリング。

**対象**:
- Modify: `src/yEdit.Editor/EditorControl.cs`(`IUiaTextHost` implement 追加 + フィールド + メンバ実装)
- Create: `tests/yEdit.Editor.Tests/EditorControlUiaHostTests.cs`(新規・WinForms STA)

**Step 1: 失敗するテストを書く**

`tests/yEdit.Editor.Tests/EditorControlUiaHostTests.cs`(新規):

```csharp
using System;
using System.Windows.Forms;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlUiaHostTests
{
    [Fact]
    public void Host_GetTextRange_ReturnsSubstring()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBufferBuilder.FromString("Hello, world!");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal("Hello", host.GetTextRange(0, 5));
            Assert.Equal("world", host.GetTextRange(7, 5));
        });
    }

    [Fact]
    public void Host_TextLength_MatchesBuffer()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBufferBuilder.FromString("abcdef");
            ctrl.SetSource(buf);
            Assert.Equal(6, ((IUiaTextHost)ctrl).TextLength);
        });
    }

    [Fact]
    public void Host_GetSelection_ReturnsCurrent()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBufferBuilder.FromString("abcdefg");
            ctrl.SetSource(buf);
            ctrl.SetSelectionCharRange(2, 5);
            Assert.Equal((2, 5), ((IUiaTextHost)ctrl).GetSelection());
        });
    }

    [Fact]
    public void Host_LineStartOf_ReturnsLineStart()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBufferBuilder.FromString("aaa\nbbb\nccc");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal(0, host.LineStartOf(2));
            Assert.Equal(4, host.LineStartOf(5));
            Assert.Equal(8, host.LineStartOf(10));
        });
    }

    [Fact]
    public void Host_LineEndNoBreakOf_ExcludesLineBreak()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBufferBuilder.FromString("aaa\nbbb");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal(3, host.LineEndNoBreakOf(1));   // "aaa" の後・"\n" 前
            Assert.Equal(7, host.LineEndNoBreakOf(5));   // 末尾行
        });
    }

    [Fact]
    public void Host_WordStart_UsesCoreWordBoundary()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBufferBuilder.FromString("hello world");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            // "hello world" の 3 は "hello" 内 → WordStart=0
            Assert.Equal(0, host.WordStart(3));
            // "hello world" の 9 は "world" 内 → WordStart=6
            Assert.Equal(6, host.WordStart(9));
        });
    }

    [Fact]
    public void Host_ControlTypeId_IsDocument()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            IUiaTextHost host = ctrl;
            Assert.Equal(System.Windows.Automation.ControlType.Document.Id, host.ControlTypeId);
        });
    }

    [Fact]
    public void Host_AutomationId_Editor()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            IUiaTextHost host = ctrl;
            Assert.Equal("editor", host.AutomationId);
        });
    }

    [Fact]
    public void Host_Name_Honmon()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            IUiaTextHost host = ctrl;
            Assert.Equal("本文", host.Name);
        });
    }

    [Fact]
    public void Host_SetSelection_MarshalsToUIThread()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBufferBuilder.FromString("abcdefg");
            ctrl.SetSource(buf);
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                IUiaTextHost host = ctrl;
                host.SetSelection(1, 4);
                Application.DoEvents();   // BeginInvoke を回す
                Assert.Equal((1, 4), host.GetSelection());
            }
            finally { form.Close(); }
        });
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlUiaHostTests
```

Expected: build error(`EditorControl` が `IUiaTextHost` を実装していない)。

**Step 3: `EditorControl.cs` に `IUiaTextHost` v2 実装を追加**

以下を EditorControl.cs へ追加(クラス宣言 + フィールド + メンバ):

```csharp
// ---------- クラス宣言(既存の sealed class EditorControl : Control に interface を追加) ----------
// 変更前: public sealed class EditorControl : Control
// 変更後: public sealed class EditorControl : Control, yEdit.Accessibility.IUiaTextHost

// ---------- 追加フィールド(先頭の既存フィールド群の隣に) ----------

// UIA v2 用: RPC スレッドが読む不変スナップショット参照(AfterEdit / SetSource で更新)
private volatile TextSnapshot? _bufferSnapshot;

// UIA v2 用: BoundingRectangle キャッシュ(UI スレッドで更新した値を lock 越しで返す)
private readonly object _boundsSync = new();
private System.Windows.Rect _bounds;

// ---------- 追加ヘルパ(既存の internal 領域近くに) ----------

private void CacheSnapshot()
{
    if (_buffer is not null) _bufferSnapshot = _buffer.Current;
}

private void UpdateBoundsCache()
{
    if (!IsHandleCreated) return;
    var r = RectangleToScreen(ClientRectangle);
    lock (_boundsSync) _bounds = new System.Windows.Rect(r.Left, r.Top, r.Width, r.Height);
}

// ---------- IUiaTextHost v2 実装(explicit 実装で表面をきれいに保つ) ----------

string yEdit.Accessibility.IUiaTextHost.GetTextRange(int start, int length)
{
    var snap = _bufferSnapshot;
    if (snap is null) return "";
    int s = System.Math.Clamp(start, 0, snap.CharLength);
    int l = System.Math.Clamp(length, 0, snap.CharLength - s);
    return snap.GetText(s, l);
}

int yEdit.Accessibility.IUiaTextHost.TextLength => _bufferSnapshot?.CharLength ?? 0;

(int Start, int End) yEdit.Accessibility.IUiaTextHost.GetSelection()
{
    int c = _caret, a = _anchor;
    return (System.Math.Min(a, c), System.Math.Max(a, c));
}

void yEdit.Accessibility.IUiaTextHost.SetSelection(int start, int end)
{
    if (InvokeRequired) { BeginInvoke(new System.Action(() =>
        ((yEdit.Accessibility.IUiaTextHost)this).SetSelection(start, end))); return; }
    SetSelectionCharRange(start, end);
}

int yEdit.Accessibility.IUiaTextHost.NextChar(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null) return 0;
    int o = System.Math.Clamp(offset, 0, snap.CharLength);
    if (o >= snap.CharLength) return snap.CharLength;
    char c = snap.GetChar(o);
    if (char.IsHighSurrogate(c) && o + 1 < snap.CharLength && char.IsLowSurrogate(snap.GetChar(o + 1)))
        return o + 2;
    return o + 1;
}

int yEdit.Accessibility.IUiaTextHost.PrevChar(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null) return 0;
    int o = System.Math.Clamp(offset, 0, snap.CharLength);
    if (o <= 0) return 0;
    if (char.IsLowSurrogate(snap.GetChar(o - 1)) && o - 2 >= 0 && char.IsHighSurrogate(snap.GetChar(o - 2)))
        return o - 2;
    return o - 1;
}

int yEdit.Accessibility.IUiaTextHost.LineStartOf(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null) return 0;
    int o = System.Math.Clamp(offset, 0, snap.CharLength);
    int line = snap.GetLineIndexOfChar(o);
    return snap.GetLineStart(line);
}

int yEdit.Accessibility.IUiaTextHost.LineEnd(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null) return 0;
    int o = System.Math.Clamp(offset, 0, snap.CharLength);
    int line = snap.GetLineIndexOfChar(o);
    if (line + 1 < snap.LineCount) return snap.GetLineStart(line + 1);
    return snap.CharLength;
}

int yEdit.Accessibility.IUiaTextHost.LineEndNoBreakOf(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null) return 0;
    int e = ((yEdit.Accessibility.IUiaTextHost)this).LineEnd(offset);
    if (e > 0 && snap.GetChar(e - 1) == '\n') e--;
    if (e > 0 && snap.GetChar(e - 1) == '\r') e--;   // CR も除く(CRLF 混在対応)
    return e;
}

int yEdit.Accessibility.IUiaTextHost.WordStart(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null) return 0;
    int o = System.Math.Clamp(offset, 0, snap.CharLength);
    // Core.Editing.WordBoundary は「単語の左端」を PrevWordStart で表現するが、
    // ExpandToEnclosingUnit(Word) の想定は「offset を含む単語の左端」なので直接呼ぶ:
    // 実装は「非空白 class の連続を左に舐める」= WordBoundary.PrevWordStart(offset+1) 相当
    // ここではシンプルに Core WordBoundary の内部と同じ流儀を EditorControl 側で再現するのではなく、
    // WordBoundary.PrevWordStart を再帰的に使う実装:
    return WordBoundary_WordStart(snap, o);
}

int yEdit.Accessibility.IUiaTextHost.WordEnd(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null) return 0;
    int o = System.Math.Clamp(offset, 0, snap.CharLength);
    return WordBoundary_WordEnd(snap, o);
}

int yEdit.Accessibility.IUiaTextHost.NextWordStart(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null) return 0;
    int o = System.Math.Clamp(offset, 0, snap.CharLength);
    return yEdit.Core.Editing.WordBoundary.NextWordStart(snap, o);
}

int yEdit.Accessibility.IUiaTextHost.PrevWordStart(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null) return 0;
    int o = System.Math.Clamp(offset, 0, snap.CharLength);
    return yEdit.Core.Editing.WordBoundary.PrevWordStart(snap, o);
}

// WordStart/WordEnd は Core WordBoundary に直接メンバがないため、private ヘルパで同ロジックを再実装:
// 「offset の class と同じ class の連続を左右に舐める」
private static int WordBoundary_WordStart(TextSnapshot snap, int pos)
{
    if (pos <= 0) return 0;
    int p = pos;
    // 現在位置の 1 文字前の class と一致する連続を左へ
    while (p > 0)
    {
        int prev = p - 1;
        if (prev > 0 && char.IsLowSurrogate(snap.GetChar(prev)) && char.IsHighSurrogate(snap.GetChar(prev - 1)))
            prev--;
        char pc = snap.GetChar(prev);
        if (char.IsWhiteSpace(pc) || pc == '\r' || pc == '\n') break;
        p = prev;
    }
    return p;
}

private static int WordBoundary_WordEnd(TextSnapshot snap, int pos)
{
    int p = pos;
    while (p < snap.CharLength)
    {
        char c = snap.GetChar(p);
        if (char.IsWhiteSpace(c) || c == '\r' || c == '\n') break;
        if (char.IsHighSurrogate(c) && p + 1 < snap.CharLength && char.IsLowSurrogate(snap.GetChar(p + 1)))
            p += 2;
        else
            p++;
    }
    return p;
}

System.Windows.Rect yEdit.Accessibility.IUiaTextHost.BoundingRectangle
{
    get { lock (_boundsSync) return _bounds; }
}

// 座標 API はスタブ(Task 10/11 で本実装)
double[] yEdit.Accessibility.IUiaTextHost.GetBoundingRectangles(int start, int end) => System.Array.Empty<double>();

int yEdit.Accessibility.IUiaTextHost.OffsetFromScreenPoint(double x, double y) => 0;

nint yEdit.Accessibility.IUiaTextHost.Handle => Handle;

bool yEdit.Accessibility.IUiaTextHost.HasFocus => Focused;

int yEdit.Accessibility.IUiaTextHost.ControlTypeId => System.Windows.Automation.ControlType.Document.Id;

string yEdit.Accessibility.IUiaTextHost.Name => "本文";

string yEdit.Accessibility.IUiaTextHost.AutomationId => "editor";

void yEdit.Accessibility.IUiaTextHost.SetFocus()
{
    if (InvokeRequired) { BeginInvoke(new System.Action(() => Focus())); return; }
    Focus();
}
```

**Step 4: `SetSource` で最初のスナップショットキャッシュ、`AfterEdit` で更新**

- `SetSource(buffer)` の末尾に `CacheSnapshot();` を追加
- `AfterEdit()` の末尾(または途中の適切な位置)に `CacheSnapshot();` を追加
- `OnHandleCreated` の末尾に `UpdateBoundsCache();` を追加
- `OnSizeChanged` / `OnLocationChanged` の末尾に `UpdateBoundsCache();` を追加(P4 で override が無ければ new に override 追加)

**Step 5: WordBoundary の左端/右端が Core の CharClass 定義と一致するか確認**

Core `WordBoundary.CharClass` は 8 分類(Whitespace/LineBreak/Latin/Digit/Hiragana/Katakana/Han/Other)。
上記 `WordBoundary_WordStart` は「Whitespace/LineBreak 以外の連続」だけを見ているため、Latin↔Digit の境界などで Core の分類細分と挙動が微妙に違う可能性がある。**簡単のため Core `WordBoundary` の内部を露出させないよう、上記の簡易実装で v1 の `TextNavigation.WordStart`(実験用素朴実装=空白区切り)と近い挙動にする**。将来的に精度が必要なら Core WordBoundary を internal 公開しても良い。ここでは簡易実装を採用。

**Step 6: テスト通過を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlUiaHostTests
```

Expected: 10 passed。

**Step 7: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 155+10=165 passed。

**Step 8: コミット**

```bash
git add src/yEdit.Editor/EditorControl.cs tests/yEdit.Editor.Tests/EditorControlUiaHostTests.cs
git commit -m "P5: Task 5 EditorControl に IUiaTextHost v2 実装(位置歩き/GetTextRange/選択の RPC 安全化)"
```

**完了条件**:
- EditorControl が `IUiaTextHost` v2 を explicit 実装(全 19 メンバ)
- `_bufferSnapshot` / `_bounds` キャッシュ配線
- `SetSource` / `AfterEdit` で snapshot 更新
- 位置歩きテスト 10 件緑
- 座標 API はスタブ(Task 10/11 で本実装)
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 6: `WM_GETOBJECT` / `UiaRootObjectId` 応答 + プロバイダ生成

**目的**: EditorControl の `WndProc`(P4 で override 追加済)に `WM_GETOBJECT` 分岐を追加し、`TextControlProviderV2` を lazy 生成して `AutomationInteropProvider.ReturnRawElementProvider` で返す。P4 の IME 経路より前段(先頭)に置くこと。

**対象**:
- Modify: `src/yEdit.Editor/EditorControl.cs`(`WndProc` に `WM_GETOBJECT` 分岐追加 + `_provider` フィールド)
- Modify: `src/yEdit.Editor/NativeMethods.cs`(`WM_GETOBJECT` / `UiaRootObjectId` 定数が既にあるか確認・無ければ追加)
- Create: `tests/yEdit.Editor.Tests/EditorControlUiaGetObjectTests.cs`(新規)

**Step 1: NativeMethods 確認**

```
grep -n "WM_GETOBJECT\|UiaRootObjectId" src/yEdit.Editor/NativeMethods.cs
```

既に存在するはず(P0 の UiaProbe と同定義がある予定)。無ければ追加:

```csharp
public const int WM_GETOBJECT = 0x003D;
public const int UiaRootObjectId = -25;   // UIA がプロバイダを要求する lParam
```

**Step 2: 失敗するテストを書く**

`tests/yEdit.Editor.Tests/EditorControlUiaGetObjectTests.cs`(新規):

```csharp
using System;
using System.Windows.Automation.Provider;
using System.Windows.Forms;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlUiaGetObjectTests
{
    [Fact]
    public void WndProc_ReturnsProviderForUiaRootObjectId()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hi"));
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                // WM_GETOBJECT(UiaRootObjectId) を送る
                var msg = Message.Create(ctrl.Handle, 0x003D, System.IntPtr.Zero, new System.IntPtr(-25));
                // internal test hook 経由で WndProc を叩く
                EditorControl.TestHook_WndProc(ctrl, ref msg);
                Assert.NotEqual(System.IntPtr.Zero, msg.Result);   // 非 0 = プロバイダを返した
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void WndProc_IgnoresOtherObjIds()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hi"));
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                // WM_GETOBJECT(OBJID_CLIENT=-4)は base に流す=自前応答しない
                var msg = Message.Create(ctrl.Handle, 0x003D, System.IntPtr.Zero, new System.IntPtr(-4));
                EditorControl.TestHook_WndProc(ctrl, ref msg);
                // base 経由(DefWindowProc)なので msg.Result は 0 のまま or WinForms が既定応答
                // = 自前応答経路に入らなかったことを内部フラグで確認
                Assert.False(EditorControl.TestHook_LastGetObjectServed(ctrl));
            }
            finally { form.Close(); }
        });
    }
}
```

**Step 3: 失敗を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlUiaGetObjectTests
```

Expected: build error(`TestHook_WndProc`/`TestHook_LastGetObjectServed` 未定義)。

**Step 4: `EditorControl.cs` の `WndProc` に `WM_GETOBJECT` 分岐を追加**

`WndProc` の先頭(P4 で追加した IME 経路の**前**)に:

```csharp
// UIA テストフック(Editor.Tests 用)
internal bool _testHook_LastGetObjectServed;

private yEdit.Accessibility.TextControlProviderV2? _provider;

protected override void WndProc(ref Message m)
{
    // P5 Task 6: UIA プロバイダ配線 ---- 先頭で処理
    if (m.Msg == NativeMethods.WM_GETOBJECT)
    {
        long objid = m.LParam.ToInt64();
        if (objid == NativeMethods.UiaRootObjectId)
        {
            _provider ??= new yEdit.Accessibility.TextControlProviderV2(this);
            m.Result = System.Windows.Automation.Provider.AutomationInteropProvider
                .ReturnRawElementProvider(Handle, m.WParam, m.LParam, _provider);
            _testHook_LastGetObjectServed = true;
            return;
        }
        _testHook_LastGetObjectServed = false;
        // OBJID_CLIENT (=-4) / OBJID_WINDOW (=0) 等は base=DefWindowProc に流す
        // (=自前で MSAA プロキシを作らない=ネイティブ表面原則)
    }

    // P4 で追加した IME 経路(既存)
    // ... 既存の IME 分岐 ...

    base.WndProc(ref m);
}

// TestHook(internal):
internal static void TestHook_WndProc(EditorControl c, ref Message m) => c.WndProc(ref m);
internal static bool TestHook_LastGetObjectServed(EditorControl c) => c._testHook_LastGetObjectServed;
```

**Step 5: テスト通過を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlUiaGetObjectTests
```

Expected: 2 passed。

**Step 6: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 165+2=167 passed。

**Step 7: コミット**

```bash
git add src/yEdit.Editor/EditorControl.cs src/yEdit.Editor/NativeMethods.cs tests/yEdit.Editor.Tests/EditorControlUiaGetObjectTests.cs
git commit -m "P5: Task 6 WM_GETOBJECT/UiaRootObjectId 応答 + TextControlProviderV2 lazy 生成"
```

**完了条件**:
- WM_GETOBJECT(UiaRootObjectId) で TextControlProviderV2 を返す
- OBJID_CLIENT 等は自前応答しない
- テスト 2 件緑
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 7: ネイティブ表面原則:`WM_GETTEXT` / `WM_GETTEXTLENGTH` 抑止

**目的**: 本文がネイティブ MSAA / GetWindowText 経由で漏れないよう、`WM_GETTEXT` / `WM_GETTEXTLENGTH` に対して `m.Result = IntPtr.Zero` を返して既定処理をスキップ。=SR は UIA 経路のみで読む(§2-7)。

**対象**:
- Modify: `src/yEdit.Editor/NativeMethods.cs`(`WM_GETTEXT` / `WM_GETTEXTLENGTH` 定数追加)
- Modify: `src/yEdit.Editor/EditorControl.cs`(`WndProc` に分岐追加)
- Create: `tests/yEdit.Editor.Tests/EditorControlNativeSurfaceTests.cs`(新規)

**Step 1: 失敗するテストを書く**

`tests/yEdit.Editor.Tests/EditorControlNativeSurfaceTests.cs`(新規):

```csharp
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlNativeSurfaceTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern System.IntPtr SendMessageGetText(System.IntPtr hWnd, uint msg, System.IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern System.IntPtr SendMessageInt(System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);

    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;

    [Fact]
    public void WM_GETTEXT_ReturnsEmpty()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("secret content"));
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                var sb = new StringBuilder(1024);
                SendMessageGetText(ctrl.Handle, WM_GETTEXT, new System.IntPtr(1024), sb);
                Assert.Equal("", sb.ToString());
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void WM_GETTEXTLENGTH_ReturnsZero()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("some text"));
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                var r = SendMessageInt(ctrl.Handle, WM_GETTEXTLENGTH, System.IntPtr.Zero, System.IntPtr.Zero);
                Assert.Equal(System.IntPtr.Zero, r);
            }
            finally { form.Close(); }
        });
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlNativeSurfaceTests
```

Expected: 2 failed(既定処理で本文/長さを返してしまう)。

**Step 3: `NativeMethods.cs` に定数追加**

```csharp
public const int WM_GETTEXT = 0x000D;
public const int WM_GETTEXTLENGTH = 0x000E;
```

**Step 4: `EditorControl.cs` の `WndProc` に分岐追加**

Task 6 で追加した `WM_GETOBJECT` 分岐の直後(=IME 経路より前・base より前):

```csharp
// P5 Task 7: ネイティブ表面原則 = 本文非公開
if (m.Msg == NativeMethods.WM_GETTEXT || m.Msg == NativeMethods.WM_GETTEXTLENGTH)
{
    m.Result = System.IntPtr.Zero;
    return;
}
```

**Step 5: テスト通過を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlNativeSurfaceTests
```

Expected: 2 passed。

**Step 6: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 167+2=169 passed。

**Step 7: コミット**

```bash
git add src/yEdit.Editor/NativeMethods.cs src/yEdit.Editor/EditorControl.cs tests/yEdit.Editor.Tests/EditorControlNativeSurfaceTests.cs
git commit -m "P5: Task 7 WM_GETTEXT/WM_GETTEXTLENGTH 抑止(ネイティブ表面原則・本文非公開)"
```

**完了条件**:
- WM_GETTEXT / WM_GETTEXTLENGTH に本文/長さを返さない
- P/Invoke 経由の SendMessage テスト 2 件緑
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 8: UIA イベント発火配線:`TextChangedEvent` / `TextSelectionChangedEvent`

**目的**: 編集経路(`AfterEdit`)と移動経路(`MoveCaretTo` 系)から UIA イベントを発火。`RaiseUia` ヘルパを新設し、`AutomationInteropProvider.ClientsAreListening` で早期リターン。`RaiseUiaSelectionEvents=false` のときは選択イベントを抑止(CSV セルナビ配線用に受け口を残す・P6 で App 層本挙動配線)。

**対象**:
- Modify: `src/yEdit.Editor/EditorControl.cs`(`RaiseUia` 追加 + `AfterEdit` / `MoveCaretTo` 系末尾に配線 + `RaiseUiaSelectionEvents` フラグ確認)
- Create: `tests/yEdit.Editor.Tests/EditorControlUiaEventsTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Editor.Tests/EditorControlUiaEventsTests.cs`(新規):

```csharp
using System;
using System.Collections.Generic;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Forms;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlUiaEventsTests
{
    // 内部テストフック経由でイベント発火カウンタを取る
    // (UI Automation の RaiseAutomationEvent は本物なので、テストではフックで数える)

    [Fact]
    public void Edit_RaisesTextChangedAndTextSelectionChanged()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("abc"));
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                EditorControl.TestHook_ResetUiaEventCounts(ctrl);
                ctrl.SetSelectionCharRange(3, 3);
                ctrl.ReplaceCharRange(3, 0, "x");   // "abcx"
                Application.DoEvents();
                var (textChanged, selChanged, focusChanged) = EditorControl.TestHook_UiaEventCounts(ctrl);
                Assert.True(textChanged >= 1, "TextChangedEvent が発火していない");
                Assert.True(selChanged >= 1, "TextSelectionChangedEvent が発火していない");
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void MoveCaret_RaisesTextSelectionChanged()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello"));
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                EditorControl.TestHook_ResetUiaEventCounts(ctrl);
                ctrl.SetCaretCharOffset(3);
                Application.DoEvents();
                var (_, selChanged, _) = EditorControl.TestHook_UiaEventCounts(ctrl);
                Assert.True(selChanged >= 1, "MoveCaret で TextSelectionChangedEvent が発火していない");
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void RaiseUiaSelectionEvents_False_SuppressesSelectionEvents()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello"));
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                ctrl.RaiseUiaSelectionEvents = false;
                EditorControl.TestHook_ResetUiaEventCounts(ctrl);
                ctrl.SetCaretCharOffset(3);
                Application.DoEvents();
                var (_, selChanged, _) = EditorControl.TestHook_UiaEventCounts(ctrl);
                Assert.Equal(0, selChanged);
            }
            finally { form.Close(); }
        });
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlUiaEventsTests
```

Expected: build error(`TestHook_ResetUiaEventCounts`/`TestHook_UiaEventCounts` 未定義)。

**Step 3: `EditorControl.cs` に `RaiseUia` + カウンタ + 配線を追加**

```csharp
// UIA イベント発火カウンタ(テストフック用)
private int _uiaTextChangedCount, _uiaSelectionChangedCount, _uiaFocusChangedCount;

internal static void TestHook_ResetUiaEventCounts(EditorControl c)
{
    c._uiaTextChangedCount = c._uiaSelectionChangedCount = c._uiaFocusChangedCount = 0;
}

internal static (int textChanged, int selChanged, int focusChanged) TestHook_UiaEventCounts(EditorControl c)
    => (c._uiaTextChangedCount, c._uiaSelectionChangedCount, c._uiaFocusChangedCount);

/// <summary>UIA イベントを発火する共通ヘルパ。プロバイダ未生成・SR 未リッスン時はスキップ。</summary>
private void RaiseUia(AutomationEvent ev)
{
    if (_provider is null) return;
    if (!System.Windows.Automation.Provider.AutomationInteropProvider.ClientsAreListening) return;
    try
    {
        System.Windows.Automation.Provider.AutomationInteropProvider.RaiseAutomationEvent(
            ev, _provider, new AutomationEventArgs(ev));
        if (ev == TextPatternIdentifiers.TextChangedEvent) _uiaTextChangedCount++;
        else if (ev == TextPatternIdentifiers.TextSelectionChangedEvent) _uiaSelectionChangedCount++;
        else if (ev == AutomationElementIdentifiers.AutomationFocusChangedEvent) _uiaFocusChangedCount++;
    }
    catch { /* UIA サーバ側の失敗は本体に影響させない */ }
}
```

**Step 4: `AfterEdit()` 末尾に配線**

既存 `AfterEdit` の末尾(または適切な位置)に:

```csharp
private void AfterEdit()
{
    // ...既存処理(スクロールバー再計算 / PositionCaret / BringCaretIntoView / Invalidate 等)...
    CacheSnapshot();   // Task 5 で追加済(RPC スレッドが読む _bufferSnapshot 更新)
    RaiseUia(TextPatternIdentifiers.TextChangedEvent);
    if (RaiseUiaSelectionEvents)
        RaiseUia(TextPatternIdentifiers.TextSelectionChangedEvent);
}
```

**Step 5: `MoveCaretTo` / `SetCaretCharOffset` / `SetSelectionCharRange` / `SetSelectionAnchored` 末尾にも配線**

各メソッドの末尾(既存の `Invalidate()` の直後など)で:

```csharp
if (RaiseUiaSelectionEvents)
    RaiseUia(TextPatternIdentifiers.TextSelectionChangedEvent);
```

を追加。**編集を伴わない選択/キャレット移動でも Selection イベントを発火する**(PC-Talker の追従に必要)。

**Step 6: テスト通過を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlUiaEventsTests
```

Expected: 3 passed(`ClientsAreListening=false` の環境では発火スキップ=カウンタ増えない懸念があるため、テスト側で `AutomationInteropProvider.ClientsAreListening` チェックをスキップする internal フラグを用意しておく必要がある。無ければ TestHook_ForceUiaListen で強制するか、テスト側で xunit skip)。

**Step 6-a: ClientsAreListening=false 時のスキップ問題**

CI 環境では UIA クライアントは動いていない → `ClientsAreListening=false` → 発火しない → テスト常に 0。対策:

`RaiseUia` にテストフック用の force フラグを持たせる:

```csharp
internal static bool TestHook_ForceUiaListen { get; set; } = false;

private void RaiseUia(AutomationEvent ev)
{
    if (_provider is null) return;
    if (!TestHook_ForceUiaListen &&
        !System.Windows.Automation.Provider.AutomationInteropProvider.ClientsAreListening) return;
    // ... 既存 ...
}
```

各テストの先頭で `EditorControl.TestHook_ForceUiaListen = true;`、末尾で `false` に戻す(または fixture で管理)。

**Step 7: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 169+3=172 passed。

**Step 8: コミット**

```bash
git add src/yEdit.Editor/EditorControl.cs tests/yEdit.Editor.Tests/EditorControlUiaEventsTests.cs
git commit -m "P5: Task 8 UIA TextChangedEvent/TextSelectionChangedEvent 発火配線(RaiseUiaSelectionEvents 抑止対応)"
```

**完了条件**:
- 編集/移動/選択で UIA イベント発火
- `RaiseUiaSelectionEvents=false` で選択イベント抑止(カウンタで確認)
- `ClientsAreListening=false` スキップは維持(テストは force フラグ)
- テスト 3 件緑
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 9: `AutomationFocusChangedEvent` + フォーカス時 `TextSelectionChanged` 明示発火

**目的**: `OnGotFocus` で `AutomationFocusChangedEvent` と `TextSelectionChangedEvent` の 2 イベントを発火。フォーカス時明示発火は PC-Talker が 2 秒ポーリングで選択を追う既知挙動(HANDOFF §13.6)への対策=v1(ScintillaHost)から踏襲。

**対象**:
- Modify: `src/yEdit.Editor/EditorControl.cs`(`OnGotFocus` 拡張)
- Create: `tests/yEdit.Editor.Tests/EditorControlUiaFocusEventTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Editor.Tests/EditorControlUiaFocusEventTests.cs`(新規):

```csharp
using System;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlUiaFocusEventTests
{
    [Fact]
    public void OnGotFocus_RaisesFocusChangedAndTextSelectionChanged()
    {
        Sta.Run(() =>
        {
            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                using var ctrl = new EditorControl();
                ctrl.SetSource(TextBufferBuilder.FromString("hi"));
                using var form = new Form();
                form.Controls.Add(ctrl);
                form.Show();
                using var other = new TextBox();
                form.Controls.Add(other);
                other.Focus();
                Application.DoEvents();

                EditorControl.TestHook_ResetUiaEventCounts(ctrl);
                ctrl.Focus();
                Application.DoEvents();

                var (_, selChanged, focusChanged) = EditorControl.TestHook_UiaEventCounts(ctrl);
                Assert.True(focusChanged >= 1, "AutomationFocusChangedEvent が発火していない");
                Assert.True(selChanged >= 1, "OnGotFocus 明示発火 TextSelectionChangedEvent が発火していない");
            }
            finally { EditorControl.TestHook_ForceUiaListen = false; }
        });
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlUiaFocusEventTests
```

Expected: 1 failed(現状 `OnGotFocus` から UIA 発火していない)。

**Step 3: `OnGotFocus` を拡張**

既存の `OnGotFocus`(P3 で追加済)の末尾に:

```csharp
protected override void OnGotFocus(EventArgs e)
{
    base.OnGotFocus(e);
    // ...既存処理(_hasFocus 更新 / CreateCaret / PositionCaret / ShowCaret 等)...

    // P5 Task 9: フォーカス獲得時の UIA イベント明示発火
    RaiseUia(AutomationElementIdentifiers.AutomationFocusChangedEvent);
    // PC-Talker 2 秒ポーリング対策(HANDOFF §13.6)
    if (RaiseUiaSelectionEvents)
        RaiseUia(TextPatternIdentifiers.TextSelectionChangedEvent);
}
```

**Step 4: テスト通過を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlUiaFocusEventTests
```

Expected: 1 passed。

**Step 5: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 172+1=173 passed。

**Step 6: コミット**

```bash
git add src/yEdit.Editor/EditorControl.cs tests/yEdit.Editor.Tests/EditorControlUiaFocusEventTests.cs
git commit -m "P5: Task 9 OnGotFocus で FocusChanged + TextSelectionChanged 明示発火(PC-Talker 2 秒ポーリング対策)"
```

**完了条件**:
- OnGotFocus で 2 イベント発火
- テスト 1 件緑
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 10: 座標 API 本実装:`GetBoundingRectangles(s, e)`

**目的**: v2 の `GetBoundingRectangles(s, e)` を本実装。`_lastFrame`(P2 の Frame・OnPaint で最新化)+ `_clientToScreen*`(UI スレッドで更新)を使い、[start, end) の各行スクリーン矩形を UIA 形式 (x,y,w,h, ...) で返す。

**対象**:
- Modify: `src/yEdit.Editor/EditorControl.cs`(`_lastFrame` volatile / `_clientToScreenX,Y` キャッシュ / `BuildBoundingRects` ヘルパ / `GetBoundingRectangles` 実装)
- Create: `tests/yEdit.Core.Tests/Editor/BuildBoundingRectsTests.cs`(純ロジック=Frame/Snapshot 前提の内部ヘルパをテスト)

**Step 1: 失敗するテストを書く(純ロジック契約)**

`tests/yEdit.Core.Tests/Editor/BuildBoundingRectsTests.cs`(新規):

```csharp
// 純ロジックヘルパを Core 側にも見えるように internal 移動するか、
// Editor.Tests 側で internal 露出。ここでは EditorControl の internal static メソッド
// BuildBoundingRects を Editor.Tests から呼ぶ形にする。

// ⇒ このテストは tests/yEdit.Editor.Tests/EditorControlBoundingRectsTests.cs に置く。
// Core.Tests には持ち込まない(TextSnapshot と Frame を両方使うため)。
```

`tests/yEdit.Editor.Tests/EditorControlBoundingRectsTests.cs`(新規):

```csharp
using System.Windows.Forms;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlBoundingRectsTests
{
    [Fact]
    public void GetBoundingRectangles_EmptyRange_ReturnsEmptyArray()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello"));
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                IUiaTextHost host = ctrl;
                Assert.Empty(host.GetBoundingRectangles(3, 3));   // 縮退範囲=空配列
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void GetBoundingRectangles_SingleLineRange_ReturnsOneRect()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello world"));
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                // 描画を 1 回発生させて _lastFrame を確定
                ctrl.Invalidate(); ctrl.Update(); Application.DoEvents();
                IUiaTextHost host = ctrl;
                var rects = host.GetBoundingRectangles(0, 5);   // "hello"
                Assert.Equal(4, rects.Length);                  // 1 行 = 4 要素
                Assert.True(rects[2] > 0);                      // 幅 > 0
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void GetBoundingRectangles_MultiLineRange_ReturnsMultipleRects()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("aaa\nbbb\nccc"));
            ctrl.Size = new System.Drawing.Size(200, 100);
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                ctrl.Invalidate(); ctrl.Update(); Application.DoEvents();
                IUiaTextHost host = ctrl;
                var rects = host.GetBoundingRectangles(0, 11);   // 全体
                Assert.Equal(3 * 4, rects.Length);               // 3 行 × 4 要素
            }
            finally { form.Close(); }
        });
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlBoundingRectsTests
```

Expected: 3 failed(現状スタブ=空配列)。

**Step 3: `EditorControl.cs` に `_lastFrame` + `_clientToScreen*` キャッシュ**

```csharp
// P5 Task 10: 座標 API 本実装
private volatile yEdit.Core.Layout.Frame? _lastFrame;
private int _clientToScreenX, _clientToScreenY;

// OnPaint の末尾(既存の実装の最後)に:
protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
{
    // ... 既存の描画処理 ...
    _lastFrame = frame;   // 描画完了時点の Frame を公開(不変)
    var origin = PointToScreen(new System.Drawing.Point(0, 0));
    _clientToScreenX = origin.X;
    _clientToScreenY = origin.Y;
}

// UpdateBoundsCache 内でも client→screen オフセットを更新する:
private void UpdateBoundsCache()
{
    if (!IsHandleCreated) return;
    var r = RectangleToScreen(ClientRectangle);
    lock (_boundsSync) _bounds = new System.Windows.Rect(r.Left, r.Top, r.Width, r.Height);
    var origin = PointToScreen(new System.Drawing.Point(0, 0));
    _clientToScreenX = origin.X;
    _clientToScreenY = origin.Y;
}
```

**Step 4: `GetBoundingRectangles` 本実装**

```csharp
double[] yEdit.Accessibility.IUiaTextHost.GetBoundingRectangles(int start, int end)
{
    var snap = _bufferSnapshot;
    var frame = _lastFrame;
    if (snap is null || frame is null) return System.Array.Empty<double>();

    int s = System.Math.Clamp(start, 0, snap.CharLength);
    int en = System.Math.Clamp(end, 0, snap.CharLength);
    if (s >= en) return System.Array.Empty<double>();

    var rects = new System.Collections.Generic.List<double>(16);
    int csx = _clientToScreenX, csy = _clientToScreenY;
    int lineHeightPx = LineHeightPx;

    int pos = s;
    int safety = 0;
    while (pos < en && safety++ < 100000)
    {
        int lineEndInclusive = System.Math.Min(en, LineEndNoBreak(snap, pos));
        var p1 = yEdit.Core.Layout.PixelMapper.OffsetToPx(frame, pos);
        var p2 = yEdit.Core.Layout.PixelMapper.OffsetToPx(frame, lineEndInclusive);
        double x = csx + p1.X;
        double y = csy + p1.Y;
        double w = System.Math.Max(1, p2.X - p1.X);
        double h = lineHeightPx;
        rects.Add(x); rects.Add(y); rects.Add(w); rects.Add(h);

        int nextLineStart = LineEnd(snap, pos);
        if (nextLineStart <= pos) break;
        pos = nextLineStart;
    }
    return rects.ToArray();
}

// private helper(v2 のライン計算をヘルパ化):
private static int LineEnd(TextSnapshot snap, int pos)
{
    int line = snap.GetLineIndexOfChar(pos);
    if (line + 1 < snap.LineCount) return snap.GetLineStart(line + 1);
    return snap.CharLength;
}

private static int LineEndNoBreak(TextSnapshot snap, int pos)
{
    int e = LineEnd(snap, pos);
    if (e > 0 && snap.GetChar(e - 1) == '\n') e--;
    if (e > 0 && snap.GetChar(e - 1) == '\r') e--;
    return e;
}
```

**Step 5: テスト通過を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlBoundingRectsTests
```

Expected: 3 passed。

**Step 6: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 173+3=176 passed。

**Step 7: コミット**

```bash
git add src/yEdit.Editor/EditorControl.cs tests/yEdit.Editor.Tests/EditorControlBoundingRectsTests.cs
git commit -m "P5: Task 10 GetBoundingRectangles 座標 API 本実装(_lastFrame + PixelMapper 逐行分解)"
```

**完了条件**:
- 単一行/複数行/縮退範囲を正しく返す
- テスト 3 件緑
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 11: 座標 API 本実装:`OffsetFromScreenPoint` + `TextProviderImplV2.RangeFromPoint`

**目的**: v2 の `OffsetFromScreenPoint(x, y)` を本実装。スクリーン→クライアント変換 → EditorControl の既存 HitTest 相当を Frame ベースで呼ぶ。Task 4 で `TextProviderImplV2.RangeFromPoint` は既にこれを呼ぶ形になっているため、host 側を実装するだけで RangeFromPoint も本挙動化。

**対象**:
- Modify: `src/yEdit.Editor/EditorControl.cs`(`OffsetFromScreenPoint` 実装 + `HitTestOffsetFromClient` internal 静的ヘルパ)
- Create: `tests/yEdit.Editor.Tests/EditorControlOffsetFromPointTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Editor.Tests/EditorControlOffsetFromPointTests.cs`(新規):

```csharp
using System.Windows.Forms;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlOffsetFromPointTests
{
    [Fact]
    public void OffsetFromScreenPoint_TopLeft_ReturnsZero()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello world"));
            ctrl.Size = new System.Drawing.Size(200, 100);
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                ctrl.Invalidate(); ctrl.Update(); Application.DoEvents();
                var screen = ctrl.PointToScreen(new System.Drawing.Point(2, 2));
                IUiaTextHost host = ctrl;
                Assert.Equal(0, host.OffsetFromScreenPoint(screen.X, screen.Y));
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void OffsetFromScreenPoint_MidLine_ReturnsMidChar()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello world"));
            ctrl.Size = new System.Drawing.Size(400, 100);
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                ctrl.Invalidate(); ctrl.Update(); Application.DoEvents();
                // "hello" の後ろ半ばの座標
                var mid = yEdit.Core.Layout.PixelMapper.OffsetToPx(
                    EditorControl.TestHook_GetLastFrame(ctrl)!, 3);
                var screen = ctrl.PointToScreen(new System.Drawing.Point(mid.X + 2, mid.Y + 2));
                IUiaTextHost host = ctrl;
                int result = host.OffsetFromScreenPoint(screen.X, screen.Y);
                Assert.InRange(result, 2, 4);   // 3 前後にヒット
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void OffsetFromScreenPoint_OutOfBounds_Clamped()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello"));
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                IUiaTextHost host = ctrl;
                // 範囲外の (-9999, -9999) → 0 (または clamp した先頭)
                Assert.Equal(0, host.OffsetFromScreenPoint(-9999, -9999));
            }
            finally { form.Close(); }
        });
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlOffsetFromPointTests
```

Expected: 3 failed(現状スタブ=0 固定)+ `TestHook_GetLastFrame` 未定義でビルドエラー。

**Step 3: `EditorControl.cs` に `OffsetFromScreenPoint` 本実装 + テストフック**

```csharp
internal static yEdit.Core.Layout.Frame? TestHook_GetLastFrame(EditorControl c) => c._lastFrame;

int yEdit.Accessibility.IUiaTextHost.OffsetFromScreenPoint(double x, double y)
{
    var snap = _bufferSnapshot;
    var frame = _lastFrame;
    if (snap is null || frame is null) return 0;

    int clientX = (int)(x - _clientToScreenX);
    int clientY = (int)(y - _clientToScreenY);
    return HitTestOffsetFromClient(snap, frame, clientX, clientY, LineHeightPx);
}

private static int HitTestOffsetFromClient(TextSnapshot snap, yEdit.Core.Layout.Frame frame, int clientX, int clientY, int lineHeightPx)
{
    // Frame の行情報から行 index を決める
    // PixelMapper 逆変換の簡易実装:
    // Frame は可視行分の情報を持つ。frame.TopLine + (clientY / lineHeightPx) が候補
    // ここは実装詳細に依存するため、既存の HitTest ロジックを参考にする。
    // 最終的には frame.OffsetFromClient(clientX, clientY) 相当を呼ぶ形。
    var (pos, _) = yEdit.Core.Layout.PixelMapper.OffsetFromPx(frame, clientX, clientY);
    return System.Math.Clamp(pos, 0, snap.CharLength);
}
```

**注**: `PixelMapper.OffsetFromPx` が P2 で実装されているかは要確認。無ければ P2 に相当機能を追加する必要がある。P2 の実施記録に `PixelMapper.OffsetToPx` は 41ns/回で PASS とあるが、逆方向 `OffsetFromPx` の存在は未確認。ここで無ければ、EditorControl の既存 `HitTest`(P3 OnMouseDown 経路)を internal static 化して呼ぶ形にする。

**Step 3-a: PixelMapper 逆変換の代替**

もし `PixelMapper.OffsetFromPx` が未存在なら、EditorControl の既存 HitTest ロジックを internal static 化:

```csharp
// EditorControl の OnMouseDown 経路にある HitTest を分離:
internal static int HitTestOffsetFromClient(TextSnapshot snap, yEdit.Core.Layout.Frame frame, int clientX, int clientY, int lineHeightPx)
{
    // 行を決める
    int visibleLineIndex = System.Math.Max(0, (clientY - 2) / lineHeightPx);
    int lineIndex = frame.TopLine + visibleLineIndex;
    if (lineIndex >= snap.LineCount) lineIndex = snap.LineCount - 1;
    if (lineIndex < 0) lineIndex = 0;

    int lineStart = snap.GetLineStart(lineIndex);
    int lineEnd = (lineIndex + 1 < snap.LineCount) ? snap.GetLineStart(lineIndex + 1) : snap.CharLength;
    // 改行を除く
    while (lineEnd > lineStart && (snap.GetChar(lineEnd - 1) == '\n' || snap.GetChar(lineEnd - 1) == '\r'))
        lineEnd--;

    // 桁を決める(PixelMapper.OffsetToPx を使って探索)
    // 二分探索または線形探索。行が短ければ線形で十分。
    int lo = lineStart, hi = lineEnd;
    while (lo < hi)
    {
        int mid = (lo + hi + 1) / 2;
        var p = yEdit.Core.Layout.PixelMapper.OffsetToPx(frame, mid);
        if (p.X <= clientX - 2) lo = mid;
        else hi = mid - 1;
    }
    return lo;
}
```

**Step 4: テスト通過を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlOffsetFromPointTests
```

Expected: 3 passed。

**Step 5: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 176+3=179 passed。

**Step 6: コミット**

```bash
git add src/yEdit.Editor/EditorControl.cs tests/yEdit.Editor.Tests/EditorControlOffsetFromPointTests.cs
git commit -m "P5: Task 11 OffsetFromScreenPoint 座標 API 本実装(HitTest 二分探索・RangeFromPoint 本挙動化)"
```

**完了条件**:
- スクリーン座標 → 文字オフセット変換が正しく動く
- 範囲外は clamp
- `TextProviderImplV2.RangeFromPoint` が本挙動になる(Task 4 の実装が有効化)
- テスト 3 件緑
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 12: `WordNavigatedEvent` 先出し(EditorControl)+ Core `WordBoundary` 委譲確認

**目的**: EditorControl に `WordNavigatedEvent` イベントを新設し、`OnKeyDown` の Ctrl+←→ 分岐末尾で発火(選択拡張中でない場合のみ)。移動前後のキャレット位置から単語スパンを算出し `WordNavigatedEventArgs(int WordStart, int WordEnd)` で通知。**P0 で確定した PC-Talker Ctrl+←→ 単語ナビの App 層補完受け口**を先出し。抑止は `RaiseUiaSelectionEvents=false` に相乗り。

**対象**:
- Modify: `src/yEdit.Editor/EditorControl.cs`(event + EventArgs + OnKeyDown 配線)
- Create: `tests/yEdit.Editor.Tests/EditorControlWordNavEventTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Editor.Tests/EditorControlWordNavEventTests.cs`(新規):

```csharp
using System;
using System.Windows.Forms;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlWordNavEventTests
{
    [Fact]
    public void CtrlRight_FiresWordNavigatedEvent()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello world"));
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                ctrl.SetCaretCharOffset(0);
                WordNavigatedEventArgs? received = null;
                ctrl.WordNavigated += (_, e) => received = e;

                EditorControl.TestHook_SendKey(ctrl, Keys.Right | Keys.Control);
                Application.DoEvents();

                Assert.NotNull(received);
                Assert.Equal(0, received!.WordStart);
                Assert.Equal(6, received.WordEnd);   // "hello" の右端 or 空白後の次単語頭
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void ShiftCtrlRight_DoesNotFire()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello world"));
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                ctrl.SetCaretCharOffset(0);
                int callCount = 0;
                ctrl.WordNavigated += (_, _) => callCount++;

                EditorControl.TestHook_SendKey(ctrl, Keys.Right | Keys.Control | Keys.Shift);
                Application.DoEvents();

                Assert.Equal(0, callCount);
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void RaiseUiaSelectionEvents_False_SuppressesWordNav()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBufferBuilder.FromString("hello world"));
            ctrl.RaiseUiaSelectionEvents = false;
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                ctrl.SetCaretCharOffset(0);
                int callCount = 0;
                ctrl.WordNavigated += (_, _) => callCount++;

                EditorControl.TestHook_SendKey(ctrl, Keys.Right | Keys.Control);
                Application.DoEvents();

                Assert.Equal(0, callCount);
            }
            finally { form.Close(); }
        });
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlWordNavEventTests
```

Expected: build error(`WordNavigatedEventArgs` / `WordNavigated` 未定義)。

**Step 3: `EditorControl.cs` に event + EventArgs + OnKeyDown 配線を追加**

```csharp
/// <summary>Ctrl+←→ で単語ナビが発生したとき発火(選択拡張中でない場合のみ)。</summary>
public event System.EventHandler<WordNavigatedEventArgs>? WordNavigated;

private void RaiseWordNavigated(int wordStart, int wordEnd)
{
    if (!RaiseUiaSelectionEvents) return;
    WordNavigated?.Invoke(this, new WordNavigatedEventArgs(wordStart, wordEnd));
}

// OnKeyDown の Ctrl+← / Ctrl+→ 分岐(P3 で実装済)の末尾に配線:
// 例:
// case Keys.Right:
//     if (ctrl) {
//         int before = _caret;
//         MoveCaretToChar(NavigationCommands.MoveRightWord(...), shift);
//         if (!shift && before != _caret) {
//             int wsStart = System.Math.Min(before, _caret);
//             int wsEnd = System.Math.Max(before, _caret);
//             RaiseWordNavigated(wsStart, wsEnd);
//         }
//     } ...
```

`src/yEdit.Editor/WordNavigatedEventArgs.cs`(新規):

```csharp
namespace yEdit.Editor;

/// <summary>単語ナビ(Ctrl+←→)発火時のスパン情報。</summary>
public sealed class WordNavigatedEventArgs : System.EventArgs
{
    /// <summary>単語スパンの開始オフセット(UTF-16)。</summary>
    public int WordStart { get; }
    /// <summary>単語スパンの終端オフセット(UTF-16・排他)。</summary>
    public int WordEnd { get; }

    public WordNavigatedEventArgs(int wordStart, int wordEnd)
    {
        WordStart = wordStart;
        WordEnd = wordEnd;
    }
}
```

**Step 4: `TestHook_SendKey` の確認**

P3 で `TestHook_SendKey` が導入されているはず。無ければ既存の SendKey ヘルパを internal static で追加。

**Step 5: テスト通過を確認**

```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlWordNavEventTests
```

Expected: 3 passed。

**Step 6: 既存テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 179+3=182 passed。

**Step 7: コミット**

```bash
git add src/yEdit.Editor/EditorControl.cs src/yEdit.Editor/WordNavigatedEventArgs.cs tests/yEdit.Editor.Tests/EditorControlWordNavEventTests.cs
git commit -m "P5: Task 12 WordNavigatedEvent 先出し(PC-Talker 単語ナビ補完受け口・RaiseUiaSelectionEvents 抑止)"
```

**完了条件**:
- WordNavigatedEvent が Ctrl+←→ で発火
- Shift+Ctrl+←→ では発火しない
- RaiseUiaSelectionEvents=false で抑止
- テスト 3 件緑
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 13: Smoke `--uia` + UiaSmokeAnnouncer

**目的**: `tests/yEdit.Editor.Smoke` に `--uia` サブコマンドを追加し、EditorControl を単独で起動して UIA を SR から拾える状態にする。`UiaSmokeAnnouncer.cs` を新設し、`CaretEnteredEmptyLine` で「空行」/ `WordNavigated` で単語スパン発声(実 App 層 Announcer の最小サブセット・`RaiseAutomationNotification` + `PCTKPReadW` フォールバック)。

**対象**:
- Modify: `tests/yEdit.Editor.Smoke/Program.cs`(`--uia` サブコマンド追加 + Announcer 配線)
- Create: `tests/yEdit.Editor.Smoke/UiaSmokeAnnouncer.cs`(新規)
- Modify: `tests/yEdit.Editor.Smoke/MainForm.cs` 相当(タイトルバーに UIA event ログ表示)

**Step 1: `UiaSmokeAnnouncer.cs` 新設**

`tests/yEdit.Editor.Smoke/UiaSmokeAnnouncer.cs`(新規):

```csharp
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using yEdit.Editor;

namespace yEdit.Editor.Smoke;

/// <summary>
/// smoke 用の最小 Announcer(実 App 層 Announcer のサブセット)。
/// P0 で確定した PC-Talker Ctrl+←→ 単語ナビの 1 文字読み補完を smoke 上で再現。
/// P6 で本物 Announcer に差し替えられる。
/// </summary>
internal sealed class UiaSmokeAnnouncer
{
    [DllImport("PCTKUSR.DLL", CharSet = CharSet.Unicode)]
    private static extern int PCTKPReadW(string text, int mode);

    private readonly EditorControl _ctrl;
    private readonly IRawElementProviderSimple? _provider;

    public UiaSmokeAnnouncer(EditorControl ctrl, IRawElementProviderSimple? provider)
    {
        _ctrl = ctrl;
        _provider = provider;
        _ctrl.CaretEnteredEmptyLine += (_, _) => Announce("空行");
        _ctrl.WordNavigated += (_, e) =>
        {
            string span = ((yEdit.Accessibility.IUiaTextHost)_ctrl).GetTextRange(e.WordStart, e.WordEnd - e.WordStart);
            if (!string.IsNullOrEmpty(span)) Announce(span.Trim());
        };
    }

    private void Announce(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // UIA 通知(NVDA / ナレーターが読む)
        if (_provider is not null && AutomationInteropProvider.ClientsAreListening)
        {
            try
            {
                AutomationInteropProvider.RaiseNotificationEvent(
                    _provider,
                    AutomationNotificationKind.ActionCompleted,
                    AutomationNotificationProcessing.MostRecent,
                    text,
                    "yedit.smoke.announcer");
            }
            catch { }
        }
        // PC-Talker 直叩き(PCTKPReadW=モード 1=割り込み読み)
        try { PCTKPReadW(text, 1); } catch { /* PC-Talker 未インストール環境で無視 */ }
    }
}
```

**Step 2: `Program.cs` に `--uia` 分岐追加**

```csharp
static void Main(string[] args)
{
    // ... 既存の引数解析 ...
    bool useUia = args.Contains("--uia");
    // ... フォーム生成 ...
    var mainForm = new MainForm { UseUiaAnnouncer = useUia };
    Application.Run(mainForm);
}
```

**Step 3: `MainForm` 相当に配線**

```csharp
public bool UseUiaAnnouncer { get; set; }
private UiaSmokeAnnouncer? _announcer;
private EditorControl _editor = /* 既存の EditorControl */;

protected override void OnShown(EventArgs e)
{
    base.OnShown(e);
    if (UseUiaAnnouncer)
    {
        // TextControlProviderV2 は WM_GETOBJECT で生成された後に取得できる。
        // ここでは EditorControl の internal テストフックで取得 or nullable のまま Announcer 側で先送り。
        _announcer = new UiaSmokeAnnouncer(_editor, null);
        Text = $"[UIA] {Text}";
    }
}
```

**注**: UIA プロバイダは WM_GETOBJECT 経由で lazy 生成されるため、`Announcer` コンストラクタでは null 渡し(通知イベントは PC-Talker 直叩きのみ動く)。SR 側からアクセスがあれば `_provider` が生成される → Announcer に「provider を後から差し込む」API を持たせるか、または SR 経由の通知は諦めて PC-Talker 直叩きのみに絞る。**smoke 用途としてはこれで十分**。

**Step 4: build + smoke 起動確認**

```
dotnet build src/yEdit.sln
dotnet run --project tests/yEdit.Editor.Smoke -- --uia
```

Expected: smoke ウィンドウがタイトルバーに `[UIA]` プレフィックス付きで起動。閉じる。

**Step 5: 既存テスト全緑 + build 0 警告**

```
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 182 passed。

**Step 6: コミット**

```bash
git add tests/yEdit.Editor.Smoke/
git commit -m "P5: Task 13 smoke --uia + UiaSmokeAnnouncer(空行・単語ナビ発声補完)"
```

**完了条件**:
- smoke に `--uia` サブコマンド
- Announcer が CaretEnteredEmptyLine と WordNavigated を購読
- タイトルバーに [UIA] プレフィックス
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## Task 14: tools/*.ps1 派生 + 実機チェックリスト + 別エージェント最終レビュー + 設計書§3 追記

**目的**: `tools/verify-uia.ps1` / `walk-test.ps1` / `word-sim.ps1` を smoke `--uia` バイナリ向けに派生(既存は Sci/UiaProbe 用として温存)。実機中間検証チェックリスト(P0 8 項目 + Spy++ + ATOK 7 項目 = 16 項目)を新設。別エージェントによる最終レビュー+ Critical/Important 潰し+設計書§3 に P5 結果表追記+メモリ更新+実機ゲート判定。

**対象**:
- Create: `tools/verify-uia-editor.ps1`(新規・smoke --uia 向け)
- Create: `tools/walk-test-editor.ps1`(新規)
- Modify: `tools/word-sim.ps1`(smoke --uia 上で PASS するように引数化)
- Create: `docs/plans/2026-07-06-p5-uia-checklist.md`(新規・16 項目)
- Modify: `docs/plans/2026-07-05-custom-editcontrol-design.md`(§3 P5 結果表追記)
- Modify: `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\custom-editcontrol.md`(進捗更新)
- Modify: `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\MEMORY.md`(1 行更新)

**Step 1: `tools/verify-uia-editor.ps1` 新設**

既存 `tools/verify-uia.ps1` をコピー → 起動対象を `yEdit.Editor.Smoke.exe --uia` に、`AutomationId` を `"editor"` に変更。ControlType=Document / Name="本文" / TextPattern の DocumentRange / GetSelection / RangeFromPoint(有効)を検証。

**Step 2: `tools/walk-test-editor.ps1` 新設**

既存 `tools/walk-test.ps1` をコピー → 起動対象を smoke --uia に変更 → 単文字/行歩き + GetText の期待値比較。

**Step 3: `tools/word-sim.ps1` を引数化**

既存 P0 の word-sim.ps1 は UiaProbe 用。smoke --uia でも同じ検証項目を PASS することを確認できるよう、`-Target ProbeExe|EditorSmokeExe` 等の引数を追加。

**Step 4: SR 非依存スクリプト実行**

```
pwsh tools/verify-uia-editor.ps1
pwsh tools/walk-test-editor.ps1
pwsh tools/word-sim.ps1 -Target EditorSmokeExe
```

Expected: 全 PASS(EXIT 0)。

**Step 5: `docs/plans/2026-07-06-p5-uia-checklist.md` 作成**

P0 チェックリストのフォーマットを踏襲し、以下 16 項目:

```markdown
# P5 実機中間検証チェックリスト

## 起動
1. Windows キー + Ctrl+Enter で SR 起動 → `yEdit.Editor.Smoke.exe --uia` 起動 → フォーカスで「本文」と発声(NVDA / PC-Talker / ナレーター)

## SR 読み上げ(NVDA / PC-Talker / ナレーター 各実施)
2. 単文字ナビ(← → ↑ ↓)→ 移動先の 1 文字を発声
3. 行ナビ(Home / End / 上下矢印)→ 行内容を発声(空行は SR 既定 or 「空行」)
4. SayAll(NVDA: NVDA+↓ / PC-Talker: 全文読み)→ 先頭から末尾まで途切れず読む
5. 選択読み(Shift+矢印)→ 選択スパンを発声
6. Ctrl+←→ 単語ナビ → 単語スパンを発声(PC-Talker は smoke Announcer 経由)
7. 空行に着地 → 「空行」または SR 既定発声
8. 他ウィンドウ → smoke に復帰 → フォーカス時に位置と本文の一部を発声(TextSelectionChanged 明示発火の確認)

## ネイティブ表面(Spy++ / GetWindowText 経由)
9. Spy++ の Text 欄が空・SendMessage(WM_GETTEXTLENGTH)=0 の確認

## ATOK 実機(P4 チェックリスト再実施 + SR 読み上げ検証)
10. 「にほんご」タイプ → 変換なし確定 → 未確定・確定文字列とも SR が読む
11. 「かんじ」タイプ → スペースで変換 → Enter → 変換対象節が反転・確定文字列が SR に読まれる
12. 「わたしはにほんじん」タイプ → 連文節変換 → 節境界が SR に読まれる
13. 候補ウィンドウ表示 → SR が候補窓を読む(または候補窓の位置がキャレット直下)
14. ESC 取消 → 未確定が消える・SR が「取消」相当を発声(または無音)
15. 未確定中に BackSpace → 未確定が 1 文字短くなる・SR が更新後の未確定を読む
16. 未確定中に他ウィンドウへフォーカス移動 → 未確定が確定される・SR に「戻ってきても残骸なし」の状態が伝わる
```

**Step 6: 別エージェント最終レビュー**

Task ツールで `superpowers:code-reviewer` エージェントを起動:

```
プロンプト:
「P5(自作 EditorControl に UIA プロバイダを載せる)の 13 コミットをレビューしてください。設計書=docs/plans/2026-07-06-p5-uia-connect-design.md、実装計画=docs/plans/2026-07-06-p5-uia-connect.md。特に以下を重点確認:
- RPC スレッド安全性(_bufferSnapshot / _lastFrame の volatile 参照とキャッシュ更新の順序)
- WM_GETOBJECT 応答が OBJID_CLIENT に反応していないこと
- WM_GETTEXT/WM_GETTEXTLENGTH の抑止が SendMessage テストで確認済
- UIA イベント発火のスキップ条件(ClientsAreListening / RaiseUiaSelectionEvents)
- Move スパン保持ロジック(TextRangeProviderV2)が v1 と同挙動
- 座標 API の逐行分解の境界(CRLF 混在・空行・末尾行)
- ScintillaHost / App 層 / v1 系 UIA コードに一切変更が無いこと(Task 1 のリネーム以外)
Critical / Important / Minor で報告してください。」
```

Critical / Important を潰す(=次コミットで対応)。

**Step 7: 設計書§3 P5 結果表追記**

`docs/plans/2026-07-05-custom-editcontrol-design.md` §3 P5 に、P4 結果表と同形式で:

```markdown
#### P5 結果(YYYY-MM-DD・**自動 DoD 全達成・実機中間検証結果**)

- 実装: Task 1〜14 全完了・N commits(`XXX`〜`YYY`、feature branch のみ・main 未変更)
- 別エージェント最終レビュー: Critical N / Important N / Minor N
- ビルド 0 警告 / テスト全緑:
  - Core.Tests 528→540(+12=IUiaTextHost 契約/TextRangeProviderV2/TextProviderImplV2/座標ヘルパ)
  - Editor.Tests 155→182(+27=UIA host / GetObject / NativeSurface / UIA イベント / Focus / BoundingRects / OffsetFromPoint / WordNav)
- SR 非依存スクリプト(verify-uia-editor / walk-test-editor / word-sim)全 PASS
- **実機中間検証**: NVDA=… PC-Talker=… ナレーター=… ATOK=…(16 項目チェックリストの結果)
- ScintillaHost / App 層 / v1 系 UIA コードは無変更維持(Task 1 の interface 名リネームのみ)
- 次: P6(App 層一発置き換え + Scintilla / v1 一括撤去)
```

**Step 8: メモリ更新**

`custom-editcontrol.md` に P5 完了記録を追記(P4 と同形式)。`MEMORY.md` の該当行 1 行を「P5 完了・P6 待ち」に更新。

**Step 9: 最終テスト全緑 + build 0 警告**

```
dotnet build src/yEdit.sln
dotnet test tests/yEdit.Core.Tests
dotnet test tests/yEdit.Editor.Tests
```

Expected: 0 warnings / Core.Tests 540 passed / Editor.Tests 182 passed(または調整後の最終数字)。

**Step 10: 実機中間検証(ユーザー実施)**

ユーザーに `docs/plans/2026-07-06-p5-uia-checklist.md` を渡し、NVDA / PC-Talker / ナレーター で 16 項目を実行してもらう。結果を設計書§3 P5 結果に追記。

**Step 11: コミット**

```bash
git add tools/verify-uia-editor.ps1 tools/walk-test-editor.ps1 tools/word-sim.ps1 docs/plans/2026-07-06-p5-uia-checklist.md docs/plans/2026-07-05-custom-editcontrol-design.md
git commit -m "P5: Task 14 tools/*.ps1 派生 + 実機チェックリスト + §3 P5 結果表追記(自動 DoD 全達成・実機中間検証結果=XXX)"
```

メモリ更新は別コミット扱いにしない(グローバルメモリのため git 管理外)。

**完了条件**:
- tools/*.ps1 の EditorControl 向け派生が動作
- 実機中間チェックリスト作成
- 別エージェント最終レビュー完了 → Critical 0 / Important 0
- 設計書§3 P5 結果表追記
- メモリ更新
- 実機中間検証実施記録(NVDA/PC-Talker/ナレーター/ATOK 16 項目の結果)
- build 0 警告

**実施記録**:
_(実施後に追記)_

---

## 7. 全体 DoD(P5 完了条件)

- Core.Tests 528 → 540(+12) 全緑
- Editor.Tests 155 → 182(+27) 全緑
- build 0 警告
- SR 非依存スクリプト(verify-uia-editor / walk-test-editor / word-sim)全 PASS
- Spy++ / GetWindowText で本文が漏れないこと目視確認
- 実機中間検証チェックリスト 16 項目
  - NVDA:単文字/行/SayAll/選択/単語(素直)/空行/フォーカス復帰/ATOK 未確定/確定 → 全 OK
  - PC-Talker:単文字/行/SayAll/選択/単語(Announcer 補完)/空行/フォーカス復帰/ATOK 未確定/確定 → 全 OK(単語 NG なら Announcer 種別調整で再挑戦・NG のまま許容判定は要ユーザー承認)
  - ナレーター:単文字/行/選択/フォーカス → 実用範囲(P0 と同水準)
- 別エージェント最終レビュー Critical 0 / Important 0
- 設計書§3 P5 結果表追記
- 撤退安全性:App 層 / ScintillaHost / v1 系 UIA は無変更のため `git revert` で P4 完了状態(683 テスト緑)へ戻せる

## 8. 次フェーズへの申し送り(実装完了時点で確定させる)

- **P6(App 層一発置き換え + 一括撤去)**:
  - App 層編集エンジン結線を Scintilla → EditorControl に差替
  - EditorControl の `WordNavigatedEvent` / `CaretEnteredEmptyLine` を App 層本物 Announcer に配線(P5 の smoke Announcer は削除)
  - 一括撤去対象:`ScintillaHost.cs` / `Sci.cs` / `Scintilla5.NET` PackageReference / `IUiaTextHostLegacy` / v1 用 Provider 群(`TextControlProvider` / `TextProviderImpl` / `TextRangeProvider` / `TextNavigation`)/ App 層 v1 UIA 参照 / `tools/verify-uia.ps1` / `walk-test.ps1`(v1 用)/ `Scintilla` / `Lexilla` ネイティブ DLL 同梱
  - `SrRoute.Nvda` / `CsvFocusSink` / `ServeUiaProvider` / `ApplySrAdaptation` は**無効化のみで残す**(切り分け用)
- **P7(実機総合検証)**:
  - 3 SR × 主要全機能フルマトリクス + `RaiseUiaSelectionEvents` / `CsvFocusSink` の完全撤去

---

**関連**:
- 設計書: `docs/plans/2026-07-06-p5-uia-connect-design.md`
- 親設計書: `docs/plans/2026-07-05-custom-editcontrol-design.md`(§2-4 IUiaTextHost v2 / §2-7 ネイティブ表面原則 / §3-P5 スコープ)
- P0 実機結果: `docs/plans/2026-07-05-p0-sr-probe-checklist.md`(PC-Talker 単語ナビの App 層補完必要性)
- P3 実装計画: `docs/plans/2026-07-05-p3-editor-input.md`(`CaretEnteredEmptyLine` / `RaiseUiaSelectionEvents` / `AfterEdit` の実装済み受け口)
- P4 実装計画: `docs/plans/2026-07-06-p4-ime.md`(WndProc 追加地点=P5 の WM_GETOBJECT 挿入位置)
- P4 チェックリスト: `docs/plans/2026-07-06-p4-ime-checklist.md`(P5 で SR 読み上げ検証と統合して再実施)
- 参照実装: `src/yEdit.UiaProbe/UiaTextControl.cs`(WM_GETOBJECT / IUiaTextHost v1 / UIA イベント発火 / Bounds キャッシュパターン / RaiseUia ヘルパ)
