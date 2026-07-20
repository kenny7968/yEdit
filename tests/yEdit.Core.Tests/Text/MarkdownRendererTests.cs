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

    [Fact]
    public void Render_EscapesRawScriptTag()
    {
        var html = MarkdownRenderer.Render("<script>alert(1)</script>", "");
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Render_EscapesRawIframeTag()
    {
        var html = MarkdownRenderer.Render("<iframe src=\"evil\"></iframe>", "");
        Assert.DoesNotContain("<iframe", html);
    }

    [Fact]
    public void Render_EscapesInlineEventHandler()
    {
        var html = MarkdownRenderer.Render("<a href=\"x\" onclick=\"evil()\">y</a>", "");
        // <a> tag itself is escaped, so onclick can never reach the DOM as an attribute
        Assert.Contains("&lt;a href=", html);
        Assert.DoesNotContain("<a href=\"x\"", html);
    }

    [Fact]
    public void Render_PreservesMarkdownGeneratedTable()
    {
        var md = "| a | b |\n|---|---|\n| 1 | 2 |";
        var html = MarkdownRenderer.Render(md, "");
        Assert.Contains("<table", html);
        Assert.Matches(@"<td[^>]*>\s*1\s*</td>", html);
    }

    [Fact]
    public void Render_PreservesCodeBlock()
    {
        var md = "```csharp\nvar x = 1;\n```";
        var html = MarkdownRenderer.Render(md, "");
        Assert.Contains("<code", html);
        Assert.Contains("var x = 1;", html);
    }

    // ---------------------------------------------------------------------
    // MD-M-3: リンク URL スキーム whitelist (二層目の防御)
    //
    // CSP `default-src 'none'` により javascript: URI の実行は現状阻止できているが、
    // MD-M-2 で CSP を弱める瞬間に live XSS 化するため、renderer 段でも href の
    // scheme を http/https/mailto/相対/fragment に限定し、それ以外は href 属性を
    // まるごと drop する。表示テキスト (<a>...</a>) は残す。
    // ---------------------------------------------------------------------

    [Fact]
    public void Render_JavascriptScheme_DropsHrefAttribute()
    {
        var html = MarkdownRenderer.Render("[click](javascript:alert(1))", Base);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Contains("<a", html); // opening <a> は残す (Write の open タグ削除変異を kill)
        Assert.Contains(">click</a>", html);
    }

    [Fact]
    public void Render_VbscriptScheme_DropsHrefAttribute()
    {
        var html = MarkdownRenderer.Render("[x](vbscript:foo)", Base);
        Assert.DoesNotContain("href=\"vbscript:", html);
        Assert.Contains("<a", html);
        Assert.Contains(">x</a>", html);
    }

    [Fact]
    public void Render_DataScheme_DropsHrefAttribute()
    {
        var html = MarkdownRenderer.Render("[x](data:text/html,<script>)", Base);
        Assert.DoesNotContain("href=\"data:", html);
        Assert.Contains("<a", html);
        Assert.Contains(">x</a>", html);
    }

    [Fact]
    public void Render_FileScheme_DropsHrefAttribute()
    {
        // MD-M-5 補完: file:// URL は本タスクでも遮断。
        var html = MarkdownRenderer.Render("[x](file://server/share)", Base);
        Assert.DoesNotContain("href=\"file:", html);
        Assert.Contains("<a", html);
        Assert.Contains(">x</a>", html);
    }

    [Fact]
    public void Render_HttpUrl_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](http://example.com/)", Base);
        Assert.Contains("href=\"http://example.com/\"", html);
    }

    [Fact]
    public void Render_HttpsUrl_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](https://example.com/)", Base);
        Assert.Contains("href=\"https://example.com/\"", html);
    }

    [Fact]
    public void Render_MailtoUrl_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](mailto:a@b)", Base);
        Assert.Contains("href=\"mailto:a@b\"", html);
    }

    [Fact]
    public void Render_RelativeLink_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](path/to.md)", Base);
        Assert.Contains("href=\"path/to.md\"", html);
    }

    [Fact]
    public void Render_RootRelativeLink_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](/root/path)", Base);
        Assert.Contains("href=\"/root/path\"", html);
    }

    [Fact]
    public void Render_FragmentOnly_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](#section)", Base);
        Assert.Contains("href=\"#section\"", html);
    }

    [Fact]
    public void Render_CaseInsensitiveScheme_JavascriptUppercase_DropsHref()
    {
        var html = MarkdownRenderer.Render("[x](JAVASCRIPT:foo)", Base);
        Assert.DoesNotContain("href=\"JAVASCRIPT:", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Contains("<a", html);
        Assert.Contains(">x</a>", html);
    }

    [Fact]
    public void Render_ImageSrc_NotFiltered_ThisTaskScopedOnly()
    {
        // image の src filter は本タスク scope 外 (CSP img-src で別途遮断済み)。
        // 通常の http(s) 画像は素通しされることを機械固定して scope 境界を明示する。
        var html = MarkdownRenderer.Render("![alt](https://yedit.preview/img.png)", Base);
        Assert.Contains("<img", html);
        Assert.Contains("src=\"https://yedit.preview/img.png\"", html);
    }

    [Fact]
    public void Render_AngleBracketAutolinkJavascript_DropsHref()
    {
        // CommonMark autolink `<javascript:alert(1)>` は AutolinkInline 経路を通り、
        // LinkInlineRenderer とは別の AutolinkInlineRenderer で処理される。
        // 同じ scheme whitelist を適用して防御の穴を塞ぐ。
        var html = MarkdownRenderer.Render("<javascript:alert(1)>", Base);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Contains("<a", html); // opening <a> は残す (Write の open タグ削除変異を kill)
        Assert.Contains("</a>", html);
    }
}
