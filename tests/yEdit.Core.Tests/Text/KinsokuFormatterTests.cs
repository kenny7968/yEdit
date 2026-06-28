using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class KinsokuFormatterTests
{
    // 禁則無し（空セット）でのテスト用ヘルパ。
    private static string Wrap(string text, int columns, string eol = "\n")
        => KinsokuFormatter.Format(text, columns, "", "", "", eol);

    [Fact]
    public void Empty_or_zero_columns_returns_as_is()
    {
        Assert.Equal("", KinsokuFormatter.Format("", 80, "", "", "", "\n"));
        Assert.Equal("abc", KinsokuFormatter.Format("abc", 0, "", "", "", "\n"));
    }

    [Fact]
    public void Short_line_is_unchanged()
        => Assert.Equal("あいう", Wrap("あいう", 80));

    [Fact]
    public void Wraps_halfwidth_at_columns()
        // 10桁: "abcdefghij" でちょうど、次の "k" で折る
        => Assert.Equal("abcdefghij\nk", Wrap("abcdefghijk", 10));

    [Fact]
    public void Fullwidth_counts_as_two_columns()
        // 4桁 = 全角2文字。3文字目で折る
        => Assert.Equal("ああ\nあ", Wrap("あああ", 4));

    [Fact]
    public void Existing_newlines_preserved_only_long_lines_split()
        // 既存改行(abc|def)は保持しつつ、各長行を2桁で折る → "ab|c" + "de|f"
        => Assert.Equal("ab\nc\nde\nf", Wrap("abc\ndef", 2));

    [Fact]
    public void Preserves_crlf_terminator_and_inserts_given_eol()
        => Assert.Equal("ab\r\ncd\r\ne", KinsokuFormatter.Format("ab\r\ncde", 2, "", "", "", "\r\n"));

    [Fact]
    public void Surrogate_pair_not_split()
    {
        // 𩸽(U+29E3D, 幅2) を含む。columns=2 なら1文字ずつ。ペアを割らない。
        string s = "\U00029E3D\U00029E3D";
        Assert.Equal("\U00029E3D\n\U00029E3D", Wrap(s, 2));
    }

    [Fact]
    public void Oversized_single_char_still_progresses()
        // columns=1 でも全角1文字は1行に出して前進（無限ループしない）
        => Assert.Equal("あ\nい", Wrap("あい", 1));

    [Fact]
    public void Tab_expands_to_tabwidth_and_wraps()
        // tabWidth=8: 行頭タブで8桁に達し、続く "x" は次行へ折れる
        => Assert.Equal("\t\nx", Wrap("\tx", 8));

    [Fact]
    public void Lf_terminator_roundtrip_only_long_line_splits()
        // 既存の \n は保持。短行 "ab" は無変更、長行 "cdef" のみ2桁で分割
        => Assert.Equal("ab\ncd\nef", Wrap("ab\ncdef", 2));

    [Fact]
    public void Cr_terminator_preserved_and_inserts_given_eol()
        // 既存の \r 終端は保持し、挿入改行は eol(="\n")。長行 "cde" は2桁で分割
        => Assert.Equal("ab\rcd\ne", Wrap("ab\rcde", 2));
}
