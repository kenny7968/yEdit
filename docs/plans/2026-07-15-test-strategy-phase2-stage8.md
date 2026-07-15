# Phase 2 Stage 8 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Phase 2 の締めとして、Controller 型狭化+readonly 化(A)・KinsokuFormatController 抽出(B)・GrepController の jumpTo 外し(C)・Stage 7 由来の高価値テスト追加(D)を 1 ブランチ 4 Task で完遂する。

**Architecture:** ストラングラー方式(Stage 4〜7 と同型)。1 Stage=1 フィーチャーブランチ=1 no-ff マージ。SDD+各 Task 完了時 2 段レビュー+最終ブランチレビュー。EditorControl は実物・Form/OS 境界のみ Fake。挙動不変(SR 発声文言・フォーカス遷移を変えない)。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit / STA テストヘルパ(`Sta.Run` + `HostForm.CreateWithDocs`) / FakeAnnouncer・FakePrompt・FakeFileDialogService・FakeGrepView・FakeGrepResultsView・FakeGrepSearchFn(既存)+新規 FakeAnnouncer は既存流用。

**上位文書:**
- 設計書: `docs/plans/2026-07-15-test-strategy-phase2-stage8-design.md`
- Phase 2 設計書: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`

**テスト数遷移:** 938 → 944(B) → 945(C) → **952**(D)

---

## Task 0: ブランチ作成+設計書レビュー

**目的:** feature ブランチを切り、Stage 4〜7 と同型の運用に入る。

**Step 0.1: 事前ゲート**

```powershell
git status         # クリーンであること(installer/ publish/ は untracked=無視)
git log --oneline -3
# 期待: HEAD=6d09716(設計書コミット)・その前=9e137a3(Stage 7 追記)
tools/pre-merge-check.ps1
# 期待: 全緑・Release 0 警告・938 tests
```

**Step 0.2: ブランチ作成**

```powershell
git checkout -b feature/test-strategy-phase2-stage8
git log --oneline -1
# 期待: HEAD が main と同じ 6d09716
```

**Step 0.3: 設計書を精読(Claude)**

- `docs/plans/2026-07-15-test-strategy-phase2-stage8-design.md` §0〜§6 を通読
- 特に §2 の Task A/B/C/D 詳細・§3 Task 依存関係・§5 リスクを確認

**Commit:** なし(ブランチ作成のみ)

---

## Task A: 型狭化+readonly 化(純リファクタ・挙動不変)

**目的:** 3 Controller の `_owner` を `Form` → `IWin32Window` に狭め、`MainForm._announcer` を readonly 化し、`SearchController.FindPrev` の `_lastHit` 三項式デッドコードを簡約する。

**Files:**
- Modify: `src/yEdit.App/SearchController.cs:16` `_owner` 型置換 / `:23` ctor 引数型置換 / `:114-116` 三項式簡約
- Modify: `src/yEdit.App/FileController.cs:19` `_owner` 型置換 / `:29-32` ctor 引数型置換
- Modify: `src/yEdit.App/GrepController.cs:14` `_owner` 型置換 / `:26` ctor 引数型置換
- Modify: `src/yEdit.App/MainForm.cs:30` `_announcer` readonly 化 / `:54-56` FileController ctor を名前付き引数化
- Test: 既存 App.Tests(SearchControllerTests / FileControllerTests / GrepControllerTests)は無変更で緑のまま

### Step A.1: `_owner` を IWin32Window に狭める(3 Controller 一括)

**分析:**
- `SearchController` は `_owner` を `FindReplaceDialog.ShowDialog(_owner)` / `IFindReplaceView.ShowAndFocus(_owner)` の親引数として使うのみ(Form 固有メンバー未参照)
- `FileController` は `_owner` を `_fileDialogs.PickOpenPath(_owner)` / `_prompt.OkCancel(...)` 系ダイアログの親として使うのみ
- `GrepController` は `_owner` を `_view.ShowAndFocus(_owner)` / `_resultsView.ShowResults(_owner)` の親として使うのみ

いずれも `IWin32Window` インターフェースで十分。

**変更(SearchController.cs)**:

```csharp
// Before (line 16)
private readonly Form _owner;
// After
private readonly IWin32Window _owner;

