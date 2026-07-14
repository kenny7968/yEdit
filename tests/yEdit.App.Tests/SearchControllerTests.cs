using yEdit.App.Tests.Fakes;

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
        public FindReplaceCallbacks? Callbacks; // 直近のファクトリ呼び出しで渡されたコールバック束
        public int FactoryCalls;

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Search = new SearchController(docs, form, Announcer,
                cb => { FactoryCalls++; Callbacks = cb; return View; });
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

    // ===== コールバック束の対応固定(Task 3 品質レビュー Important 対応) =====

    [Fact]
    public void Callbacks_AreWiredToMatchingControllerMethods() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("abc abc");
        host.View.Pattern = "abc";
        host.View.Replacement = "X";
        host.Search.OpenReplace();
        var cb = host.Callbacks!;
        Assert.NotNull(cb);

        // Func<bool> 2 本の判別: FindNext は前進・FindPrev は後退(取り違えると選択位置で失敗)
        Assert.True(cb.FindNext());
        Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());
        Assert.True(cb.FindNext());
        Assert.Equal((4, 7), doc.Editor.GetSelectionCharRange());
        Assert.True(cb.FindPrev());
        Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());

        // Action 3 本の判別 1: UpdateCount は発声も本文変更もしない
        int saidBefore = host.Announcer.Said.Count;
        cb.UpdateCount();
        Assert.Equal(saidBefore, host.Announcer.Said.Count);
        Assert.Equal("abc abc", doc.Editor.Text);

        // Action<bool> は 1 本のみ=型で一意(OFF に戻して以降の置換へ影響させない)
        cb.InSelectionToggled(false);

        // Action 3 本の判別 2: ReplaceOne は選択中の 1 件のみ置換(ReplaceAll なら "X X" になる)
        cb.ReplaceOne();
        Assert.Equal("X abc", doc.Editor.Text);

        // Action 3 本の判別 3: ReplaceAll は全置換(ReplaceOne なら 1 件のみ)
        doc.Editor.Text = "abc abc";
        cb.ReplaceAll();
        Assert.Equal("X X", doc.Editor.Text);
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
