# テスト戦略 Phase 2 Stage 7: GrepController シーム導入+テスト 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** GrepController の 2 つの Form 境界(入力ダイアログ・結果窓)と 1 つの OS 境界(バックグラウンド検索)を `IGrepView` + `IGrepResultsView` + 注入可能な検索デリゲートで注入化し、`async void Run()` を `internal Task RunAsync()` に改めて、App.Tests で Grep の状態機械(Open ライフサイクル・入力検証 3 分岐・成功系・追い越し guard・BeginClose 抑止・Cancel)を固定する。挙動不変。

**Architecture:** ストラングラー方式のシーム導入。`IGrepView`+`GrepCallbacks(Func<Task> RunAsync, Action Cancel)`(Stage 4 の FindReplaceCallbacks と同型)で GrepDialog↔GrepController の相互参照をコールバック化して切る。結果窓は `IGrepResultsView`+`GrepResultsCallbacks(Action<GrepHit> OnActivate)` で対称化(ジャンプは Controller ctor に残す既存契約と両立)。バックグラウンド検索は `Func<GrepRequest, IProgress<GrepProgress>, CancellationToken, Task<GrepOutcome>>` を ctor で注入(既定=`GrepService.Search` を `Task.Run` で包む・既存の `await Task.Run(...)` パターンを保存)。テストは実 DocumentManager+実 EditorControl+Fake 境界(FakeGrepView/FakeGrepResultsView/FakeGrepSearchFn)で駆動し、実 I/O ゼロ・決定的タイミング(TaskCompletionSource で追い越し/BeginClose を制御)。

**Tech Stack:** .NET 9 / WinForms / xUnit v2(STA ヘルパ=`Sta.Run`・可視 HostForm パターン=`TestHost.CreateWithDocs`)

- 日付: 2026-07-14
- 上位文書: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` §2.2・§3・§4 Stage 7・§5(SR 経路への影響なし=L5 不要)
- ベースライン: main `f633ab7`(Stage 6 マージ+ハッシュ追記済)・テスト数 918(Core 573+Editor 218+App 127)

---

## 0. 設計精密化(上位文書 §2.2 からの追記=4 点)

上位文書 §2.2 は「`IGrepView` / `IGrepResultsView` を切る・`async void Run()` は `internal Task RunAsync()` に改める(メニューは async void ラッパ)」のみを規定している。以下 4 点で精密化する(いずれも Stage 4/5/6 と同型のパターン適用)。

1. **`GrepCallbacks` / `GrepResultsCallbacks` の 2 束を導入**(Stage 4 の `FindReplaceCallbacks` と同型): 素の `Func<IGrepView>` ファクトリだと MainForm 側のラムダが未代入の `_grep` を閉じ込む時間的結合(Stage 4 §0 と同じアンチパターン)。Controller 自身が自メソッドからコールバック束を組んでファクトリへ渡せば結合ゼロ。生成タイミング(初回 Open・初回ヒット時)は現状維持。
    - `GrepCallbacks(Func<Task> RunAsync, Action Cancel)` — GrepDialog→Controller の 2 経路(現行 `_run.Click` / `_stop.Click`)。RunAsync は `Func<Task>` にして UI の `async (_, _) => await cb.RunAsync();` から fire-and-forget、テストは戻り値の Task を await できる。
    - `GrepResultsCallbacks(Action<GrepHit> OnActivate)` — GrepResultsWindow→Controller の 1 経路(現行 `HitActivated += hit => _jumpTo(hit);`)。Controller は既存 `_jumpTo` フィールドから毎回束ねる。

2. **`async void Run()` → `internal Task RunAsync()`**: 戻り値の Task をテストが await するため。`internal` は `InternalsVisibleTo yEdit.App.Tests`(Stage 2 で既に有効)経由で App.Tests から呼べる。例外の観測性向上の副次効果(async void は AppDomain へ抜ける)。

3. **バックグラウンド検索の注入化**(§2.2 に無い追加シーム): `Func<GrepRequest, IProgress<GrepProgress>?, CancellationToken, Task<GrepOutcome>>` を ctor で注入(既定=`(req, prog, ct) => Task.Run(() => GrepService.Search(req, prog, ct))`)。既定は現行の `await Task.Run(() => GrepService.Search(...))` と 1:1(Task.Run はデリゲート内に閉じ込む=呼び出し側の await 位置と例外セマンティクスが不変)。テストは `Task.FromResult(fakeOutcome)` で即完了・`TaskCompletionSource<GrepOutcome>` で保留=追い越し/BeginClose を決定的に制御(実 I/O ゼロ)。**理由**: 実 GrepService は再帰列挙+バイナリスニッフ+デコードを行うため、実 I/O テストは遅く不安定(SDD ループが痛む)。かつ本 Stage の責務は「Controller の状態機械」であって GrepService の再検証ではない(Core.Tests で被覆済み)。

4. **GrepDialog の `UiaAnnouncer` 直生成は温存**(Stage 2 由来の申し送りへの結論): Controller は `IGrepView.RaiseNotification` 経由でしか発声しない=テストは view の呼び出しを直接観測できる。UiaAnnouncer の切り出しは Controller 単体テストの目的に貢献しないため見送る。GrepResultsWindow 側は元々 IAnnouncer 非依存(標準 ListBox に SR ネイティブ読みを任せる設計)。Stage 8 で MainForm 痩身時に判断可能。

そのほか設計書どおり: `IGrepView` は Stage 4 の `IFindReplaceView` と同じ形で「入力値の getter+`IsDisposed`+`Visible`+`SetRunning`+`SetStatus`+`RaiseNotification`+`ShowAndFocus`+`SetFolder`」に集約(`ShowAndFocus` は現行 Open の「!Visible なら Show(owner)→Activate→FocusPattern」の 3 行を集約=Stage 4 と同型)。`FocusPattern` は public から不要化(Open のみが呼ぶため view 側に閉じ込む)。

## 1. スコープ

- **導入するシーム**(4 ファイル・すべて新規): `src/yEdit.App/Abstractions/IGrepView.cs`(GrepCallbacks 含む)・`src/yEdit.App/Abstractions/IGrepResultsView.cs`(GrepResultsCallbacks 含む)。GrepDialog/GrepResultsWindow は各 view インターフェースを実装し、GrepController への型参照を除去。
- **テスト**: `GrepControllerTests` **20 件**。テスト数 918 → **938**(App 127→147・純増 +20)。
- **触らないもの**: GrepDialog/GrepResultsWindow の UI 配線・レイアウト(L5 手動の領分。コールバック置換は機械的な参照差し替えのみ)・`GrepService`(Core 検証済み)・EditorControl(モックしない)・他 Controller・MainForm の Ctrl+Shift+F/BeginClose/CancelClose 配線(呼び出し先シグネチャ不変=`_grep.Open()`/`_grep.BeginClose()`/`_grep.CancelClose()`)・`_jumpTo` の実体(`OpenAndSelect`・呼び出しシグネチャ不変)。

## 2. 現状の結合分析(2026-07-14 コード精読)

- GrepController→GrepDialog(`GrepController.cs:31,33,34,35` で直 new+呼び出し): 使用メンバー=`Pattern/Folder/Filter/Recursive/MatchCase/WholeWord/UseRegex/Visible/IsDisposed/SetFolder/FocusPattern/Show/Activate/SetRunning/SetStatus/RaiseNotification` → すべて IGrepView に載る。
- GrepDialog→GrepController(`GrepDialog.cs:12` フィールド): 呼び出し=`Run`(実行ボタン)・`Cancel`(中止ボタン・Esc・Close) → GrepCallbacks の 2 delegate に 1:1 対応。
- GrepController→GrepResultsWindow(`GrepController.cs:139-142`): 使用メンバー=`IsDisposed/Populate/ShowResults`+イベント `HitActivated` → interface に載る 3 メンバー+コールバック 1 本(OnActivate)。
- GrepDialog の生成箇所は GrepController.Open の 1 箇所のみ・GrepResultsWindow の生成箇所は GrepController.ShowResults の 1 箇所のみ・GrepController の生成箇所は `MainForm.cs:59-60` の 1 箇所のみ(grep 確認済み)。
- 状態遷移の外部観測点: `IGrepView`(Pattern/Folder/... の getter を Fake が返す・SetRunning/SetStatus/RaiseNotification/ShowAndFocus を Fake が記録)・`IGrepResultsView`(Populate/ShowResults/IsDisposed を Fake が記録)・`FakeGrepSearchFn`(Invocations/Pending キュー)・`Docs.Active`(DefaultFolder の入力=実 DocumentManager から観測可能)。

## 規約(全 Task 共通)

- ブランチ: `feature/test-strategy-phase2-stage7`(同一ディレクトリのフィーチャーブランチ→main へ no-ff マージ=いつもの運用)
- コミットメッセージは日本語。末尾に `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` を付ける
- 各 Task 末尾で `dotnet build yEdit.sln -c Release -warnaserror` が 0 警告であること
- git status に見えている untracked の `installer/`・`publish/` はこの作業と無関係。**絶対にコミットに含めない**(`git add` はパス指定で行う)
- 特徴付けテストが赤になった場合: 原則テスト側の期待を現行挙動へ合わせる。**ただし「追い越し guard(`ReferenceEquals(_cts, cts)`)」「BeginClose の `_closing` 抑止」「エラー件数ありの必須発声」「Progress の後発上書き抑止」の赤は UI ロジックの根幹=実装バグの可能性があるため、修正せずユーザーへ報告する**(Stage 4/5/6 の同型規約の Grep 版=Grep はデータ破損ではなく UI 状態の破損リスク)

---

### Task 1: ブランチ作成

**Step 1: main から作業ブランチを切る**

Run:
```powershell
git -C <repo> switch -c feature/test-strategy-phase2-stage7 main
```
Expected: `Switched to a new branch 'feature/test-strategy-phase2-stage7'`

---

### Task 2: シーム定義(未配線・コンパイルのみ)

**Files:**
- Create: `src/yEdit.App/Abstractions/IGrepView.cs`
- Create: `src/yEdit.App/Abstractions/IGrepResultsView.cs`

**Step 1: IGrepView と GrepCallbacks を定義**

Create `src/yEdit.App/Abstractions/IGrepView.cs`:

```csharp
namespace yEdit.App;

