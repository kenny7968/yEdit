using yEdit.App.Tests.Fakes;
using yEdit.Core.Csv;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 6: CsvController の配線・状態機械・端メッセージ・GoToCell の 3 分岐・
/// BeginEdit の起動配線・parse-error 後始末・DocumentState 書き戻しのテスト。
/// 実 DocumentManager+実 EditorControl を STA 上で使い、Form 境界(FakeCellPicker)と
/// 通知(FakeAnnouncer)だけを偽物にする。CsvDocument の照合正しさ(Core 検証済み)は
/// 再検証しない(責務=配線・遷移・SR 誤読み抑止フラグ・通知文言・DocumentState 書き戻し)。
/// </summary>
public class CsvControllerTests
{
    /// <summary>CsvController を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeAnnouncer Announcer { get; } = new();
        public FakeCellPicker Picker { get; } = new();
        public CsvController Csv { get; }

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Csv = new CsvController(docs: Docs, announcer: Announcer, cellPicker: Picker);
        }

        /// <summary>本文に CSV テキストを載せて Active に返す(EditorControl.Text は新バッファ=Modified=false)。</summary>
        public Document NewCsvDoc(string csv)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = csv;
            return doc;
        }

        public void Dispose()
        {
            Csv.AbortEdit(); // 進行中の F2 編集を落とす(冪等)
            Form.Dispose();
        }
    }

    // 3×3 の素朴 CSV。行 = 頭文字(a=1行目・b=2行目・c=3行目)、列 = 末尾数字(1=1列目・2=2列目・3=3列目)。
    // 例: "b2" は 2 行 2 列。改行は LF 固定。
    private const string Grid3x3 =
        "a1,a2,a3\n" +
        "b1,b2,b3\n" +
        "c1,c2,c3";

    // ===== ctor(対応固定=Picker は ctor で呼ばれない) =====

    [Fact]
    public void Ctor_DoesNotInvokePicker_NorAnnouncer() => Sta.Run(() =>
    {
        using var host = new Host();
        Assert.Equal(0, host.Picker.PickCount);
        Assert.Empty(host.Announcer.Said);
    });

    // ===== TryEnterMode(5 分岐) =====

    [Fact]
    public void TryEnterMode_AlreadyInMode_ReturnsFalse_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        Assert.True(host.Csv.TryEnterMode(doc));
        int saidBefore = host.Announcer.Said.Count;

        Assert.False(host.Csv.TryEnterMode(doc)); // 2 回目は false・追加通知なし
        Assert.Equal(saidBefore, host.Announcer.Said.Count);
    });

    [Fact]
    public void TryEnterMode_UnparseableCsv_AnnouncesParseError_DoesNotEnter() => Sta.Run(() =>
    {
        using var host = new Host();
        // 引用符未終端 → CsvDocument.Ok=false
        var doc = host.NewCsvDoc("a1,\"b1\na2,b2");

        Assert.False(host.Csv.TryEnterMode(doc));
        Assert.False(doc.State.CsvMode);
        Assert.False(doc.Editor.ReadOnly);
        Assert.Contains(CsvAnnounceFormatter.ParseError, host.Announcer.Said);
    });

    [Fact]
    public void TryEnterMode_EmptyCsv_EntersMode_AnnouncesModeOnOnly() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(""); // Rows.Count=0

        Assert.True(host.Csv.TryEnterMode(doc));
        Assert.True(doc.State.CsvMode);
        Assert.True(doc.Editor.ReadOnly);
        Assert.False(doc.Editor.RaiseUiaSelectionEvents); // SR 誤読み抑止
        // データ無しは ModeOn のみ(セル情報なし)
        Assert.Single(host.Announcer.Said);
        Assert.Equal(CsvAnnounceFormatter.ModeOn, host.Announcer.Said[0]);
    });

    [Fact]
    public void TryEnterMode_ParseableCsv_EntersMode_ReadOnlyAndUiaOff_AnnouncesModeOnAndCell() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);

        Assert.True(host.Csv.TryEnterMode(doc));
        Assert.True(doc.State.CsvMode);
        Assert.True(doc.Editor.ReadOnly);
        Assert.False(doc.Editor.RaiseUiaSelectionEvents);
        // ModeOn + Cell が結合された 1 通知(現行実装=1 回 Say)。初期セルは caret=0→(0,0)="a1"。
        Assert.Contains($"{CsvAnnounceFormatter.ModeOn} {CsvAnnounceFormatter.Cell("a1", 1, 1)}", host.Announcer.Said);
    });

    [Fact]
    public void TryEnterMode_InitialCell_IsDerivedFromCaretPosition() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        // "a1,a2,a3\nb1,b2,b3\n..." の 2 行目 "b2" 相当の位置(0 始まり=9+3=12 前後)にキャレットを寄せる。
        // 正確なオフセットは EditorControl の EOL 処理に依存するため、CsvDocument.FindCell に任せて
        // "b" が含まれる位置(text.IndexOf("b2"))へキャレットを置く。
        int caret = doc.Editor.SnapshotText.IndexOf("b2", StringComparison.Ordinal);
        doc.Editor.MoveCaretCharOffset(caret);

        Assert.True(host.Csv.TryEnterMode(doc));
        Assert.Equal(1, doc.State.CsvRow); // 0 始まり=2 行目
        Assert.Equal(1, doc.State.CsvCol); // 0 始まり=2 列目
    });

    // ===== ExitMode(ToggleMode 経由・外部 API は ToggleMode のみ) =====

    [Fact]
    public void ToggleMode_FromOn_ExitsMode_RestoresReadWriteAndUia_AnnouncesModeOff() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();

        host.Csv.ToggleMode();

        Assert.False(doc.State.CsvMode);
        Assert.False(doc.Editor.ReadOnly);
        Assert.True(doc.Editor.RaiseUiaSelectionEvents); // 通常編集の SR 挙動に戻す
        Assert.Equal(CsvAnnounceFormatter.ModeOff, host.Announcer.Said[^1]);
    });

    [Fact]
    public void ToggleMode_FromOn_MovesCaretToLastCellStart() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); // (0,0)→(0,1)="a2"
        int expected = doc.Editor.SnapshotText.IndexOf("a2", StringComparison.Ordinal);

        host.Csv.ToggleMode();

        Assert.Equal(expected, doc.Editor.CaretCharOffset);
    });

    [Fact]
    public void ToggleMode_NoActiveDoc_IsNoOp() => Sta.Run(() =>
    {
        using var host = new Host();
        // Docs.CreateNew を呼ばない(Active=null)
        host.Csv.ToggleMode();

        Assert.Empty(host.Announcer.Said); // 通知も発火しない
    });

    // ===== ToggleMode(進入方向) =====

    [Fact]
    public void ToggleMode_FromOff_EntersMode() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);

        host.Csv.ToggleMode();

        Assert.True(doc.State.CsvMode);
    });

    // ===== Move(移動+読み上げ・端メッセージ) =====

    [Fact]
    public void Move_ToAdjacentCell_UpdatesStateAndAnnouncesCell() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();

        host.Csv.Move(Direction.Right); // (0,0)→(0,1)

        Assert.Equal(0, doc.State.CsvRow);
        Assert.Equal(1, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("a2", 1, 2), host.Announcer.Said[^1]);
    });

    [Fact]
    public void Move_AtLeftEdge_AnnouncesLeftEdge_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);            // (0,0) から開始
        host.Announcer.Said.Clear();

        host.Csv.Move(Direction.Left);         // 左端

        Assert.Equal(0, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.LeftEdge, host.Announcer.Said[^1]);
    });

    [Fact]
    public void Move_NotInMode_IsNoOp() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewCsvDoc(Grid3x3); // モードには入らない

        host.Csv.Move(Direction.Right);

        Assert.Empty(host.Announcer.Said);
    });

    // ===== 端ジャンプ(6 API から代表 2 件・残りは第 2 弾で被覆) =====

    [Fact]
    public void MoveTopLeft_MovesTo_0_0() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); host.Csv.Move(Direction.Down); // (1,1) へ
        host.Announcer.Said.Clear();

        host.Csv.MoveTopLeft();

        Assert.Equal(0, doc.State.CsvRow);
        Assert.Equal(0, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("a1", 1, 1), host.Announcer.Said[^1]);
    });
}
