using yEdit.Core.Search;
using Xunit;

namespace yEdit.Core.Tests.Search;

public class TextSearcherTests
{
    private static TextSearcher Make(
        string pattern, bool matchCase = false, bool wholeWord = false, bool useRegex = false)
        => new(new SearchOptions(pattern, matchCase, wholeWord, useRegex));

    // ----- Count（照合件数） -----

    [Fact]
    public void Count_literal_is_case_insensitive_by_default()
        => Assert.Equal(3, Make("ab").Count("ab AB Ab xy"));

    [Fact]
    public void Count_with_match_case_distinguishes()
        => Assert.Equal(1, Make("ab", matchCase: true).Count("ab AB Ab xy"));

    [Fact]
    public void Count_whole_word_excludes_substrings()
        => Assert.Equal(2, Make("cat", wholeWord: true).Count("cat category cat-dog"));

    [Fact]
    public void Count_literal_treats_meta_chars_literally()
    {
        // リテラルモードでは "a.b" は正規表現メタでなくドットそのもの。
        Assert.Equal(1, Make("a.b").Count("a.b axb a-b"));
    }

    [Fact]
    public void Count_regex_mode_interprets_meta_chars()
    {
        // 正規表現モードでは "a.b" の "." は任意 1 文字。
        Assert.Equal(3, Make("a.b", useRegex: true).Count("a.b axb a-b"));
    }

    // ----- 妥当性（不正な正規表現・空パターン） -----

    [Fact]
    public void Invalid_regex_is_not_valid_and_reports_error()
    {
        var s = Make("(", useRegex: true);
        Assert.False(s.IsValid);
        Assert.NotNull(s.Error);
        Assert.Equal(0, s.Count("any text"));
    }

    [Fact]
    public void Empty_pattern_is_not_valid()
    {
        var s = Make("");
        Assert.False(s.IsValid);
        Assert.NotNull(s.Error);
    }

    // ----- FindNext（from 以降・折り返しなし） -----

    [Fact]
    public void FindNext_returns_first_hit_at_or_after_from()
    {
        var s = Make("ab");
        Assert.Equal(new MatchSpan(0, 2), s.FindNext("ab ab ab", 0));
        Assert.Equal(new MatchSpan(3, 2), s.FindNext("ab ab ab", 1));
        Assert.Equal(new MatchSpan(3, 2), s.FindNext("ab ab ab", 3));
    }

    [Fact]
    public void FindNext_does_not_wrap_and_returns_null_past_end()
    {
        var s = Make("ab");
        Assert.Null(s.FindNext("ab ab ab", 7));
    }

    [Fact]
    public void FindNext_clamps_negative_from_to_zero()
        => Assert.Equal(new MatchSpan(0, 2), Make("ab").FindNext("ab ab", -5));

    // ----- FindPrev（厳密前・折り返しなし） -----

    [Fact]
    public void FindPrev_returns_last_hit_strictly_before()
    {
        var s = Make("ab");
        Assert.Equal(new MatchSpan(0, 2), s.FindPrev("ab ab ab", 3));
        Assert.Equal(new MatchSpan(3, 2), s.FindPrev("ab ab ab", 6));
    }

    [Fact]
    public void FindPrev_is_strict_and_excludes_hit_at_before()
        => Assert.Equal(new MatchSpan(0, 2), Make("ab").FindPrev("ab ab ab", 3));

    [Fact]
    public void FindPrev_returns_null_when_no_hit_before()
        => Assert.Null(Make("ab").FindPrev("ab ab ab", 0));

    // ----- CJK オフセットは UTF-16 文字位置 -----

    [Fact]
    public void Offsets_are_utf16_char_positions_for_cjk()
        => Assert.Equal(new MatchSpan(5, 2), Make("世界").FindNext("こんにちは世界", 0));
}