/// <summary>
/// GrepDialog 生成時に渡す Controller 側コールバック束(Phase 2 Stage 7・上位文書 §2.2)。
/// ビュー→Controller 方向を delegate 化することで GrepDialog から GrepController への型参照
/// (相互参照)を断つ(Stage 4 の <see cref="FindReplaceCallbacks"/> と同型)。
/// RunAsync は Func&lt;Task&gt;: UI 側は <c>async (_, _) =&gt; await cb.RunAsync();</c> で
/// fire-and-forget、テストは戻り値の Task を await できる。
/// </summary>
public sealed record GrepCallbacks(Func<Task> RunAsync, Action Cancel);

/// <summary>
/// GrepDialog の Controller 向け表面(Phase 2 設計書 §2.2)。
/// GrepController は入力値の読み取りとこの表示操作だけでビューを扱う。
/// IsDisposed は Progress コールバック/await 後の再入判定で従来コードを一字一句保存するために載せる
/// (Form が既に持つ)。Visible は Open の再表示判定で使う(現行 Open の `if (!_dialog.Visible)`)。
/// </summary>
public interface IGrepView
{
    string Pattern { get; }
    string Folder { get; }
    string Filter { get; }
    bool Recursive { get; }
    bool MatchCase { get; }
    bool WholeWord { get; }
    bool UseRegex { get; }
    bool Visible { get; }
    bool IsDisposed { get; }

    void SetFolder(string path);
    void SetRunning(bool running);
    void SetStatus(string text);
    void RaiseNotification(string message);
    /// <summary>従来の Open 手順「非表示なら Show(owner)→Activate→FocusPattern」を 1 メソッドに集約(Stage 4 と同型)。</summary>
    void ShowAndFocus(IWin32Window owner);
}
```

**Step 2: IGrepResultsView と GrepResultsCallbacks を定義**

Create `src/yEdit.App/Abstractions/IGrepResultsView.cs`:

```csharp
using yEdit.Core.Search;

namespace yEdit.App;

/// <summary>
/// GrepResultsWindow 生成時に渡す Controller 側コールバック束(Phase 2 Stage 7・上位文書 §2.2)。
/// 結果一覧のアクティベート(Enter/ダブルクリック)からジャンプ動作(<see cref="GrepController"/> ctor 引数の
/// jumpTo)への 1 経路を delegate 化する。GrepCallbacks と対称。
/// </summary>
public sealed record GrepResultsCallbacks(Action<GrepHit> OnActivate);

/// <summary>
/// GrepResultsWindow の Controller 向け表面(Phase 2 設計書 §2.2)。
/// Populate は結果流し込み・ShowResults はモードレス表示。IsDisposed は再生成判定
/// (owner クローズ等での破棄検出)を従来コードのまま保存するために載せる。
/// </summary>
public interface IGrepResultsView
{
    bool IsDisposed { get; }
    void Populate(string pattern, string folder, GrepOutcome outcome);
    void ShowResults(IWin32Window owner);
}
```

**Step 3: ビルド確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: 0 警告(新規ファイルは未参照でもコンパイルされる)

**Step 4: Commit**

```powershell
git -C <repo> add src/yEdit.App/Abstractions/IGrepView.cs src/yEdit.App/Abstractions/IGrepResultsView.cs
git -C <repo> commit -m "feat: IGrepView/IGrepResultsView シームを追加(Stage 7・未配線)"
```

---

### Task 3: GrepDialog+GrepResultsWindow をコールバック化+GrepController 注入化+MainForm 配線(1 コミットにまとめる)

置換は**機械的**に行う。条件分岐・文言・順序・(発声→視覚)の 2 行順・await 位置・追い越し guard 順序を一切変えない(diff レビューで確認できる粒度)。

**Files:**
- Modify: `src/yEdit.App/GrepDialog.cs`(ctor+イベント配線+ShowAndFocus 追加+`SearchController` ならぬ `GrepController` への型参照を除去)
- Modify: `src/yEdit.App/GrepResultsWindow.cs`(ctor をコールバック受けにする)
- Modify: `src/yEdit.App/GrepController.cs`(ctor+`_dialog`→`_view`/`_results`→`_resultsView`+`Run`→`RunAsync`+検索関数注入)
- Modify: `src/yEdit.App/MainForm.cs:59-60`(ファクトリ 2 本を渡す)

**Step 1: GrepDialog をコールバック受けにして IGrepView を実装**

`GrepDialog.cs` のクラス宣言・フィールド・ctor を変更:

```csharp
/// <summary>
/// grep の入力収集モードレスダイアログ。検索文字列・フォルダ・フィルタ・各オプションを集め、
/// 操作は生成時に受け取るコールバック(<see cref="GrepCallbacks"/>)経由。実行中は入力を無効化し
/// 中止のみ可能にする。
/// </summary>
public sealed class GrepDialog : Form, IGrepView
{
    private readonly GrepCallbacks _cb;
    // (既存の TextBox/Button/CheckBox/Label フィールドは無変更)
    private readonly IAnnouncer _announcer;  // 温存(§0 精密化 4)

