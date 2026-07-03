namespace yEdit.Core.Reading;

/// <summary>
/// キャレットが空行（改行を除く長さが 0 の行）上にあるかを判定する純ロジック。
/// PC-Talker は UIA 経由で長さ 0 の行を無音にするため（HANDOFF §4.1）、
/// 能動発声「空行」の要否判定に使う（NVDA はネイティブに「ブランク」を読むため対象外）。
/// </summary>
public static class EmptyLineDetector
{
    /// <summary>
    /// text 内の caret（UTF-16 オフセット）が空行上にあるか。
    /// 総関数: 範囲外や CRLF の間（Scintilla では取り得ないキャレット位置）は false。
    /// </summary>
    public static bool IsCaretOnEmptyLine(string text, int caret)
    {
        if (caret < 0 || caret > text.Length) return false;
        if (caret > 0 && caret < text.Length && text[caret - 1] == '\r' && text[caret] == '\n')
            return false; // CRLF の間 = 行頭でも行末でもない不正位置
        bool atLineStart = caret == 0 || text[caret - 1] == '\n' || text[caret - 1] == '\r';
        bool atLineEnd = caret == text.Length || text[caret] == '\n' || text[caret] == '\r';
        return atLineStart && atLineEnd;
    }
}
