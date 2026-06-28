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
}
