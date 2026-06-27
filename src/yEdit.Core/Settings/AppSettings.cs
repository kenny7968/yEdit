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

    /// <summary>自動バックアップ（クラッシュ復元）を有効にするか。</summary>
    public bool BackupEnabled { get; set; } = true;
    /// <summary>自動バックアップの間隔（秒）。</summary>
    public int BackupIntervalSeconds { get; set; } = 30;

    /// <summary>配色テーマ Id（AppearanceThemes.All の Id・既定は標準）。</summary>
    public string Theme { get; set; } = "default";

    /// <summary>編集画面で指定桁数の表示折り返しを行うか（保存内容は不変）。</summary>
    public bool WrapColumnEnabled { get; set; } = false;
    /// <summary>表示折り返しの桁数（半角換算・全角=2桁）。既定80・範囲10〜1000。</summary>
    public int WrapColumn { get; set; } = 80;

    /// <summary>最近開いたファイル（先頭が最新）。</summary>
    public List<string> RecentFiles { get; set; } = new();
}