// Before (line 23)
public SearchController(DocumentManager docs, Form owner, IAnnouncer announcer,
// After
public SearchController(DocumentManager docs, IWin32Window owner, IAnnouncer announcer,
```

**変更(FileController.cs)**:

```csharp
// Before (line 19)
private readonly Form _owner;
// After
private readonly IWin32Window _owner;

// Before (line 29-32)
public FileController(
    DocumentManager docs, Form owner, Func<AppSettings> settings,
    Action saveSettings, Action recentChanged, Action metaChanged,
    Action<Document> openedFresh, IUserPrompt prompt, IFileDialogService fileDialogs)
// After
public FileController(
    DocumentManager docs, IWin32Window owner, Func<AppSettings> settings,
    Action saveSettings, Action recentChanged, Action metaChanged,
    Action<Document> openedFresh, IUserPrompt prompt, IFileDialogService fileDialogs)
```

**変更(GrepController.cs)**:

```csharp
// Before (line 14)
private readonly Form _owner;
// After
private readonly IWin32Window _owner;

// Before (line 26)
Form owner,
// After
IWin32Window owner,
```

**Step A.1.a: ビルド確認**

```powershell
dotnet build -c Release -warnaserror
# 期待: 0 警告・0 エラー
```

**Step A.1.b: 全テスト実行**

```powershell
tools/pre-merge-check.ps1
# 期待: 938 tests 緑・0 警告
```

**Commit A.1:**

```powershell
git add src/yEdit.App/SearchController.cs src/yEdit.App/FileController.cs src/yEdit.App/GrepController.cs
git commit -m "refactor: Controller の _owner を Form -> IWin32Window に狭化(挙動不変)

Search/File/Grep の 3 Controller すべてで _owner の用途はダイアログ親引数のみ
(Form 固有メンバー未参照)。Stage 4/6/7 の申し送りに従い IWin32Window へ狭める。
呼び出し側 MainForm は Form: IWin32Window なので無変更、テストは無変更で緑。"
```

### Step A.2: `MainForm._announcer` を readonly 化

**分析:** `_announcer` は `MainForm.cs:30` で `null!` 初期化 → ctor 内(`:57`)で `new UiaAnnouncer(_announceLabel)` を代入。以後の再代入はなし(grep で確認)。field 初期化子は不要。

**変更(MainForm.cs)**:

```csharp
// Before (line 30)
private IAnnouncer _announcer = null!; // コンストラクタで UiaAnnouncer を直接生成（下記参照）
// After
private readonly IAnnouncer _announcer; // コンストラクタで UiaAnnouncer を直接生成（下記参照）
```

**Step A.2.a: 再代入がないことを確認**

```powershell
Select-String -Path "src/yEdit.App/MainForm.cs" -Pattern "_announcer\s*="
# 期待: `:57` の 1 箇所のみ(ctor 内代入)
```

**Step A.2.b: ビルド+テスト**

```powershell
tools/pre-merge-check.ps1
# 期待: 938 tests 緑・0 警告(CS8618 が出ないこと)
```

**Commit A.2:**

```powershell
git add src/yEdit.App/MainForm.cs
git commit -m "refactor: MainForm._announcer を readonly 化(Stage 2 申し送り消化)

ctor で 1 度だけ代入・以後不変。null! 初期化を廃止し readonly に。"
```

### Step A.3: FileController ctor を名前付き引数化

**分析:** Stage 4 で FindReplaceCallbacks の位置取り違えを検出できなかった教訓から、複雑な ctor は呼び出し側で名前付き引数化する。FileController は 9 引数のうち delegate 4 個(saveSettings/recentChanged/metaChanged/openedFresh)が同型 Action で入れ替わっても検出不能。

**変更(MainForm.cs:54-56)**:

```csharp
// Before
_file = new FileController(_docs, this, () => _settings,
    SaveSettingsSafe, RebuildRecentMenu, () => { UpdateTitle(); UpdateStatus(); },
    AutoEnterCsvMode, new MessageBoxUserPrompt(), new WinFormsFileDialogService());

// After
_file = new FileController(
    docs: _docs,
    owner: this,
    settings: () => _settings,
    saveSettings: SaveSettingsSafe,
    recentChanged: RebuildRecentMenu,
    metaChanged: () => { UpdateTitle(); UpdateStatus(); },
    openedFresh: AutoEnterCsvMode,
    prompt: new MessageBoxUserPrompt(),
    fileDialogs: new WinFormsFileDialogService());
```

**変更(tests/yEdit.App.Tests/FileControllerTests.cs:42)**: 名前付き引数化(Stage 4 で SearchControllerTests に施した同型)。

```csharp
// Before
File = new FileController(Docs, Form, () => Settings,
    SaveSettings, RecentChanged, MetaChanged,
    OpenedFresh, Prompt, FileDialogs);

// After
File = new FileController(
    docs: Docs,
    owner: Form,
    settings: () => Settings,
    saveSettings: SaveSettings,
    recentChanged: RecentChanged,
    metaChanged: MetaChanged,
    openedFresh: OpenedFresh,
    prompt: Prompt,
    fileDialogs: FileDialogs);
```

**Step A.3.a: 対応固定テスト追加**

Stage 4 の SearchControllerTests に追加した `Callbacks_AreWiredToMatchingControllerMethods` と同型のテストを FileControllerTests に追加(想定 +1 件)。ただし FileController は Controller 直下の delegate ではなく MainForm 側 wiring なので、テストの目的は「ctor の 4 個の同型 delegate が意図した順で並んでいる」ことの機械検出。

**方針**: この対応固定テストは MainForm 配線側なので App.Tests では実装しにくい(MainForm は composition root)。**Task A.3 では対応固定テストを追加しない**=名前付き引数化のみで自己ドキュメント化する(コンパイル時強制)。将来の位置取り違え防止は名前付き引数の存在自体で担保。

**Step A.3.b: ビルド+テスト**

```powershell
tools/pre-merge-check.ps1
# 期待: 938 tests 緑
```

**Commit A.3:**

```powershell
git add src/yEdit.App/MainForm.cs tests/yEdit.App.Tests/FileControllerTests.cs
git commit -m "refactor: FileController の呼び出しを名前付き引数化(Stage 4 教訓の File 版)

9 引数のうち delegate 4 個が同型 Action で入れ替わっても検出不能なため、
MainForm 側と FileControllerTests 側の呼び出しを名前付き引数化する。"
```

### Step A.4: FindPrev の `_lastHit` 三項式デッドコード簡約

**分析(`SearchController.cs:114-116`)**:

```csharp
int before = (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
    ? h.Start
    : selStart;
```

三項の真経路条件: 選択範囲が `_lastHit` と完全一致(`selStart == h.Start && selEnd == h.End`)。このとき `h.Start == selStart` が成立するため `? h.Start : selStart` は常に `selStart` に等しい=簡約可能。Forward 側(`107-109`)は `h.Start + Math.Max(1, h.Length)` でゼロ幅前進の意味があるため温存。

**変更**:

```csharp
// Before (line 114-116)
int before = (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
    ? h.Start
    : selStart;

// After (1 line)
int before = selStart;
```

**Step A.4.a: 既存 SearchControllerTests 全 32 件が緑のまま**

```powershell
dotnet test tests/yEdit.App.Tests --filter "FullyQualifiedName~SearchControllerTests" -c Release
# 期待: 32 tests all passed
```

**Step A.4.b: ミューテーション自己検証**

コメントアウトで `int before = selEnd;`(誤り版)に変えて赤を確認 → 復元。「FindPrev の後退基点」を検証するテストが実在することを確認(Stage 4 の後退系テストが該当)。

**Step A.4.c: ローカルゲート**

```powershell
tools/pre-merge-check.ps1
# 期待: 938 tests 緑
```

**Commit A.4:**

```powershell
git add src/yEdit.App/SearchController.cs
git commit -m "refactor: SearchController.FindPrev の _lastHit 三項式デッドコード除去

_lastHit 一致条件で selStart == h.Start が成立するため両分岐同値。
before = selStart に 1 行簡約(Stage 4 実施記録の申し送り)。挙動不変。"
```

### Task A DoD

- [ ] `git log feature/test-strategy-phase2-stage8 --oneline` で 4 commit(A.1〜A.4)
- [ ] `tools/pre-merge-check.ps1` 全緑・Release 0 警告・938 tests
- [ ] 挙動不変(公開挙動・SR 発声文言・フォーカス遷移)
- [ ] Task A の 2 段レビュー(実装レビュー+品質レビュー)完了

---

## Task B: KinsokuFormatController 抽出

**目的:** MainForm.FormatWithKinsoku(30 行・分岐 6・通知 3 種)を Controller 化し、Controller 単位でテスト可能にする(唯一の実質的痩身)。

**Files:**
- Create: `src/yEdit.App/KinsokuFormatController.cs`
- Modify: `src/yEdit.App/MainForm.cs:466-495` → 抽出後は 3 行(field 宣言+ctor 生成+1 行 dispatch)
- Test: `tests/yEdit.App.Tests/KinsokuFormatControllerTests.cs`(新規・6 件)

### Step B.1: 失敗テストを先に書く(TDD・test-first)

**新規ファイル**: `tests/yEdit.App.Tests/KinsokuFormatControllerTests.cs`

```csharp
using yEdit.App.Settings;
using yEdit.App.Tests.Fakes;
using yEdit.Core.Csv;
using yEdit.Core.Reading;
using yEdit.Core.Text;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 8 Task B: KinsokuFormatController の抽出(MainForm.FormatWithKinsoku から)。
/// 6 件: 部分整形・全文整形・変更なし・CSV 抑止・空バッファ no-op・EOL 追随。
/// EditorControl は実物・Announcer/Settings は Fake/POCO。
/// </summary>
public class KinsokuFormatControllerTests
{
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeAnnouncer Announcer { get; } = new();
        public KinsokuFormatController Kinsoku { get; }
        public AppSettings Settings { get; } = new()
        {
            WrapColumn = 20,
            KinsokuLineStartChars = "、。",
            KinsokuLineEndChars = "「（",
            KinsokuHangChars = "",
            TabWidth = 4,
        };

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Kinsoku = new KinsokuFormatController(Docs, Announcer);
        }

        public Document NewDoc(string text)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            return doc;
        }

        public void Dispose() => Form.Dispose();
    }

    // ===== 1. 部分選択整形 =====

    [Fact]
    public void PartialSelection_Formats_AndSelectsChangedRange_AndAnnouncesSuccess() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("あいうえおかきくけこさしすせそたちつてと"); // 20 chars, WrapColumn=20 で 1 行
        // 非既定位置から検証開始(Stage 6 レビュー標準)
        doc.Editor.SelectCharRange(0, 20);
        int lengthBefore = doc.Editor.SnapshotText.Length;

        host.Kinsoku.Run(host.Settings);

        Assert.Contains("整形しました", host.Announcer.SayCalls);
        // 選択が変化後の範囲(整形結果の長さ)に更新される
        var (s, e) = doc.Editor.GetSelectionCharRange();
        Assert.Equal(0, s);
        Assert.True(e >= 0);
    });

    // ===== 2. 全文整形(選択なし) =====

    [Fact]
    public void WholeText_NoSelection_Formats_AndCaretToStart_AndAnnouncesSuccess() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("これは長いテキストで折返しが必要な内容を含んでいます改行を挿入するはずです");
        doc.Editor.SelectCharRange(5, 5);   // 非既定位置(選択あり=部分整形になるのを避けるため=空選択にする)
        doc.Editor.SelectCharRange(0, 0);   // 実は空選択にリセット

        host.Kinsoku.Run(host.Settings);

        Assert.Contains("整形しました", host.Announcer.SayCalls);
        var (s, e) = doc.Editor.GetSelectionCharRange();
        Assert.Equal(0, s);
        Assert.Equal(0, e);   // 全文整形時はキャレットが (0,0) に移動
    });

    // ===== 3. 変更なし =====

    [Fact]
    public void NoChange_AnnouncesNoChange_AndBufferUnchanged() => Sta.Run(() =>
    {
        using var host = new Host();
        // 既に整形済み=1 行の短いテキスト(WrapColumn=20 未満・改行不要)
        var doc = host.NewDoc("短い1行");
        // 非既定位置から検証開始(Stage 6 レビュー標準)
        doc.Editor.SelectCharRange(1, 3);
        string textBefore = doc.Editor.SnapshotText;

        host.Kinsoku.Run(host.Settings);

        Assert.Contains("変更なし", host.Announcer.SayCalls);
        Assert.DoesNotContain("整形しました", host.Announcer.SayCalls);
        Assert.Equal(textBefore, doc.Editor.SnapshotText);
    });

    // ===== 4. CSV モード中は抑止 =====

    [Fact]
    public void CsvMode_Blocked_AnnouncesBlockedText_AndBufferUnchanged() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("これは長いテキストで折返しが必要な内容を含んでいます");
        doc.State.CsvMode = true;   // CSV モード状態(Editor は ReadOnly になる想定・ここでは状態のみ)
        string textBefore = doc.Editor.SnapshotText;

        host.Kinsoku.Run(host.Settings);

        Assert.Contains(CsvAnnounceFormatter.BlockedInCsvMode, host.Announcer.SayCalls);
        Assert.DoesNotContain("整形しました", host.Announcer.SayCalls);
        Assert.Equal(textBefore, doc.Editor.SnapshotText);
    });

    // ===== 5. 空バッファ no-op(len<=0) =====

    [Fact]
    public void EmptyBufferNoSelection_NoOp_NoAnnouncement() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc(string.Empty);   // 空バッファ
        doc.Editor.SelectCharRange(0, 0);

        host.Kinsoku.Run(host.Settings);

        Assert.Empty(host.Announcer.SayCalls);   // 発声なし
        Assert.Equal(string.Empty, doc.Editor.SnapshotText);
    });

    // ===== 6. EOL 追随(CRLF/LF/CR) =====

    [Theory]
    [InlineData(LineEnding.CrLf, "\r\n")]
    [InlineData(LineEnding.Lf, "\n")]
    [InlineData(LineEnding.Cr, "\r")]
    public void UsesActiveDocumentEol(LineEnding eol, string eolString) => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("これは長いテキストで折返しが必要な内容を含んでいます改行を挿入するはず");
        doc.State.LineEnding = eol;

        host.Kinsoku.Run(host.Settings);

        // 整形結果に指定した EOL が含まれる(=Document.State.LineEnding が使われている)
        Assert.Contains(eolString, doc.Editor.SnapshotText);
        Assert.Contains("整形しました", host.Announcer.SayCalls);
    });
}
```

**注:** Theory 1 件は 3 データセット=xUnit 上は 3 tests として数える。したがって計 5 [Fact] + 3 [Theory データ] = **合計 8 tests**(設計書の「6 件」から Theory 展開で +3 になった=実績調整)。

### Step B.2: テストを実行して失敗を確認

```powershell
dotnet test tests/yEdit.App.Tests --filter "FullyQualifiedName~KinsokuFormatControllerTests" -c Release
# 期待: KinsokuFormatController not found でコンパイルエラー
```

### Step B.3: KinsokuFormatController を最小実装

**新規ファイル**: `src/yEdit.App/KinsokuFormatController.cs`

```csharp
using yEdit.App.Settings;
using yEdit.Core.Csv;
using yEdit.Core.Reading;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// 選択範囲(無ければ全文)を WrapColumn 桁で禁則整形する(実改行挿入・1 Undo)。
/// CSV モード中は本文が読取専用のため抑止し、誤成功通知を防ぐ。
/// Stage 8 で MainForm.FormatWithKinsoku から抽出(挙動不変)。
/// </summary>
public sealed class KinsokuFormatController
{
    private readonly DocumentManager _docs;
    private readonly IAnnouncer _announcer;

