namespace yEdit.Core.Csv;

/// <summary>CSV モードの読み上げ文字列を組み立てる純ロジック（UI 非依存・テスト可能）。</summary>
public static class CsvAnnounceFormatter
{
    /// <summary>セル移動時の読み上げ。内容→位置の順（例「田中 2行2列」）。空セルは「空」。row/col は 1 始まり。</summary>
    public static string Cell(string value, int row, int col)
        => $"{(string.IsNullOrEmpty(value) ? "空" : value)} {row}行{col}列";

    /// <summary>見出し読み上げ。空なら「空」。</summary>
    public static string Header(string value)
        => string.IsNullOrEmpty(value) ? "空" : value;

    /// <summary>左端で左移動したときの読み上げ。</summary>
    public const string LeftEdge = "左端です";
    /// <summary>右端で右移動したときの読み上げ。</summary>
    public const string RightEdge = "右端です";
    /// <summary>先頭行で上移動したときの読み上げ。</summary>
    public const string TopEdge = "先頭行です";
    /// <summary>最終行で下移動したときの読み上げ。</summary>
    public const string BottomEdge = "最終行です";
}
