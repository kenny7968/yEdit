# テスト戦略 Phase 2 Stage 4: SearchController シーム導入+テスト 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** SearchController の Form 境界(FindReplaceDialog)を IFindReplaceView で注入化し、FindReplaceDialog↔SearchController の相互参照をコールバック化で断ったうえで、歩進状態・スコープ捕捉・通知文言を App.Tests で固定する。

**Architecture:** ストラングラー方式のシーム導入。ダイアログを「入力値と表示操作だけの薄い表面(IFindReplaceView)」に抽象化し、ビュー→Controller 方向は FindReplaceCallbacks(delegate 束)へ機械的に置換して型循環を断つ(条件分岐・文言・表示手順は一切変えない=挙動不変)。テストは実 DocumentManager+実 EditorControl に Fake 境界(FakeFindReplaceView/FakeAnnouncer)だけを差して特徴付けする(green から開始)。

**Tech Stack:** .NET 9 / WinForms / xUnit v2(STA ヘルパ=`Sta.Run`・可視 HostForm パターン=Stage 4 で共通化)

- 日付: 2026-07-14
- 上位文書: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` §2.2・§3 SearchController 行・§4 Stage 4・§5
- ベースライン: main `eaa520c`(Stage 3+復元 dirty 化バグ修正済み)・テスト数 832(Core 573+Editor 218+App 41)

---

## 0. 設計書からの逸脱(2 点・いずれも精密化)

| 設計書 §2.2 | 本計画 | 理由 |
|---|---|---|
| `SearchController はビューを Func<IFindReplaceView> ファクトリで受け` | `Func<FindReplaceCallbacks, IFindReplaceView>` で受ける | 循環切断のコールバック束はダイアログの ctor 引数になる。素の `Func<IFindReplaceView>` だと MainForm 側のラムダが未代入の `_search` フィールドを閉じ込む時間的結合(Stage 3 申し送りで Stage 8 送りにした同型のアンチパターン)が生まれる。Controller 自身が自メソッド群からコールバック束を組んでファクトリへ渡せば結合ゼロ。生成タイミング(初回 Open 時)は現状維持 |
| インターフェースに `IsDisposed` なし | `bool IsDisposed { get; }` を追加 | 現行 `Open` の再生成チェック `if (_dialog is null \|\| _dialog.IsDisposed)` を一字一句保存するため(挙動不変)。Form が既に持つプロパティなのでダイアログ側の実装は不要 |

そのほか設計書どおり: `ShowAndFocus(IWin32Window)` が従来の Show(非表示時のみ)→Activate→FocusPattern を 1 メソッドに集約(`FocusPattern` は public から private へ=呼び出し元は Open のみと grep 確認済み)。

## 1. スコープ

- **導入するシーム**: `IFindReplaceView` + `FindReplaceCallbacks`(`src/yEdit.App/Abstractions/IFindReplaceView.cs`)。FindReplaceDialog は IFindReplaceView を実装し、SearchController への型参照を除去
- **テスト**: `SearchControllerTests` 32 件+`AnnouncerTests` 1 件(Stage 2 申し送り「空白のみメッセージの特徴付け」の回収)。テスト数 832 → **865**(App 41→74・純増 +33)
- **テストユーティリティ共通化**: HostForm パターンが 3 クラス目(SearchControllerTests)で必要になるため、Stage 3 申し送りの「3 copy 目が現れたら」ルールを発動して `TestHost.cs` へ抽出(DocumentManagerTests/FileControllerTests も追随)
- **触らないもの**: FindReplaceDialog の UI 配線・レイアウト(L5 手動の領分。コールバック置換は機械的な参照差し替えのみ)・SnapshotSearcher(Core 検証済み)・EditorControl(モックしない)・他 Controller・MainForm の F3/メニュー配線(呼び出し先シグネチャ不変)

## 2. 現状の結合分析(2026-07-14 コード精読)

- SearchController→FindReplaceDialog(`SearchController.cs:45` で直 new): 使用メンバー= `Pattern/Replacement/MatchCase/WholeWord/UseRegex/InSelection/Visible/IsDisposed/SetMode/SetStatus/Show/Activate/FocusPattern` → すべて IFindReplaceView に載る
- FindReplaceDialog→SearchController(`FindReplaceDialog.cs:9` フィールド): 呼び出し= `FindNext`(Click/Enter/F3 の 3 経路・bool 戻りで Hide 判断=G-2)・`FindPrev`・`ReplaceOne`・`ReplaceAll`・`UpdateCount`(TextChanged+CheckBox×3)・`OnInSelectionToggled` → FindReplaceCallbacks の 6 delegate に 1:1 対応
- FindReplaceDialog の生成箇所は SearchController.Open の 1 箇所のみ・SearchController の生成箇所は MainForm.cs:57 の 1 箇所のみ(grep 確認済み)
- CSV 抑止の判定は `_docs.Active?.State.CsvMode`(`DocumentState.CsvMode` は素の set 可能プロパティ)=テストから CsvController なしで立てられる

## 規約(全 Task 共通)

- ブランチ: `feature/test-strategy-phase2-stage4`(同一ディレクトリのフィーチャーブランチ→main へ no-ff マージ=いつもの運用)
- コミットメッセージは日本語。末尾に `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` を付ける
- 各 Task 末尾で `dotnet build yEdit.sln -c Release -warnaserror` が 0 警告であること
- git status に見えている untracked の `installer/`・`publish/` はこの作業と無関係。**絶対にコミットに含めない**(`git add` はパス指定で行う)
- 特徴付けテストが赤になった場合: 原則テスト側の期待を現行挙動へ合わせる(ゼロ幅正規表現の件数/序数など Core 側の数え方は特徴付けで追随しコメントに記録)。**ただし置換系(ReplaceOne/ReplaceAll)の赤はデータ破損リスク=実装バグの可能性があるため、修正せずユーザーへ報告する**

---

### Task 1: ブランチ作成

**Step 1: main から作業ブランチを切る**

Run:
```powershell
git -C <repo> switch -c feature/test-strategy-phase2-stage4 main
```
Expected: `Switched to a new branch 'feature/test-strategy-phase2-stage4'`

---

### Task 2: シーム定義(未配線・コンパイルのみ)

**Files:**
- Create: `src/yEdit.App/Abstractions/IFindReplaceView.cs`

**Step 1: IFindReplaceView と FindReplaceCallbacks を定義**

Create `src/yEdit.App/Abstractions/IFindReplaceView.cs`:

```csharp
namespace yEdit.App;