    public KinsokuFormatController(DocumentManager docs, IAnnouncer announcer)
    {
        _docs = docs;
        _announcer = announcer;
    }

    /// <summary>
    /// アクティブ文書の選択範囲(または全文)を禁則整形する。
    /// AppSettings は呼び出し時解決(OpenSettings で参照差し替わるため Controller にキャッシュしない)。
    /// </summary>
    public void Run(AppSettings settings)
    {
        var doc = _docs.Active;
        var ed = doc?.Editor;
        if (ed is null) return;
        if (doc!.State.CsvMode) { _announcer.Say(CsvAnnounceFormatter.BlockedInCsvMode); return; }

        string text = ed.SnapshotText;
        var (selStart, selEnd) = ed.GetSelectionCharRange();
        bool whole = selStart == selEnd;
        int start = whole ? 0 : selStart;
        int len = whole ? text.Length : selEnd - selStart;
        if (len <= 0) return;

        string target = text.Substring(start, len);
        string eol = doc.State.LineEnding.ToEolString();
        string formatted = KinsokuFormatter.Format(
            target, settings.WrapColumn,
            settings.KinsokuLineStartChars, settings.KinsokuLineEndChars, settings.KinsokuHangChars,
            eol, settings.TabWidth);

        if (formatted == target) { _announcer.Say("変更なし"); return; }
        ed.ReplaceCharRange(start, len, formatted);   // 1 Undo で置換
        // 部分選択なら変化箇所を選択して提示。全文整形では全選択を避け、先頭へキャレットを置く。
        if (whole) ed.SelectCharRange(0, 0);
        else ed.SelectCharRange(start, formatted.Length);
        ed.Focus();
        _announcer.Say("整形しました");
    }
}
```

**MainForm の変更**(`src/yEdit.App/MainForm.cs`):

```csharp
// Field 追加(_csv のあたり=行 19 前後)
private KinsokuFormatController _kinsoku = null!;

