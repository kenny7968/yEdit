using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// MD-M-2: MarkdownPreviewForm 用の CSP HTTP header injector。
/// <see cref="CoreWebView2.WebResourceRequested"/> に登録して、preview 用 CSS 仮想パス
/// (<see cref="MarkdownRenderer.PreviewStylesheetPath"/>) へのリクエストを intercept し、
/// CSS 本体 + <c>Content-Security-Policy</c> ヘッダを含む response を返す。
/// (実 file は存在しない — <see cref="MarkdownRenderer.PreviewStylesheet"/> が single source of truth。)
/// <para>
/// preview 内の他リソース (画像等) は passthrough し、<c>SetVirtualHostNameToFolderMapping</c>
/// に任せる。WebView2 API では passthrough 時の response header 上書き手段が API 未提供のため、
/// それらリソースの CSP は HTML 内の <c>&lt;meta http-equiv&gt;</c> fallback で担保する。
/// </para>
/// <para>
/// <b>重要 (仕様):</b> <c>NavigateToString(html)</c> の初回 bootstrap は
/// <c>data:text/html;...</c> URI であり、<see cref="CoreWebView2.WebResourceRequested"/> は
/// <c>data:</c> URI に対しては発火しない (WebView2 仕様)。従って初回 HTML 文書自身の
/// CSP は <c>&lt;meta http-equiv&gt;</c> 側が唯一の防御。本 Injector は
/// <c>https://yedit.preview/*</c> 配下の sub-resource (styles.css および将来の絶対
/// URL 経由アクセス) にのみ効く。
/// </para>
/// <para>
/// App 層内部にのみ露出するため <c>internal sealed</c>。テストは
/// <c>InternalsVisibleTo yEdit.App.Tests</c> 経由でアクセスする。
/// </para>
/// </summary>
internal sealed class PreviewCspHeaderInjector
{
    // WebView2 の CreateWebResourceResponse は MIME sniff を持たないため
    // Content-Type を明示する。charset=utf-8 は PreviewStylesheet が ASCII 部分集合
    // であっても将来の多言語コメント混入時に備え固定。
    private const string StylesheetContentType = "text/css; charset=utf-8";

    private readonly CoreWebView2Environment _env;

    public PreviewCspHeaderInjector(CoreWebView2Environment env)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
    }

    /// <summary>
    /// <see cref="CoreWebView2"/> に URL filter と <see cref="CoreWebView2.WebResourceRequested"/>
    /// ハンドラを登録する。
    /// <para>
    /// 呼び出し順:
    /// <c>EnsureCoreWebView2Async(env)</c> → <c>Attach(core)</c> → <c>NavigateToString(html)</c>。
    /// filter/handler を装着してから NavigateToString しないと初回サブリソース要求
    /// (styles.css) で virtual CSS response を返す機会を失う。
    /// </para>
    /// <para>
    /// ハンドラ解除は WebView2 の <c>Dispose</c> と一緒に自動で行われる (Form 側の
    /// <c>Dispose(bool)</c> で <c>WebView2</c> コントロールが破棄されると event 購読も切れる)。
    /// </para>
    /// </summary>
    public void Attach(CoreWebView2 core)
    {
        // filter は preview 仮想ホスト配下の全リソース (styles.css だけでなく画像等の
        // passthrough 判定にも event 発火が必要 = All)。
        core.AddWebResourceRequestedFilter(
            "https://" + MarkdownRenderer.PreviewVirtualHost + "/*",
            CoreWebView2WebResourceContext.All
        );
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!IsPreviewStylesheetRequest(e.Request.Uri))
        {
            // passthrough: virtual host mapping (画像等) に任せる。Response 未設定なので
            // WebView2 が既定パスで解決する。CSP は meta http-equiv fallback で担保。
            return;
        }

        byte[] cssBytes = Encoding.UTF8.GetBytes(MarkdownRenderer.PreviewStylesheet);
        // MemoryStream は WebView2 側が Read するので writable=false + 可視 buffer 不要。
        var stream = new MemoryStream(cssBytes, writable: false);
        e.Response = _env.CreateWebResourceResponse(stream, 200, "OK", BuildResponseHeaders());
    }

    /// <summary>
    /// URL が preview 用 CSS 仮想パスへのリクエストか判定する pure helper。
    /// <list type="bullet">
    ///   <item>null / 空 / malformed URI → false (安全側: 誤 Response を装着しない)</item>
    ///   <item>scheme が <c>https</c> 以外 → false (preview 仮想ホストは https のみで張っている)</item>
    ///   <item>host が preview 仮想ホスト以外 → false</item>
    ///   <item>path 完全一致 (case-insensitive) → true</item>
    ///   <item>query / fragment は無視 (<see cref="Uri.AbsolutePath"/> が既に落とすため)</item>
    /// </list>
    /// </summary>
    internal static bool IsPreviewStylesheetRequest(string? requestUrl)
    {
        if (string.IsNullOrEmpty(requestUrl))
        {
            return false;
        }
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }
        if (!string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (
            !string.Equals(
                parsed.Host,
                MarkdownRenderer.PreviewVirtualHost,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }
        return string.Equals(
            parsed.AbsolutePath,
            MarkdownRenderer.PreviewStylesheetPath,
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// preview CSS response の HTTP headers を CRLF 区切り 1 本文字列で組み立てる。
    /// <see cref="CoreWebView2Environment.CreateWebResourceResponse"/> の <c>headers</c> 引数は
    /// この形式 (LF 単独では header が壊れる)。
    /// <para>
    /// 2 本のみ: <c>Content-Type</c> + <c>Content-Security-Policy</c>。CSP は
    /// <see cref="MarkdownRenderer.PreviewCspHeader"/> を参照 (meta 側と同一の
    /// single source of truth)。
    /// </para>
    /// </summary>
    internal static string BuildResponseHeaders()
    {
        return "Content-Type: "
            + StylesheetContentType
            + "\r\n"
            + "Content-Security-Policy: "
            + MarkdownRenderer.PreviewCspHeader;
    }
}
