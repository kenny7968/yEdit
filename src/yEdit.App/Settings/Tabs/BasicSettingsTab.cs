using yEdit.App.Settings;
using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App.Settings.Tabs;

/// <summary>「基本」タブ。既定の文字コードと既定の改行を扱う。</summary>
public sealed class BasicSettingsTab : ISettingsTab
{
    public string Title => "基本";

    private static readonly IReadOnlyList<EncodingCatalog.EncodingOption> Encodings = EncodingCatalog.SelectableEncodings;
    private static readonly (string Name, int Id)[] Eols =
    {
        ("CRLF（Windows）", 0), ("LF（Unix）", 1), ("CR（旧 Mac）", 2),
    };

    private readonly ComboBox _encoding = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "既定の文字コード" };
    private readonly ComboBox _eol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "既定の改行" };

    public Control BuildPage()
    {
        foreach (var e in Encodings) _encoding.Items.Add(e.DisplayName);
        foreach (var (name, _) in Eols) _eol.Items.Add(name);

        var root = SettingsTabLayoutHelper.NewRoot();
        SettingsTabLayoutHelper.AddRow(root, 0, "既定の文字コード(&E):", _encoding, tabBase: 0);
        SettingsTabLayoutHelper.AddRow(root, 1, "既定の改行(&L):", _eol, tabBase: 2);
        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        int encSel = 0;
        for (int i = 0; i < Encodings.Count; i++)
            if (Encodings[i].CodePage == s.DefaultCodePage) { encSel = i; break; }
        _encoding.SelectedIndex = encSel;

        int eolSel = 0;
        for (int i = 0; i < Eols.Length; i++)
            if (Eols[i].Id == s.DefaultLineEnding) { eolSel = i; break; }
        _eol.SelectedIndex = eolSel;
    }

    public void SaveTo(AppSettings r)
    {
        r.DefaultCodePage = Encodings[_encoding.SelectedIndex].CodePage;
        r.DefaultLineEnding = Eols[_eol.SelectedIndex].Id;
    }
}