    public GrepDialog(GrepCallbacks callbacks)
    {
        _cb = callbacks;
        Text = "フォルダ検索 (grep)";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildLayout();
        _announcer = new UiaAnnouncer(_status);

        _browse.Click += (_, _) => BrowseFolder();
        _run.Click += async (_, _) => await _cb.RunAsync();  // fire-and-forget=UI 都合(戻り値は捨てる・例外は Controller 内で処理済み)
        _stop.Click += (_, _) => _cb.Cancel();
        _close.Click += (_, _) => HideAndCancel();
        AcceptButton = _run;
    }
```

**Step 2: FocusPattern を private 化し ShowAndFocus を追加(Stage 4 と同型)**

`GrepDialog.cs:59` を置換:

```csharp
    private void FocusPattern() { _pattern.Focus(); _pattern.SelectAll(); }

    /// <summary>従来 GrepController.Open が行っていた表示手順(非表示なら Show→Activate→検索語フォーカス)の集約。順序を変えない。</summary>
    public void ShowAndFocus(IWin32Window owner)
    {
        if (!Visible) Show(owner);
        Activate();
        FocusPattern();
    }
```

**Step 3: HideAndCancel のコールバック差し替え+ProcessCmdKey**

`GrepDialog.cs:83-98` の `_controller.Cancel()` を `_cb.Cancel()` に機械的置換(Escape/OnFormClosing 経路の 2 呼び出し):

```csharp
    private void HideAndCancel()
    {
        _cb.Cancel();
        Hide();
    }
```

(ProcessCmdKey・OnFormClosing 本体は無変更=`HideAndCancel()` 呼び出しのみで内部は上記に差し替え済み)

**Step 4: GrepResultsWindow をコールバック受けにして IGrepResultsView を実装**

`GrepResultsWindow.cs` のクラス宣言・ctor を変更(`HitActivated` イベントを削除しコールバックへ):

```csharp
/// <summary>
/// grep 結果のモードレス一覧(ListBox・1 行 1 ヒット)。標準 Win32 ListBox なので
/// PC-Talker/NVDA が各項目をネイティブに読む(我々の UIA 層は不要)。Enter/ダブルクリックで
/// 選択ヒットを生成時に受け取ったコールバック(<see cref="GrepResultsCallbacks.OnActivate"/>)で
/// 通知し、上位(MainForm)がジャンプする。
/// </summary>
public sealed class GrepResultsWindow : Form, IGrepResultsView
{
    private readonly GrepResultsCallbacks _cb;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    private string _baseFolder = "";

    public GrepResultsWindow(GrepResultsCallbacks callbacks)
    {
        _cb = callbacks;
        Text = "検索結果";
        Width = 760;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        _list.AccessibleName = "検索結果";
        _list.DoubleClick += (_, _) => ActivateSelected();
        Controls.Add(_list);
    }
```

`ActivateSelected` の中身は `HitActivated?.Invoke(row.Hit)` → `_cb.OnActivate(row.Hit)` に差し替え(1 行):

```csharp
    private void ActivateSelected()
    {
        if (_list.SelectedItem is Row row) _cb.OnActivate(row.Hit);
    }
```

`public event Action<GrepHit>? HitActivated;` 行は削除。`Populate/ShowResults/IsDisposed` は既存のまま(interface が要求する形と 1:1)。

**Step 5: GrepController を注入化(ctor+`_dialog`→`_view`+`_results`→`_resultsView`+`Run`→`RunAsync`+検索関数)**

`GrepController.cs` を大幅置換(以下、全体構造)。**条件分岐・文言・(発声→視覚)の順・追い越し guard の 3 条件(IsDisposed / ReferenceEquals(_cts, cts) / _closing)・エラー件数ありの必須発声を一字一句保存する**。

```csharp
using System.IO;
using yEdit.Core.Search;

namespace yEdit.App;

/// <summary>
/// grep の統括。ダイアログ入力を検索デリゲート(既定=<see cref="GrepService"/>・別スレッド)へ渡し、
/// 結果を結果窓へ反映し件数を SR 通知する。結果のジャンプは jumpTo デリゲートへ委譲(MainForm が
/// ファイルを開いて該当を選択)。Core はスレッド非依存のため、スレッド制御は本クラスに閉じる(§4.1)。
/// </summary>
public sealed class GrepController
{
    private readonly DocumentManager _docs;
    private readonly Form _owner;
    private readonly Action<GrepHit> _jumpTo;
    private readonly Func<GrepCallbacks, IGrepView> _viewFactory;
    private readonly Func<GrepResultsCallbacks, IGrepResultsView> _resultsFactory;
    private readonly Func<GrepRequest, IProgress<GrepProgress>?, CancellationToken, Task<GrepOutcome>> _searchFn;
    private IGrepView? _view;
    private IGrepResultsView? _resultsView;
    private CancellationTokenSource? _cts;
    private bool _closing; // アプリ終了中は結果反映を抑止

    public GrepController(
        DocumentManager docs,
        Form owner,
        Action<GrepHit> jumpTo,
        Func<GrepCallbacks, IGrepView> viewFactory,
        Func<GrepResultsCallbacks, IGrepResultsView> resultsFactory,
        Func<GrepRequest, IProgress<GrepProgress>?, CancellationToken, Task<GrepOutcome>>? searchFn = null)
    {
        _docs = docs;
        _owner = owner;
        _jumpTo = jumpTo;
        _viewFactory = viewFactory;
        _resultsFactory = resultsFactory;
        // 既定=現行の `await Task.Run(() => GrepService.Search(...))` と 1:1(await 位置と例外セマンティクス不変)
        _searchFn = searchFn ?? ((req, prog, ct) => Task.Run(() => GrepService.Search(req, prog, ct)));
    }

    /// <summary>ダイアログを開く(既定フォルダ=アクティブ文書のフォルダ)。</summary>
    public void Open()
    {
        if (_view is null || _view.IsDisposed)
            _view = _viewFactory(new GrepCallbacks(RunAsync, Cancel));
        if (string.IsNullOrEmpty(_view.Folder)) _view.SetFolder(DefaultFolder());
        _view.ShowAndFocus(_owner);
    }