/// <summary>
/// FindReplaceDialog 生成時に渡す Controller 側コールバック束。
/// ビュー→Controller 方向を delegate 化することで FindReplaceDialog から
/// SearchController への型参照(相互参照)を断つ(Phase 2 設計書 §5)。
/// FindNext/FindPrev の bool は「ヒットして選択を移動できた」— 検索モードの
/// ダイアログが自身を Hide するか(G-2)の判断に使う。
/// </summary>
public sealed record FindReplaceCallbacks(
    Func<bool> FindNext,
    Func<bool> FindPrev,
    Action ReplaceOne,
    Action ReplaceAll,
    Action UpdateCount,
    Action<bool> InSelectionToggled);

/// <summary>
/// FindReplaceDialog の Controller 向け表面(Phase 2 設計書 §2.2)。
/// SearchController は入力値の読み取りとこの表示操作だけでビューを扱う。
/// IsDisposed は Open の再生成チェック(owner クローズ等での破棄検出)を
/// 従来コードのまま保存するために載せる(Form が既に持つ)。
/// </summary>
public interface IFindReplaceView
{
    string Pattern { get; }
    string Replacement { get; }
    bool MatchCase { get; }
    bool WholeWord { get; }
    bool UseRegex { get; }
    bool InSelection { get; }
    bool Visible { get; }
    bool IsDisposed { get; }
    void SetMode(bool replaceMode);
    void SetStatus(string text);
    /// <summary>従来の Open 手順「非表示なら Show(owner)→Activate→検索語フォーカス」を 1 メソッドに集約。</summary>
    void ShowAndFocus(IWin32Window owner);
}
```

**Step 2: ビルド確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: 0 警告(新規ファイルは未参照でもコンパイルされる)

**Step 3: Commit**

```powershell
git -C <repo> add src/yEdit.App/Abstractions/IFindReplaceView.cs
git -C <repo> commit -m "feat: IFindReplaceView/FindReplaceCallbacks シームを追加(Stage 4・未配線)"
```

---

### Task 3: FindReplaceDialog コールバック化+SearchController ビュー注入化+MainForm 配線(挙動不変)

置換は**機械的**に行う。条件分岐・文言・表示手順・歩進条件を一切変えない(diff レビューで確認できる粒度)。

**Files:**
- Modify: `src/yEdit.App/FindReplaceDialog.cs`(ctor+イベント配線+ShowAndFocus 追加)
- Modify: `src/yEdit.App/SearchController.cs`(ctor+`_dialog`→`_view`+Open)
- Modify: `src/yEdit.App/MainForm.cs:57`(ファクトリを渡す)

**Step 1: FindReplaceDialog をコールバック受けにして IFindReplaceView を実装**

`FindReplaceDialog.cs` のクラス宣言・フィールド・ctor を変更(クラス頭書きの「SearchController 経由」もコールバックの記述へ追随):

```csharp
/// <summary>
/// モードレスの検索・置換ダイアログ。入力収集とステータス表示に徹し、操作は
/// 生成時に受け取るコールバック(FindReplaceCallbacks)経由。検索モード/置換モードで
/// フィールド表示を切替える。
/// </summary>
public sealed class FindReplaceDialog : Form, IFindReplaceView
{
    private readonly FindReplaceCallbacks _cb;
```

ctor(`FindReplaceDialog.cs:26-49`):

```csharp
    public FindReplaceDialog(FindReplaceCallbacks callbacks)
    {
        _cb = callbacks;
        Text = "検索";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildLayout();

        _next.Click += (_, _) => { if (_cb.FindNext() && !_isReplaceMode) Hide(); };
        _prev.Click += (_, _) => { if (_cb.FindPrev() && !_isReplaceMode) Hide(); };
        _replaceOne.Click += (_, _) => _cb.ReplaceOne();
        _replaceAll.Click += (_, _) => _cb.ReplaceAll();
        _close.Click += (_, _) => Hide();
        _pattern.TextChanged += (_, _) => _cb.UpdateCount();
        _matchCase.CheckedChanged += (_, _) => _cb.UpdateCount();
        _wholeWord.CheckedChanged += (_, _) => _cb.UpdateCount();
        _useRegex.CheckedChanged += (_, _) => _cb.UpdateCount();
        _inSelection.CheckedChanged += (_, _) => _cb.InSelectionToggled(_inSelection.Checked);
    }
```

**Step 2: FocusPattern を private 化し ShowAndFocus を追加**

`FindReplaceDialog.cs:58` を置換:

```csharp
    private void FocusPattern() { _pattern.Focus(); _pattern.SelectAll(); }

    /// <summary>従来 SearchController.Open が行っていた表示手順(非表示なら Show→Activate→検索語フォーカス)の集約。順序を変えない。</summary>
    public void ShowAndFocus(IWin32Window owner)
    {
        if (!Visible) Show(owner);
        Activate();
        FocusPattern();
    }
```

**Step 3: ProcessCmdKey のコールバック差し替え**

`FindReplaceDialog.cs:73-83`(結果を捨てる F3 系は従来どおり捨てる):

```csharp
            case Keys.Escape: Hide(); return true;
            case Keys.F3: _cb.FindNext(); return true;
            case Keys.Shift | Keys.F3: _cb.FindPrev(); return true;
            case Keys.Enter when _pattern.Focused: if (_cb.FindNext() && !_isReplaceMode) Hide(); return true;
```

**Step 4: SearchController のフィールド・ctor・Open を注入化**

`SearchController.cs:15-51` を置換(`_dialog`→`_view` はフィールド型変更に伴うリネーム。各メソッド内の `var d = _dialog;` も `var d = _view;` へ機械的に追随):

```csharp
    private readonly DocumentManager _docs;
    private readonly Form _owner;
    private readonly IAnnouncer _announcer;
    private readonly Func<FindReplaceCallbacks, IFindReplaceView> _viewFactory;
    private IFindReplaceView? _view;
    private MatchSpan? _lastHit; // 直前に選択したヒット（ゼロ幅でも前進できるよう歩進に使う）
    private (int Start, int End)? _selectionScope; // 「選択範囲のみ」ON 時に捕捉した置換対象範囲

