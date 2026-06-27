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
        def.SizeF = settings.FontSize > 0 ? settings.FontSize : 12f; // 破損設定で不可視にしない
        def.ForeColor = fore;
        def.BackColor = back;
        ed.StyleClearAll();          // 既定スタイルを全スタイルへ伝播（配色を一律に）
        ed.CaretForeColor = fore;    // キャレットも前景色に合わせて視認性を保つ
        // 選択範囲は前景/背景を反転して高コントラストにする（弱視で選択を視認しやすく）。
        ed.SelectionTextColor = back;
        ed.SelectionBackColor = fore;

        // 表示折り返し（指定桁・本文不変）。フォント適用後に半角幅を測るためここで最後に呼ぶ。
        ed.ApplyWrapColumn(settings.WrapColumnEnabled ? settings.WrapColumn : 0);
    }

    private static Color FromRgb(int rgb)
        => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
