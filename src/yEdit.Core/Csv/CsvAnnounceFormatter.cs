namespace yEdit.Core.Csv;

/// <summary>CSV モードの読み上げ文字列を組み立てる純ロジック（UI 非依存・テスト可能）。</summary>
public static class CsvAnnounceFormatter
{
    /// <summary>セル移動時の読み上げ。内容→位置の順（例「田中 2行2列」）。空セルは「ブランク」。row/col は 1 始まり。</summary>
    public static string Cell(string value, int row, int col) =>
        $"{(string.IsNullOrEmpty(value) ? "ブランク" : value)} {row}行{col}列";

    /// <summary>見出し読み上げ。空なら「ブランク」。</summary>
    public static string Header(string value) => string.IsNullOrEmpty(value) ? "ブランク" : value;

    /// <summary>CSV モードをオンにしたときの読み上げ。</summary>
    public const string ModeOn = "CSVモード オン";

    /// <summary>CSV モードをオフにしたときの読み上げ。</summary>
    public const string ModeOff = "CSVモード オフ";

    /// <summary>オープン時に CSV として解析できず、テキストとして開いたときの読み上げ。</summary>
    public const string OpenParseFailed = "CSVとして解析できませんでした。テキストとして開きます";

    /// <summary>操作時に CSV として解析できないときの読み上げ。</summary>
    public const string ParseError = "CSVとして解析できません";

    /// <summary>CSV だが行データが無いときの読み上げ。</summary>
    public const string NoData = "データがありません";

    /// <summary>移動先セルが取得できない異常時のフォールバック読み上げ。</summary>
    public const string CannotMove = "移動できません";

    /// <summary>セル指定移動で範囲外を指定したときの読み上げ。</summary>
    public const string OutOfRange = "範囲外です";

    /// <summary>セル指定の書式が不正なときの読み上げ。</summary>
    public const string BadCellFormat = "書式が不正です。行,列 の形式で入力してください";

    /// <summary>CSVモード中に本文変更コマンド（置換/折り返し整形等）を試みたときの読み上げ。</summary>
    public const string BlockedInCsvMode = "CSVモード中は実行できません";

    /// <summary>左端で左移動したときの読み上げ。</summary>
    public const string LeftEdge = "左端です";

    /// <summary>右端で右移動したときの読み上げ。</summary>
    public const string RightEdge = "右端です";

    /// <summary>先頭行で上移動したときの読み上げ。</summary>
    public const string TopEdge = "先頭行です";

    /// <summary>最終行で下移動したときの読み上げ。</summary>
    public const string BottomEdge = "最終行です";
}
