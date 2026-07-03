using yEdit.App.Settings;
using yEdit.Core.Settings;

namespace yEdit.App.Settings.Tabs;

/// <summary>「編集」タブ。表示折り返しの ON/OFF と桁数を扱う。</summary>
public sealed class EditSettingsTab : ISettingsTab
{
    public string Title => "編集";

    private readonly CheckBox _wrapEnabled = new() { Text = "指定文字数で折り返す(&W)", AutoSize = true };
    private readonly NumericUpDown _wrapColumn = new()
    {
        Minimum = 10, Maximum = 1000, Width = 100, AccessibleName = "折り返し桁数",
    };

    public Control BuildPage()
    {
        _wrapEnabled.CheckedChanged += (_, _) => _wrapColumn.Enabled = _wrapEnabled.Checked;

        var root = SettingsTabLayoutHelper.NewRoot();

        // 1 行目: チェックボックス（ラベル兼用）。TabIndex=0。
        _wrapEnabled.TabIndex = 0;
        root.Controls.Add(_wrapEnabled, 0, 0);

        // 2 行目: 「折り返し桁数(&K):」ラベル ＋ NumericUpDown。
        var wrapPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 1 };
        var wrapLbl = new Label { Text = "折り返し桁数(&K):", AutoSize = true, TabIndex = 1, Anchor = AnchorStyles.Left };
        _wrapColumn.TabIndex = 2;
        wrapPanel.Controls.Add(wrapLbl);
        wrapPanel.Controls.Add(_wrapColumn);
        root.Controls.Add(wrapPanel, 1, 0);

        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _wrapEnabled.Checked = s.WrapColumnEnabled;
        _wrapColumn.Value = Math.Clamp(s.WrapColumn, (int)_wrapColumn.Minimum, (int)_wrapColumn.Maximum);
        _wrapColumn.Enabled = _wrapEnabled.Checked;   // 初期状態でも ON/OFF を反映
    }

    public void SaveTo(AppSettings r)
    {
        r.WrapColumnEnabled = _wrapEnabled.Checked;
        r.WrapColumn = (int)_wrapColumn.Value;
    }
}