    public SearchController(DocumentManager docs, Form owner, IAnnouncer announcer,
        Func<FindReplaceCallbacks, IFindReplaceView> viewFactory)
    {
        _docs = docs;
        _owner = owner;
        _announcer = announcer;
        _viewFactory = viewFactory;
        _docs.ActiveDocumentChanged += (_, _) =>
        {
            _lastHit = null;                              // 別文書の歩進状態を持ち越さない
            _selectionScope = null;                       // 別文書へ切替時は捕捉済みスコープも無効化
            if (_view?.Visible == true) UpdateCount();    // 表示中なら新アクティブで件数を更新
        };
    }
```

Open(`SearchController.cs:43-51`):

```csharp
    private void Open(bool replaceMode)
    {
        if (_view is null || _view.IsDisposed)
            _view = _viewFactory(new FindReplaceCallbacks(
                FindNext, FindPrev, ReplaceOne, ReplaceAll, UpdateCount, OnInSelectionToggled));
        _view.SetMode(replaceMode);
        _view.ShowAndFocus(_owner); // 従来の「!Visible なら Show→Activate→FocusPattern」と同順(ビュー側に集約)
        UpdateCount();
    }
```

残りの `_dialog` 参照 3 箇所を `_view` へ(ロジック不変):
- `CurrentOptions` の `var d = _dialog;` → `var d = _view;`
- `UpdateCount` の `var d = _dialog;` → `var d = _view;`
- `ReplaceOne`/`ReplaceAll` の `var d = _dialog;` → `var d = _view;`
- `Announce` の `if (_dialog?.Visible == true) _dialog.SetStatus(message);` → `if (_view?.Visible == true) _view.SetStatus(message);`

**Step 5: MainForm の生成箇所(`MainForm.cs:57`)にファクトリを渡す**

```csharp
// 変更前
        _search = new SearchController(_docs, this, _announcer);
// 変更後
        _search = new SearchController(_docs, this, _announcer, cb => new FindReplaceDialog(cb));
```

**Step 6: 循環切断の確認(消し漏れなし)**

Run:
```powershell
git -C <repo> grep -n "SearchController" -- "src/yEdit.App/FindReplaceDialog.cs"
```
Expected: ヒットなし(exit code 1)=型参照・コメントとも SearchController への言及ゼロ

**Step 7: ビルド+既存全テストで挙動不変を確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.Core.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.Editor.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Core 573+Editor 218+App 41 全緑

**Step 8: Commit**

```powershell
git -C <repo> add src/yEdit.App/FindReplaceDialog.cs src/yEdit.App/SearchController.cs src/yEdit.App/MainForm.cs
git -C <repo> commit -m "refactor: FindReplaceDialog↔SearchController の相互参照を IFindReplaceView+コールバックで切断(挙動不変)"
```

---

### Task 4: テストユーティリティ共通化(3 copy 目ルール発動)

Stage 3 申し送り「HostForm 抽出等は 3 copy 目が現れたら」を発動する。SearchControllerTests が 3 クラス目のため、可視 HostForm パターンを共有クラスへ抽出し、既存 2 クラスを機械的に追随させる。**Sta.cs の 3 プロジェクト目条件(Editor.Tests との共有)は未成立のため対象外**(App.Tests 内の重複解消のみ)。

**Files:**
- Create: `tests/yEdit.App.Tests/TestHost.cs`
- Modify: `tests/yEdit.App.Tests/DocumentManagerTests.cs:11-37`(private HostForm 削除+Make 委譲)
- Modify: `tests/yEdit.App.Tests/FileControllerTests.cs:20-56`(private HostForm 削除+Host ctor 委譲)

**Step 1: 共有 HostForm を作成**

Create `tests/yEdit.App.Tests/TestHost.cs`:

```csharp
namespace yEdit.App.Tests;

/// <summary>
/// テストホスト用フォーム(Stage 4 で 3 クラス目の複製が現れたため共通化)。
/// フォーカスを奪わないよう非アクティブ(ShowWithoutActivation)・画面外・タスクバー非表示で
/// 「可視状態」まで作る。TabControl の Selected/Deselecting/SelectedIndexChanged は
/// ハンドル生成だけではプログラム切替で発火せず、ウィンドウ可視のとき同期発火する
/// (Stage 1 プローブ実測)。実運用の MainForm は常に可視なので、可視で作るのが忠実な再現。
/// </summary>
internal sealed class HostForm : Form
{
    protected override bool ShowWithoutActivation => true;

    /// <summary>DocumentManager の TabHost を載せて可視状態まで作る(Controller テスト共通の土台)。</summary>
    public static (Form Form, DocumentManager Docs) CreateWithDocs()
    {
        var form = new HostForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000), // 画面外(テスト実行中のチラつき防止)
        };
        var docs = new DocumentManager(() => new EditorControl());
        form.Controls.Add(docs.TabHost);
        form.Show();
        return (form, docs);
    }
}
```

**Step 2: DocumentManagerTests を追随**

`DocumentManagerTests.cs:11-37` の private `HostForm` クラスと `Make()` 本体を削除し、以下へ置換(呼び出し側は無変更):

```csharp
    /// <summary>可視状態の HostForm+DocumentManager を作る(可視が必要な理由と共通化の経緯は TestHost.cs 参照)。</summary>
    private static (Form form, DocumentManager dm) Make() => HostForm.CreateWithDocs();
```

**Step 3: FileControllerTests を追随**

`FileControllerTests.cs:20-23` の private `HostForm` クラスを削除し、`Host` ctor(`FileControllerTests.cs:42-56`)の Form 組み立てを委譲へ置換(FileController 生成行は無変更):

```csharp
        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            File = new FileController(Docs, Form, () => Settings,
                () => SaveSettingsCount++, () => RecentChangedCount++, () => MetaChangedCount++,
                d => OpenedFresh.Add(d), Prompt, Dialogs);
        }
```

`Host` の頭書きコメント「DocumentManagerTests と同じ〜パターン」は「共通 HostForm.CreateWithDocs を使う」へ 1 行修正。

**Step 4: 既存テストが緑のままであることを確認(共通化はふるまいを変えない)**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Passed! 41 件

**Step 5: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/TestHost.cs tests/yEdit.App.Tests/DocumentManagerTests.cs tests/yEdit.App.Tests/FileControllerTests.cs
git -C <repo> commit -m "test: 可視 HostForm パターンを TestHost.cs へ共通化(3 copy 目ルール発動)"
```

