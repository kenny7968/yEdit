using ScintillaNET;
using yEdit.Core.Settings;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>設定（フォント・配色テーマ）をエディタへ適用する。Core のテーマ RGB を Color へ変換する境界。</summary>
public static class EditorAppearance
{
    public static void Apply(ScintillaHost ed, AppSettings settings)
    {
        var theme = AppearanceThemes.ById(settings.Theme);
        Color fore = FromRgb(theme.ForeRgb);
        Color back = FromRgb(theme.BackRgb);

        var def = ed.Styles[Style.Default];
        def.Font = settings.FontName;
        def.SizeF = settings.FontSize;
        def.ForeColor = fore;
        def.BackColor = back;
        ed.StyleClearAll();          // 既定スタイルを全スタイルへ伝播（配色を一律に）
        ed.CaretForeColor = fore;    // キャレットも前景色に合わせて視認性を保つ
    }

    private static Color FromRgb(int rgb)
        => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