    private string DefaultFolder()
    {
        string? path = _docs.Active?.State.Path;
        if (path is not null)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir is not null && dir.Length > 0) return dir;
            }
            catch { /* 不正パスはマイドキュメントへフォールバック */ }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>入力を検証して別スレッドで grep を実行し、結果を反映する。</summary>
    internal async Task RunAsync()
    {
        var d = _view;
        if (d is null) return;
        if (string.IsNullOrEmpty(d.Pattern)) { d.RaiseNotification("検索文字列を入力してください"); return; }
        if (!Directory.Exists(d.Folder)) { d.RaiseNotification("フォルダが見つかりません"); return; }

        var opts = new SearchOptions(d.Pattern, d.MatchCase, d.WholeWord, d.UseRegex);
        if (!new TextSearcher(opts).IsValid) { d.RaiseNotification("正規表現が正しくありません"); return; }

        var req = new GrepRequest(d.Folder, d.Filter, d.Recursive, opts);
        string pattern = d.Pattern, folder = d.Folder;

        // 連打対策: 直前の実行を中止し、本実行専用の CTS を作る(破棄は本実行の finally で)。
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        var progress = new Progress<GrepProgress>(p =>
        {
            // 破棄済み・後発実行に追い越された・終了中なら、古い進捗で新しい状態を上書きしない。
            if (d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing) return;
            d.SetStatus(p.CurrentFile is null
                ? $"{p.FilesScanned} ファイル走査・{p.HitCount} 件"
                : $"{p.FilesScanned} ファイル走査中… {p.HitCount} 件");
        });

        d.SetRunning(true);
        // 発声→視覚の順(Say は Label も更新するため、後から視覚専用の実行中表示で上書きして保持する)。
        d.RaiseNotification("検索を開始しました");
        d.SetStatus("検索中…");
        try
        {
            // 検索デリゲート(既定=GrepService.Search を Task.Run で包む)は協調キャンセルで
            // 部分結果+Cancelled を返す(例外で打ち切らない)。
            var outcome = await _searchFn(req, progress, ct);

            // ビュー破棄済み・後発の実行に追い越された・終了中なら UI を触らない(結果窓も出さない)。
            if (d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing) return;

            ShowResults(pattern, folder, outcome);
            // ヒットがあれば結果窓のフォーカスが SR を駆動するので二重読みを避ける。ただし
            // 読み取りエラーがある時は走査が不完全な旨を必ず音声化する(誤った「見つかりません」防止)。
            if (outcome.Hits.Count == 0 || outcome.Errors.Count > 0)
                d.RaiseNotification(Summary(outcome));
            else
                d.SetStatus(Summary(outcome));
        }
        catch (Exception ex)
        {
            if (!d.IsDisposed && ReferenceEquals(_cts, cts))
                d.RaiseNotification("検索エラー: " + ex.Message);
        }
        finally
        {
            // 本実行が最新なら状態をリセット。後発実行に追い越されていればそちらに任せる。
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                if (!d.IsDisposed) d.SetRunning(false);
            }
            cts.Dispose();
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>アプリ終了開始: 実行中の grep を中止し、以後の結果反映を抑止する。</summary>
    public void BeginClose() { _closing = true; _cts?.Cancel(); }

    /// <summary>終了がキャンセルされた場合に通常運用へ戻す。</summary>
    public void CancelClose() => _closing = false;

    private static string Summary(GrepOutcome o)
    {
        string errs = o.Errors.Count > 0 ? $"・読み取り不可 {o.Errors.Count} 件" : "";
        if (o.Hits.Count == 0)
            return (o.Cancelled ? "中断しました(0 件)" : "見つかりません") + errs;
        string head = o.Cancelled ? "中断: " : "";
        return $"{head}{o.Hits.Count} 行 / {o.FilesMatched} ファイル{errs}";
    }

    private void ShowResults(string pattern, string folder, GrepOutcome outcome)
    {
        if (_resultsView is null || _resultsView.IsDisposed)
            _resultsView = _resultsFactory(new GrepResultsCallbacks(_jumpTo));
        _resultsView.Populate(pattern, folder, outcome);
        if (outcome.Hits.Count > 0) _resultsView.ShowResults(_owner);
    }
}
```

**注意点(diff レビュー観点)**:
- 「中断しました(0 件)」の全角括弧はコード原文どおり(現行 `GrepController.cs:130`)。テスト側もこの文言を使う。
- `Progress<GrepProgress>` の生成タイミング/コールバック本体は完全に不変。
- `_view?.Visible` 参照は無い(Open のみが Visible を使う)=Stage 4 の Announce ガードのような分岐は Grep に元々無い。
- `_dialog?.Visible` の参照は Open のみ(Show 判定)。旧コードの `if (!_dialog.Visible) _dialog.Show(_owner); _dialog.Activate(); _dialog.FocusPattern();` は `_view.ShowAndFocus(_owner)` に集約(3 行が 1 メソッドに移設・順序不変)。

**Step 6: MainForm の生成箇所(`MainForm.cs:59-60`)にファクトリ 2 本を渡す**

```csharp
// 変更前
        _grep = new GrepController(_docs, this,
            hit => OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength));
// 変更後(名前付き引数化=§0 精密化 1 の時間的結合回避の担保・Stage 4/6 と同型)
        _grep = new GrepController(
            docs: _docs,
            owner: this,
            jumpTo: hit => OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength),
            viewFactory: cb => new GrepDialog(cb),
            resultsFactory: cb => new GrepResultsWindow(cb));
```

`searchFn` は既定(既存の `Task.Run(() => GrepService.Search(...))`)を使うため省略。

**Step 7: 循環切断・シームの完全性を grep で確認**

Run:
```powershell
git -C <repo> grep -n "GrepController" -- "src/yEdit.App/GrepDialog.cs" "src/yEdit.App/GrepResultsWindow.cs"
```
Expected: ヒットなし(exit code 1)=型参照・コメントとも GrepController への言及ゼロ

Run:
```powershell
git -C <repo> grep -n "new GrepDialog\|new GrepResultsWindow" -- "src/yEdit.App"
```
Expected: `src/yEdit.App/MainForm.cs` の 2 行のみ(ファクトリラムダ内)

Run:
```powershell
git -C <repo> grep -n "async void Run\b\|public.*void Run\b" -- "src/yEdit.App/GrepController.cs"
```
Expected: ヒットなし(exit code 1)=旧 async void Run が消えている

**Step 8: ビルド+既存全テストで挙動不変を確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.Core.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.Editor.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Core 573+Editor 218+App 127=918 全緑

**Step 9: Commit**

```powershell
git -C <repo> add src/yEdit.App/GrepDialog.cs src/yEdit.App/GrepResultsWindow.cs src/yEdit.App/GrepController.cs src/yEdit.App/MainForm.cs
git -C <repo> commit -m "refactor: GrepController の view/results/検索関数を IGrepView+IGrepResultsView+デリゲートで注入化+RunAsync 化(挙動不変)"
```

---

### Task 4: Fake 3 種を追加

**Files:**
- Create: `tests/yEdit.App.Tests/Fakes/FakeGrepView.cs`
- Create: `tests/yEdit.App.Tests/Fakes/FakeGrepResultsView.cs`
- Create: `tests/yEdit.App.Tests/Fakes/FakeGrepSearchFn.cs`

**Step 1: FakeGrepView を作成**

Create `tests/yEdit.App.Tests/Fakes/FakeGrepView.cs`:

```csharp
namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IGrepView"/> のテスト用フェイク。入力値(Pattern/Folder/…)は直接設定し、
/// SetRunning/SetStatus/RaiseNotification/ShowAndFocus/SetFolder の呼び出しを順序どおり記録する。
/// ShowAndFocus は Visible=true にする(実ダイアログの Show 相当)。Hide 相当は
/// テストが Visible=false を直接設定する(現行 GrepDialog の HideAndCancel 経路の再現)。
/// </summary>
public sealed class FakeGrepView : IGrepView
{
    public string Pattern { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Filter { get; set; } = "*.*";
    public bool Recursive { get; set; } = true;
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool UseRegex { get; set; }
    public bool Visible { get; set; }
    public bool IsDisposed { get; set; }

    public List<string> FolderLog { get; } = new();
    public List<bool> RunningLog { get; } = new();
    public List<string> StatusLog { get; } = new();
    public List<string> Notifications { get; } = new();
    public int ShowAndFocusCount;

    public string? Status => StatusLog.Count == 0 ? null : StatusLog[^1];

    public void SetFolder(string path) { Folder = path; FolderLog.Add(path); }
    public void SetRunning(bool running) => RunningLog.Add(running);
    public void SetStatus(string text) => StatusLog.Add(text);
    public void RaiseNotification(string message) => Notifications.Add(message);
    public void ShowAndFocus(IWin32Window owner) { ShowAndFocusCount++; Visible = true; }
}
```

**Step 2: FakeGrepResultsView を作成**

Create `tests/yEdit.App.Tests/Fakes/FakeGrepResultsView.cs`:

```csharp
using yEdit.Core.Search;

namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IGrepResultsView"/> のテスト用フェイク。Populate/ShowResults の呼び出しを記録し、
/// コールバック(<see cref="GrepResultsCallbacks.OnActivate"/>)を <see cref="FireActivate"/> で発火できる
/// (ジャンプ配線の対応固定用)。IsDisposed はテストが直接設定して再生成分岐を再現。
/// </summary>
public sealed class FakeGrepResultsView : IGrepResultsView
{
    private readonly GrepResultsCallbacks _cb;
    public FakeGrepResultsView(GrepResultsCallbacks callbacks) { _cb = callbacks; }

    public bool IsDisposed { get; set; }