---

### Task 5: FakeFindReplaceView+SearchControllerTests 第 1 弾(Open/UpdateCount/Announce 契約・8 件)+AnnouncerTests 追補 1 件

**Files:**
- Create: `tests/yEdit.App.Tests/Fakes/FakeFindReplaceView.cs`
- Create: `tests/yEdit.App.Tests/SearchControllerTests.cs`
- Modify: `tests/yEdit.App.Tests/AnnouncerTests.cs`(1 件追記)

**Step 1: FakeFindReplaceView を作成**

Create `tests/yEdit.App.Tests/Fakes/FakeFindReplaceView.cs`:

```csharp
namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IFindReplaceView"/> のテスト用フェイク。入力値(Pattern 等)は直接設定し、
/// SetMode/SetStatus/ShowAndFocus の呼び出しを順序どおり記録する。
/// ShowAndFocus は Visible=true にする(実ダイアログの Show 相当)。Hide 相当は
/// テストが Visible=false を直接設定する(G-2 の「次を検索」後 Hide の再現)。
/// </summary>
public sealed class FakeFindReplaceView : IFindReplaceView
{
    public string Pattern { get; set; } = "";
    public string Replacement { get; set; } = "";
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool UseRegex { get; set; }
    public bool InSelection { get; set; }
    public bool Visible { get; set; }
    public bool IsDisposed { get; set; }

    public List<bool> ModeLog { get; } = new();     // SetMode(replaceMode) の履歴
    public List<string> StatusLog { get; } = new(); // SetStatus の履歴
    public int ShowAndFocusCount;

    /// <summary>現在表示中のステータス(未設定なら null)。</summary>
    public string? Status => StatusLog.Count == 0 ? null : StatusLog[^1];

    public void SetMode(bool replaceMode) => ModeLog.Add(replaceMode);
    public void SetStatus(string text) => StatusLog.Add(text);
    public void ShowAndFocus(IWin32Window owner) { ShowAndFocusCount++; Visible = true; }
}
```

**Step 2: テストハーネス+第 1 弾テストを書く**

Create `tests/yEdit.App.Tests/SearchControllerTests.cs`:

```csharp
using yEdit.App.Tests.Fakes;
using yEdit.Core.Csv;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 4: SearchController の配線・歩進状態・通知文言のテスト(設計書 §3)。
/// 実 DocumentManager+実 EditorControl を STA 上で使い、Form 境界(FakeFindReplaceView)と
/// 通知(FakeAnnouncer)だけを偽物にする。照合・件数の正しさ(SnapshotSearcher)は
/// Core 検証済みのため再検証しない(責務=歩進・スコープ・状態リセット・文言の配線)。
/// </summary>
public class SearchControllerTests
{
    /// <summary>SearchController を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public SearchController Search { get; }
        public FakeAnnouncer Announcer { get; } = new();
        public FakeFindReplaceView View { get; } = new();
        public int FactoryCalls;

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Search = new SearchController(docs, form, Announcer, _ => { FactoryCalls++; return View; });
        }

        /// <summary>クリーンな本文を持つアクティブ文書を作る(Text セッター=新規バッファで Modified=false・キャレット 0)。</summary>
        public Document NewDoc(string text)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            return doc;
        }

        public void Dispose() => Form.Dispose();
    }

    // ===== Open(ビューのライフサイクルと表示配線) =====

    [Fact]
    public void OpenFind_ShowsViewInFindMode_AndClearsStatus() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");

        host.Search.OpenFind();

        Assert.Equal(1, host.FactoryCalls);
        Assert.Equal(new[] { false }, host.View.ModeLog);   // 検索モード
        Assert.Equal(1, host.View.ShowAndFocusCount);
        Assert.True(host.View.Visible);
        Assert.Equal("", host.View.Status);                 // 空パターン=ステータスはクリア
        Assert.Empty(host.Announcer.Said);                  // Open は発声しない
    });

    [Fact]
    public void OpenReplace_ShowsViewInReplaceMode() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");

        host.Search.OpenReplace();

        Assert.Equal(new[] { true }, host.View.ModeLog);
        Assert.Equal(1, host.View.ShowAndFocusCount);
    });

    [Fact]
    public void Open_ReusesView_WhileAlive() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");

        host.Search.OpenFind();
        host.Search.OpenReplace();  // 検索→置換の切替は同一ビューのモード変更

        Assert.Equal(1, host.FactoryCalls);
        Assert.Equal(new[] { false, true }, host.View.ModeLog);
        Assert.Equal(2, host.View.ShowAndFocusCount);       // 再表示のたびフォーカス手順
    });

    [Fact]
    public void Open_RecreatesView_AfterDispose() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");
        host.Search.OpenFind();

        host.View.IsDisposed = true;   // owner クローズ等でダイアログが破棄された状況
        host.Search.OpenFind();

        Assert.Equal(2, host.FactoryCalls);                 // 作り直す(Disposed ビューを使い回さない)
    });

    // ===== UpdateCount(ステータスのみ・発声しない) =====

    [Fact]
    public void UpdateCount_WithHits_ShowsCount_WithoutSpeech() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc abc abc");
        host.View.Pattern = "abc";

        host.Search.OpenFind();   // Open 経由で UpdateCount が走る

        Assert.Equal("3 件", host.View.Status);
        Assert.Empty(host.Announcer.Said);                  // 件数はステータスのみ(発声しない)
    });

    [Fact]
    public void UpdateCount_NoHits_ShowsNotFound() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");
        host.View.Pattern = "xyz";

        host.Search.OpenFind();

        Assert.Equal("見つかりません", host.View.Status);
    });

    [Fact]
    public void UpdateCount_InvalidRegex_ShowsErrorStatus() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");
        host.View.Pattern = "(";
        host.View.UseRegex = true;

        host.Search.OpenFind();

        Assert.Equal("正規表現が正しくありません", host.View.Status);
        Assert.Empty(host.Announcer.Said);                  // カウントのエラーは通知しない(ステータスのみ)
    });

    // ===== Announce 契約(非表示ビューを経由しない=G-2 の支え) =====

    [Fact]
    public void Announce_ViewHidden_SpeaksWithoutStatusUpdate() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");
        host.View.Pattern = "abc";
        host.Search.OpenFind();
        host.View.Visible = false;   // G-2: 検索モードは「次を検索」成功後にダイアログが Hide される
        int statusBefore = host.View.StatusLog.Count;

        Assert.True(host.Search.FindNext());   // F3/メニュー経路(ダイアログ非表示のまま)

        Assert.Equal("1 件中 1 件目", host.Announcer.Said[^1]);   // 発声は共有 Announcer 直結で成立
        Assert.Equal(statusBefore, host.View.StatusLog.Count);    // 非表示中は SetStatus しない
    });
}
```

