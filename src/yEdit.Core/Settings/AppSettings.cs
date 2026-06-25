namespace yEdit.Core.Settings;

/// <summary>永続化するアプリ設定（v0.1 最小キー）。今後マイルストーンで拡張。</summary>
public sealed class AppSettings
{
    public string FontName { get; set; } = "ＭＳ ゴシック";
    public float FontSize { get; set; } = 12f;
    public int WindowWidth { get; set; } = 960;
    public int WindowHeight { get; set; } = 640;
    /// <summary>新規ファイル・既定の保存文字コード（コードページ）。</summary>
    public int DefaultCodePage { get; set; } = 65001;
    /// <summary>新規ファイルの既定改行（0=CRLF,1=LF,2=CR）。</summary>
    public int DefaultLineEnding { get; set; } = 0;
}