    public List<(string Pattern, string Folder, GrepOutcome Outcome)> PopulateLog { get; } = new();
    public int ShowResultsCount;

    public void Populate(string pattern, string folder, GrepOutcome outcome)
        => PopulateLog.Add((pattern, folder, outcome));
    public void ShowResults(IWin32Window owner) => ShowResultsCount++;

    /// <summary>結果一覧の「アクティベート」相当を発火(コールバックが Controller の jumpTo に届くかを検証)。</summary>
    public void FireActivate(GrepHit hit) => _cb.OnActivate(hit);
}
```

**Step 3: FakeGrepSearchFn を作成**

Create `tests/yEdit.App.Tests/Fakes/FakeGrepSearchFn.cs`:

```csharp
using yEdit.Core.Search;

namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="GrepController"/> の検索デリゲート(§0 精密化 3)のテスト用フェイク。
/// <see cref="Pending"/> に <see cref="TaskCompletionSource{TResult}"/> を積むと、呼び出し順に
/// dequeue して保留 Task を返す(追い越し/BeginClose の決定的タイミング再現に使う)。
/// Pending が空のときは <see cref="DefaultOutcome"/> を <see cref="Task.FromResult"/> で即時返す。
/// </summary>
public sealed class FakeGrepSearchFn
{
    public Queue<TaskCompletionSource<GrepOutcome>> Pending { get; } = new();
    public GrepOutcome DefaultOutcome { get; set; } = EmptyOutcome();
    public List<(GrepRequest Request, CancellationToken Token)> Invocations { get; } = new();

    public Task<GrepOutcome> Invoke(GrepRequest req, IProgress<GrepProgress>? prog, CancellationToken ct)
    {
        Invocations.Add((req, ct));
        return Pending.Count > 0 ? Pending.Dequeue().Task : Task.FromResult(DefaultOutcome);
    }