**Step 3: AnnouncerTests に空白のみメッセージの特徴付けを追記**(Stage 2 申し送りの回収)

`tests/yEdit.App.Tests/AnnouncerTests.cs` の `Say_Null_ClearsLabel_WithoutSpeaking` の直後に追記:

```csharp
    [Fact]
    public void Say_WhitespaceOnly_UpdatesLabel_AndSpeaks() => Sta.Run(() =>
    {
        using var label = new Label { Text = "前回の通知" };
        var announcer = new RecordingAnnouncer(label);
        announcer.Say(" ");
        // ガードは IsNullOrEmpty であって IsNullOrWhiteSpace ではない: 空白のみは表示・発声される。
        // この区別の固定が Stage 2 レビュー申し送り(空白 1 文字を「クリア扱い」に変えると
        // SR の読み上げ挙動が変わるため、変えるなら意図的に)。
        Assert.Equal(" ", label.Text);
        Assert.Equal(new[] { " " }, announcer.Spoken);
    });
```

**Step 4: テスト実行(green を確認=特徴付けの成立)**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~SearchControllerTests|FullyQualifiedName~AnnouncerTests"
```
Expected: **Passed! 14 件**(SearchController 8+Announcer 既存 5+新規 1)

**Step 5: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/Fakes/FakeFindReplaceView.cs tests/yEdit.App.Tests/SearchControllerTests.cs tests/yEdit.App.Tests/AnnouncerTests.cs
git -C <repo> commit -m "test: SearchController の Open/UpdateCount/Announce 契約 8 件+空白のみメッセージ特徴付け 1 件"
```

---

### Task 6: SearchControllerTests 第 2 弾(Find 歩進 8 件+文書切替リセット 3 件)

**Files:**
- Modify: `tests/yEdit.App.Tests/SearchControllerTests.cs`(テスト追記)

**Step 1: Find 歩進のテストを追記**

```csharp
    // ===== FindNext/FindPrev(歩進=_lastHit と選択の一致判定) =====

    [Fact]
    public void FindNext_SelectsFirstHit_AndAnnouncesOrdinal() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc abc");
        host.View.Pattern = "abc";
        host.Search.OpenFind();

        Assert.True(host.Search.FindNext());

        Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());
        Assert.Equal("3 件中 1 件目", host.Announcer.Said[^1]);
        Assert.Equal("3 件中 1 件目", host.View.Status);   // 表示中はダイアログ内ステータスにも同文言
    });

    [Fact]
    public void FindNext_Repeated_AdvancesFromLastHit() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc abc");
        host.View.Pattern = "abc";
        host.Search.OpenFind();

        host.Search.FindNext();
        Assert.True(host.Search.FindNext());   // 選択が _lastHit と一致=その次から

        Assert.Equal((4, 7), doc.Editor.GetSelectionCharRange());
        Assert.Equal("3 件中 2 件目", host.Announcer.Said[^1]);
    });

    [Fact]
    public void FindNext_ZeroWidthHit_AdvancesByOne() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("aaa");
        host.View.Pattern = "(?=a)";   // ゼロ幅ヒット(長さ 0)
        host.View.UseRegex = true;
        host.Search.OpenFind();

        host.Search.FindNext();                 // (0,0)
        Assert.True(host.Search.FindNext());    // Max(1, h.Length)=1 で前進(同位置に張り付かない)

        Assert.Equal((1, 1), doc.Editor.GetSelectionCharRange());
        Assert.Equal("3 件中 2 件目", host.Announcer.Said[^1]);
    });

    [Fact]
    public void FindNext_SelectionMovedByUser_SearchesFromSelectionEnd() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc abc");
        host.View.Pattern = "abc";
        host.Search.OpenFind();
        host.Search.FindNext();                 // (0,3)
        doc.Editor.SelectCharRange(5, 0);       // ユーザーがキャレット移動(選択≠_lastHit)

        Assert.True(host.Search.FindNext());

        Assert.Equal((8, 11), doc.Editor.GetSelectionCharRange());   // 5 以降の次ヒット(4 始まりは跨ぎ済み)
        Assert.Equal("3 件中 3 件目", host.Announcer.Said[^1]);
    });

    [Fact]
    public void FindNext_NoMoreHits_AnnouncesWithoutMoving() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc");
        host.View.Pattern = "abc";
        host.Search.OpenFind();
        host.Search.FindNext();                 // (0,3)=最後のヒット

        Assert.False(host.Search.FindNext());   // 折り返さない

        Assert.Equal("これ以上見つかりません", host.Announcer.Said[^1]);
        Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());    // 選択は動かない
    });

    [Fact]
    public void FindPrev_FromLastHit_SelectsPreviousHit() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc abc");
        host.View.Pattern = "abc";
        host.Search.OpenFind();
        host.Search.FindNext();
        host.Search.FindNext();                 // (4,7)=2 件目

        Assert.True(host.Search.FindPrev());    // _lastHit の Start より前を探す

        Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());
        Assert.Equal("3 件中 1 件目", host.Announcer.Said[^1]);
    });

    [Fact]
    public void Find_InvalidRegex_AnnouncesError() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");
        host.View.Pattern = "(";
        host.View.UseRegex = true;
        host.Search.OpenFind();

        Assert.False(host.Search.FindNext());

        Assert.Equal("正規表現が正しくありません", host.Announcer.Said[^1]);
    });

    [Fact]
    public void FindNext_BeforeOpeningDialog_ReturnsFalse_Silently() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");

        Assert.False(host.Search.FindNext());   // Ctrl+F 前の F3/メニュー: ビュー未生成=条件不足で無反応

        Assert.Empty(host.Announcer.Said);
        Assert.Equal(0, host.FactoryCalls);     // 勝手にビューを作らない
    });
```

