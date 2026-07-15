using yEdit.App.Tests.Fakes;
using yEdit.Core.Csv;
using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 8 Task B: KinsokuFormatController の抽出(MainForm.FormatWithKinsoku から)。
/// 5 Fact + 3 Theory rows = 計 8 件: 部分整形/全文整形/変更なし/CSV 抑止/空 no-op/EOL 追随。
/// 実 DocumentManager+実 EditorControl を STA 上で使い、通知(FakeAnnouncer)だけを偽物にする。
/// AppSettings は Run 引数(=呼び出し時解決)なので POCO を直接渡す。
/// </summary>
public class KinsokuFormatControllerTests
{
    /// <summary>KinsokuFormatController を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeAnnouncer Announcer { get; } = new();
        public KinsokuFormatController Kinsoku { get; }

        /// <summary>WrapColumn=20(半角換算=CJK 10 文字で折返し)。禁則集合は最小 POCO(テストが必要とする範囲のみ)。</summary>
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

        /// <summary>クリーンな本文を持つアクティブ文書を作る(Text セッター=新規バッファで Modified=false・キャレット 0)。</summary>
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
        // 20 CJK 文字(=40 半角桁) > WrapColumn=20 → 少なくとも 1 回は改行挿入されて formatted != target
        var doc = host.NewDoc("あいうえおかきくけこさしすせそたちつてと");
        doc.Editor.SelectCharRange(0, 20);   // 全 20 文字を選択(部分選択パスに入る=selStart != selEnd)

        host.Kinsoku.Run(host.Settings);

        Assert.Contains("整形しました", host.Announcer.Said);
        // 部分整形: SelectCharRange(start, formatted.Length) → [0, formatted.Length)
        var (s, e) = doc.Editor.GetSelectionCharRange();
        Assert.Equal(0, s);
        Assert.Equal(doc.Editor.SnapshotText.Length, e);   // 置換で buffer 全体 = formatted になり、その全長が選択される
        Assert.True(e > 20, "整形で EOL が挿入され buffer が伸びるはず(20 -> >20)");
    });

    // ===== 2. 全文整形(選択なし=whole=true) =====

    [Fact]
    public void WholeText_NoSelection_Formats_AndCaretToStart_AndAnnouncesSuccess() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("あいうえおかきくけこさしすせそたちつてと");   // 折返し発生する 20 CJK
        // 非既定位置から検証開始(Stage 6 レビュー標準): 一旦選択を作ってから (0,0) に戻す
        doc.Editor.SelectCharRange(5, 5);   // [5, 10) に選択
        doc.Editor.SelectCharRange(0, 0);   // 空選択=whole パス

        host.Kinsoku.Run(host.Settings);

        Assert.Contains("整形しました", host.Announcer.Said);
        // 全文整形時はキャレットが (0, 0) に移動(全選択を避ける仕様)
        var (s, e) = doc.Editor.GetSelectionCharRange();
        Assert.Equal(0, s);
        Assert.Equal(0, e);
        Assert.True(doc.Editor.SnapshotText.Length > 20, "全文整形で EOL が挿入されているはず");
    });

    // ===== 3. 変更なし =====

    [Fact]
    public void NoChange_AnnouncesNoChange_AndBufferUnchanged() => Sta.Run(() =>
    {
        using var host = new Host();
        // 既に整形済み=WrapColumn=20 未満で改行不要な短い部分
        var doc = host.NewDoc("短い1行");
        // 非既定位置から検証開始(Stage 6 レビュー標準): 部分選択パス(selStart != selEnd)
        doc.Editor.SelectCharRange(1, 3);   // [1, 4) を選択 → 部分整形パス
        string textBefore = doc.Editor.SnapshotText;

        host.Kinsoku.Run(host.Settings);

        Assert.Contains("変更なし", host.Announcer.Said);
        Assert.DoesNotContain("整形しました", host.Announcer.Said);
        Assert.Equal(textBefore, doc.Editor.SnapshotText);   // バッファ不変
    });

    // ===== 4. CSV モード中は抑止 =====

    [Fact]
    public void CsvMode_Blocked_AnnouncesBlockedText_AndBufferUnchanged() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("あいうえおかきくけこさしすせそたちつてと");   // 本来なら整形される長さ
        doc.State.CsvMode = true;   // CsvController を介さず状態だけ立てる(判定は State 経由)
        string textBefore = doc.Editor.SnapshotText;

        host.Kinsoku.Run(host.Settings);

        Assert.Contains(CsvAnnounceFormatter.BlockedInCsvMode, host.Announcer.Said);
        Assert.DoesNotContain("整形しました", host.Announcer.Said);
        Assert.Equal(textBefore, doc.Editor.SnapshotText);   // 誤成功通知/誤変更の両方を抑止
    });

    // ===== 5. 空バッファ no-op(len<=0) =====

    [Fact]
    public void EmptyBufferNoSelection_NoOp_NoAnnouncement() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc(string.Empty);   // 空バッファ
        doc.Editor.SelectCharRange(0, 0);

        host.Kinsoku.Run(host.Settings);

        Assert.Empty(host.Announcer.Said);   // 発声なし(誤成功/誤失敗通知どちらもしない)
        Assert.Equal(string.Empty, doc.Editor.SnapshotText);
    });

    // ===== 6. EOL 追随(CRLF/LF/CR) =====

    [Theory]
    [InlineData(LineEnding.Crlf, "\r\n")]
    [InlineData(LineEnding.Lf, "\n")]
    [InlineData(LineEnding.Cr, "\r")]
    public void UsesActiveDocumentEol(LineEnding eol, string eolString) => Sta.Run(() =>
    {
        using var host = new Host();
        // 折返しが確実に発生する長さ(20 CJK 超) & 元テキストに改行を含めない(挿入 EOL のみが結果に現れる)
        var doc = host.NewDoc("あいうえおかきくけこさしすせそたちつてと");
        doc.State.LineEnding = eol;

        host.Kinsoku.Run(host.Settings);

        // 整形結果に指定した EOL が含まれる(=Document.State.LineEnding が使われている)
        string result = doc.Editor.SnapshotText;
        Assert.Contains(eolString, result);
        Assert.Contains("整形しました", host.Announcer.Said);

        // 他 EOL 種が混入していないことも確認(CRLF/LF/CR の区別が実質的に効いていることの保証)
        // - LF 選択時: \r があってはならない(挿入は "\n" のみ)
        // - CR 選択時: \n があってはならない(挿入は "\r" のみ)
        // - CRLF 選択時: \r と \n の数が一致(すべての \r に対応する \n がある)
        if (eol == LineEnding.Lf) Assert.DoesNotContain("\r", result);
        if (eol == LineEnding.Cr) Assert.DoesNotContain("\n", result);
        if (eol == LineEnding.Crlf) Assert.Equal(result.Count(c => c == '\r'), result.Count(c => c == '\n'));
    });
}
