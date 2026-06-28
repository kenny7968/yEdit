namespace yEdit.Core.Csv;

/// <summary>CSV のパース結果。Rows は論理行×フィールド。Ok=false は不正（引用符未終端）。</summary>
public sealed partial class CsvDocument
{
    public IReadOnlyList<IReadOnlyList<CsvField>> Rows { get; }
    public bool Ok { get; }

    public CsvDocument(IReadOnlyList<IReadOnlyList<CsvField>> rows, bool ok)
    {
        Rows = rows;
        Ok = ok;
    }

    /// <summary>キャレット（UTF-16 オフセット）を含むセルを返す。含むセルが無ければ直前のセル、無ければ原点。</summary>
    public (int row, int col) FindCell(int caretOffset)
    {
        (int row, int col) best = (0, 0);
        int bestStart = -1;
        bool found = false;
        for (int r = 0; r < Rows.Count; r++)
        {
            var row = Rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                var f = row[c];
                if (caretOffset >= f.Start && caretOffset <= f.Start + f.Length)
                    return (r, c);
                if (f.Start <= caretOffset && f.Start > bestStart)
                {
                    bestStart = f.Start; best = (r, c); found = true;
                }
            }
        }
        return found ? best : (0, 0);
    }

    /// <summary>row,col から方向 dir に1セル移動した座標。端で動けなければ null。上下移動は列数不足の行で末尾列にクランプ。</summary>
    public (int row, int col)? MoveCell(int row, int col, Direction dir)
    {
        switch (dir)
        {
            case Direction.Left:
                return col > 0 ? (row, col - 1) : null;
            case Direction.Right:
                return col < Rows[row].Count - 1 ? (row, col + 1) : ((int, int)?)null;
            case Direction.Up:
                return row > 0 ? (row - 1, ClampCol(row - 1, col)) : ((int, int)?)null;
            case Direction.Down:
                return row < Rows.Count - 1 ? (row + 1, ClampCol(row + 1, col)) : ((int, int)?)null;
        }
        return null;
    }

    private int ClampCol(int row, int col)
    {
        int last = Rows[row].Count - 1;
        return col > last ? last : col;
    }

    /// <summary>row,col のフィールド。範囲外は null。</summary>
    public CsvField? GetField(int row, int col)
    {
        if (row < 0 || row >= Rows.Count) return null;
        var r = Rows[row];
        if (col < 0 || col >= r.Count) return null;
        return r[col];
    }

    /// <summary>col 列の先頭セル（見出し＝0行目）。範囲外は null。</summary>
    public CsvField? Header(int col) => GetField(0, col);
}
