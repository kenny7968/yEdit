namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 1: DocumentManager の配線・状態遷移テスト(設計書 §3)。
/// リファクタ不要で実物 EditorControl+TabControl を STA 上で使い、
/// タブ生成/ラベル更新/イベント転送のアクティブ限定/巡回選択/KeyBasedSwitch を検証する。
/// Core が検証済みの照合・I/O 正しさは再検証しない(責務=App 層の配線)。
/// </summary>
public class DocumentManagerTests
{
    /// <summary>可視状態の HostForm+DocumentManager を作る(可視が必要な理由と共通化の経緯は TestHost.cs 参照)。</summary>
    private static (Form form, DocumentManager dm) Make() => HostForm.CreateWithDocs();

    /// <summary>クリーンな本文を持つ文書を作る(Text セッター=新規バッファで Modified=false)。</summary>
    private static Document NewDocWithText(DocumentManager dm, string text)
    {
        var doc = dm.CreateNew();
        doc.Editor.Text = text;
        return doc;
    }

    // ===== CreateNew の配線 =====

    [Fact]
    public void CreateNew_FirstDocument_BecomesActiveWithUntitledLabel() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var doc = dm.CreateNew();
            Assert.Same(doc, dm.Active);
            Assert.Equal(1, dm.Count);
            Assert.Equal("無題", doc.Page.Text); // 変更なし=「*」なし
        }
    });

    [Fact]
    public void CreateNew_SecondDocument_ActivatesIt_AndRaisesActiveDocumentChanged() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var doc1 = dm.CreateNew();
            int changed = 0;
            dm.ActiveDocumentChanged += (_, _) => changed++;
            var doc2 = dm.CreateNew();
            Assert.Same(doc2, dm.Active);
            Assert.Equal(1, changed); // タブ切替(doc1→doc2)が 1 回だけ転送される
            Assert.Equal(new[] { doc1, doc2 }, dm.Documents);
        }
    });

    [Fact]
    public void DirtyEdit_OnActiveDocument_MarksLabel_AndRaisesActiveDirtyChanged() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var doc = NewDocWithText(dm, "abc");
            int dirtyChanged = 0;
            dm.ActiveDirtyChanged += (_, _) => dirtyChanged++;

            doc.Editor.ReplaceCharRange(0, 0, "x"); // SavePointLeft
            Assert.Equal("* 無題", doc.Page.Text);
            Assert.Equal(1, dirtyChanged);

            doc.Editor.SetSavePoint();              // SavePointReached
            Assert.Equal("無題", doc.Page.Text);
            Assert.Equal(2, dirtyChanged);
        }
    });

    [Fact]
    public void DirtyEdit_OnInactiveDocument_MarksItsLabel_ButDoesNotRaiseActiveDirtyChanged() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var doc1 = NewDocWithText(dm, "abc");
            var doc2 = NewDocWithText(dm, "def"); // doc2 がアクティブ
            int dirtyChanged = 0;
            dm.ActiveDirtyChanged += (_, _) => dirtyChanged++;

            doc1.Editor.ReplaceCharRange(0, 0, "x"); // 非アクティブの編集
            Assert.Equal("* 無題", doc1.Page.Text);  // ラベルはどのタブでも更新される
            Assert.Equal(0, dirtyChanged);           // 転送はアクティブ限定
            Assert.Same(doc2, dm.Active);
        }
    });

    [Fact]
    public void CaretChange_ForwardedOnlyForActiveDocument() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var doc1 = NewDocWithText(dm, "abc");
            var doc2 = NewDocWithText(dm, "def"); // doc2 がアクティブ
            int caretChanged = 0;
            dm.ActiveCaretChanged += (_, _) => caretChanged++;

            doc2.Editor.ReplaceCharRange(0, 0, "x"); // AfterEdit は常に UpdateUI を発火
            Assert.True(caretChanged >= 1);

            caretChanged = 0;
            doc1.Editor.ReplaceCharRange(0, 0, "y"); // 非アクティブ分は転送しない
            Assert.Equal(0, caretChanged);
        }
    });

    // ===== FindByPath(PathKey 同一視) =====

    [Fact]
    public void FindByPath_MatchesCaseAndSeparatorInsensitively() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var doc = dm.CreateNew();
            doc.State.Path = @"C:\Temp\A.TXT";
            Assert.Same(doc, dm.FindByPath("c:/temp/a.txt")); // 大小文字・区切り差を同一視
        }
    });

    [Fact]
    public void FindByPath_IgnoresUntitled_AndReturnsNullWhenNoMatch() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            _ = dm.CreateNew(); // 未保存(Path=null)は対象外
            var doc = dm.CreateNew();
            doc.State.Path = @"C:\Temp\a.txt";
            Assert.Null(dm.FindByPath(@"C:\Temp\other.txt"));
        }
    });

    // ===== TryClose =====

    [Fact]
    public void TryClose_ConfirmRejected_KeepsDocumentAlive() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var doc = dm.CreateNew();
            Assert.False(dm.TryClose(doc, _ => false));
            Assert.Equal(1, dm.Count);
            Assert.Contains(doc, dm.Documents);
            Assert.False(doc.Editor.IsDisposed);
            Assert.False(doc.Page.IsDisposed);
        }
    });

    [Fact]
    public void TryClose_ConfirmAccepted_RemovesAndDisposes() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var doc = dm.CreateNew();
            Document? asked = null;
            Assert.True(dm.TryClose(doc, d => { asked = d; return true; }));
            Assert.Same(doc, asked);        // confirm には対象文書が渡る
            Assert.Equal(0, dm.Count);
            Assert.DoesNotContain(doc, dm.Documents);
            Assert.True(doc.Editor.IsDisposed); // ネイティブ資源の解放
            Assert.True(doc.Page.IsDisposed);
        }
    });

    // ===== SelectNext 巡回 / SelectAt 範囲外 no-op =====

    [Fact]
    public void SelectNext_WrapsFromLastToFirst() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var docs = new[] { dm.CreateNew(), dm.CreateNew(), dm.CreateNew() }; // アクティブ=末尾
            dm.SelectNext(+1);
            Assert.Same(docs[0], dm.Active); // 端は巡回
        }
    });

    [Fact]
    public void SelectNext_WrapsFromFirstToLast() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var docs = new[] { dm.CreateNew(), dm.CreateNew(), dm.CreateNew() };
            dm.SelectAt(0);
            dm.SelectNext(-1);
            Assert.Same(docs[2], dm.Active); // 先頭から逆方向も巡回
        }
    });

    [Fact]
    public void SelectAt_OutOfRange_IsNoOp() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var docs = new[] { dm.CreateNew(), dm.CreateNew() }; // アクティブ=docs[1]
            int switched = 0;
            dm.KeyBasedSwitch += (_, _) => switched++;
            dm.SelectAt(-1);
            dm.SelectAt(2);
            Assert.Same(docs[1], dm.Active);
            Assert.Equal(0, switched);
        }
    });

    // ===== KeyBasedSwitch は実切替時のみ発火 =====

    [Fact]
    public void KeyBasedSwitch_FiresWithNewDocument_OnlyWhenIndexActuallyChanges() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var docs = new[] { dm.CreateNew(), dm.CreateNew(), dm.CreateNew() }; // アクティブ=docs[2]
            var switchedTo = new List<Document>();
            dm.KeyBasedSwitch += (_, d) => switchedTo.Add(d);

            dm.SelectAt(0);            // 実切替 → 発火(新アクティブが渡る)
            Assert.Equal(new[] { docs[0] }, switchedTo);

            dm.SelectAt(0);            // 同一 index → no-op で発火しない
            Assert.Equal(new[] { docs[0] }, switchedTo);
        }
    });

    [Fact]
    public void KeyBasedSwitch_SingleTab_SelectNext_DoesNotFire() => Sta.Run(() =>
    {
        var (form, dm) = Make();
        using (form)
        {
            var doc = dm.CreateNew();
            int switched = 0;
            dm.KeyBasedSwitch += (_, _) => switched++;
            dm.SelectNext(+1); // 1 タブでは index が変わらない=冗長発声を出さない
            Assert.Equal(0, switched);
            Assert.Same(doc, dm.Active);
        }
    });
}
