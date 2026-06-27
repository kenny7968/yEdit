using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// 設定ダイアログ（モーダル・アクセシブル）。フォント・配色テーマ・既定の文字コード/改行を編集する。
/// 配色はテーマプリセットのみ（合意）。OK 後に呼び出し側が公開プロパティを読み、適用＋永続化する。
/// ラベルにアクセスキーを付け、TabIndex でラベル→コントロールの順にして Alt+キーで移れるようにする。
/// </summary>
public sealed class SettingsDialog : Form
{
    private string _fontName;
    private float _fontSize;

    private readonly Label _fontLabel = new() { AutoSize = true }; // Text をそのまま SR に読ませる
    private readonly Button _fontButton = new() { Text = "変更(&F)...", AutoSize = true };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "配色テーマ" };
    private readonly ComboBox _encoding = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "既定の文字コード" };
    private readonly ComboBox _eol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "既定の改行" };
    private readonly CheckBox _wrapEnabled = new() { Text = "指定文字数で折り返す(&W)", AutoSize = true };
    private readonly NumericUpDown _wrapColumn = new()
    {
        Minimum = 10, Maximum = 1000, Width = 100, AccessibleName = "折り返し桁数",
    };

    private static readonly IReadOnlyList<EncodingCatalog.EncodingOption> Encodings = EncodingCatalog.SelectableEncodings;
    private static readonly (string Name, int Id)[] Eols =
    {
        ("CRLF（Windows）", 0), ("LF（Unix）", 1), ("CR（旧 Mac）", 2),
    };

    public SettingsDialog(AppSettings s)
    {
        _fontName = s.FontName;
        _fontSize = s.FontSize;

        Text = "設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        foreach (var t in AppearanceThemes.All) _theme.Items.Add(t.DisplayName);
        _theme.SelectedIndex = IndexOfTheme(s.Theme);

        int encSel = 0;
        for (int i = 0; i < Encodings.Count; i++)
        {
            _encoding.Items.Add(Encodings[i].DisplayName);
            if (Encodings[i].CodePage == s.DefaultCodePage) encSel = i;
        }
        _encoding.SelectedIndex = encSel;

        int eolSel = 0;
        for (int i = 0; i < Eols.Length; i++)
        {
            _eol.Items.Add(Eols[i].Name);
            if (Eols[i].Id == s.DefaultLineEnding) eolSel = i;
        }
        _eol.SelectedIndex = eolSel;

        _wrapEnabled.Checked = s.WrapColumnEnabled;
        _wrapColumn.Value = Math.Clamp(s.WrapColumn, (int)_wrapColumn.Minimum, (int)_wrapColumn.Maximum);
        _wrapColumn.Enabled = _wrapEnabled.Checked;       // OFF 時は桁数入力を無効化
        _wrapEnabled.CheckedChanged += (_, _) => _wrapColumn.Enabled = _wrapEnabled.Checked;

        _fontButton.Click += (_, _) => PickFont();
        UpdateFontLabel();
        BuildLayout();
    }

    public string FontName => _fontName;
    public float FontSize => _fontSize;
    public string ThemeId => AppearanceThemes.All[_theme.SelectedIndex].Id;
    public int DefaultCodePage => Encodings[_encoding.SelectedIndex].CodePage;
    public int DefaultLineEnding => Eols[_eol.SelectedIndex].Id;
    public bool WrapColumnEnabled => _wrapEnabled.Checked;
    public int WrapColumn => (int)_wrapColumn.Value;

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
        using var dlg = new FontDialog { Font = SafeFont(), ShowEffects = false, FontMustExist = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _fontName = dlg.Font.Name;
        _fontSize = dlg.Font.Size;
        UpdateFontLabel();
    }

    private Font SafeFont()
    {
        try { return new Font(_fontName, _fontSize <= 0 ? 12f : _fontSize); }
        catch { return new Font(FontFamily.GenericMonospace, 12f); }
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(12),
        };

        // フォント行: ラベル ＋ [現在表示 + 変更ボタン]。アクセスキー &F はボタン側に一本化（重複回避）。
        var fontLabelCol = new Label { Text = "フォント:", AutoSize = true, TabIndex = 0 };
        var fontPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 1 };
        fontPanel.Controls.Add(_fontLabel);
        fontPanel.Controls.Add(_fontButton);
        root.Controls.Add(fontLabelCol, 0, 0);
        root.Controls.Add(fontPanel, 1, 0);

        AddRow(root, 1, "配色(&C):", _theme, tabBase: 2);
        AddRow(root, 2, "既定の文字コード(&E):", _encoding, tabBase: 4);
        AddRow(root, 3, "既定の改行(&L):", _eol, tabBase: 6);

        // 折り返し: チェックボックス（行ラベル兼用）＋ 桁数入力。
        root.Controls.Add(_wrapEnabled, 0, 4);
        var wrapPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 9 };
        var wrapLbl = new Label { Text = "折り返し桁数(&K):", AutoSize = true, TabIndex = 9, Anchor = AnchorStyles.Left };
        _wrapColumn.TabIndex = 10;
        wrapPanel.Controls.Add(wrapLbl);
        wrapPanel.Controls.Add(_wrapColumn);
        root.Controls.Add(wrapPanel, 1, 4);
        _wrapEnabled.TabIndex = 8;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, TabIndex = 1 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, TabIndex = 2 };
        // ボタン群は設定コントロール（TabIndex 0..7）より後にする。パネル自身の TabIndex が
        // root 内の並びを決めるため、十分大きい値を明示（未設定だと既定 0 で先頭に来てしまう）。
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, TabIndex = 100 };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        root.Controls.Add(buttons, 0, 5);   // 折り返し行(4)の下へ
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        // 開いた直後のフォーカスを先頭の設定項目（フォント変更ボタン）に置く（OK に乗らないように）。
        ActiveControl = _fontButton;
    }

    private static void AddRow(TableLayoutPanel root, int row, string label, Control control, int tabBase)
    {
        var lbl = new Label { Text = label, AutoSize = true, TabIndex = tabBase };
        control.TabIndex = tabBase + 1;
        root.Controls.Add(lbl, 0, row);
        root.Controls.Add(control, 1, row);
    }
}