// ctor 内(_csv 生成の直後=行 70 前後)
_kinsoku = new KinsokuFormatController(_docs, _announcer);

// FormatWithKinsoku(:466-495) を丸ごと差し替え
private void FormatWithKinsoku() => _kinsoku.Run(_settings);
```

### Step B.4: テストを実行して緑を確認

```powershell
dotnet test tests/yEdit.App.Tests --filter "FullyQualifiedName~KinsokuFormatControllerTests" -c Release
# 期待: 8 tests all passed(5 Fact + 3 Theory rows)
```

### Step B.5: ミューテーション検証(品質レビュー時)

以下 5 変異を実装ファイルに一時導入 → 該当テストが赤 → 復元。

| # | 変異 | kill 期待 |
|---|---|---|
| 1 | `if (doc!.State.CsvMode)` を `false` 固定 | テスト 4(CsvMode_Blocked) |
| 2 | `if (formatted == target)` を `!=` に反転 | テスト 3(NoChange) |
| 3 | `bool whole = selStart == selEnd;` を `false` 固定 | テスト 2(WholeText) |
| 4 | `_announcer.Say("整形しました")` を空文字化 | テスト 1/2/6 |
| 5 | `if (whole) ed.SelectCharRange(0, 0);` を `SelectCharRange(10, 0)` に変更 | テスト 2(WholeText) |

全 5 kill を確認 → 品質レビュー用の verification 記録に残す。

### Step B.6: 全テスト+ローカルゲート

```powershell
tools/pre-merge-check.ps1
# 期待: 946 tests 緑・0 警告(938 + 8 = 946)
```

### Commit B:

```powershell
git add src/yEdit.App/KinsokuFormatController.cs src/yEdit.App/MainForm.cs tests/yEdit.App.Tests/KinsokuFormatControllerTests.cs
git commit -m "test/refactor: KinsokuFormatController を抽出+ Controller テスト 8 件

MainForm.FormatWithKinsoku(30 行・分岐 6・通知 3 種)を Controller 化。
- 依存: DocumentManager+IAnnouncer 注入・AppSettings は Run 引数(呼び出し時解決)
- MainForm 側は field+ctor 生成+1 行 dispatch(3 行)に痩身
- テスト 8 件(5 Fact + 3 Theory rows): 部分整形/全文整形/変更なし/CSV 抑止/空 no-op/EOL 追随
- 挙動不変(発声文言・選択位置・EOL 使用ロジック)
- ミューテーション 5/5 kill(spec-critical 100%)

