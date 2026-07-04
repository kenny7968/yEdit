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

        // タブ・キャレット・空白可視化（設定 2026-07-04）。
        ed.TabWidth = settings.TabWidth;
        ed.UseTabs = !settings.TabsToSpaces;   // 新規 Tab 入力にのみ効く（既存のタブ文字は変換しない）
        ed.CaretWidth = Math.Clamp(settings.CaretWidth, 1, 5);
        // 現在行強調は要素色のアルファで ON/OFF を表現する（CaretLineVisible は Scintilla 5 で旧形式。
        // SC_ELEMENT_CARET_LINE_BACK はアルファ 0 を「未設定＝非表示」と扱う）。色はテーマから自動算出
        // （カスタム色 UI なし・設計合意）。
        ed.CaretLineBackColor = settings.HighlightCurrentLine ? Blend(back, fore, 0.12) : Color.Transparent;
        ed.ViewWhitespace = settings.ShowWhitespace ? WhitespaceMode.VisibleAlways : WhitespaceMode.Invisible;
        ed.ViewEol = settings.ShowWhitespace;

        // 行番号は StyleClearAll の後（行番号スタイル確定後に幅を測る）・折り返しの前
        // （折返し右マージンの計算が左マージン群の幅を含むため）。
        ed.ShowLineNumbers = settings.ShowLineNumbers;

        // 表示折り返し（指定桁・本文不変）。フォント適用後に半角幅を測るためここで最後に呼ぶ。
        ed.ApplyWrapColumn(settings.WrapColumnEnabled ? settings.WrapColumn : 0);
    }

    /// <summary>base に accent を ratio(0..1) だけ混ぜた色。現在行強調の自動算出用（全 4 テーマで破綻しない淡い強調）。</summary>
    private static Color Blend(Color baseColor, Color accent, double ratio) => Color.FromArgb(255,
        (int)Math.Round(baseColor.R + (accent.R - baseColor.R) * ratio),
        (int)Math.Round(baseColor.G + (accent.G - baseColor.G) * ratio),
        (int)Math.Round(baseColor.B + (accent.B - baseColor.B) * ratio));

    private static Color FromRgb(int rgb)
        => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
