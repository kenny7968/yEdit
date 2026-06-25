namespace yEdit.Accessibility;

/// <summary>
/// テキスト単位（文字／単語／行）の境界計算。すべて文字列スナップショット上の純粋関数。
/// オフセットは UTF-16 コード単位、行末（LineEnd）は改行を含み End は排他。
/// </summary>
public static class TextNavigation
{
    public static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    // ---------- 行 ----------

    /// <summary>offset を含む行の開始位置。</summary>
    public static int LineStart(string text, int offset)
    {
        offset = Clamp(offset, 0, text.Length);
        int i = offset;
        while (i > 0 && text[i - 1] != '\n') i--;
        return i;
    }

    /// <summary>offset を含む行の終端の次（= 次行の開始）。改行を含む。末尾なら text.Length。</summary>
    public static int LineEnd(string text, int offset)
    {
        offset = Clamp(offset, 0, text.Length);
        int i = offset;
        while (i < text.Length && text[i] != '\n') i++;
        if (i < text.Length) i++; // 改行を含める
        return i;
    }

    /// <summary>
    /// offset を含む行の終端（改行を含まない）。空行では LineStart と一致し長さ0になる。
    /// SR が空行を各自の流儀（NVDA=ブランク / PC-Talker=改行）で読めるよう、
    /// 行の読み取り単位はこちらを終端に使う。
    /// </summary>
    public static int LineEndNoBreak(string text, int offset)
    {
        int e = LineEnd(text, offset);
        if (e > 0 && text[e - 1] == '\n') e--;
        return e;
    }

    // ---------- 文字（サロゲートペア考慮） ----------

    public static int NextChar(string text, int offset)
    {
        offset = Clamp(offset, 0, text.Length);
        if (offset >= text.Length) return text.Length;
        if (char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]))
            return offset + 2;
        return offset + 1;
    }

    public static int PrevChar(string text, int offset)
    {
        offset = Clamp(offset, 0, text.Length);
        if (offset <= 0) return 0;
        if (char.IsLowSurrogate(text[offset - 1]) && offset - 2 >= 0 && char.IsHighSurrogate(text[offset - 2]))
            return offset - 2;
        return offset - 1;
    }

    // ---------- 単語（実験用の素朴な定義: 空白区切り） ----------

    private static bool IsWordSep(char c) => char.IsWhiteSpace(c);

    /// <summary>次の単語の先頭へ（現在の語を抜け、続く空白を飛ばす）。</summary>
    public static int NextWord(string text, int offset)
    {
        offset = Clamp(offset, 0, text.Length);
        int i = offset;
        while (i < text.Length && !IsWordSep(text[i])) i++;
        while (i < text.Length && IsWordSep(text[i])) i++;
        return i;
    }

    /// <summary>前の単語の先頭へ。</summary>
    public static int PrevWord(string text, int offset)
    {
        offset = Clamp(offset, 0, text.Length);
        int i = offset;
        while (i > 0 && IsWordSep(text[i - 1])) i--;
        while (i > 0 && !IsWordSep(text[i - 1])) i--;
        return i;
    }

    /// <summary>offset を含む単語の開始（非空白の連なり）。</summary>
    public static int WordStart(string text, int offset)
    {
        offset = Clamp(offset, 0, text.Length);
        int i = offset;
        while (i > 0 && !IsWordSep(text[i - 1])) i--;
        return i;
    }

    /// <summary>offset から始まる単語の終端（非空白の連なり）。</summary>
    public static int WordEnd(string text, int offset)
    {
        offset = Clamp(offset, 0, text.Length);
        int i = offset;
        while (i < text.Length && !IsWordSep(text[i])) i++;
        return i;
    }
}
