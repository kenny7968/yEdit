namespace yEdit.App;

/// <summary>
/// MarkdownPreviewForm の WebView2 ナビゲーション対象 URI を「preview 内で許可」
/// 「既定ブラウザ/アプリで開く」「阻止」の 3 クラスに分類する純粋ロジック。
/// <para>
/// WebView2 の <c>NavigationStarting</c> / <c>NewWindowRequested</c> ハンドラから
/// 呼ばれる。Process.Start の副作用や WebView2 依存を持たないため単体テスト可能。
/// </para>
/// <para>
/// 攻撃面(audit doc MD-M-1 / MD-M-5):
/// <list type="bullet">
///   <item>外部 http/https は in-frame ナビゲートさせず既定ブラウザへ逃がす
///     (プレビュー窓の title を保ったまま偽サイトが表示される phishing 防止)。</item>
///   <item><c>file://</c> UNC は Windows が SMB 経由で NTLM 認証を通してしまうため
///     全面 Block (NTLMv2 challenge/response のオフラインクラック用漏洩防止)。</item>
///   <item><c>javascript:</c>/<c>vbscript:</c>/<c>data:</c> 等の script scheme は
///     全面 Block (renderer 段の SafeLinkExtension を弱めた瞬間の live XSS 二層防御)。</item>
/// </list>
/// </para>
/// </summary>
public static class PreviewNavigationPolicy
{
    /// <summary>ナビゲーション分類。</summary>
    public enum Classification
    {
        /// <summary>preview 内で許可 (about:blank + https://yedit.preview/*)。</summary>
        AllowIntra,

        /// <summary>既定ブラウザ/アプリで開く安全 scheme (http/https 非 preview, mailto)。</summary>
        LaunchExternal,

        /// <summary>阻止 (file://, ftp://, data:, javascript:, vbscript:, その他 unknown)。</summary>
        Block,
    }

    /// <summary>
    /// WebView2 の navigation 対象 URI を 3 クラスに分類する。
    /// </summary>
    /// <param name="uri">WebView2 の <c>NavigationStartingEventArgs.Uri</c> 相当の絶対 URI 文字列。</param>
    /// <returns>分類結果。詳細は <see cref="Classification"/> 参照。</returns>
    public static Classification Classify(string? uri)
    {
        // Task 3 test commit: 型 surface のみ先行公開。実装は次コミットで埋める。
        throw new NotImplementedException();
    }
}