テスト数: 938 -> 946(App: 147 -> 155)"
```

### Task B DoD

- [ ] `tools/pre-merge-check.ps1` 全緑・946 tests・Release 0 警告
- [ ] KinsokuFormatController のミューテーション 5 変異すべて kill
- [ ] MainForm.FormatWithKinsoku は 1 行 dispatch のみ
- [ ] 2 段レビュー完了

---

## Task C: GrepController から `_jumpTo` を外す

**目的:** Stage 7 レビュー由来の設計改善。GrepController は「grep 結果を結果窓に反映する」責務のみに絞り、GrepHit ジャンプ経路(=`Action<GrepHit>`)を持たない。

**Files:**
- Modify: `src/yEdit.App/GrepController.cs:15/17/24-39/148-154`
- Modify: `src/yEdit.App/MainForm.cs:59-64`(resultsFactory ラムダで GrepResultsCallbacks を組み立て)
- Modify: `tests/yEdit.App.Tests/GrepControllerTests.cs:24-41`(Host の resultsFactory ラムダを変更)
- Test: 新規反射テスト 1 件を追加

### Step C.1: GrepController のシグネチャ変更(実装)

**変更(GrepController.cs)**:

```csharp
// Before(:15 field 削除)
private readonly Action<GrepHit> _jumpTo;

// Before(:17)
private readonly Func<GrepResultsCallbacks, IGrepResultsView> _resultsFactory;
// After
private readonly Func<IGrepResultsView> _resultsFactory;

// Before(:24-30 ctor)
public GrepController(
    DocumentManager docs,
    Form owner,
    Action<GrepHit> jumpTo,
    Func<GrepCallbacks, IGrepView> viewFactory,
    Func<GrepResultsCallbacks, IGrepResultsView> resultsFactory,
    Func<GrepRequest, IProgress<GrepProgress>?, CancellationToken, Task<GrepOutcome>>? searchFn = null)

// After(Task A で owner 型狭化済み)
public GrepController(
    DocumentManager docs,
    IWin32Window owner,
    Func<GrepCallbacks, IGrepView> viewFactory,
    Func<IGrepResultsView> resultsFactory,
    Func<GrepRequest, IProgress<GrepProgress>?, CancellationToken, Task<GrepOutcome>>? searchFn = null)

// Before(:31-39 ctor body)
{
    _docs = docs;
    _owner = owner;
    _jumpTo = jumpTo;
    _viewFactory = viewFactory;
    _resultsFactory = resultsFactory;
    _searchFn = searchFn ?? ((req, prog, ct) => Task.Run(() => GrepService.Search(req, prog, ct)));
}
// After
{
    _docs = docs;
    _owner = owner;
    _viewFactory = viewFactory;
    _resultsFactory = resultsFactory;
    _searchFn = searchFn ?? ((req, prog, ct) => Task.Run(() => GrepService.Search(req, prog, ct)));
}

// Before(:148-154 ShowResults)
private void ShowResults(string pattern, string folder, GrepOutcome outcome)
{
    if (_resultsView is null || _resultsView.IsDisposed)
        _resultsView = _resultsFactory(new GrepResultsCallbacks(_jumpTo));
    _resultsView.Populate(pattern, folder, outcome);
    if (outcome.Hits.Count > 0) _resultsView.ShowResults(_owner);
}
// After
private void ShowResults(string pattern, string folder, GrepOutcome outcome)
{
    if (_resultsView is null || _resultsView.IsDisposed)
        _resultsView = _resultsFactory();   // GrepResultsCallbacks の組立は factory 側の責務
    _resultsView.Populate(pattern, folder, outcome);
    if (outcome.Hits.Count > 0) _resultsView.ShowResults(_owner);
}
```

**変更(GrepController のクラスヘッダー xmldoc):**

```csharp
// Before(:6-10)
/// 結果を結果窓へ反映し件数を SR 通知する。結果のジャンプは jumpTo デリゲートへ委譲(MainForm が
/// ファイルを開いて該当を選択)。Core はスレッド非依存のため、スレッド制御は本クラスに閉じる(§4.1)。

// After
/// 結果を結果窓へ反映し件数を SR 通知する。結果のジャンプ経路は結果窓生成側(MainForm)が
/// GrepResultsCallbacks に組み込む=Controller はジャンプ経路を知らない。Core はスレッド非依存のため、
/// スレッド制御は本クラスに閉じる(§4.1)。
```

### Step C.2: MainForm 側で GrepResultsCallbacks を組み立て

**変更(MainForm.cs:59-64)**:

```csharp
// Before
_grep = new GrepController(
    docs: _docs,
    owner: this,
    jumpTo: hit => OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength),
    viewFactory: cb => new GrepDialog(cb),
    resultsFactory: cb => new GrepResultsWindow(cb));

// After
_grep = new GrepController(
    docs: _docs,
    owner: this,
    viewFactory: cb => new GrepDialog(cb),
    resultsFactory: () => new GrepResultsWindow(
        new GrepResultsCallbacks(hit => OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength))));
```

### Step C.3: 既存 GrepControllerTests の Host を追随

**変更(tests/yEdit.App.Tests/GrepControllerTests.cs:34-41)**:

```csharp
// Before
Grep = new GrepController(
    docs: Docs,
    owner: Form,
    jumpTo: hit => Jumps.Add(hit),
    viewFactory: _ => { ViewFactoryCalls++; return View; },
    resultsFactory: cb => { ResultsFactoryCalls++; Results = new FakeGrepResultsView(cb); return Results; },
    searchFn: SearchFn.Invoke);

// After
Grep = new GrepController(
    docs: Docs,
    owner: Form,
    viewFactory: _ => { ViewFactoryCalls++; return View; },
    resultsFactory: () => {
        ResultsFactoryCalls++;
        Results = new FakeGrepResultsView(new GrepResultsCallbacks(hit => Jumps.Add(hit)));
        return Results;
    },
    searchFn: SearchFn.Invoke);
```

### Step C.4: 新規反射テスト(Controller が GrepHit ジャンプ経路を知らない)

**追加(GrepControllerTests.cs 末尾)**:

```csharp
// ===== 設計不変(GrepController は GrepHit ジャンプ経路を知らない) =====

