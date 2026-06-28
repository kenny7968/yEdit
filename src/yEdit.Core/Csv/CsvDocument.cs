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
}
