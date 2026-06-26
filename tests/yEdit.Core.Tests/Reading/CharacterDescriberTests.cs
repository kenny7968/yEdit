using yEdit.Core.Reading;
using Xunit;

namespace yEdit.Core.Tests.Reading;

public class CharacterDescriberTests
{
    [Theory]
    [InlineData(0x20, "半角スペース (U+0020)")]
    [InlineData(0x3000, "全角スペース (U+3000)")]
    [InlineData(0xA0, "ノーブレークスペース (U+00A0)")] // NBSP（半角スペースと紛らわしいため数値で指定）
    [InlineData(0x09, "タブ (U+0009)")]
    [InlineData(0x0A, "改行 (U+000A)")]
    [InlineData(0x0D, "復帰 (U+000D)")]
    public void Whitespace_and_breaks(int cp, string expected)
        => Assert.Equal(expected, CharacterDescriber.Describe(cp));

    [Theory]
    [InlineData(0x01, "制御文字 (U+0001)")] // C0
    [InlineData(0x7F, "制御文字 (U+007F)")] // DEL
    [InlineData(0x80, "制御文字 (U+0080)")] // C1
    [InlineData(0x85, "制御文字 (U+0085)")] // C1 NEL
    [InlineData(0x9F, "制御文字 (U+009F)")] // C1 末尾
    public void Control_characters_include_codepoint(int cp, string expected)
        => Assert.Equal(expected, CharacterDescriber.Describe(cp));

    [Fact]
    public void Invalid_codepoints_do_not_throw()
    {
        Assert.Equal("不正なコードポイント (U+D800)", CharacterDescriber.Describe(0xD800)); // 単独サロゲート
        Assert.Equal("不正なコードポイント (U+110000)", CharacterDescriber.Describe(0x110000)); // 範囲外
    }

    [Fact]
    public void DescribeAt_handles_lone_surrogates_without_throwing()
    {
        // 末尾の孤立した上位サロゲート、先頭の孤立した下位サロゲート。
        Assert.Equal("不正なサロゲート (U+D83D)", CharacterDescriber.DescribeAt("x\uD83D", 1));
        Assert.Equal("不正なサロゲート (U+DE00)", CharacterDescriber.DescribeAt("\uDE00x", 0));
    }

    [Fact]
    public void Cjk_extension_g_is_kanji()
        => Assert.Equal("漢字 " + char.ConvertFromUtf32(0x30000), CharacterDescriber.Describe(0x30000));

    [Theory]
    [InlineData(0x3042, "ひらがな あ")]   // あ
    [InlineData(0x30A2, "カタカナ ア")]   // ア
    [InlineData(0xFF71, "半角カタカナ ｱ")] // ｱ
    [InlineData(0xFF21, "全角 Ａ")]        // Ａ
    [InlineData(0x6F22, "漢字 漢")]        // 漢
    public void Japanese_categories(int cp, string expected)
        => Assert.Equal(expected, CharacterDescriber.Describe(cp));

    [Theory]
    [InlineData('A', "A")]
    [InlineData('1', "1")]
    [InlineData('z', "z")]
    public void Ascii_printable_is_bare(char c, string expected)
        => Assert.Equal(expected, CharacterDescriber.Describe(c));

    [Fact]
    public void Symbol_outside_known_groups_gets_codepoint()
        => Assert.Equal("★ (U+2605)", CharacterDescriber.Describe(0x2605));

    [Fact]
    public void Astral_emoji_via_describe_at_handles_surrogate()
    {
        string text = "x" + char.ConvertFromUtf32(0x1F600) + "y"; // 😀 = U+1F600（サロゲートペア）
        const string expected = "😀 (U+1F600)";
        // index 1,2 はサロゲートペア。どちらを指してもペア先頭の絵文字を説明する。
        Assert.Equal(expected, CharacterDescriber.DescribeAt(text, 1));
        Assert.Equal(expected, CharacterDescriber.DescribeAt(text, 2));
    }

    [Fact]
    public void DescribeAt_out_of_range_is_empty()
    {
        Assert.Equal("", CharacterDescriber.DescribeAt("abc", 3));
        Assert.Equal("", CharacterDescriber.DescribeAt("abc", -1));
        Assert.Equal("", CharacterDescriber.DescribeAt("", 0));
    }

    [Fact]
    public void DescribeAt_reads_codepoint_at_index()
        => Assert.Equal("全角スペース (U+3000)", CharacterDescriber.DescribeAt("a" + "　" + "b", 1));
}
