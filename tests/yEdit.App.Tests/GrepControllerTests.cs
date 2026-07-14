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

    /// <summary>DefaultFolder の "activePath 由来" と "MyDocuments フォールバック" を区別するための実在ディレクトリ(必ず MyDocuments と異なる)。</summary>
    private static readonly string TempFolder =
        Path.TrimEndingDirectorySeparator(Path.GetTempPath());

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
        doc.State.Path = Path.Combine(TempFolder, "sample.txt"); // ディレクトリ=TempFolder(MyDocuments フォールバックと区別)

        host.Grep.Open();

        Assert.Equal(1, host.ViewFactoryCalls);
        Assert.Equal(1, host.View.ShowAndFocusCount);
        Assert.True(host.View.Visible);
        Assert.Equal(new[] { TempFolder }, host.View.FolderLog); // activePath から派生した DefaultFolder が設定される
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
        Assert.Empty(host.View.RunningLog);   // SetRunning にも到達しない
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
        Assert.Empty(host.View.RunningLog);   // SetRunning にも到達しない
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

        // 結果反映は抑止(ShowResults 早期 return により resultsFactory が呼ばれない=結果ビュー自体を生成しない)
        Assert.Equal(0, host.ResultsFactoryCalls);
        Assert.Null(host.Results);
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
}
