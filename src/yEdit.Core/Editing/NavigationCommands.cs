using yEdit.Core.Buffers;

namespace yEdit.Core.Editing;

/// <summary>
/// TextSnapshot 上の位置移動関数群(純ロジック)。
/// EditorControl から呼ばれる。範囲外指定時のスナップは呼び出し側の責務で、
/// この関数群は caret ∈ [0, snap.CharLength] かつ code-point 境界を前提とする。
/// </summary>
/// <remarks>
/// 前提違反時(caret が [0, CharLength] を外れる/サロゲート中間)は、
/// TextSnapshot 側(GetChar / GetLineIndexOfChar 等)から
/// <see cref="ArgumentOutOfRangeException"/> が透過的に伝播する。
/// EditorControl 側は Task 6 の SnapAndClamp で必ずスナップしてから呼ぶこと
/// (呼び忘れると即例外でキー入力が消えるため)。
/// </remarks>
public static class NavigationCommands
{
    /// <summary>左に1文字移動。サロゲートペアは1文字として扱う。先頭では動かない。</summary>
    public static int MoveLeftChar(TextSnapshot s, int caret)
    {
        if (caret <= 0) return 0;
        int prev = caret - 1;
        if (prev > 0 && char.IsLowSurrogate(s.GetChar(prev)) && char.IsHighSurrogate(s.GetChar(prev - 1)))
            return prev - 1;
        return prev;
    }

    /// <summary>右に1文字移動。サロゲートペアは1文字として扱う。末尾では動かない。</summary>
    public static int MoveRightChar(TextSnapshot s, int caret)
    {
        if (caret >= s.CharLength) return s.CharLength;
        char c = s.GetChar(caret);
        if (char.IsHighSurrogate(c) && caret + 1 < s.CharLength && char.IsLowSurrogate(s.GetChar(caret + 1)))
            return caret + 2;
        return caret + 1;
    }

    /// <summary>現在行の先頭(char offset)。</summary>
    public static int MoveHome(TextSnapshot s, int caret)
    {
        int line = s.GetLineIndexOfChar(caret);
        return s.GetLineStart(line);
    }

    /// <summary>現在行の末尾(break 直前)。</summary>
    public static int MoveEnd(TextSnapshot s, int caret)
    {
        int line = s.GetLineIndexOfChar(caret);
        return s.GetLineEnd(line, includeBreak: false);
    }

    /// <summary>Home スマート: 先頭空白の後 ⇔ 行頭 をトグル。</summary>
    /// <remarks>
    /// - キャレットが行頭(lineStart)にいる → 先頭空白の後(firstNonWs)へ
    /// - キャレットが firstNonWs にいる → 行頭(lineStart)へ
    /// - それ以外(本文内) → firstNonWs へ
    /// 空白のみの行では firstNonWs == lineEnd。トグルは lineStart ↔ lineEnd 相当だが問題なし。
    /// 空白判定は半角空白(' ')とタブ('\t')のみ。全角空白(U+3000)や他の Unicode 空白は含めない
    /// (Scintilla 版 M6 と同じ挙動。char.IsWhiteSpace は改行を巻き込むため使わない)。
    /// </remarks>
    public static int MoveHomeSmart(TextSnapshot s, int caret)
    {
        int line = s.GetLineIndexOfChar(caret);
        int lineStart = s.GetLineStart(line);
        int lineEnd = s.GetLineEnd(line, includeBreak: false);
        int firstNonWs = lineStart;
        while (firstNonWs < lineEnd)
        {
            char c = s.GetChar(firstNonWs);
            if (c != ' ' && c != '\t') break;
            firstNonWs++;
        }
        // すでに firstNonWs にいる → lineStart。それ以外 → firstNonWs
        if (caret == firstNonWs) return lineStart;
        return firstNonWs;
    }
}
