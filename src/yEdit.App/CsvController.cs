using yEdit.Core.Csv;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// CSV モードのセルナビゲーション・読み上げ配線（SearchController/GrepController と同型）。
/// 現在セルはキャレットの文字オフセットから毎回導出するため、自由編集後も状態が陳腐化しない。
/// </summary>
public sealed class CsvController
{
    private readonly DocumentManager _docs;
    private readonly Announcer _announcer;

    public CsvController(DocumentManager docs, Announcer announcer)
    {
        _docs = docs;
        _announcer = announcer;
    }

    /// <summary>アクティブが CSV モードならそのエディタ、でなければ null。</summary>
    private ScintillaHost? ActiveCsvEditor()
    {
        var doc = _docs.Active;
        return (doc is not null && doc.State.CsvMode) ? doc.Editor : null;
    }

    /// <summary>方向 dir に1セル移動して選択・読み上げ。端では端アナウンス。</summary>
    public void Move(Direction dir)
    {
        var ed = ActiveCsvEditor();
        if (ed is null) return;

        var csv = CsvParser.Parse(ed.SnapshotText);
        if (!csv.Ok || csv.Rows.Count == 0) { _announcer.Say("CSVとして解析できません"); return; }

        var (row, col) = csv.FindCell(ed.CaretCharOffset);
        var target = csv.MoveCell(row, col, dir);
        if (target is null) { _announcer.Say(EdgeMessage(dir)); return; }

        var (tr, tc) = target.Value;
        var f = csv.GetField(tr, tc);
        if (f is null) { _announcer.Say(EdgeMessage(dir)); return; }

        ed.SelectCharRange(f.Start, f.Length);
        ed.Focus();
        _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, tr + 1, tc + 1));
    }

    /// <summary>現在列の先頭セル（見出し）を読み上げる（カーソル移動なし）。</summary>
    public void ReadColumnHeader()
    {
        var ed = ActiveCsvEditor();
        if (ed is null) return;

        var csv = CsvParser.Parse(ed.SnapshotText);
        if (!csv.Ok || csv.Rows.Count == 0) { _announcer.Say("CSVとして解析できません"); return; }

        var (_, col) = csv.FindCell(ed.CaretCharOffset);
        var h = csv.Header(col);
        _announcer.Say(h is null ? "空" : CsvAnnounceFormatter.Header(h.Value));
    }

    /// <summary>CSV モードを手動でトグルする（自動判定の救済・任意拡張子での利用）。</summary>
    public void ToggleMode()
    {
        var doc = _docs.Active;
        if (doc is null) return;
        doc.State.CsvMode = !doc.State.CsvMode;
        _announcer.Say(doc.State.CsvMode ? "CSVモード オン" : "CSVモード オフ");
    }

    private static string EdgeMessage(Direction dir) => dir switch
    {
        Direction.Left => CsvAnnounceFormatter.LeftEdge,
        Direction.Right => CsvAnnounceFormatter.RightEdge,
        Direction.Up => CsvAnnounceFormatter.TopEdge,
        _ => CsvAnnounceFormatter.BottomEdge,
    };
}