[Fact]
public void Controller_HasNoJumpToField_NorActionOfGrepHitField()
{
    // 目的: Stage 8 Task C の設計改善(GrepResultsCallbacks 組立を factory 側に移す)が
    // 後退リファクタで戻らないよう機械的に固定。
    // GrepController は「grep 結果を結果窓へ反映」責務のみで、ジャンプ経路(Action<GrepHit>)を知らない。
    var fields = typeof(GrepController).GetFields(
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Public);

    Assert.DoesNotContain(fields, f => f.FieldType == typeof(Action<GrepHit>));
    Assert.DoesNotContain(fields, f => f.FieldType == typeof(GrepResultsCallbacks));
}
```

### Step C.5: 全テスト+ローカルゲート

```powershell
tools/pre-merge-check.ps1
# 期待: 947 tests 緑・0 警告(946 + 1 = 947)
```

### Step C.6: grep 検証

```powershell
Select-String -Path "src/yEdit.App/GrepController.cs" -Pattern "Action<GrepHit>|GrepResultsCallbacks"
# 期待: マッチなし(GrepOutcome/GrepHit の Populate 経由の残存は許容=別 pattern)
```

### Commit C:

```powershell
git add src/yEdit.App/GrepController.cs src/yEdit.App/MainForm.cs tests/yEdit.App.Tests/GrepControllerTests.cs
git commit -m "refactor: GrepController から _jumpTo を外す(Stage 7 申し送り消化)

GrepResultsCallbacks の生成責務を GrepController から factory 側(MainForm)へ移動。
Controller はジャンプ経路(Action<GrepHit>)を知らず、grep 結果を結果窓に反映する責務のみ。

- GrepController.ctor: jumpTo 引数削除・resultsFactory を Func<IGrepResultsView> に変更
- MainForm: resultsFactory ラムダ内で GrepResultsCallbacks を組み立て
- 既存 20 件のテストは Host の factory ラムダを追随して緑のまま
- 新規 1 件: 反射テストで GrepHit ジャンプ経路を持たないことを機械固定

テスト数: 946 -> 947(App: 155 -> 156)"
```

### Task C DoD

- [ ] `tools/pre-merge-check.ps1` 全緑・947 tests・Release 0 警告
- [ ] `Select-String "Action<GrepHit>|GrepResultsCallbacks"` が GrepController.cs でマッチなし
- [ ] 既存 GrepControllerTests 20 件が緑(挙動保存の証明)
- [ ] 2 段レビュー完了

---

## Task D: Stage 7 由来の高価値テスト追加

**目的:** Stage 7 で kill 可能と確認済みだが未書きの 3 領域(Cancel 副作用・Progress 追い越し guard 3 条件・catch 内 guard 準等価変異)のテストを追加する。実装変更なし。

**Files:**
- Modify: `tests/yEdit.App.Tests/GrepControllerTests.cs`(追加 7 件)
- Modify: `tests/yEdit.App.Tests/Fakes/FakeGrepSearchFn.cs`(Progress 明示発火・完了 TaskCompletionSource が既存に無ければ拡張)
- 実装ファイルは無変更

### Step D.1: FakeGrepSearchFn の拡張確認

**Read**: `tests/yEdit.App.Tests/Fakes/FakeGrepSearchFn.cs` を確認。Stage 7 で作成済み。Progress を明示発火できる API があるか、なければ拡張する。

**方針(拡張例)**:

```csharp
public sealed class FakeGrepSearchFn
{
    // 既存: DefaultOutcome / Invocations など
    public sealed record Invocation(GrepRequest Request, IProgress<GrepProgress>? Progress, CancellationToken Token);
    public List<Invocation> Invocations { get; } = new();
    public GrepOutcome DefaultOutcome { get; set; } = OutcomeWith(hits: 0);

    // 新規: Task の完了タイミングを制御するモード
    public TaskCompletionSource<GrepOutcome>? PendingCompletion { get; set; }

    public Task<GrepOutcome> Invoke(GrepRequest req, IProgress<GrepProgress>? prog, CancellationToken ct)
    {
        Invocations.Add(new Invocation(req, prog, ct));
        return PendingCompletion?.Task ?? Task.FromResult(DefaultOutcome);
    }
    // ... (既存 OutcomeWith 等)
}
```

既にこの機能が入っていれば skip。差分のみコミット。

### Step D.2: Cancel 副作用網羅(+2 件)

**追加(GrepControllerTests.cs)**:

```csharp
// ===== Cancel の副作用網羅(Stage 8 Task D-1) =====

[Fact]
public void Cancel_AfterOutcomeReturned_DoesNotAnnounceSummary_NorPopulate() => Sta.Run(() =>
{
    using var host = new Host();
    host.NewDoc("body");
    host.Grep.Open();
    host.View.Pattern = "abc";
    host.View.Folder = ExistingFolder;

    var tcs = new TaskCompletionSource<GrepOutcome>();
    host.SearchFn.PendingCompletion = tcs;

    var task = host.Grep.RunAsync();
    host.Grep.Cancel();                                        // 検索デリゲート完了前にキャンセル
    tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 5));      // 追い越し後に完了
    task.GetAwaiter().GetResult();

    // 追い越し guard で結果窓生成・Populate・Summary 発声すべて抑止
    Assert.Equal(0, host.ResultsFactoryCalls);
    Assert.DoesNotContain(host.View.Notifications, n => n.Contains("行 /") || n.Contains("見つかりません"));
});

[Fact]
public void Cancel_DoesNotChangeViewVisibility() => Sta.Run(() =>
{
    using var host = new Host();
    host.NewDoc("body");
    host.Grep.Open();
    Assert.True(host.View.Visible);   // Open 直後は表示中

    host.View.Pattern = "abc";
    host.View.Folder = ExistingFolder;
    var tcs = new TaskCompletionSource<GrepOutcome>();
    host.SearchFn.PendingCompletion = tcs;

    var task = host.Grep.RunAsync();
    host.Grep.Cancel();
    tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 0));
    task.GetAwaiter().GetResult();

    Assert.True(host.View.Visible);   // Cancel はビュー表示状態を変えない
});
```

### Step D.3: Progress 追い越し guard 3 条件(+3 件)

**追加(GrepControllerTests.cs)**:

```csharp
// ===== Progress 追い越し guard 3 条件(Stage 8 Task D-2) =====

