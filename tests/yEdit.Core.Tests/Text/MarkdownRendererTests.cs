using yEdit.Core.Text;

namespace yEdit.Core.Tests.Text;

public class MarkdownRendererTests
{
    private const string Base = "https://yedit.preview/";

    [Fact]
    public void Heading_becomes_h1() =>
        Assert.Contains("<h1", MarkdownRenderer.Render("# 見出し", Base));

    [Fact]
    public void Bold_becomes_strong() =>
        Assert.Contains("<strong>太字</strong>", MarkdownRenderer.Render("**太字**", Base));

    [Fact]
    public void Inline_code_becomes_code() =>
        Assert.Contains("<code>x</code>", MarkdownRenderer.Render("`x`", Base));

    [Fact]
    public void Fenced_code_becomes_pre() =>
        Assert.Contains("<pre><code", MarkdownRenderer.Render("```\ncode\n```", Base));

    [Fact]
    public void Pipe_table_becomes_table()
    {
        string md = "| A | B |\n|---|---|\n| 1 | 2 |";
        Assert.Contains("<table", MarkdownRenderer.Render(md, Base));
    }

    [Fact]
    public void Text_special_chars_are_escaped() =>
        Assert.Contains("1 &lt; 2 &amp; 3", MarkdownRenderer.Render("1 < 2 & 3", Base));

    [Fact]
    public void Base_href_is_injected() =>
        Assert.Contains(
            "<base href=\"https://yedit.preview/\">",
            MarkdownRenderer.Render("x", Base)
        );

    [Fact]
    public void Document_declares_utf8_charset() =>
        Assert.Contains("charset=\"utf-8\"", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Null_markdown_does_not_throw() =>
        Assert.Contains("<html", MarkdownRenderer.Render(null, Base));

    [Fact]
    public void Empty_base_href_omits_base_tag() =>
        Assert.DoesNotContain("<base", MarkdownRenderer.Render("x", ""));

    [Fact]
    public void Base_href_is_attribute_escaped() =>
        Assert.Contains("<base href=\"a&amp;b&quot;c\">", MarkdownRenderer.Render("x", "a&b\"c"));

    [Fact]
    public void Document_includes_csp_blocking_scripts()
    {
        string html = MarkdownRenderer.Render("x", Base);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("default-src 'none'", html);
    }
}
