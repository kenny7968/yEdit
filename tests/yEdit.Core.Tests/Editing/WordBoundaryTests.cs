using yEdit.Core.Buffers;
using yEdit.Core.Editing;

namespace yEdit.Core.Tests.Editing;

public class WordBoundaryTests
{
    private static TextSnapshot S(string s) => TextBuffer.FromString(s).Current;

    // ===== NextWordStart: ASCII =====
    [Theory]
    [InlineData("hello world", 0, 6)]    // 'hello' の後 → 空白スキップ → 'w'
    [InlineData("hello world", 5, 6)]    // 空白位置 → 空白スキップ → 'w'
    [InlineData("hello world", 6, 11)]   // 'world' → EOF
    [InlineData("aaa bbb ccc", 3, 4)]    // 空白 → 'b'
    [InlineData("abc\r\ndef", 3, 5)]     // CRLF まとめて skip → 'd'
    [InlineData("abc\ndef", 3, 4)]       // LF skip → 'd'
    [InlineData("hello", 5, 5)]          // EOF 停留
    public void NextWordStart_Ascii(string text, int from, int expected)
        => Assert.Equal(expected, WordBoundary.NextWordStart(S(text), from));

    // ===== PrevWordStart: ASCII =====
    [Theory]
    [InlineData("hello world", 11, 6)]   // EOF → 'w' の頭
    [InlineData("hello world", 6, 0)]    // 'w' → 'h' の頭
    [InlineData("hello world", 5, 0)]    // 空白位置 → 'h'
    [InlineData("hello", 0, 0)]          // BOF 停留
    [InlineData("hello", 3, 0)]          // 'l' → 'h'
    public void PrevWordStart_Ascii(string text, int from, int expected)
        => Assert.Equal(expected, WordBoundary.PrevWordStart(S(text), from));

    // ===== 文字クラス切替(CJK 混在) =====
    [Fact]
    public void NextWordStart_ClassSwitch_CJK()
    {
        // "あいう漢字abc123" → ひらがな→漢字→英字→数字
        var s = S("あいう漢字abc123");
        Assert.Equal(3, WordBoundary.NextWordStart(s, 0));   // "あいう" の後 → 漢字頭
        Assert.Equal(5, WordBoundary.NextWordStart(s, 3));   // "漢字" の後 → 英字頭
        Assert.Equal(8, WordBoundary.NextWordStart(s, 5));   // "abc" の後 → 数字頭
        Assert.Equal(11, WordBoundary.NextWordStart(s, 8));  // "123" の後 → EOF
    }

    [Fact]
    public void PrevWordStart_ClassSwitch_CJK()
    {
        var s = S("あいう漢字abc123");
        Assert.Equal(8, WordBoundary.PrevWordStart(s, 11));  // 数字末尾 → 数字頭
        Assert.Equal(5, WordBoundary.PrevWordStart(s, 8));   // 英字頭 → 英字頭(=数字頭の前)は "abc" 頭=5
        Assert.Equal(3, WordBoundary.PrevWordStart(s, 5));   // 英字頭 → 漢字頭
        Assert.Equal(0, WordBoundary.PrevWordStart(s, 3));   // 漢字頭 → ひらがな頭
    }

    // ===== 記号(Other クラス) =====
    [Fact]
    public void NextWordStart_TreatsPunctuationAsSeparateClass()
    {
        var s = S("abc,def");
        Assert.Equal(3, WordBoundary.NextWordStart(s, 0));   // 'abc' → ','
        Assert.Equal(4, WordBoundary.NextWordStart(s, 3));   // ',' → 'd'
    }

    // ===== カタカナ =====
    [Fact]
    public void WordBoundary_Katakana_ClassifiesCorrectly()
    {
        var s = S("アイウエオ漢字");
        Assert.Equal(5, WordBoundary.NextWordStart(s, 0));   // カタカナ → 漢字
    }

    // ===== 空文書 =====
    [Fact]
    public void NextWordStart_OnEmptyBuffer_ReturnsZero()
    {
        var s = S("");
        Assert.Equal(0, WordBoundary.NextWordStart(s, 0));
    }

    [Fact]
    public void PrevWordStart_OnEmptyBuffer_ReturnsZero()
    {
        var s = S("");
        Assert.Equal(0, WordBoundary.PrevWordStart(s, 0));
    }

    // ===== サロゲート(絵文字は Other クラス扱い) =====
    [Fact]
    public void NextWordStart_SurrogatePair_TreatedAsSingleCp()
    {
        var s = S("😀😀abc");
        // 絵文字(サロゲート)は Other クラス。連続 2 個 = 4 code units
        Assert.Equal(4, WordBoundary.NextWordStart(s, 0));   // 絵文字 2 個 → 'a'
    }

    [Fact]
    public void PrevWordStart_SurrogatePair_TreatedAsSingleCp()
    {
        var s = S("a😀b");
        // caret=4('b' の後)から Prev → 'b' 頭=3
        // その前は 😀(Other)= class 切替 → 3 で止まる
        Assert.Equal(3, WordBoundary.PrevWordStart(s, 4));
    }
}
