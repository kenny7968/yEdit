using yEdit.Core.Reading;
using Xunit;

namespace yEdit.Core.Tests.Reading;

public class EmptyLineDetectorTests
{
    // ---- 空行（長さ0の行）上のキャレット → true ----

    [Fact]
    public void Empty_document_is_empty_line()
        => Assert.True(EmptyLineDetector.IsCaretOnEmptyLine("", 0));

    [Fact]
    public void Empty_line_between_lf_lines()
        => Assert.True(EmptyLineDetector.IsCaretOnEmptyLine("a\n\nb", 2));

    [Fact]
    public void Empty_line_between_crlf_lines()
        => Assert.True(EmptyLineDetector.IsCaretOnEmptyLine("a\r\n\r\nb", 3));

    [Fact]
    public void Empty_line_between_cr_lines()
        => Assert.True(EmptyLineDetector.IsCaretOnEmptyLine("a\r\rb", 2));

    [Fact]
    public void Empty_first_line()
        => Assert.True(EmptyLineDetector.IsCaretOnEmptyLine("\nabc", 0));

    [Fact]
    public void Document_end_after_trailing_lf()
        => Assert.True(EmptyLineDetector.IsCaretOnEmptyLine("abc\n", 4));

    [Fact]
    public void Document_end_after_trailing_crlf()
        => Assert.True(EmptyLineDetector.IsCaretOnEmptyLine("abc\r\n", 5));

    // ---- 空でない行・行の途中 → false ----

    [Fact]
    public void Start_of_nonempty_line_is_not_empty()
        => Assert.False(EmptyLineDetector.IsCaretOnEmptyLine("a\nb", 2));

    [Fact]
    public void End_of_nonempty_line_is_not_empty()
        => Assert.False(EmptyLineDetector.IsCaretOnEmptyLine("ab\ncd", 2));

    [Fact]
    public void Middle_of_line_is_not_empty()
        => Assert.False(EmptyLineDetector.IsCaretOnEmptyLine("abc", 1));

    [Fact]
    public void Whitespace_only_line_is_not_empty()
        => Assert.False(EmptyLineDetector.IsCaretOnEmptyLine("a\n \nb", 2));

    [Fact]
    public void Document_end_without_trailing_newline_is_not_empty()
        => Assert.False(EmptyLineDetector.IsCaretOnEmptyLine("abc", 3));

    [Fact]
    public void Empty_line_between_mixed_lf_cr_is_empty()
        => Assert.True(EmptyLineDetector.IsCaretOnEmptyLine("a\n\rb", 2));

    // ---- 防御（総関数）: 不正位置では false（例外を投げない） ----

    [Fact]
    public void Inside_crlf_pair_is_not_empty_line()
        => Assert.False(EmptyLineDetector.IsCaretOnEmptyLine("a\r\n\r\nb", 4));

    [Fact]
    public void Inside_first_crlf_pair_is_not_empty_line()
        => Assert.False(EmptyLineDetector.IsCaretOnEmptyLine("a\r\n\r\nb", 2));

    [Theory]
    [InlineData("abc", -1)]
    [InlineData("abc", 4)]
    [InlineData("", 1)]
    public void Out_of_range_caret_is_false(string text, int caret)
        => Assert.False(EmptyLineDetector.IsCaretOnEmptyLine(text, caret));
}