    public static GrepOutcome EmptyOutcome()
        => new(Array.Empty<GrepHit>(), 0, 0, Array.Empty<GrepError>(), false);
    public static GrepOutcome OutcomeWith(int hits, int errors = 0, bool cancelled = false)
    {
        var hs = new GrepHit[hits];
        for (int i = 0; i < hits; i++)
            hs[i] = new GrepHit("C:/fake/x.txt", i + 1, 1, "line", 0, 1, i);
        var es = new GrepError[errors];
        for (int i = 0; i < errors; i++) es[i] = new GrepError("C:/fake/y.txt", "err");
        return new GrepOutcome(hs, hits, hits > 0 ? 1 : 0, es, cancelled);
    }
}
```

**Step 4: ビルド確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: 0 警告

**Step 5: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/Fakes/FakeGrepView.cs tests/yEdit.App.Tests/Fakes/FakeGrepResultsView.cs tests/yEdit.App.Tests/Fakes/FakeGrepSearchFn.cs
git -C <repo> commit -m "test: FakeGrepView/FakeGrepResultsView/FakeGrepSearchFn を追加"
```

---

### Task 5: GrepControllerTests 第 1 弾(Open ライフサイクル 5 件+入力検証 4 件+ctor 対応固定 1 件=10 件)

**Files:**
- Create: `tests/yEdit.App.Tests/GrepControllerTests.cs`

**Step 1: テストハーネス+第 1 弾を書く**

Create `tests/yEdit.App.Tests/GrepControllerTests.cs`:

```csharp
using System.IO;
using yEdit.App.Tests.Fakes;
using yEdit.Core.Search;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 7: GrepController の配線・Open ライフサイクル・入力検証・成功系・
/// 追い越し guard・BeginClose 抑止・Cancel のテスト。
/// 実 DocumentManager+実 EditorControl を STA 上で使い、Form 境界(FakeGrepView/FakeGrepResultsView)と
/// 検索(FakeGrepSearchFn)だけを偽物にする。GrepService の照合正しさ(Core 検証済み)は
/// 再検証しない(責務=Controller の状態機械・SR 通知・エラー件数ありの必須発声・追い越し guard)。
/// </summary>
public class GrepControllerTests
{
    /// <summary>GrepController を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeGrepView View { get; } = new();
        public FakeGrepResultsView Results { get; private set; } = null!;
        public FakeGrepSearchFn SearchFn { get; } = new();
        public GrepController Grep { get; }
        public List<GrepHit> Jumps { get; } = new();
        public int ViewFactoryCalls;
        public int ResultsFactoryCalls;

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Grep = new GrepController(
                docs: Docs,
                owner: Form,
                jumpTo: hit => Jumps.Add(hit),
                viewFactory: _ => { ViewFactoryCalls++; return View; },
                resultsFactory: cb => { ResultsFactoryCalls++; Results = new FakeGrepResultsView(cb); return Results; },
                searchFn: SearchFn.Invoke);
        }

        public Document NewDoc(string text)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            return doc;
        }

        public void Dispose() => Form.Dispose();
    }

    /// <summary>テストで folder として渡す実在ディレクトリ(Directory.Exists ガードのみ突破すればよい)。</summary>
    private static readonly string ExistingFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // ===== ctor(対応固定=生成時点で view/results/searchFn 呼び出しなし) =====

    [Fact]
    public void Ctor_DoesNotInvokeViewOrResultsOrSearchFn() => Sta.Run(() =>
    {
        using var host = new Host();
        Assert.Equal(0, host.ViewFactoryCalls);
        Assert.Equal(0, host.ResultsFactoryCalls);
        Assert.Empty(host.SearchFn.Invocations);
        Assert.Empty(host.View.Notifications);
    });

    // ===== Open(ビューのライフサイクル・DefaultFolder 分岐) =====

    [Fact]
    public void Open_First_ShowsView_UsesDefaultFolderFromActiveDoc() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("body");
        doc.State.Path = Path.Combine(ExistingFolder, "sample.txt"); // ディレクトリ=ExistingFolder

        host.Grep.Open();

        Assert.Equal(1, host.ViewFactoryCalls);
        Assert.Equal(1, host.View.ShowAndFocusCount);
        Assert.True(host.View.Visible);
        Assert.Equal(new[] { ExistingFolder }, host.View.FolderLog); // 空だったので DefaultFolder が設定される
    });

    [Fact]
    public void Open_UsesFolderAsIs_IfAlreadySet() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.View.Folder = "C:/preset";   // 既にセット済み=上書きしない(現行 IsNullOrEmpty ガード)

        host.Grep.Open();

        Assert.Empty(host.View.FolderLog);  // SetFolder が呼ばれない
        Assert.Equal("C:/preset", host.View.Folder);
    });

    [Fact]
    public void Open_NoActivePath_FallsBackToMyDocuments() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");   // State.Path=null(無題)

        host.Grep.Open();

        Assert.Single(host.View.FolderLog);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), host.View.FolderLog[0]);
    });

    [Fact]
    public void Open_ReusesView_WhileAlive() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");

        host.Grep.Open();
        host.Grep.Open();   // 再表示は同一ビュー

        Assert.Equal(1, host.ViewFactoryCalls);
        Assert.Equal(2, host.View.ShowAndFocusCount);  // 再表示のたびフォーカス手順
    });

    [Fact]
    public void Open_RecreatesView_AfterDispose() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();

        host.View.IsDisposed = true;   // owner クローズ等でダイアログが破棄された状況
        host.Grep.Open();

        Assert.Equal(2, host.ViewFactoryCalls);   // 作り直す(Disposed ビューを使い回さない)
    });

    // ===== 入力検証(3 分岐+早期 return=検索デリゲートは呼ばれない) =====

    [Fact]
    public void RunAsync_EmptyPattern_Notifies_AndDoesNotInvokeSearchFn() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "";
        host.View.Folder = ExistingFolder;

        host.Grep.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(new[] { "検索文字列を入力してください" }, host.View.Notifications);
        Assert.Empty(host.SearchFn.Invocations);
        Assert.Empty(host.View.RunningLog);   // SetRunning にも到達しない
    });

    [Fact]
    public void RunAsync_MissingFolder_Notifies_AndDoesNotInvokeSearchFn() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = "Z:/no/such/folder/definitely/absent";

        host.Grep.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(new[] { "フォルダが見つかりません" }, host.View.Notifications);
        Assert.Empty(host.SearchFn.Invocations);
    });

    [Fact]
    public void RunAsync_InvalidRegex_Notifies_AndDoesNotInvokeSearchFn() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "(";
        host.View.Folder = ExistingFolder;
        host.View.UseRegex = true;

        host.Grep.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(new[] { "正規表現が正しくありません" }, host.View.Notifications);
        Assert.Empty(host.SearchFn.Invocations);
    });

    [Fact]
    public void RunAsync_NoView_IsNoOp() => Sta.Run(() =>
    {
        using var host = new Host();
        // Open を呼ばず _view=null のまま
        host.Grep.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(0, host.ViewFactoryCalls);
        Assert.Empty(host.SearchFn.Invocations);
        Assert.Empty(host.View.Notifications);   // view は生成されていないので Fake も呼ばれない
    });
}
```

**Step 2: テスト実行(green を確認=特徴付けの成立)**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~GrepControllerTests"
```
Expected: **Passed! 10 件**

**Step 3: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/GrepControllerTests.cs
git -C <repo> commit -m "test: GrepController の ctor/Open ライフサイクル/入力検証 10 件"
```

---

### Task 6: GrepControllerTests 第 2 弾(成功系 6 件+追い越し/BeginClose/Cancel 3 件+ジャンプ配線 1 件=10 件)

**Files:**
- Modify: `tests/yEdit.App.Tests/GrepControllerTests.cs`(テスト追記)

**Step 1: 成功系を追記**

```csharp
    // ===== RunAsync 成功系(検索デリゲート即完了=Task.FromResult パス) =====

    [Fact]
    public void RunAsync_ValidInputs_TogglesRunning_AndAnnouncesStart() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        host.SearchFn.DefaultOutcome = FakeGrepSearchFn.EmptyOutcome();

        host.Grep.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(new[] { true, false }, host.View.RunningLog);   // 開始で true・完了で false
        Assert.Contains("検索を開始しました", host.View.Notifications);
    });

    [Fact]
    public void RunAsync_WithHits_PopulatesResults_AndShowsResults_WithSilentSummary() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 3);

        host.Grep.RunAsync().GetAwaiter().GetResult();

        Assert.Single(host.Results.PopulateLog);
        Assert.Equal("abc", host.Results.PopulateLog[0].Pattern);
        Assert.Equal(ExistingFolder, host.Results.PopulateLog[0].Folder);
        Assert.Equal(1, host.Results.ShowResultsCount);
        // ヒットありエラー無しは Summary を発声せず視覚のみ(結果窓フォーカスの二重読み回避)
        Assert.DoesNotContain(host.View.Notifications, s => s.Contains("行 /"));
        Assert.Contains("3 行 / 1 ファイル", host.View.Status ?? "");
    });

    [Fact]
    public void RunAsync_WithHits_AndErrors_AnnouncesSummary_ForcedSpeech() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 3, errors: 2);

        host.Grep.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(1, host.Results.ShowResultsCount);   // ヒット>0 なので結果窓は開く
        // エラーがある時は summary を必ず発声(誤った「見つかりません」防止)
        Assert.Contains(host.View.Notifications, s => s.Contains("3 行 / 1 ファイル") && s.Contains("読み取り不可 2 件"));
    });

    [Fact]
    public void RunAsync_NoHits_AnnouncesNotFound_AndDoesNotShowResults() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        host.SearchFn.DefaultOutcome = FakeGrepSearchFn.EmptyOutcome();

        host.Grep.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(0, host.Results.ShowResultsCount);   // ヒット 0 なので窓は出さない
        Assert.Single(host.Results.PopulateLog);           // Populate は呼ぶ(見つかりません表示のため)
        Assert.Contains("見つかりません", host.View.Notifications);
    });

    [Fact]
    public void RunAsync_Cancelled_AnnouncesInterrupted() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 0, cancelled: true);

        host.Grep.RunAsync().GetAwaiter().GetResult();

        // Summary: Hits=0 かつ Cancelled → "中断しました(0 件)"(現行 GrepController.Summary の文言=全角括弧)
        Assert.Contains(host.View.Notifications, s => s.StartsWith("中断しました"));
    });

    [Fact]
    public void RunAsync_SearchFnThrows_AnnouncesError_AndResetsRunning() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        var tcs = new TaskCompletionSource<GrepOutcome>();
        host.SearchFn.Pending.Enqueue(tcs);

        var task = host.Grep.RunAsync();
        tcs.SetException(new InvalidOperationException("boom"));
        task.GetAwaiter().GetResult();

        Assert.Contains(host.View.Notifications, s => s.StartsWith("検索エラー: ") && s.Contains("boom"));
        Assert.Equal(new[] { true, false }, host.View.RunningLog);   // catch でも finally は必ず走る
    });

    // ===== 追い越し guard・BeginClose 抑止・Cancel =====

    [Fact]
    public void SecondRunAsync_OvertakesFirst_FirstResultsSkipped() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        var tcs1 = new TaskCompletionSource<GrepOutcome>();
        var tcs2 = new TaskCompletionSource<GrepOutcome>();
        host.SearchFn.Pending.Enqueue(tcs1);
        host.SearchFn.Pending.Enqueue(tcs2);

        var task1 = host.Grep.RunAsync();   // 保留中: _cts=cts1
        var task2 = host.Grep.RunAsync();   // 追い越し: _cts=cts2(cts1 は Cancel 済み)

        tcs1.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 9));   // 先行の結果が到着
        task1.GetAwaiter().GetResult();
        tcs2.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 3));   // 後発の結果
        task2.GetAwaiter().GetResult();

        // 先行の結果は UI に反映されない=Populate/ShowResults は 1 回(後発分だけ)
        Assert.Single(host.Results.PopulateLog);
        Assert.Equal(3, host.Results.PopulateLog[0].Outcome.Hits.Count);
        Assert.Equal(1, host.Results.ShowResultsCount);
        // 先行の cts はキャンセルされていること(協調キャンセルの入り口が生きていることの担保)
        Assert.True(host.SearchFn.Invocations[0].Token.IsCancellationRequested);
    });

    [Fact]
    public void BeginClose_DuringRun_SuppressesResults() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        var tcs = new TaskCompletionSource<GrepOutcome>();
        host.SearchFn.Pending.Enqueue(tcs);

        var task = host.Grep.RunAsync();
        host.Grep.BeginClose();                              // 終了開始
        tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 5));  // 遅れて結果到着
        task.GetAwaiter().GetResult();

        Assert.Empty(host.Results.PopulateLog);   // 結果反映は抑止
        Assert.Equal(0, host.Results.ShowResultsCount);
        // Notifications は "検索を開始しました" までは記録される(BeginClose 前に発声済み)
        Assert.Contains("検索を開始しました", host.View.Notifications);
    });

    [Fact]
    public void Cancel_CancelsCurrentRun_TokenObserved() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        var tcs = new TaskCompletionSource<GrepOutcome>();
        host.SearchFn.Pending.Enqueue(tcs);

        var task = host.Grep.RunAsync();
        host.Grep.Cancel();                                   // 実行中の中止(Stop ボタン相当)
        tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 0, cancelled: true));   // GrepService は cancelled=true で戻る
        task.GetAwaiter().GetResult();

        Assert.True(host.SearchFn.Invocations[0].Token.IsCancellationRequested);
        Assert.Equal(new[] { true, false }, host.View.RunningLog);   // 中止でも finally で SetRunning(false)
    });

    // ===== 結果窓のアクティベート→jumpTo 対応固定 =====

    [Fact]
    public void ResultsActivate_InvokesJumpTo_WithHit() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("body");
        host.Grep.Open();
        host.View.Pattern = "abc";
        host.View.Folder = ExistingFolder;
        host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 2);
        host.Grep.RunAsync().GetAwaiter().GetResult();

        var hit = host.Results.PopulateLog[0].Outcome.Hits[1];
        host.Results.FireActivate(hit);   // ダブルクリック/Enter 相当

        Assert.Single(host.Jumps);
        Assert.Same(hit, host.Jumps[0]);
    });
```

**Step 2: テスト実行(全 20 件 green を確認)**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~GrepControllerTests"
```
Expected: **Passed! 20 件**

**Step 3: App.Tests 全体+ビルドを確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Passed! **147 件**(既存 127+新規 20)

**Step 4: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/GrepControllerTests.cs
git -C <repo> commit -m "test: GrepController の成功系/追い越し guard/BeginClose 抑止/Cancel/ジャンプ配線 10 件"
```

---

### Task 7: ローカルゲート+設計書へ実施記録

**Files:**
- Modify: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`(「Stage 6 実施記録」節の直後に追記)

**Step 1: ローカルゲートを全実行**

Run:
```powershell
powershell -File <repo>\tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`(Release 0 警告・Core 573+Editor 218+App 147=938 緑)

**Step 2: 設計書に実施記録(暫定)を追記**

`docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` の「Stage 6 実施記録」節の直後に追記:

```markdown

### Stage 7 実施記録(2026-07-14)

- **完了**: 実装計画=`docs/plans/2026-07-14-test-strategy-phase2-stage7.md`(上位文書 §2.2 からの精密化 4 点=①GrepCallbacks/GrepResultsCallbacks の 2 束(Stage 4 と同型)で相互参照を切る ②`Run` → `internal Task RunAsync()`(戻り値の Task をテストが await) ③`Func<GrepRequest, IProgress<GrepProgress>, CancellationToken, Task<GrepOutcome>>` の注入化(実 I/O ゼロ・TaskCompletionSource で追い越し/BeginClose 決定的タイミング) ④GrepDialog の UiaAnnouncer 直生成温存=Stage 2 由来の申し送りへの結論。 ①IGrepView+IGrepResultsView シーム追加 ②GrepDialog/GrepResultsWindow のコールバック化で GrepController への型参照を除去+`Run`→`RunAsync`+検索関数注入+MainForm 配線(1 コミット・挙動不変) ③FakeGrepView/FakeGrepResultsView/FakeGrepSearchFn 追加 ④GrepControllerTests 20 件(ctor 1+Open ライフサイクル 5+入力検証 4+成功系 6+追い越し guard 1+BeginClose 抑止 1+Cancel 1+ジャンプ配線 1)。
- **テスト数**: 918 → **938**(App 127→147・純増 +20)。ゲート全通過(Release 0 警告)。
- **L5 スポット確認**: 不要(§5 のとおりダイアログ抽象化のみで SR 経路不変=`RaiseNotification` は同一 UiaAnnouncer・結果窓 SR ネイティブ読み不変)。手動スモーク 1 分は任意。
```

(マージコミットのハッシュはマージ後にユーザー確認のうえ追記)

**Step 3: Commit**

```powershell
git -C <repo> add docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md docs/plans/2026-07-14-test-strategy-phase2-stage7.md
git -C <repo> commit -m "docs: Phase2 設計書に Stage 7 実施記録を追記+実装計画を追加"
```

---

### Task 8: レビュー→手動スモーク(任意)→マージ

**Step 1: 別エージェントによるコードレビュー**(いつもの運用)

ブランチ全 diff(`git diff main...feature/test-strategy-phase2-stage7`)を対象に依頼。観点:

- **挙動不変**:
  - Open の表示手順(view null/Disposed なら生成→Folder 空なら SetFolder→ShowAndFocus)の順序と条件
  - RunAsync の 3 分岐(Empty pattern / Missing folder / Invalid regex)と早期 return(SetRunning にも進まない)
  - 「発声→視覚」の 2 行順(`d.RaiseNotification("検索を開始しました"); d.SetStatus("検索中…");`)
  - 追い越し guard の 3 条件(`d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing`)の順序・論理和・progress 内と await 後の両方に配置
  - エラー件数ありの必須発声(`outcome.Hits.Count == 0 || outcome.Errors.Count > 0` の or 分岐)
  - finally の `ReferenceEquals(_cts, cts)` 保護下でしか SetRunning(false) しない=追い越された run が UI を壊さない
  - `_cts?.Cancel()` の位置(2 箇所: RunAsync 冒頭・BeginClose)
- **シームの完全性**:
  - `GrepDialog.cs` / `GrepResultsWindow.cs` に `GrepController` への型参照ゼロ(grep 2 本)
  - `new GrepDialog` / `new GrepResultsWindow` の唯一の生成場所は `MainForm.cs`(ファクトリラムダ内・grep で 2 ヒット)
  - `async void Run\b` が `GrepController.cs` から消えている(grep で 0 ヒット)
- **RunAsync の Task セマンティクス**: `internal Task` に変更されている・戻り値 Task の完了と finally の関係(例外時も RunningLog が [true,false] になる)
- **テストの実効性=ミューテーション検証**(Stage 3/4/5/6 標準)。最低限の変異例:
  - `RunAsync` の `if (string.IsNullOrEmpty(d.Pattern)) { ... return; }` の `return` 削除 → `RunAsync_EmptyPattern_...` が赤
  - `if (!Directory.Exists(d.Folder))` の `!` 反転 → `RunAsync_MissingFolder_...` が赤
  - await 後の追い越し guard 3 条件のうち `!ReferenceEquals(_cts, cts)` を削除 → `SecondRunAsync_OvertakesFirst_...` が赤(Populate が 2 回になる)
  - await 後の追い越し guard の `_closing` を削除 → `BeginClose_DuringRun_...` が赤
  - Summary 分岐の `|| outcome.Errors.Count > 0` を削除(ヒット>0 ならエラーの通知が消える) → `RunAsync_WithHits_AndErrors_...` が赤
  - `if (outcome.Hits.Count > 0) _resultsView.ShowResults(_owner);` の条件を反転 → `RunAsync_NoHits_...` が赤
  - `Open` の `if (string.IsNullOrEmpty(_view.Folder)) _view.SetFolder(DefaultFolder());` の条件除去(常に SetFolder) → `Open_UsesFolderAsIs_IfAlreadySet` が赤
  - `Open` の `if (_view is null || _view.IsDisposed)` を `_view is null` のみに縮小 → `Open_RecreatesView_AfterDispose` が赤
  - `ShowResults` の `if (_resultsView is null || _resultsView.IsDisposed)` を条件から `IsDisposed` を落とす → (Stage 8 テスト候補=現行はカバー無し・記録のみ)
  - `finally` の `if (ReferenceEquals(_cts, cts))` を除去(常に SetRunning(false) にしてしまう) → 追い越しテストで観測できる差分は無い(先行は SetRunning しない前提の設計)=**準等価変異として記録**
  - `RunAsync` の `d.RaiseNotification("検索エラー: " + ex.Message)` を削除 → `RunAsync_SearchFnThrows_...` が赤
- **Core 検証済み事項の再検証をしていないか**(GrepService.Search・BuildFilterRegex・EnumerateFiles=Fake で全部バイパス)

**Step 2: 手動スモーク(ユーザー任意・L5 実機 SR は不要)**

SR 経路不変(ダイアログ抽象化のみ・RaiseNotification は同一 UiaAnnouncer・結果窓 SR ネイティブ読み不変)のため L5 は実施しない(設計書 §5)。配線の実感確認として 1〜2 分のスモークを任意で:

1. 起動→Ctrl+Shift+F で grep ダイアログ→検索文字列に "class"+フォルダにプロジェクトルート→検索→結果窓が出る→Enter でジャンプ
2. 空パターンで検索→"検索文字列を入力してください" が読まれる
3. 存在しないフォルダで検索→"フォルダが見つかりません" が読まれる
4. 検索中に中止ボタン→中断される
5. 検索実行中に別の検索を開始(連打)→結果は最後の 1 回分のみ
6. 検索中にアプリを閉じる→終了確認で No→grep が通常運用に戻る

**Step 3: main へ no-ff マージ**

```powershell
git -C <repo> switch main
git -C <repo> merge --no-ff feature/test-strategy-phase2-stage7 -m "テスト戦略 Phase2 Stage7: GrepController シーム導入+テスト 20 件をマージ"
powershell -File <repo>\tools\pre-merge-check.ps1
git -C <repo> branch -d feature/test-strategy-phase2-stage7
```
Expected: マージ後ゲート全緑(938)

**Step 4: 実施記録へマージコミットのハッシュを追記**(小コミット)

```powershell
git -C <repo> log --oneline -1 main    # マージコミットのハッシュを確認
# 上記ハッシュを Stage 7 実施記録に追記して commit
git -C <repo> add docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md
git -C <repo> commit -m "docs: Stage 7 実施記録にマージハッシュとマージ後ゲート結果を追記"
```

---

## DoD(Stage 7)

1. `tools/pre-merge-check.ps1` 全緑(Release ビルド 0 警告)
2. テスト数 918 → **938**(App 127→147・純増 +20)
3. **挙動不変**: Open の表示手順/RunAsync の 3 分岐早期 return/(発声→視覚)の 2 行順/追い越し guard の 3 条件/エラー件数ありの必須発声/finally の SetRunning ガード(diff レビューで機械的確認)
4. **相互参照の切断**: GrepDialog・GrepResultsWindow から GrepController への型参照ゼロ(grep 2 本で確認)
5. **RunAsync 化**: `async void Run\b` が GrepController.cs から消えている(grep で確認)
6. 別エージェントによるコードレビュー(マージ前・ミューテーション検証を標準適用)
7. L5 実機 SR スポット確認は**不要**(根拠: 設計書 §5「他 Stage はダイアログ抽象化のみで SR 経路不変」。RaiseNotification 経路=UiaAnnouncer は無変更・結果窓 SR ネイティブ読み不変)。手動スモーク 1〜2 分は任意
8. main へ no-ff マージ+設計書へ実施記録・マージハッシュ追記

## リスクと対策

- **`async void Run` → `internal Task RunAsync` 化のフリンジ**: GrepDialog の `_run.Click += async (_, _) => await _cb.RunAsync();` は fire-and-forget=UI 都合(戻り値は捨てる)。例外は Controller の try/catch で処理済みなので UI ハンドラに漏れない(既存 async void と同等)。テストは戻り値の Task を await できる。
- **追い越し/BeginClose の決定的タイミング**: 実 GrepService は再帰列挙+バイナリスニッフ+デコードで実 I/O 依存だが、`Func<...Task<GrepOutcome>>` の注入化で `TaskCompletionSource<GrepOutcome>` により先行実行を任意タイミングで完了できる(SetResult 前に 2nd RunAsync を呼ぶ・SetResult 前に BeginClose を呼ぶ=どちらも決定的)。実 I/O ゼロ・不安定要素なし。
- **`Progress<GrepProgress>` の SyncContext**: 本 Stage の Fake 経路では検索デリゲート内で progress が呼ばれない(FakeGrepSearchFn は Report しない)ため、Progress コールバックの内部ロジック(追い越し guard/BeginClose/IsDisposed)は Stage 7 のユニットで直接テストしない。await 後の guard(同一 3 条件)はテスト済み(overtake/BeginClose)なので、Progress 側の同型 guard は「同じ実装が並行 2 経路に置かれている」ことの diff レビューで担保する。Stage 8 の候補として「Progress を直接手動 Report して各 guard 分岐が働くか」の追加テストを申し送りに記録。
- **STA スレッドで SyncContext 未設定の await**: STA テストスレッドに WinFormsSyncContext を明示的に張らないため、`await _searchFn(...)` の継続は `TaskScheduler.Default`(ThreadPool)へ posted される可能性がある。Fake は POCO(Form 派生でない・スレッドアフィニティなし)なので継続が別スレッドで走っても差は無い。`task.GetAwaiter().GetResult()` で STA が Join 相当のブロックをかけて継続完了を待つ=アサーションのタイミング競合なし。プロダクションでは WinFormsSyncContext が張られており、view(=GrepDialog=Form)へは正しくマーシャルされる=テスト側の差はプロダクション挙動を隠さない。
- **DefaultFolder 分岐の Path.GetDirectoryName 例外**: `try/catch` はそのまま保存(現行の "不正パスはマイドキュメントへフォールバック" 挙動)。テストは正常な Path.Combine で担保(不正パスは L5 手動の領分)。
- **中断時 Summary の全角括弧**: 現行文言「中断しました(0 件)」(0 の前後が全角括弧)は原文どおり保存。テストは `StartsWith("中断しました")` で prefix 一致に緩めておく(表記揺れの一時的な影響を避ける)。
- **特徴付けの赤**: 原則テスト側を現行挙動へ合わせるが、**「追い越し guard の 3 条件」「BeginClose の抑止」「エラー件数ありの必須発声」「Progress の後発上書き抑止」の赤は UI 状態の破損リスク=修正せずユーザーへ報告**(規約参照)。

## 申し送り(Stage 8 以降へ)

- **次 Stage**: (任意)Stage 8 MainForm 痩身。上位文書 §4 のとおり費用対効果を再評価(Stage 7 で Controller 陣が全てシーム化済みのため、MainForm には ProcessCmdKey/配線/コマンドロジックが残っている)。
- **Stage 8 候補(本 Stage 由来)**:
  - `Progress<GrepProgress>` のコールバック内の追い越し guard 3 条件を直接テスト(Progress を手動 Report=SyncContext 経由の post を含めた挙動固定)
  - `ShowResults` の `_resultsView.IsDisposed` 分岐の被覆(現行はカバーなし=結果窓を 2 回開いて 1 回目を破棄した状態で 2 回目を開くケース)
  - `finally` の `ReferenceEquals(_cts, cts)` ガード除去の準等価変異(先行 run の SetRunning(false) は現在テストで検出できない=先行の run はそもそも SetRunning(true) を経ないまま追い越しで終わるケースを増やせないか検討)
  - GrepController の `_owner`(Form)は Show/Activate/ShowAndFocus の実引数として IWin32Window で足りる=Stage 4/6 の同件と合わせて狭める候補
  - `_jumpTo` を Controller ctor から外し、`GrepResultsCallbacks(Action<GrepHit> OnActivate)` の生成を MainForm 側で行う(Controller が GrepHit ジャンプの経路を知る必要が実は無い)=リファクタ候補
  - GrepDialog の `UiaAnnouncer` 直生成の外出し(Stage 2 由来・本 Stage 見送り)=Stage 8 で MainForm 痩身時に判断(status Label→announcer の生成順序に注意)
- **Sta.cs の共有抽出**: 3 プロジェクト目条件は継続監視(本 Stage では発動しない・App.Tests 内で完結)。
- **`GrepDialog`/`GrepResultsWindow` の UI**: 本 Phase の対象外(L5 手動)。プレゼンター抽出は必要が生じた Stage で個別判断。
- **CI 初回観察ポイント**: 可視 Form 方式は windows-latest 実機未検証の申し送りが続く。本 Stage は既存の HostForm.CreateWithDocs を流用するのみで新規リスクなし。
