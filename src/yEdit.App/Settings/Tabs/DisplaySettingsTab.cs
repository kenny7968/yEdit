using yEdit.App.Settings;
using yEdit.Core.Settings;

namespace yEdit.App.Settings.Tabs;

/// <summary>「表示」タブ。フォントと配色テーマを扱う。</summary>
public sealed class DisplaySettingsTab : ISettingsTab
{
    public string Title => "表示";

    private string _fontName = "";
    private float _fontSize = 12f;

    private readonly Label _fontLabel = new() { AutoSize = true };
    private readonly Button _fontButton = new() { Text = "変更(&F)...", AutoSize = true };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "配色テーマ" };

    public Control BuildPage()
    {
        foreach (var t in AppearanceThemes.All) _theme.Items.Add(t.DisplayName);
        _fontButton.Click += (_, _) => PickFont();

        var root = SettingsTabLayoutHelper.NewRoot();

        // フォント行: ラベル ＋ [現在表示 + 変更ボタン]。アクセスキー &F はボタン側に一本化。
        var fontLabelCol = new Label { Text = "フォント:", AutoSize = true, TabIndex = 0 };
        var fontPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 1 };
        fontPanel.Controls.Add(_fontLabel);
        fontPanel.Controls.Add(_fontButton);
        root.Controls.Add(fontLabelCol, 0, 0);
        root.Controls.Add(fontPanel, 1, 0);

        SettingsTabLayoutHelper.AddRow(root, 1, "配色(&C):", _theme, tabBase: 2);

        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _fontName = s.FontName;
        _fontSize = s.FontSize;
        UpdateFontLabel();
        _theme.SelectedIndex = IndexOfTheme(s.Theme);
    }

    public void SaveTo(AppSettings r)
    {
        r.FontName = _fontName;
        r.FontSize = _fontSize;
        r.Theme = AppearanceThemes.All[_theme.SelectedIndex].Id;
    }

    private static int IndexOfTheme(string id)
    {
        for (int i = 0; i < AppearanceThemes.All.Count; i++)
            if (AppearanceThemes.All[i].Id == id) return i;
        return 0;
    }

    private void UpdateFontLabel()
    {
        string desc = $"{_fontName}, {_fontSize:0.#} pt";
        _fontLabel.Text = desc;
        // 変更ボタンに現在値を載せ、フォーカス時に SR が「フォント変更 現在 …」と読めるようにする。
        _fontButton.AccessibleName = $"フォント変更 現在 {desc}";
    }

    private void PickFont()
    {
        // タブは Form ではないため、親ダイアログは FindForm() で取得する（フォーカスが親に戻る挙動を保つ）。
        using var dlg = new FontDialog { Font = SafeFont(), ShowEffects = false, FontMustExist = true };
        if (dlg.ShowDialog(_fontButton.FindForm()) != DialogResult.OK) return;
        _fontName = dlg.Font.Name;
        _fontSize = dlg.Font.Size;
        UpdateFontLabel();
    }

    private Font SafeFont()
    {
        try { return new Font(_fontName, _fontSize <= 0 ? 12f : _fontSize); }
        catch { return new Font(FontFamily.GenericMonospace, 12f); }
    }
}
