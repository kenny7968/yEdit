using yEdit.Editor;

namespace yEdit.App;

public sealed partial class MainForm : Form
{
    private readonly ScintillaHost _editor;

    public MainForm()
    {
        Text = "yEdit";
        Width = 960;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;

        _editor = new ScintillaHost { Dock = DockStyle.Fill };
        _editor.ConfigureForCurrentScreenReader(); // ハンドル生成前に SR 適応を確定
        Controls.Add(_editor);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _editor.Focus();
    }
}
