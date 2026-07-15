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

    // 5×5 の CSV。値 = "r{行0始まり}c{列0始まり}" で全セルユニーク(行/列の取り違えを文言で検出)。
    // 開始位置 (2,2) は全方向に 2 以上の余地があり、隣接移動と端ジャンプの到達先が必ず異なる。
    private const string Grid5x5 =
        "r0c0,r0c1,r0c2,r0c3,r0c4\n" +
        "r1c0,r1c1,r1c2,r1c3,r1c4\n" +
        "r2c0,r2c1,r2c2,r2c3,r2c4\n" +
        "r3c0,r3c1,r3c2,r3c3,r3c4\n" +
        "r4c0,r4c1,r4c2,r4c3,r4c4";

    /// <summary>CSV モードへ入り、開始セルを (row,col) に直接設定する(非既定位置からの検証開始標準)。
    /// DocumentState が真実源(TryContext が読む)なので直接設定で十分。通知履歴はクリアして返す。</summary>
    private static Document EnterAt(Host host, string csv, int row, int col)
    {
        var doc = host.NewCsvDoc(csv);
        Assert.True(host.Csv.TryEnterMode(doc));
        doc.State.CsvRow = row;
        doc.State.CsvCol = col;
        host.Announcer.Said.Clear();
        return doc;
    }

    /// <summary>Grid5x5 の (2,2) から開始(端ジャンプ/ByKey 共通 fixture)。</summary>
    private static Document EnterAt22(Host host) => EnterAt(host, Grid5x5, 2, 2);

    /// <summary>Grid5x5 上で (row0,col0) に居ることを State と Cell 文言の両面で assert する。
    /// 文言はセル値がユニーク("r{row}c{col}")なので行/列の取り違えも検出する。</summary>
    private static void AssertAt(Host host, Document doc, int row0, int col0)
    {
        Assert.Equal(row0, doc.State.CsvRow);
        Assert.Equal(col0, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell($"r{row0}c{col0}", row0 + 1, col0 + 1), host.Announcer.Said[^1]);
    }

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

    // kill 対象: EdgeMessage の Right→LeftEdge 化(変異 C)・右端判定(列数-1)の破壊。
    [Fact]
    public void Move_AtRightEdge_AnnouncesRightEdge_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt(host, Grid5x5, 2, 4);   // 右端(2,4)

        host.Csv.Move(Direction.Right);

        Assert.Equal(2, doc.State.CsvRow);
        Assert.Equal(4, doc.State.CsvCol);        // 動かない
        Assert.Equal(CsvAnnounceFormatter.RightEdge, host.Announcer.Said[^1]);
    });

    // kill 対象: EdgeMessage の Up 分岐削除(default=BottomEdge へ落ちる)・先頭行判定の破壊。
    [Fact]
    public void Move_AtTopEdge_AnnouncesTopEdge_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt(host, Grid5x5, 0, 2);   // 先頭行(0,2)

        host.Csv.Move(Direction.Up);

        Assert.Equal(0, doc.State.CsvRow);        // 動かない
        Assert.Equal(2, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.TopEdge, host.Announcer.Said[^1]);
    });

    // kill 対象: EdgeMessage の default(BottomEdge)の他文言化・最終行判定の破壊。
    [Fact]
    public void Move_AtBottomEdge_AnnouncesBottomEdge_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt(host, Grid5x5, 4, 2);   // 最終行(4,2)

        host.Csv.Move(Direction.Down);

        Assert.Equal(4, doc.State.CsvRow);        // 動かない
        Assert.Equal(2, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.BottomEdge, host.Announcer.Said[^1]);
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

    // ===== 端ジャンプ(残り) =====

    [Fact]
    public void MoveBottomRight_MovesToLastCell() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();

        host.Csv.MoveBottomRight();

        Assert.Equal(2, doc.State.CsvRow);
        Assert.Equal(2, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("c3", 3, 3), host.Announcer.Said[^1]);
    });

    // ===== 端ジャンプ(第 2 弾=残り 4 API・(2,2) 起点で隣接移動との取り違えも kill) =====

    // kill 対象: RowStart→RowEnd の取り違え(変異 A)・Left 隣接移動との混同((2,1) なら赤)。
    [Fact]
    public void MoveRowStart_From22_MovesToRowHead() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt22(host);

        host.Csv.MoveRowStart();

        AssertAt(host, doc, 2, 0);
    });

    // kill 対象: RowEnd→RowStart の取り違え・Right 隣接移動との混同((2,3) なら赤)。
    [Fact]
    public void MoveRowEnd_From22_MovesToRowTail() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt22(host);

        host.Csv.MoveRowEnd();

        AssertAt(host, doc, 2, 4);
    });

    // kill 対象: ColumnTop→ColumnBottom/RowStart の取り違え・Up 隣接移動との混同((1,2) なら赤)。
    [Fact]
    public void MoveColumnTop_From22_MovesToColumnHead() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt22(host);

        host.Csv.MoveColumnTop();

        AssertAt(host, doc, 0, 2);
    });

    // kill 対象: ColumnBottom→ColumnTop/RowEnd の取り違え・Down 隣接移動との混同((3,2) なら赤)。
    [Fact]
    public void MoveColumnBottom_From22_MovesToColumnTail() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt22(host);

        host.Csv.MoveColumnBottom();

        AssertAt(host, doc, 4, 2);
    });

    // ===== GoToCell(3 分岐+対応固定) =====

    [Fact]
    public void GoToCell_PickerCanceled_NoAnnounce_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); host.Csv.Move(Direction.Down); // 既定 (0,0) から (1,1) へ
        host.Announcer.Said.Clear();
        host.Picker.NextResult = CellPickResult.Canceled;

        host.Csv.GoToCell();

        Assert.Equal(1, host.Picker.PickCount);
        Assert.Empty(host.Announcer.Said);      // Cancel は無音
        Assert.Equal(1, doc.State.CsvRow);      // 変化なし=(1,1) のまま
        Assert.Equal(1, doc.State.CsvCol);
    });

    [Fact]
    public void GoToCell_InvalidFormat_AnnouncesBadFormat_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); host.Csv.Move(Direction.Down); // 既定 (0,0) から (1,1) へ
        host.Announcer.Said.Clear();
        host.Picker.NextResult = CellPickResult.InvalidFormat;

        host.Csv.GoToCell();

        Assert.Equal(CsvAnnounceFormatter.BadCellFormat, host.Announcer.Said[^1]);
        Assert.Equal(1, doc.State.CsvRow);      // 変化なし=(1,1) のまま
        Assert.Equal(1, doc.State.CsvCol);
    });

    [Fact]
    public void GoToCell_OutOfRange_AnnouncesOutOfRange_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); host.Csv.Move(Direction.Down); // 既定 (0,0) から (1,1) へ
        host.Announcer.Said.Clear();
        host.Picker.NextResult = CellPickResult.Ok(99, 99);   // 3×3 の外

        host.Csv.GoToCell();

        Assert.Equal(CsvAnnounceFormatter.OutOfRange, host.Announcer.Said[^1]);
        Assert.Equal(1, doc.State.CsvRow);      // 変化なし=(1,1) のまま
        Assert.Equal(1, doc.State.CsvCol);
    });

    [Fact]
    public void GoToCell_Ok_MovesToTarget_AnnouncesCell() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();
        host.Picker.NextResult = CellPickResult.Ok(3, 2);     // 1 始まり=(2,1) 0 始まり="c2"

        host.Csv.GoToCell();

        Assert.Equal(2, doc.State.CsvRow);
        Assert.Equal(1, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("c2", 3, 2), host.Announcer.Said[^1]);
    });

    [Fact]
    public void GoToCell_PassesCurrentCellToPicker_As1Based() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        // 非対称位置(1,2)= 2 行 3 列。Pick 呼び出しで row と col の取り違えを検出可能にする。
        host.Csv.Move(Direction.Down);
        host.Csv.Move(Direction.Right); host.Csv.Move(Direction.Right);
        host.Picker.NextResult = CellPickResult.Canceled;

        host.Csv.GoToCell();

        Assert.Equal(2, host.Picker.LastCurrentRow1); // 2 行(1 始まり)
        Assert.Equal(3, host.Picker.LastCurrentCol1); // 3 列(1 始まり)
    });

    // ===== 読み上げ(移動なし) =====

    [Fact]
    public void ReadCurrent_AnnouncesCurrentCell_NoStateChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); // (0,1)
        host.Announcer.Said.Clear();

        host.Csv.ReadCurrent();

        Assert.Equal(CsvAnnounceFormatter.Cell("a2", 1, 2), host.Announcer.Said[^1]);
        Assert.Equal(0, doc.State.CsvRow);   // 位置は動かない
        Assert.Equal(1, doc.State.CsvCol);
    });

    [Fact]
    public void ReadColumnTopAndRowHead_AnnounceHeaders() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); host.Csv.Move(Direction.Down); // (1,1)
        host.Announcer.Said.Clear();

        host.Csv.ReadColumnTop();
        Assert.Equal(CsvAnnounceFormatter.Header("a2"), host.Announcer.Said[^1]);

        host.Csv.ReadRowHead();
        Assert.Equal(CsvAnnounceFormatter.Header("b1"), host.Announcer.Said[^1]);
    });

    // ===== BeginEdit/AbortEdit(オーバーレイの起動配線のみ検証・Enter/Esc の E2E は L5 領分) =====

    [Fact]
    public void BeginEdit_NotInMode_IsNoOp() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewCsvDoc(Grid3x3); // モードに入らない

        host.Csv.BeginEdit();

        Assert.False(host.Csv.IsEditing);
    });

    [Fact]
    public void BeginEdit_InMode_StartsOverlay_IsEditingTrue() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);

        host.Csv.BeginEdit();

        Assert.True(host.Csv.IsEditing);
        host.Csv.AbortEdit(); // 後始末(HostForm 破棄前に必ず落とす)
    });

    [Fact]
    public void AbortEdit_WhenEditing_ExitsEditing_AndIsIdempotent() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.BeginEdit();
        Assert.True(host.Csv.IsEditing);

        host.Csv.AbortEdit();
        Assert.False(host.Csv.IsEditing);

        host.Csv.AbortEdit(); // 2 回目=冪等(例外を出さない)
        Assert.False(host.Csv.IsEditing);
    });

    // ===== クランプ(本文編集で行/列が減った後の補正) =====

    [Fact]
    public void Move_AfterContentReducedRows_ClampsToLastRow() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.MoveBottomRight();               // (2,2)
        // 本文を 1 行だけに置換(モード中でも Text setter は無条件で通る=クランプ機構のテスト)
        doc.Editor.ReadOnly = false;
        doc.Editor.Text = "x1,x2,x3";
        doc.Editor.ReadOnly = true;
        host.Announcer.Said.Clear();

        host.Csv.ReadCurrent();                    // (2,2) → クランプ → (0,2)

        Assert.Equal(0, doc.State.CsvRow);
        Assert.Equal(2, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("x3", 1, 3), host.Announcer.Said[^1]);
    });

    // ragged CSV(1 行目 3 列・2 行目 1 列)。クランプは「その行の幅」基準であることの検証にも使う。
    private const string Ragged =
        "a,b,c\n" +
        "d";

    // kill 対象: ClampCol の上限クランプ除去(変異 D=範囲外のまま→CannotMove/例外)・書き戻し(State 更新)の削除。
    [Fact]
    public void ReadCurrent_ColBeyondRowWidth_ClampsToLastCol_AndWritesBack() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt(host, Ragged, row: 0, col: 99);   // 幅 3 の行で列 99

        host.Csv.ReadCurrent();

        Assert.Equal(CsvAnnounceFormatter.Cell("c", 1, 3), host.Announcer.Said[^1]);
        Assert.Equal(2, doc.State.CsvCol);   // クランプ結果の書き戻し
        Assert.Equal(0, doc.State.CsvRow);
    });

    // kill 対象: ClampCol の下限(c<0→0)クランプ除去(負のまま→CannotMove/例外)・書き戻しの削除。
    [Fact]
    public void ReadCurrent_NegativeCol_ClampsToZero_AndWritesBack() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt(host, Ragged, row: 0, col: -1);

        host.Csv.ReadCurrent();

        Assert.Equal(CsvAnnounceFormatter.Cell("a", 1, 1), host.Announcer.Said[^1]);
        Assert.Equal(0, doc.State.CsvCol);   // クランプ結果の書き戻し
    });

    // kill 対象: ClampRow の下限(r<0→0)クランプ除去(負のまま→CannotMove/例外)・書き戻しの削除。
    // 列は 1 に置き、行クランプ後も列が保存されること(原点フォールバックとの混同)も検出する。
    [Fact]
    public void ReadCurrent_NegativeRow_ClampsToRowZero_AndWritesBack() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = EnterAt(host, Ragged, row: -5, col: 1);

        host.Csv.ReadCurrent();

        Assert.Equal(CsvAnnounceFormatter.Cell("b", 1, 2), host.Announcer.Said[^1]);
        Assert.Equal(0, doc.State.CsvRow);   // クランプ結果の書き戻し
        Assert.Equal(1, doc.State.CsvCol);
    });

    // ===== parse-error 後始末(モード中に本文が引用符未終端になったケース) =====

    [Fact]
    public void AnyCommand_AfterContentBecomesUnparseable_AnnouncesParseError() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        // モード中に本文を書き換えて Ok=false 化(引用符未終端)
        doc.Editor.ReadOnly = false;
        doc.Editor.Text = "a1,\"broken\nx,y";
        doc.Editor.ReadOnly = true;
        doc.ClearCsvCache();                        // Snapshot の再パースを強制
        host.Announcer.Said.Clear();

        host.Csv.Move(Direction.Right);             // TryContext が ParseError を通知

        Assert.Contains(CsvAnnounceFormatter.ParseError, host.Announcer.Said);
    });
}
