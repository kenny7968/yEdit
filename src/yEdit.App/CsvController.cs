using yEdit.Core.Csv;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 新CSVモード（グリッド型ナビゲーション）の配線。CSVモード中は ScintillaHost.ReadOnly=true で
/// 本文を編集不可にし、素キーのコマンドでセル移動・読み上げを行う。現在セルはキャレットの文字
/// オフセットから毎回導出するため、編集後も状態が陳腐化しない。F2 は CsvCellEditor に委譲する。
/// </summary>
public sealed class CsvController
{
    private readonly DocumentManager _docs;
    private readonly IAnnouncer _announcer;
    private readonly CsvCellEditor _editor = new();

    public CsvController(DocumentManager docs, IAnnouncer announcer)
    {
        _docs = docs;
        _announcer = announcer;
    }

    /// <summary>F2 編集オーバーレイ表示中か（MainForm がキー横取りを抑止するのに使う）。</summary>
    public bool IsEditing => _editor.IsEditing;

    /// <summary>CSVモードを手動でトグルする。ON 時は読取専用化＋現在セルを確定して読み上げ。</summary>
    public void ToggleMode()
    {
        var doc = _docs.Active;
        if (doc is null || _editor.IsEditing) return;

        if (!doc.State.CsvMode)
        {
            var csv = CsvParser.Parse(doc.Editor.SnapshotText);
            if (!csv.Ok) { _announcer.Say(CsvAnnounceFormatter.ParseError); return; } // 解析不可ならモードに入らない
            doc.State.CsvMode = true;
            doc.Editor.ReadOnly = true;
            _announcer.Say(CsvAnnounceFormatter.ModeOn);
            if (csv.Rows.Count == 0) { doc.Editor.ClearHighlight(); return; }
            var (row, col) = csv.FindCell(doc.Editor.CaretCharOffset);
            ApplyCell(doc.Editor, csv, row, col, announce: true);
        }
        else
        {
            doc.State.CsvMode = false;
            doc.Editor.ReadOnly = false;
            doc.Editor.ClearHighlight();
            _announcer.Say(CsvAnnounceFormatter.ModeOff);
        }
    }

