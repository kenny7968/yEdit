namespace yEdit.Core.Settings;

/// <summary>永続化するアプリ設定（v0.1 最小キー）。今後マイルストーンで拡張。</summary>
// P7 撤去: PreferredScreenReader フィールドは削除（SR 二系統機構の実質死・優先 SR タブも削除済み）。
// 既存 settings.json に該当キーが残っていても System.Text.Json の既定挙動で未知プロパティは
// 無視されるため、データ移行不要。
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
    public int BackupIntervalSeconds { get; set; } = 300;

    /// <summary>配色テーマ Id（AppearanceThemes.All の Id・既定は標準）。</summary>
    public string Theme { get; set; } = "default";

    /// <summary>編集画面で指定桁数の表示折り返しを行うか（保存内容は不変）。</summary>
    public bool WrapColumnEnabled { get; set; } = false;
    /// <summary>表示折り返しの桁数（半角換算・全角=2桁）。既定80・範囲10〜1000。</summary>
    public int WrapColumn { get; set; } = 80;

    /// <summary>行頭に来てはいけない文字（追い出し対象）。空で無効。</summary>
    public string KinsokuLineStartChars { get; set; } = ")]}）］｝〕〉》」』】〗〙、。，．・：；！？";
    /// <summary>行末に来てはいけない文字（開き括弧。次行へ送る）。空で無効。</summary>
    public string KinsokuLineEndChars { get; set; } = "([{（［｛〔〈《「『【〖〘";
    /// <summary>行末にぶら下げ可能な文字（句読点）。空で無効。</summary>
    public string KinsokuHangChars { get; set; } = "、。，．";

    /// <summary>.csv ファイルを開いたとき自動的に CSV モードにするか（開く系のみ・grep ジャンプ除外）。</summary>
    public bool CsvAutoModeOnOpen { get; set; } = false;

    /// <summary>タブ幅（桁数・範囲 1〜16）。表示と禁則整形の両方が使う。</summary>
    public int TabWidth { get; set; } = 4;
    /// <summary>Tab キー入力をスペースにするか（既存のタブ文字は変換しない）。</summary>
    public bool TabsToSpaces { get; set; } = false;

    /// <summary>行番号マージンを表示するか。</summary>
    public bool ShowLineNumbers { get; set; } = false;
    /// <summary>現在行を強調表示するか（色はテーマから自動算出）。</summary>
    public bool HighlightCurrentLine { get; set; } = false;
    /// <summary>キャレットの太さ（px・範囲 1〜5）。弱視のキャレット視認性対策。</summary>
    public int CaretWidth { get; set; } = 1;
    /// <summary>空白（全半角）と改行記号を可視化するか。</summary>
    public bool ShowWhitespace { get; set; } = false;

    /// <summary>起動時にバックアップを復元するか確認する（false なら確認なしで全復元）。</summary>
    public bool ConfirmRestoreOnStartup { get; set; } = true;

    /// <summary>最近開いたファイル（先頭が最新）。</summary>
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>独立したコピーを返す（RecentFiles も複製）。設定ダイアログの編集用スナップショット。</summary>
    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.RecentFiles = new List<string>(RecentFiles);
        return c;
    }
}