**Step 2: 文書切替リセットのテストを追記**

```csharp
    // ===== 文書切替(_lastHit/_selectionScope のリセット+件数の追随) =====

    [Fact]
    public void ActiveDocumentChanged_ResetsStepState() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc1 = host.NewDoc("aaa");
        host.View.Pattern = "(?=a)";   // ゼロ幅: リセット有無で歩進結果が分かれる(通常パターンでは区別不能)
        host.View.UseRegex = true;
        host.Search.OpenFind();
        host.Search.FindNext();
        host.Search.FindNext();        // (1,1)=2 件目・_lastHit=(1,0)

        _ = host.NewDoc("x");          // 文書切替(リセット発火)
        host.Docs.SelectAt(0);         // doc1 へ戻す(再度リセット・選択 (1,1) は保持されている)

        Assert.True(host.Search.FindNext());
        // リセット済みなら選択終端(1)から再探索=同じ 2 件目。_lastHit が残っていれば 1+Max(1,0)=2 から=3 件目になる
        Assert.Equal((1, 1), doc1.Editor.GetSelectionCharRange());
        Assert.Equal("3 件中 2 件目", host.Announcer.Said[^1]);
    });

    [Fact]
    public void ActiveDocumentChanged_WhileVisible_RefreshesCount() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("abc");
        host.View.Pattern = "abc";
        host.Search.OpenFind();
        Assert.Equal("1 件", host.View.Status);

        _ = host.Docs.CreateNew();     // 空の新文書がアクティブに

        Assert.Equal("見つかりません", host.View.Status);   // 新アクティブ文書で件数を更新
    });

    [Fact]
    public void ActiveDocumentChanged_ClearsSelectionScope() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc1 = host.NewDoc("abc abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.View.InSelection = true;
        host.Search.OpenReplace();
        doc1.Editor.SelectCharRange(0, 3);
        host.Search.OnInSelectionToggled(true);   // doc1 で [0,3) を捕捉

        var doc2 = host.NewDoc("abc");            // 文書切替=捕捉済みスコープ無効化
        host.Search.ReplaceAll();

        Assert.Equal("選択範囲がありません", host.Announcer.Said[^1]);
        Assert.Equal("abc", doc2.Editor.Text);    // 新文書は置換されない
        Assert.Equal("abc abc", doc1.Editor.Text);// 旧文書のスコープへも波及しない
    });
```

**Step 3: テスト実行**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~SearchControllerTests"
```
Expected: **Passed! 19 件**(8+11)

**Step 4: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/SearchControllerTests.cs
git -C <repo> commit -m "test: SearchController の歩進(ゼロ幅前進含む)+文書切替リセット 11 件"
```

---

### Task 7: SearchControllerTests 第 3 弾(ReplaceOne 6 件+ReplaceAll 6 件)

**Files:**
- Modify: `tests/yEdit.App.Tests/SearchControllerTests.cs`(テスト追記)

**Step 1: ReplaceOne のテストを追記**

```csharp
    // ===== ReplaceOne(VSCode 準拠 G-3: 未選択なら検索して即置換) =====

    [Fact]
    public void ReplaceOne_SelectedHit_ReplacesAndSelectsNext() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.Search.OpenReplace();
        host.Search.FindNext();   // (0,3) を選択

        host.Search.ReplaceOne();

        Assert.Equal("X abc abc", doc.Editor.Text);
        Assert.Equal((2, 5), doc.Editor.GetSelectionCharRange());   // 置換後テキスト上の次ヒットを選択
        Assert.Equal("置換しました。2 件中 1 件目", host.Announcer.Said[^1]);
    });

    [Fact]
    public void ReplaceOne_NoSelection_ReplacesNextHitImmediately() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.Search.OpenReplace();   // キャレット (0,0)・選択なしのまま

        host.Search.ReplaceOne();    // G-3: 検索して即置換(選択待ちの空振りにしない)

        Assert.Equal("X abc", doc.Editor.Text);
        Assert.Equal((2, 5), doc.Editor.GetSelectionCharRange());
        Assert.Equal("置換しました。1 件中 1 件目", host.Announcer.Said[^1]);
    });

    [Fact]
    public void ReplaceOne_LastHit_AnnouncesReplacedAndNoMore() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.Search.OpenReplace();
        host.Search.FindNext();

        host.Search.ReplaceOne();

        Assert.Equal("X", doc.Editor.Text);
        Assert.Equal("置換しました。これ以上見つかりません", host.Announcer.Said[^1]);
    });

    [Fact]
    public void ReplaceOne_EmptyReplacement_DoesNotSkipAdjacentHit() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("aa");
        host.View.Pattern = "a";
        host.View.Replacement = "";
        host.Search.OpenReplace();
        host.Search.FindNext();      // (0,1)

        host.Search.ReplaceOne();    // 空置換(削除)後の前進は repl.Length=0(+1 すると隣接ヒットを取りこぼす)

        Assert.Equal("a", doc.Editor.Text);
        Assert.Equal((0, 1), doc.Editor.GetSelectionCharRange());   // 詰めて隣接した次ヒットを選択
        Assert.Equal("置換しました。1 件中 1 件目", host.Announcer.Said[^1]);
    });

    [Fact]
    public void ReplaceOne_InCsvMode_IsBlocked() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.Search.OpenReplace();
        doc.State.CsvMode = true;    // CsvController を介さず状態だけ立てる(判定は State 経由)

        host.Search.ReplaceOne();

        Assert.Equal("abc", doc.Editor.Text);   // 読取専用本文への無反映置換=誤成功通知を出さない
        Assert.Equal(CsvAnnounceFormatter.BlockedInCsvMode, host.Announcer.Said[^1]);
    });

    [Fact]
    public void ReplaceOne_InvalidRegex_AnnouncesError() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc");
        host.View.Pattern = "(";
        host.View.UseRegex = true;
        host.View.Replacement = "X";
        host.Search.OpenReplace();

        host.Search.ReplaceOne();    // Find と別コードパスの同ガード(削除するとここで例外になる)

        Assert.Equal("正規表現が正しくありません", host.Announcer.Said[^1]);
        Assert.Equal("abc", doc.Editor.Text);
    });
```

**Step 2: ReplaceAll のテストを追記**