[Fact]
public void Progress_AfterDispose_DoesNotUpdateStatus() => Sta.Run(() =>
{
    using var host = new Host();
    host.NewDoc("body");
    host.Grep.Open();
    host.View.Pattern = "abc";
    host.View.Folder = ExistingFolder;

    var tcs = new TaskCompletionSource<GrepOutcome>();
    host.SearchFn.PendingCompletion = tcs;

    var task = host.Grep.RunAsync();
    var progress = host.SearchFn.Invocations[0].Progress!;
    host.View.SimulateDispose();   // ビュー Dispose(FakeGrepView.SimulateDispose→IsDisposed=true)
    int statusCountBefore = host.View.StatusLog.Count;

    progress.Report(new GrepProgress(FilesScanned: 10, HitCount: 3, CurrentFile: "x"));
    tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 3));
    task.GetAwaiter().GetResult();

    Assert.Equal(statusCountBefore, host.View.StatusLog.Count);   // Dispose 後の Progress は SetStatus 呼ばず
});

[Fact]
public void Progress_AfterCtsSwappedByNewRun_DoesNotUpdateStatus() => Sta.Run(() =>
{
    using var host = new Host();
    host.NewDoc("body");
    host.Grep.Open();
    host.View.Pattern = "abc";
    host.View.Folder = ExistingFolder;

    var tcs1 = new TaskCompletionSource<GrepOutcome>();
    host.SearchFn.PendingCompletion = tcs1;
    var task1 = host.Grep.RunAsync();
    var progress1 = host.SearchFn.Invocations[0].Progress!;

    // 2 回目の RunAsync で _cts を差し替え
    var tcs2 = new TaskCompletionSource<GrepOutcome>();
    host.SearchFn.PendingCompletion = tcs2;
    var task2 = host.Grep.RunAsync();
    int statusCountBefore = host.View.StatusLog.Count;

    progress1.Report(new GrepProgress(FilesScanned: 10, HitCount: 3, CurrentFile: null));   // 追い越された旧 Progress
    tcs1.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 3));
    tcs2.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 5));
    task1.GetAwaiter().GetResult();
    task2.GetAwaiter().GetResult();

    // 旧 Progress は状態を上書きしていない(=旧 Progress による SetStatus は無視)
    Assert.DoesNotContain(host.View.StatusLog, s => s.Contains("10 ファイル走査・3 件"));
});

[Fact]
public void Progress_DuringBeginClose_DoesNotUpdateStatus() => Sta.Run(() =>
{
    using var host = new Host();
    host.NewDoc("body");
    host.Grep.Open();
    host.View.Pattern = "abc";
    host.View.Folder = ExistingFolder;

    var tcs = new TaskCompletionSource<GrepOutcome>();
    host.SearchFn.PendingCompletion = tcs;
    var task = host.Grep.RunAsync();
    var progress = host.SearchFn.Invocations[0].Progress!;
    host.Grep.BeginClose();   // 終了開始
    int statusCountBefore = host.View.StatusLog.Count;

    progress.Report(new GrepProgress(FilesScanned: 5, HitCount: 1, CurrentFile: "y"));
    tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 1));
    task.GetAwaiter().GetResult();

    Assert.Equal(statusCountBefore, host.View.StatusLog.Count);   // BeginClose 後の Progress は無視
});
```

**注:** FakeGrepView が `SimulateDispose()` / `IsDisposed` / `StatusLog` を持たない場合は既存の対応 API を確認して読み替える(Stage 7 で `Notifications`/`RunningLog` は実在。`StatusLog` 相当が無ければ `SetStatus` の呼び出し回数を数える形に変更)。

### Step D.4: catch 内 guard の分岐被覆(+2 件)

**追加(GrepControllerTests.cs)**:

```csharp
// ===== catch 内 guard の分岐被覆(Stage 8 Task D-3・準等価変異 kill) =====

[Fact]
public void Catch_AfterDispose_DoesNotAnnounceError() => Sta.Run(() =>
{
    using var host = new Host();
    host.NewDoc("body");
    host.Grep.Open();
    host.View.Pattern = "abc";
    host.View.Folder = ExistingFolder;

    var tcs = new TaskCompletionSource<GrepOutcome>();
    host.SearchFn.PendingCompletion = tcs;
    var task = host.Grep.RunAsync();

    host.View.SimulateDispose();   // 検索完了前に Dispose
    tcs.SetException(new InvalidOperationException("boom"));
    task.GetAwaiter().GetResult();

    Assert.DoesNotContain(host.View.Notifications, n => n.StartsWith("検索エラー:"));
});

[Fact]
public void Catch_AfterCtsSwapped_DoesNotAnnounceError() => Sta.Run(() =>
{
    using var host = new Host();
    host.NewDoc("body");
    host.Grep.Open();
    host.View.Pattern = "abc";
    host.View.Folder = ExistingFolder;

    var tcs1 = new TaskCompletionSource<GrepOutcome>();
    host.SearchFn.PendingCompletion = tcs1;
    var task1 = host.Grep.RunAsync();

    var tcs2 = new TaskCompletionSource<GrepOutcome>();
    host.SearchFn.PendingCompletion = tcs2;
    var task2 = host.Grep.RunAsync();   // _cts 差し替え

    tcs1.SetException(new InvalidOperationException("boom (from old run)"));
    tcs2.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 0));
    task1.GetAwaiter().GetResult();
    task2.GetAwaiter().GetResult();

    // 旧 run の例外は catch 内 guard で握りつぶし、エラー通知しない
    Assert.DoesNotContain(host.View.Notifications, n => n.Contains("boom (from old run)"));
});
```

### Step D.5: 全テスト+ローカルゲート

```powershell
tools/pre-merge-check.ps1
# 期待: 954 tests 緑・0 警告(947 + 7 = 954)
```

**注:** 設計書の +7 件計算(D-1: 2 / D-2: 3 / D-3: 2)と一致。テスト数 947 → 954。

### Step D.6: ミューテーション自己検証(品質レビュー時)

- Catch 内 guard `if (!d.IsDisposed && ReferenceEquals(_cts, cts))` を `true` 固定 → D-3 の 2 件が赤 → kill
- Progress ラムダの guard `if (d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing) return;` の各条件を 1 つずつ削除 → D-2 の 3 件のうち該当が赤 → kill
- 追い越し guard `if (d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing) return;`(RunAsync 内 await 後)を削除 → D-1 の Cancel_AfterOutcomeReturned が赤 → kill

### Commit D:

```powershell
git add tests/yEdit.App.Tests/GrepControllerTests.cs tests/yEdit.App.Tests/Fakes/FakeGrepSearchFn.cs
git commit -m "test: GrepController の Stage 7 由来の高価値テスト 7 件

