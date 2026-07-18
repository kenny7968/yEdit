using Markdig;

namespace yEdit.Core.Text;

/// <summary>マークダウン本文を、プレビュー表示用の完結した HTML 文書へ変換する。</summary>
public static class MarkdownRenderer
{
    /// <summary>プレビュー用の仮想ホスト名（相対リソース解決の基準。App 側のマッピングと一致させる）。</summary>
    public const string PreviewVirtualHost = "yedit.preview";

    /// <summary>プレビュー HTML の base href（PreviewVirtualHost への https URL）。</summary>
    public const string PreviewBaseHref = "https://" + PreviewVirtualHost + "/";

    // CommonMark + GFM 拡張（表・チェックリスト・自動リンク等）。スレッドセーフなので使い回す。
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// markdown を HTML 化し、&lt;base href&gt;・charset・読みやすい CSS を備えた
    /// 完結した HTML 文書文字列を返す。baseHref は相対リソース解決の基準 URL。
    /// </summary>
    public static string Render(string? markdown, string baseHref)
    {
        string body = Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
        string baseTag = string.IsNullOrEmpty(baseHref)
            ? string.Empty
            : $"<base href=\"{HtmlAttr(baseHref)}\">";
        return $$"""
            <!DOCTYPE html>
            <html lang="ja">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https://{{PreviewVirtualHost}} data:; media-src https://{{PreviewVirtualHost}}; style-src 'unsafe-inline' https://{{PreviewVirtualHost}}; font-src https://{{PreviewVirtualHost}} data:;">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            {{baseTag}}
            <style>{{Css}}</style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }

    private static string HtmlAttr(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    private const string Css = """
        body { font-family: "Segoe UI", "Meiryo", sans-serif; line-height: 1.6;
               max-width: 900px; margin: 0 auto; padding: 24px; color: #1f2328; }
        h1, h2 { border-bottom: 1px solid #d0d7de; padding-bottom: .3em; }
        code { background: #afb8c133; padding: .2em .4em; border-radius: 6px;
               font-family: "Consolas", monospace; }
        pre { background: #f6f8fa; padding: 16px; border-radius: 6px; overflow: auto; }
        pre code { background: none; padding: 0; }
        table { border-collapse: collapse; }
        th, td { border: 1px solid #d0d7de; padding: 6px 13px; }
        blockquote { color: #57606a; border-left: .25em solid #d0d7de;
                     padding: 0 1em; margin: 0; }
        img { max-width: 100%; }
        a { color: #0969da; }
        """;
}