```csharp
    // ===== ReplaceAll(全文/捕捉済み選択スコープ) =====

    [Fact]
    public void ReplaceAll_ReplacesAllMatches_AndAnnouncesCount() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.Search.OpenReplace();

        host.Search.ReplaceAll();

        Assert.Equal("X X X", doc.Editor.Text);
        Assert.Equal("3 件置換しました", host.Announcer.Said[^1]);
    });

    [Fact]
    public void ReplaceAll_NoMatch_AnnouncesNotFound_AndKeepsText() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc");
        host.View.Pattern = "xyz";
        host.View.Replacement = "X";
        host.Search.OpenReplace();

        host.Search.ReplaceAll();

        Assert.Equal("abc", doc.Editor.Text);
        Assert.Equal("見つかりません", host.Announcer.Said[^1]);
    });

    [Fact]
    public void ReplaceAll_InSelection_ReplacesOnlyCapturedScope() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.View.InSelection = true;
        host.Search.OpenReplace();
        doc.Editor.SelectCharRange(0, 7);       // "abc abc" を選択
        host.Search.OnInSelectionToggled(true); // スコープ捕捉

        host.Search.ReplaceAll();

        Assert.Equal("X X abc", doc.Editor.Text);   // 範囲外の 3 件目は置換されない
        Assert.Equal("2 件置換しました", host.Announcer.Said[^1]);
    });

    [Fact]
    public void ReplaceAll_InSelection_WithoutCapturedScope_Announces() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.View.InSelection = true;
        host.Search.OpenReplace();
        host.Search.OnInSelectionToggled(true); // 選択なし(ゼロ幅)で ON=スコープは捕捉されない

        host.Search.ReplaceAll();

        Assert.Equal("選択範囲がありません", host.Announcer.Said[^1]);
        Assert.Equal("abc", doc.Editor.Text);
    });

    [Fact]
    public void ReplaceAll_CapturedScope_SurvivesFindMoves() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.View.InSelection = true;
        host.Search.OpenReplace();
        doc.Editor.SelectCharRange(0, 7);
        host.Search.OnInSelectionToggled(true);  // [0,7) を捕捉

        Assert.True(host.Search.FindNext());     // 検索移動で実選択は (8,11) へクロバーされる
        host.Search.ReplaceAll();

        Assert.Equal("X X abc", doc.Editor.Text);   // 捕捉時のスコープが生きている(実選択に追随しない)
        Assert.Equal("2 件置換しました", host.Announcer.Said[^1]);
    });

    [Fact]
    public void ReplaceAll_InCsvMode_IsBlocked() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.Search.OpenReplace();
        doc.State.CsvMode = true;

        host.Search.ReplaceAll();

        Assert.Equal("abc", doc.Editor.Text);
        Assert.Equal(CsvAnnounceFormatter.BlockedInCsvMode, host.Announcer.Said[^1]);
    });
```

**Step 3: テスト実行**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~SearchControllerTests"
```
Expected: **Passed! 31 件**(19+12)

**Step 4: App.Tests 全体+ビルドを確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Passed! **73 件**(既存 41+新規 32)

**Step 5: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/SearchControllerTests.cs
git -C <repo> commit -m "test: SearchController の置換系(VSCode 準拠・空置換前進・選択スコープ・CSV 抑止)12 件"
```

---

### Task 8: ローカルゲート+設計書へ実施記録

**Files:**
- Modify: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`(「Stage 3 実施記録」節の直後に追記)

**Step 1: ローカルゲートを全実行**

Run:
```powershell
powershell -File <repo>\tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`(Release 0 警告・Core 573+Editor 218+App 74=865 緑)

**Step 2: 設計書に実施記録を追記**

`docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` の「Stage 3 実施記録」節の直後に追記:

```markdown
### Stage 4 実施記録(2026-07-14)

