namespace yEdit.Core.Reading;

/// <summary>
/// 1 文字（コードポイント）を日本語で説明する純ロジック（SR 読み上げ用・UI 非依存・テスト可能）。
/// 全角/半角空白・タブ・制御文字・かな/カナ/半角カナ/全角英数/漢字/ASCII/記号・サロゲートを区別し、
/// 紛らわしい/不可視文字には U+XXXX を併記して曖昧さを解消する。
/// </summary>
public static class CharacterDescriber
{
    /// <summary>
    /// text の index 位置のコードポイント（サロゲートペアは結合した 1 文字）を説明する。
    /// index が範囲外なら空文字を返す（呼び出し側で「末尾」等に振り分ける）。
    /// </summary>
    public static string DescribeAt(string text, int index)
    {
        if (index < 0 || index >= text.Length) return "";
        char c = text[index];
        // 低サロゲートの途中を指していたら直前のペア先頭へ寄せる。
        if (char.IsLowSurrogate(c) && index > 0 && char.IsHighSurrogate(text[index - 1])) { index--; c = text[index]; }
        // 孤立サロゲート（ペアを成さない）は ConvertToUtf32 が例外を投げるため、生のコードユニットで説明する。
        bool pairedHigh = char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
        if (char.IsSurrogate(c) && !pairedHigh) return $"不正なサロゲート (U+{(int)c:X4})";
        return Describe(char.ConvertToUtf32(text, index));
    }

    /// <summary>コードポイントを日本語で説明する。不正値（範囲外・サロゲート単独）でも例外を投げない。</summary>
    public static string Describe(int codePoint)
    {
        if (codePoint < 0 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            return $"不正なコードポイント (U+{codePoint:X4})";

        switch (codePoint)
        {
            case 0x20: return "半角スペース (U+0020)";
            case 0x3000: return "全角スペース (U+3000)";
            case 0xA0: return "ノーブレークスペース (U+00A0)";
            case 0x09: return "タブ (U+0009)";
            case 0x0A: return "改行 (U+000A)";
            case 0x0D: return "復帰 (U+000D)";
        }

        // C0(<0x20) ＋ DEL(0x7F) ＋ C1(0x80–0x9F) を制御文字として扱う。
        if (codePoint < 0x20 || (codePoint >= 0x7F && codePoint <= 0x9F))
            return $"制御文字 (U+{codePoint:X4})";

        string s = char.ConvertFromUtf32(codePoint);

        if (codePoint is >= 0x3040 and <= 0x309F) return $"ひらがな {s}";
        if (codePoint is >= 0x30A0 and <= 0x30FF) return $"カタカナ {s}";
        if (codePoint is >= 0xFF61 and <= 0xFF9F) return $"半角カタカナ {s}";
        if (codePoint is >= 0xFF01 and <= 0xFF5E) return $"全角 {s}";              // 全角英数記号
        if (IsCjkIdeograph(codePoint)) return $"漢字 {s}";
        if (codePoint is >= 0x21 and <= 0x7E) return s;                            // ASCII 印字可（素の文字）

        // それ以外（記号・各種文字）は文字＋コードポイント。:X4 は最小桁数なので astral も自然に 5〜6 桁になる。
        return $"{s} (U+{codePoint:X4})";
    }

    private static bool IsCjkIdeograph(int cp) =>
        cp is (>= 0x4E00 and <= 0x9FFF)   // CJK 統合漢字
            or (>= 0x3400 and <= 0x4DBF)  // 拡張A
            or (>= 0xF900 and <= 0xFAFF)  // 互換漢字
            or (>= 0x20000 and <= 0x2FA1F) // 拡張B〜F（astral）
            or (>= 0x30000 and <= 0x323AF); // 拡張G・H（astral）
}
