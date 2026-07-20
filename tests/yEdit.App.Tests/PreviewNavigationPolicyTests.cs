namespace yEdit.App.Tests;

/// <summary>
/// audit doc `docs/plans/2026-07-19-security-hardening-medium-low.md` §MD-M-1 / §MD-M-5。
/// MarkdownPreviewForm の WebView2 ナビゲーション対象 URI 分類の期待挙動を機械固定する。
///
/// 分類ルール:
///   - null / 空 → Block
///   - about:blank (大小区別なし) → AllowIntra (NavigateToString の初回 origin)
///   - https://yedit.preview/* (大小区別なし) → AllowIntra (仮想ホスト経由の相対リソース)
///   - http/https 非 preview → LaunchExternal (既定ブラウザへ逃がす)
///   - mailto → LaunchExternal
///   - file / ftp / data / javascript / vbscript / その他 → Block
///     * file:// は特に MD-M-5 の NTLM 漏出対策の主眼
///     * javascript:/vbscript:/data: は MD-M-3 (renderer 段) の二層目
///
/// MarkdownPreviewForm 本体の event handler 配線は WebView2 runtime 依存で unit test 不可。
/// PR description に L5 manual smoke test 項目を書き残すことで代替する。
/// </summary>
public class PreviewNavigationPolicyTests
{
    [Fact]
    public void Classify_Null_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify(null)
        );

    [Fact]
    public void Classify_Empty_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("")
        );

    [Fact]
    public void Classify_AboutBlank_ReturnsAllowIntra() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.AllowIntra,
            PreviewNavigationPolicy.Classify("about:blank")
        );

    [Fact]
    public void Classify_AboutBlank_UpperCase_ReturnsAllowIntra() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.AllowIntra,
            PreviewNavigationPolicy.Classify("ABOUT:BLANK")
        );

    [Fact]
    public void Classify_HttpsPreviewHost_ReturnsAllowIntra() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.AllowIntra,
            PreviewNavigationPolicy.Classify("https://yedit.preview/foo/bar.md")
        );

    [Fact]
    public void Classify_HttpsPreviewHost_UpperCase_ReturnsAllowIntra() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.AllowIntra,
            PreviewNavigationPolicy.Classify("HTTPS://YEDIT.PREVIEW/x")
        );

    [Fact]
    public void Classify_HttpsNonPreviewHost_ReturnsLaunchExternal() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.LaunchExternal,
            PreviewNavigationPolicy.Classify("https://example.com/")
        );

    [Fact]
    public void Classify_HttpNonPreviewHost_ReturnsLaunchExternal() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.LaunchExternal,
            PreviewNavigationPolicy.Classify("http://example.com/")
        );

    /// <summary>
    /// http は preview として認めない (strict)。https 経由の VirtualHost マッピングだけを preview とする。
    /// http://yedit.preview/... が誤って allow-intra 化されないことを機械固定。
    /// </summary>
    [Fact]
    public void Classify_HttpPreviewHost_ReturnsLaunchExternal() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.LaunchExternal,
            PreviewNavigationPolicy.Classify("http://yedit.preview/")
        );

    [Fact]
    public void Classify_MailtoUrl_ReturnsLaunchExternal() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.LaunchExternal,
            PreviewNavigationPolicy.Classify("mailto:a@b.example")
        );

    /// <summary>MD-M-5 の主眼: file:// UNC の NTLM ハッシュ漏出を navigation 段で確実に block。</summary>
    [Fact]
    public void Classify_FileUnc_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("file://server/share/x")
        );

    /// <summary>ローカル file:// も preview モーダル内のローカルファイル表示を防ぐため block。</summary>
    [Fact]
    public void Classify_FileLocal_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("file:///C:/secret.txt")
        );

    [Fact]
    public void Classify_JavascriptScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("javascript:alert(1)")
        );

    [Fact]
    public void Classify_VbscriptScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("vbscript:msgbox(1)")
        );

    [Fact]
    public void Classify_DataScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("data:text/html,<script>alert(1)</script>")
        );

    [Fact]
    public void Classify_FtpScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("ftp://server/x")
        );

    /// <summary>allow list 方式なので未知 scheme は既定 Block (safe by default)。</summary>
    [Fact]
    public void Classify_UnknownScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("foo:bar")
        );

    /// <summary>Uri.TryCreate が失敗する malformed 入力は Block (safe by default)。</summary>
    [Fact]
    public void Classify_MalformedUri_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("not a url")
        );
}
