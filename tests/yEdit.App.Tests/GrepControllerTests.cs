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