    // ---- 移動（読み上げ付き） ----
    public void Move(Direction dir)
    {
        if (!TryContext(out var ed, out var csv, out var row, out var col)) return;
        var t = csv.MoveCell(row, col, dir);
        if (t is null) { _announcer.Say(EdgeMessage(dir)); return; }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    public void MoveRowStart()    { if (TryContext(out var ed, out var csv, out var r, out _)) ApplyTarget(ed, csv, csv.RowStart(r)); }
    public void MoveRowEnd()      { if (TryContext(out var ed, out var csv, out var r, out _)) ApplyTarget(ed, csv, csv.RowEnd(r)); }
    public void MoveColumnTop()   { if (TryContext(out var ed, out var csv, out _, out var c)) ApplyTarget(ed, csv, csv.ColumnTop(c)); }
    public void MoveColumnBottom(){ if (TryContext(out var ed, out var csv, out _, out var c)) ApplyTarget(ed, csv, csv.ColumnBottom(c)); }
    public void MoveTopLeft()     { if (TryContext(out var ed, out var csv, out _, out _))     ApplyTarget(ed, csv, csv.TopLeft()); }
    public void MoveBottomRight() { if (TryContext(out var ed, out var csv, out _, out _))     ApplyTarget(ed, csv, csv.BottomRight()); }

    /// <summary>セル指定移動（G）。「行,列」入力ボックス→範囲検証→移動。</summary>
    public void GoToCell()
    {
        if (!TryContext(out var ed, out var csv, out var row, out var col)) return;
        using var dlg = new CsvGoToCellDialog(row + 1, col + 1);
        if (dlg.ShowDialog(ed.FindForm()) != DialogResult.OK) return;
        if (!dlg.TryGetCell(out int r1, out int c1)) { _announcer.Say(CsvAnnounceFormatter.BadCellFormat); return; }
        var t = csv.GoTo(r1 - 1, c1 - 1);
        if (t is null) { _announcer.Say(CsvAnnounceFormatter.OutOfRange); return; }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    // ---- 読み上げのみ（移動なし） ----
    /// <summary>現在セルを読み上げる（Tab）。</summary>
    public void ReadCurrent()
    {
        if (!TryContext(out _, out var csv, out var row, out var col)) return;
        var f = csv.GetField(row, col);
        if (f is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
    }

    /// <summary>現在列の最上段セルを読み上げる（C）。</summary>
    public void ReadColumnTop()
    {
        if (!TryContext(out _, out var csv, out _, out var col)) return;
        var t = csv.ColumnTop(col);
        var f = t is null ? null : csv.GetField(t.Value.row, t.Value.col);
        _announcer.Say(CsvAnnounceFormatter.Header(f?.Value ?? ""));
    }

    /// <summary>現在行の左端セルを読み上げる（R）。</summary>
    public void ReadRowHead()
    {
        if (!TryContext(out _, out var csv, out var row, out _)) return;
        var f = csv.GetField(row, 0);
        _announcer.Say(CsvAnnounceFormatter.Header(f?.Value ?? ""));
    }

    /// <summary>現在セルを F2 編集する。確定で CSV 直列化→本文反映→再ハイライト＋読み上げ。</summary>
    public void BeginEdit()
    {
        if (_editor.IsEditing) return;
        if (!TryContext(out var ed, out _, out var row, out var col)) return;
        // 開始時点のセル span（直列化対象）を確定。row/col はセル内容変更では不変。
        var startCsv = CsvParser.Parse(ed.SnapshotText);
        var f = startCsv.GetField(row, col);
        if (f is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        int start = f.Start, length = f.Length;

        _editor.Begin(ed, f,
            onCommit: text =>
            {
                string serialized = CsvWriter.EscapeField(text);
                bool wasRo = ed.ReadOnly;
                ed.ReadOnly = false;
                ed.ReplaceCharRange(start, length, serialized);
                ed.ReadOnly = wasRo;
                var csv2 = CsvParser.Parse(ed.SnapshotText);
                if (csv2.Ok) ApplyCell(ed, csv2, row, col, announce: true);
            },
            onCancel: () =>
            {
                var csv2 = CsvParser.Parse(ed.SnapshotText);
                if (csv2.Ok && csv2.Rows.Count > 0) ApplyCell(ed, csv2, row, col, announce: false);
            });
    }

    // ==================== 内部 ====================

    /// <summary>アクティブが CSV モードならそのエディタ、でなければ null。</summary>
    private ScintillaHost? ActiveCsvEditor()
    {
        var doc = _docs.Active;
        return (doc is not null && doc.State.CsvMode) ? doc.Editor : null;
    }

    /// <summary>パースして現在 (row,col) を得る。CSVでない/解析不可/データ無しは読み上げて false。</summary>
    private bool TryContext(out ScintillaHost ed, out CsvDocument csv, out int row, out int col)
    {
        ed = null!; csv = null!; row = 0; col = 0;
        var e = ActiveCsvEditor();
        if (e is null) return false;
        ed = e;
        csv = CsvParser.Parse(ed.SnapshotText);
        if (!csv.Ok) { ed.ClearHighlight(); _announcer.Say(CsvAnnounceFormatter.ParseError); return false; }
        if (csv.Rows.Count == 0) { ed.ClearHighlight(); _announcer.Say(CsvAnnounceFormatter.NoData); return false; }
        (row, col) = csv.FindCell(ed.CaretCharOffset);
        return true;
    }

    private void ApplyTarget(ScintillaHost ed, CsvDocument csv, (int row, int col)? t)
    {
        if (t is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    /// <summary>(row,col) のセルへ ハイライト＋キャレット移動（選択は作らない）＋必要なら読み上げ。</summary>
    private void ApplyCell(ScintillaHost ed, CsvDocument csv, int row, int col, bool announce)
    {
        var f = csv.GetField(row, col);
        if (f is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        ed.HighlightCharRange(f.Start, f.Length);
        ed.MoveCaretCharOffset(f.Start);
        ed.Focus();
        if (announce) _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
    }

    private static string EdgeMessage(Direction dir) => dir switch
    {
        Direction.Left => CsvAnnounceFormatter.LeftEdge,
        Direction.Right => CsvAnnounceFormatter.RightEdge,
        Direction.Up => CsvAnnounceFormatter.TopEdge,
        _ => CsvAnnounceFormatter.BottomEdge,
    };
}
