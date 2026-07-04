using yEdit.Core.Settings;

namespace yEdit.App.Settings.Tabs;

/// <summary>「読み上げ」タブ。優先するスクリーンリーダー（反映は再起動後）を扱う。</summary>
public sealed class SpeechSettingsTab : ISettingsTab
{
    public string Title => "読み上げ";

    // 表示順とインデックスを対応させる（0=NVDA, 1=PC-Talker）。Id は AppSettings.PreferredScreenReader の値。
    private static readonly (string Name, string Id)[] Readers =
    {
        ("NVDA（既定）", "nvda"), ("PC-Talker", "pctalker"),
    };

    private readonly ComboBox _preferred = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "優先するスクリーンリーダー",
    };

    public Control BuildPage()
    {
        foreach (var (name, _) in Readers) _preferred.Items.Add(name);

        var root = SettingsTabLayoutHelper.NewRoot();
        SettingsTabLayoutHelper.AddRow(root, 0, "優先するスクリーンリーダー(&R):", _preferred, tabBase: 0);

        // 反映は再起動後（起動時確定方針）。変更時の能動通知は MainForm.OpenSettings が行う。
        var note = new Label { Text = "この設定は yEdit の再起動後に有効になります。", AutoSize = true, TabIndex = 2 };
        root.Controls.Add(note, 0, 1);
        root.SetColumnSpan(note, 2);
        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        int sel = 0;
        for (int i = 0; i < Readers.Length; i++)
            if (Readers[i].Id == s.PreferredScreenReader) { sel = i; break; }
        _preferred.SelectedIndex = sel;
    }

    public void SaveTo(AppSettings r) => r.PreferredScreenReader = Readers[_preferred.SelectedIndex].Id;
}
