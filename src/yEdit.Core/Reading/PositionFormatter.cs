namespace yEdit.Core.Reading;

/// <summary>現在位置・文字数の読み上げ文字列を組み立てる純ロジック（UI 非依存・テスト可能）。</summary>
public static class PositionFormatter
{
    /// <summary>
    /// 「行 L / 全 N、桁 C、文字数 M」を組み立てる。selectionLength&gt;0 なら「、選択 K 文字」を付ける。
    /// line/column は 1 始まり、totalChars は本文の UTF-16 文字数。
    /// </summary>
    public static string Format(int line, int totalLines, int column, int totalChars, int selectionLength)
    {
        string s = $"行 {line} / 全 {totalLines}、桁 {column}、文字数 {totalChars}";
        if (selectionLength > 0) s += $"、選択 {selectionLength} 文字";
        return s;
    }
}