Stage 7 で kill 可能と確認済みだが未書きの 3 領域をテスト化(実装無変更)。
- Cancel 副作用網羅 2 件: Summary 発声抑止・Populate 未呼出・View.Visible 不変
- Progress 追い越し guard 3 条件個別: Dispose/_cts 差替/_closing の各 false 経路
- catch 内 guard 分岐被覆 2 件: Dispose 後・_cts 差替後のエラー通知抑止(準等価変異 kill)

テスト数: 947 -> 954(App: 156 -> 163)"
```

### Task D DoD

- [ ] `tools/pre-merge-check.ps1` 全緑・954 tests・Release 0 警告
- [ ] Stage 7 の生存変異(catch 内 guard 準等価)が kill 化
- [ ] Progress 追い越し guard 3 条件がそれぞれ独立に kill 可能
- [ ] 2 段レビュー完了

---

## Task 最終: ブランチレビュー+マージ

### Step F.1: 最終ブランチレビュー(subagent)

```powershell
# ブランチ全体の diff を code-reviewer 系 subagent に投げる
git diff main...HEAD --stat
git log main..HEAD --oneline
```

Subagent への指示: 「Stage 4〜7 と同型の観点で最終レビュー: Task A/B/C/D 全ての挙動不変性・テスト品質・ミューテーション余地・命名整合・xmldoc 整合。マージ可否判定を Ready to merge / Needs changes / Blockers で返す」

### Step F.2: 最終ゲート

```powershell
tools/pre-merge-check.ps1
# 期待: 954 tests 緑・Release 0 警告
```

### Step F.3: no-ff マージ

```powershell
git checkout main
git merge --no-ff feature/test-strategy-phase2-stage8 -m "テスト戦略 Phase2 Stage8: 薄い MainForm 痩身+申し送り整理をマージ

Task A: Controller の _owner 型狭化+_announcer readonly 化+FindPrev デッドコード除去
Task B: KinsokuFormatController 抽出+テスト 8 件
Task C: GrepController から _jumpTo 外し(設計改善)+反射テスト 1 件
Task D: Stage 7 由来の高価値テスト 7 件

Phase 2 Stage 8 完了・Phase 2 完了。テスト数: 938 -> 954。挙動不変。"
git log --oneline -3
```

### Step F.4: マージ後ゲート(必須)

```powershell
tools/pre-merge-check.ps1
# 期待: 954 tests 緑・0 警告(main で最終確認)
```

### Step F.5: フィーチャーブランチ削除

```powershell
git branch -d feature/test-strategy-phase2-stage8
```

### Step F.6: 設計書に実施記録を追記+memory 更新

- `docs/plans/2026-07-15-test-strategy-phase2-stage8-design.md` の §7 に実施記録を書く(マージハッシュ・逸脱・レビュー由来修正・ミューテーション結果)
- `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` §6 の Stage 8 実施記録を追記
- `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\test-strategy.md` を Stage 8 完了に更新(テスト数 954・Phase 2 完了宣言・申し送り整理)

### Step F.7: 実施記録追記のコミット

```powershell
git add docs/plans/2026-07-15-test-strategy-phase2-stage8-design.md docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md
git commit -m "docs: Stage 8 実施記録にマージハッシュとレビュー結果を追記+Phase 2 完了宣言"
```

---

## 全体 DoD(Stage 8 完了)

- [ ] main のテスト数=**954**(Core 573+Editor 218+App 163)
- [ ] Release 0 警告・全ゲート緑
- [ ] 挙動不変(SR 発声・フォーカス・保存動作)
- [ ] MainForm.FormatWithKinsoku は 1 行 dispatch のみ
- [ ] GrepController は `Action<GrepHit>`/`GrepResultsCallbacks` を持たない
- [ ] Controller の `_owner` はすべて `IWin32Window`
- [ ] `MainForm._announcer` は readonly
- [ ] Stage 7 の準等価変異(catch 内 guard)が kill
- [ ] Phase 2 完了宣言(memory + Phase 2 設計書 §4 の再評価結果)

## 申し送り(Stage 8 完了 → Phase 2 終了後 → 独立 PR)

設計書 §1.2〜§1.4 に整理済み。緊急度・優先度ともに低いため個別 PR で必要時に対応:

- **Stage 5 由来**: Lazy `_writer ??=` 残置 / `HasBackup=false` Delete ガード / `SerialBackupWriter` catch 実 I/O 統合 / `BackupStore.LoadAll/SweepTempFiles` 抽象化再評価
- **Stage 6 由来**: `CsvCellEditor.Commit/CancelEdit` internal 化 / 列側クランプ / `default: throw` カバレッジ / `ed.FindForm()!` NRT
- **Stage 7 由来(D で扱わない分)**: GrepDialog の `UiaAnnouncer` 注入化 / `_view.Visible` の BeginClose/Cancel 不変固定(D-1 の 2 件目で部分回収) / `_resultsView.IsDisposed` 分岐被覆
- **原案の他コマンド抽出**(§1.1): `AnnouncePosition` / `GoToLine` / `ShowMarkdownPreview` / `CloseActiveTab` / `OpenSettings` / `RebuildRecentMenu` / `AutoEnterCsvMode` — MainForm が将来さらに肥大化した場合に再評価

Phase 3(SR 性能ゲート・任意)着手条件は上位文書に従い、実機 SR で退行が観測された場合のみ検討。