- **完了**: 実装計画=`docs/plans/2026-07-14-test-strategy-phase2-stage4.md`。①IFindReplaceView+FindReplaceCallbacks シーム(§2.2 からの精密化 2 点=ファクトリを `Func<FindReplaceCallbacks, IFindReplaceView>` 化・IsDisposed 追加は実装計画 §0 参照) ②FindReplaceDialog のコールバック化で SearchController への型参照を除去(相互参照の切断=§5)+ShowAndFocus 集約 ③SearchControllerTests 31 件(歩進/スコープ/文言/CSV 抑止)+AnnouncerTests 1 件(空白のみメッセージ=Stage 2 申し送り回収)。
- **テストユーティリティ共通化**: 可視 HostForm パターンを `TestHost.cs` へ抽出(3 copy 目ルール発動=Stage 3 申し送りの判断)。DocumentManagerTests/FileControllerTests も追随。
- **テスト数**: 832 → 864(App 41→73)。ゲート全通過(Release 0 警告)。
- **L5 スポット確認**: 不要(§5 のとおりダイアログ抽象化のみで SR 経路不変。Announce は同一 UiaAnnouncer・ダイアログ表示手順同順)。
```

(マージコミットのハッシュはマージ後にユーザー確認のうえ追記)

**Step 3: Commit**

```powershell
git -C <repo> add docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md docs/plans/2026-07-14-test-strategy-phase2-stage4.md
git -C <repo> commit -m "docs: Phase2 設計書に Stage 4 実施記録を追記+実装計画を追加"
```

---

### Task 9: レビュー→手動スモーク(任意)→マージ

**Step 1: 別エージェントによるコードレビュー**(いつもの運用)

ブランチ全 diff(`git diff main...feature/test-strategy-phase2-stage4`)を対象に依頼。観点:
- **挙動不変**: Open の表示手順(SetMode→[!Visible なら Show]→Activate→FocusPattern→UpdateCount の順)・通知文言・歩進条件(`h.Start + Math.Max(1, h.Length)` / ReplaceOne の `repl.Length` 前進)・G-2 の Hide 判定(bool 戻り値の意味)を変えていないか
- **循環切断の完全性**: FindReplaceDialog に SearchController への参照が残っていないか・コールバック 6 本の対応が 1:1 か
- **ShowAndFocus にロジック混入がないか**(3 行の移設に徹しているか)
- **テストの実効性=ミューテーション検証(Stage 3 で常用化した標準)**。最低限の変異例:
  - `SearchController.Find` の `Math.Max(1, h.Length)` → `h.Length` で `FindNext_ZeroWidthHit_AdvancesByOne` が赤になること
  - `ReplaceOne` の `span.Start + repl.Length` → `span.Start + Math.Max(1, repl.Length)` で `ReplaceOne_EmptyReplacement_DoesNotSkipAdjacentHit` が赤になること
  - `ActiveDocumentChanged` ハンドラの `_selectionScope = null;` 削除で `ActiveDocumentChanged_ClearsSelectionScope` が赤になること
  - `Announce` の `if (_view?.Visible == true)` ガード除去で `Announce_ViewHidden_SpeaksWithoutStatusUpdate` が赤になること
- Core 検証済み事項(SnapshotSearcher の照合・件数)の再検証をしていないか

**Step 2: 手動スモーク(ユーザー任意・L5 実機 SR は不要)**

SR 経路不変(ダイアログ抽象化のみ・Announce は同一 UiaAnnouncer)のため L5 は実施しない(設計書 §5)。配線の実感確認として 1 分のスモークを任意で:
- 起動→Ctrl+F で検索(件数表示・次を検索で Hide)→F3 で続行(通知が出る)→Ctrl+H で置換(すべて置換・選択範囲のみ)→Esc で閉じる

**Step 3: main へ no-ff マージ**

```powershell
git -C <repo> switch main
git -C <repo> merge --no-ff feature/test-strategy-phase2-stage4 -m "テスト戦略 Phase2 Stage4: SearchController シーム導入+テスト 32 件をマージ"
powershell -File <repo>\tools\pre-merge-check.ps1
git -C <repo> branch -d feature/test-strategy-phase2-stage4
```
Expected: マージ後ゲート全緑(865)

**Step 4: 実施記録へマージコミットのハッシュを追記**(小コミット)

---

## DoD(Stage 4)

1. `tools/pre-merge-check.ps1` 全緑(Release ビルド 0 警告)
2. テスト数 832 → **865**(App 41→74・純増 +33)
3. **挙動不変**: 通知文言/表示手順の順序/歩進条件/G-2 の Hide 判定/コールバック 6 本の 1:1 対応(diff レビューで機械的確認)
4. **相互参照の切断**: FindReplaceDialog から SearchController への参照ゼロ(grep で確認)
5. 別エージェントによるコードレビュー(マージ前・ミューテーション検証を標準適用)
6. L5 実機 SR スポット確認は**不要**(根拠: 設計書 §5「他 Stage はダイアログ抽象化のみで SR 経路不変」。Announce 経路=UiaAnnouncer は無変更)。手動スモーク 1 分は任意
7. main へ no-ff マージ+設計書へ実施記録・マージハッシュ追記

## リスクと対策

- **FindReplaceDialog ctor 変更の影響範囲**: 生成箇所は SearchController.Open の 1 箇所のみ(grep 確認済み)=Task 3 でファクトリ化と同時に変わるため中間状態でもビルドが割れない。
- **表示手順の順序退行**: ShowAndFocus は「!Visible なら Show→Activate→FocusPattern」の 3 行移設に限定(順序を変えると SR のフォーカス読み上げが変わり得る)。レビュー観点に明記。
- **ゼロ幅正規表現の Core 挙動**: `(?=a)` の Count/Locate の数え方(想定: "aaa" で 3 件)が想定と違う場合は特徴付けの原則どおりテスト期待を現行挙動へ合わせ、コメントに実測を記録する(歩進「同位置に張り付かない」ことの検証が本質で、件数文言は従属)。
- **Fake の Visible 手動管理**: ShowAndFocus で true・Hide 相当はテストが明示的に false を設定(実ダイアログの Hide タイミング=UI 配線は本 Phase 対象外・L5 手動の領分)。
- **特徴付けの赤**: 原則テスト側を現行挙動へ合わせるが、**置換系(ReplaceOne/ReplaceAll)の赤はデータ破損リスク=修正せずユーザーへ報告**(規約参照)。

## 申し送り(Stage 5 へ)

- 次 Stage: BackupCoordinator(TimeProvider/IBackupWriter/IRestorePrompt 導入・Reconcile internal 化)= Phase 2 設計書 §4 Stage 5。復元 dirty 化バグ修正(`59ad8b5`)後の挙動が前提(OfferRestoreOnStartup 系テストの期待は「復元タブ=Modified=true」)。
- Stage 2 レビュー由来の申し送りの残り: `MainForm._announcer` readonly 化=Stage 8/GrepDialog の IAnnouncer 注入化=Stage 7 設計時判断(本 Stage で回収したのは「空白のみメッセージの特徴付け」のみ)。
- SearchController の `_owner`(Form)は IFindReplaceView.ShowAndFocus の引数型(IWin32Window)まで狭められる=FileController の同件と合わせて Stage 8 で判断(Stage 3 申し送りと同型)。
- Sta.cs の共有抽出は「3 プロジェクト目が現れたら」を継続(今回共通化したのは App.Tests 内の HostForm のみ)。
- **実績が計画から +1 件増**: Task 3 品質レビュー指摘(FindReplaceCallbacks の同型 delegate の位置取り違えがコンパイル・テストとも検出不能)への対応として、①Open の構築を名前付き引数化(`0ba0ad1`)②Host で callbacks を捕捉する対応固定テスト `Callbacks_AreWiredToMatchingControllerMethods` を Task 5 へ追加。本文中の 31/73/864 系の数値は 32/74/865 に読み替え済み(スコープ・DoD は更新済み)。
- **ミューテーション生存 1 件(許容・記録のみ)**: `SearchController.cs` の Find 未ヒット分岐の `_lastHit = null;` は削除しても全テスト緑(準等価変異=観測差が出るのはゼロ幅ヒットが末尾で尽きた後の再検索という極端系のみ)。
- **FindPrev の `_lastHit` 三項は実質デッドコード**(Task 6 品質レビュー所見): 条件成立時は必ず `h.Start == selStart` のため全経路で `before = selStart` と等価。挙動不変の簡約(`before = selStart`)を Stage 8(MainForm 痩身)などのリファクタ機会に検討。
- **TestHost.cs のファイル名とクラス名(HostForm)の不一致**(Task 4 品質レビュー Minor): 計画指定の名前を採用(テストホスト系ユーティリティの集積地として TestHost.cs を名乗る意図)。リネームする場合は DocumentManagerTests のコメント(「TestHost.cs 参照」)の追随が必要。
