namespace yEdit.Core.Editing;

public readonly record struct ImeCompositionState(
    int Start,
    string Text,
    int CursorPos,
    byte[] Attrs,
    int[] Clauses)
{
    public static ImeCompositionState Empty { get; } = new(0, "", 0, [], []);
    public bool IsActive => Text.Length > 0;

    /// <summary>GCS_COMPATTR のバイト列をそのままコピー(未確定文字列 UTF-16 code unit ごと 1 バイト)。</summary>
    public static byte[] ParseAttrs(ReadOnlySpan<byte> src)
    {
        if (src.Length == 0) return [];
        var buf = new byte[src.Length];
        src.CopyTo(buf);
        return buf;
    }

    /// <summary>GCS_COMPCLAUSE のバイト列を int32 (little-endian) 配列としてデコード。
    /// 半端バイト(4 の倍数でない末尾)は切り捨てる。</summary>
    public static int[] ParseClauses(ReadOnlySpan<byte> src)
    {
        int n = src.Length / 4;
        if (n == 0) return [];
        var buf = new int[n];
        for (int i = 0; i < n; i++)
        {
            int off = i * 4;
            buf[i] = src[off] | (src[off + 1] << 8) | (src[off + 2] << 16) | (src[off + 3] << 24);
        }
        return buf;
    }

    /// <summary>CursorPos がサロゲート pair の low 位置を指していたら high 位置にスナップ。</summary>
    public static int SnapCursorPos(string text, int cursor)
    {
        if (cursor <= 0 || cursor >= text.Length) return Math.Clamp(cursor, 0, text.Length);
        if (char.IsLowSurrogate(text[cursor]) && char.IsHighSurrogate(text[cursor - 1]))
            return cursor - 1;
        return cursor;
    }
}
