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

    // MD-L-4: baseHref は空文字か PreviewBaseHref 定数以外を受け付けない (単一 caller の防御ガード)。
    [Fact]
    public void Render_Throws_ArgumentException_OnUnknownBaseHref()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            MarkdownRenderer.Render("x", "https://evil.com/")
        );
        Assert.Equal("baseHref", ex.ParamName);
    }

    [Fact]
    public void Render_Accepts_EmptyBaseHref()
    {
        var html = MarkdownRenderer.Render("x", "");
        Assert.Contains("<html", html);
        Assert.DoesNotContain("<base", html);
    }

    [Fact]
    public void Render_Accepts_PreviewBaseHref()
    {
        var html = MarkdownRenderer.Render("x", MarkdownRenderer.PreviewBaseHref);
        Assert.Contains("<html", html);
        Assert.Contains("<base href=\"https://yedit.preview/\">", html);
    }

    [Fact]
    public void PreviewBaseHref_ContainsOnly_HtmlAttrSafeChars()
    {
        // MD-L-4 の allow-list は "baseHref は PreviewBaseHref か空文字のみ" を保証するので
        // 直接 interpolate しても安全。ただし PreviewBaseHref 自体が将来 URL-safe 外の
        // 文字を持つとその前提が崩れる。ここで機械固定して回帰を防ぐ。
        Assert.DoesNotContain('"', MarkdownRenderer.PreviewBaseHref);
        Assert.DoesNotContain('<', MarkdownRenderer.PreviewBaseHref);
        Assert.DoesNotContain('>', MarkdownRenderer.PreviewBaseHref);
        Assert.DoesNotContain('&', MarkdownRenderer.PreviewBaseHref);
    }

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

    // ---------------------------------------------------------------------
    // MD-L-3: レンダー入力サイズ上限 (既定 4,000,000 文字 = 8 MB UTF-16 相当)。
    //
    // ネスト深度 / テーブルサイズの pre-scan は入れない (設計書: 入力サイズ
    // 4 MB で実質封じられる・保守負担を優先)。入口一箇所の cap のみで DoS を
    // 抑える。境界値 (ちょうど 4M 文字は許容 / +1 で throw) と const 値を
    // 機械固定する。
    // ---------------------------------------------------------------------

    [Fact]
    public void MaxMarkdownChars_IsFourMillion()
    {
        // const を書き換える PR は必ずこのテストを更新する = レビュー強制。
        Assert.Equal(4_000_000, MarkdownRenderer.MaxMarkdownChars);
    }

    [Fact]
    public void Render_Throws_DocumentTooLarge_WhenExceedingCap()
    {
        var md = new string('a', MarkdownRenderer.MaxMarkdownChars + 1);
        Assert.Throws<DocumentTooLargeException>(() => MarkdownRenderer.Render(md, ""));
    }

    [Fact]
    public void Render_Accepts_MaxSizedInput()
    {
        // 境界: ちょうど上限は素通し。cap を off-by-one で厳しくする回帰を防ぐ。
        var md = new string('a', MarkdownRenderer.MaxMarkdownChars);
        var html = MarkdownRenderer.Render(md, "");
        Assert.Contains("<html", html);
    }

    [Fact]
    public void Render_DocumentTooLargeException_ReportsAttemptedBytes()
    {
        // AttemptedBytes は UTF-16 バイト換算 (Length * 2)。TextBufferBuilder の
        // 「実格納バイト数」とは意味が違うが、DocumentTooLargeException の契約に
        // 揃える (テストが期待値を機械固定する)。
        var md = new string('a', MarkdownRenderer.MaxMarkdownChars + 1);
        var ex = Assert.Throws<DocumentTooLargeException>(() => MarkdownRenderer.Render(md, ""));
        Assert.Equal((long)(MarkdownRenderer.MaxMarkdownChars + 1) * 2L, ex.AttemptedBytes);
    }

    // ---------------------------------------------------------------------
    // MD-L-5: 拡張子ガード用の pure 判定ヘルパ。allowed リストは AppSettings.MarkdownExtensions
    // から渡す想定 (小文字ドット付き)。ヘルパ側は case-insensitive で判定する。
    //
    // 場所は AppSettings ではなく MarkdownRenderer に置く: MD 関連は既にここで中央集権
    // されており、pure static としてテストしやすい (依存: Path.GetExtension のみ)。
    // ---------------------------------------------------------------------

    private static readonly IReadOnlyList<string> DefaultMdExtensions = new List<string>
    {
        ".md",
        ".markdown",
        ".mkd",
        ".mkdn",
    };

    [Fact]
    public void IsMarkdownExtension_Md_Returns_True() =>
        Assert.True(MarkdownRenderer.IsMarkdownExtension("readme.md", DefaultMdExtensions));

    [Fact]
    public void IsMarkdownExtension_MD_UpperCase_Returns_True() =>
        Assert.True(MarkdownRenderer.IsMarkdownExtension("README.MD", DefaultMdExtensions));

    [Fact]
    public void IsMarkdownExtension_Markdown_Returns_True() =>
        Assert.True(MarkdownRenderer.IsMarkdownExtension("doc.markdown", DefaultMdExtensions));

    [Fact]
    public void IsMarkdownExtension_Txt_Returns_False() =>
        Assert.False(MarkdownRenderer.IsMarkdownExtension("readme.txt", DefaultMdExtensions));

    [Fact]
    public void IsMarkdownExtension_Null_Returns_False() =>
        Assert.False(MarkdownRenderer.IsMarkdownExtension(null, DefaultMdExtensions));

    [Fact]
    public void IsMarkdownExtension_Empty_Returns_False() =>
        Assert.False(MarkdownRenderer.IsMarkdownExtension(string.Empty, DefaultMdExtensions));

    [Fact]
    public void IsMarkdownExtension_NoExtension_Returns_False() =>
        Assert.False(MarkdownRenderer.IsMarkdownExtension("README", DefaultMdExtensions));

    [Fact]
    public void IsMarkdownExtension_EmptyAllowList_Returns_False()
    {
        // 常に拒否契約: 空リストは「全プレビュー封鎖」に倒す (誤操作で気付ける安全側)。
        // AppSettings 側の xmldoc と対応。
        Assert.False(MarkdownRenderer.IsMarkdownExtension("readme.md", new List<string>()));
    }
}
