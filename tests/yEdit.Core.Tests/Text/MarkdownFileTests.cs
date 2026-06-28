using yEdit.Core.Text;

namespace yEdit.Core.Tests.Text;

public class MarkdownFileTests
{
    [Theory]
    [InlineData("a.md", true)]
    [InlineData("a.MD", true)]
    [InlineData(@"C:\dir\readme.Md", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.markdown", false)] // 要件は .md のみ。.markdown は対象外
    [InlineData("noext", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsMarkdownPath_detects_md(string? path, bool expected)
        => Assert.Equal(expected, MarkdownFile.IsMarkdownPath(path));
}
