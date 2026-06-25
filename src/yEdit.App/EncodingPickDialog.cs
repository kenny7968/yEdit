namespace yEdit.App;

/// <summary>
/// 文字コードを選ぶアクセシブルなダイアログ（DropDownList の ComboBox + OK/キャンセル）。
/// ラベルにアクセスキー(&amp;E)を付け、TabIndex でラベル→コンボの順にして
/// Alt+E でコンボへフォーカスが移るようにする（ラベルはニーモニックを次のコントロールへ転送）。
/// </summary>
public sealed class EncodingPickDialog : Form
{
    private readonly ComboBox _combo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "文字コード",
    };

    /// <summary>OK 押下時に確定する選択コードページ。</summary>
    public int SelectedCodePage { get; private set; } = 65001;

    private static readonly (string Name, int Cp)[] Choices =
    {
        ("UTF-8", 65001), ("Shift_JIS", 932), ("EUC-JP", 51932),
        ("UTF-16 LE", 1200), ("UTF-16 BE", 1201),
    };

    public EncodingPickDialog(int currentCodePage)
    {
        Text = "文字コードを指定して開き直す";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(320, 110);

        var label = new Label { Text = "文字コード(&E):", AutoSize = true, Left = 12, Top = 16, TabIndex = 0 };

        _combo.Left = 12;
        _combo.Top = 38;
        _combo.TabIndex = 1;
        foreach (var c in Choices) _combo.Items.Add(c.Name);
        _combo.SelectedIndex = Math.Max(0, Array.FindIndex(Choices, c => c.Cp == currentCodePage));

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 150, Top = 72, Width = 75, TabIndex = 2 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Left = 232, Top = 72, Width = 75, TabIndex = 3 };
        ok.Click += (_, _) => SelectedCodePage = Choices[_combo.SelectedIndex].Cp;

        Controls.AddRange(new Control[] { label, _combo, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
