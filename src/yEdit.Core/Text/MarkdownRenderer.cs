using Markdig;

namespace yEdit.Core.Text;

/// <summary>マークダウン本文を、プレビュー表示用の完結した HTML 文書へ変換する。</summary>
public static class MarkdownRenderer
{
    /// <summary>プレビュー用の仮想ホスト名（相対リソース解決の基準。App 側のマッピングと一致させる）。</summary>
    public const string PreviewVirtualHost = "yedit.preview";

    /// <summary>プレビュー HTML の base href（PreviewVirtualHost への https URL）。</summary>
    public const string PreviewBaseHref = "https://" + PreviewVirtualHost + "/";

    /// <summary>
    /// MD-L-3: レンダー入力サイズ上限 (4,000,000 文字 = 8 MB UTF-16 相当)。
    /// ネスト深度 / テーブルサイズの pre-scan は入れず、入口一箇所の cap で
    /// パーサ側の pathological な計算量爆発を封じる (設計書: MD-L-3)。
    /// </summary>
    public const int MaxMarkdownChars = 4_000_000;

    // CommonMark + GFM 拡張（表・チェックリスト・自動リンク等）。スレッドセーフなので使い回す。
    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

    private static MarkdownPipeline BuildPipeline()
    {
        // CSP との二重防御: raw HTML (script/iframe/on* 等) をパーサ段で無効化。
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        // MD-M-3: リンク URL scheme whitelist (二層目の防御)。CSP を弱めた瞬間の
        // live XSS を防ぐため javascript:/vbscript:/data:/file: 等は href を drop する。
        builder.Extensions.AddIfNotAlready<SafeLinkExtension>();
        return builder.Build();
    }

    /// <summary>
    /// markdown を HTML 化し、&lt;base href&gt;・charset・読みやすい CSS を備えた
    /// 完結した HTML 文書文字列を返す。baseHref は相対リソース解決の基準 URL で、
    /// <see cref="PreviewBaseHref"/> 定数か空文字のみ受け付ける (MD-L-4)。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// baseHref が空文字でも <see cref="PreviewBaseHref"/> 定数でもない場合。
    /// 単一 caller の防御的ガードで、将来 caller が増えた際の混入を fail-fast で止める。
    /// </exception>
    /// <exception cref="DocumentTooLargeException">
    /// markdown が <see cref="MaxMarkdownChars"/> を超える場合 (MD-L-3)。
    /// caller (MainForm.ShowMarkdownPreview) が捕えてユーザに MessageBox で提示する。
    /// </exception>
    public static string Render(string? markdown, string baseHref)
    {
        // MD-L-4: baseHref は空文字か PreviewBaseHref 定数のみを受け付ける allow-list ガード。
        // 属性エスケープを万一漏らした場合の被害を封じるため、定数比較でホワイトリスト化する。
        if (baseHref != string.Empty && baseHref != PreviewBaseHref)
        {
            throw new ArgumentException(
                $"baseHref must be either empty or MarkdownRenderer.PreviewBaseHref (\"{PreviewBaseHref}\").",
                nameof(baseHref)
            );
        }

        // MD-L-3: 入力サイズ cap (4M 文字 = 8 MB UTF-16 相当)。ネスト深度 / テーブル
        // サイズの pre-scan は入れず、入口一箇所で Markdig への pathological な
        // 入力を封じる。null は既存の ?? string.Empty で吸収されるため対象外。
        if (markdown != null && markdown.Length > MaxMarkdownChars)
        {
            long attemptedBytes = (long)markdown.Length * 2L;
            throw new DocumentTooLargeException(
                attemptedBytes,
                $"マークダウン本文が上限を超えました({markdown.Length:N0}/{MaxMarkdownChars:N0} 文字)"
            );
        }

        string body = Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
        string baseTag = string.IsNullOrEmpty(baseHref)
            ? string.Empty
            : $"<base href=\"{baseHref}\">";
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
