using System.Text;
using yEdit.Core.Buffers;
using yEdit.Editor;

namespace yEdit.Editor.Smoke;

/// <summary>
/// P2 Task 14 の smoke 起動器メインフォーム。EditorControl を Dock=Fill で置き、
/// メニューから UTF-8 / Shift_JIS / EUC-JP のファイルを開いて eye check する用途。
/// P2 はビューア相当なのでキーバインドは付けない(スクロールとメニューだけ)。
/// SetSource は 1 度限りなので、開き直しごとに EditorControl を差し替える。
/// </summary>
public sealed class MainForm : Form
{
    private EditorControl _editor;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripMenuItem _wrapOff;
    private readonly ToolStripMenuItem _wrap40;
    private readonly ToolStripMenuItem _wrap80;
    private readonly ToolStripMenuItem _showLn;
    private readonly ToolStripMenuItem _showWs;
    private readonly ToolStripMenuItem _hlLine;

    // 開き直し時に前回の表示設定を復元するための保持
    private int _currentWrap;
    private bool _currentShowLn;
    private bool _currentShowWs;
    private bool _currentHlLine;
    private string? _currentPath;
    private string _encodingLabel = "UTF-8";

    public MainForm(string? initialPath)
    {
        Text = "yEdit.Editor.Smoke";
        Width = 900;
        Height = 700;

        // フィールドを先に初期化してから View メニューのクリックハンドラを配線する
        // (逆順にすると nullable 解析が「_editor が未初期化のままハンドラを capture 」と
        // 誤警告 CS8602 を出す。実行時は capture 後の invocation なので問題ないが黙らせる)。
        _editor = new EditorControl { Dock = DockStyle.Fill };

        // ---- メニュー ----
        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("ファイル(&F)");
        var openUtf8 = new ToolStripMenuItem("開く UTF-8(&U)...", null, (_, _) => OpenFile(Encoding.UTF8, "UTF-8"));
        var openSjis = new ToolStripMenuItem("開く Shift_JIS(&S)...", null, (_, _) => OpenFile(Encoding.GetEncoding(932), "SJIS"));
        var openEuc = new ToolStripMenuItem("開く EUC-JP(&E)...", null, (_, _) => OpenFile(Encoding.GetEncoding(51932), "EUC-JP"));
        var quit = new ToolStripMenuItem("終了(&X)", null, (_, _) => Close());
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { openUtf8, openSjis, openEuc, new ToolStripSeparator(), quit });
        menu.Items.Add(fileMenu);

        var viewMenu = new ToolStripMenuItem("表示(&V)");
        _wrapOff = new ToolStripMenuItem("折り返し OFF") { Checked = true };
        _wrap40 = new ToolStripMenuItem("折り返し 40 桁");
        _wrap80 = new ToolStripMenuItem("折り返し 80 桁");
        _showLn = new ToolStripMenuItem("行番号") { CheckOnClick = true };
        _showWs = new ToolStripMenuItem("空白可視化") { CheckOnClick = true };
        _hlLine = new ToolStripMenuItem("現在行強調") { CheckOnClick = true };
        _wrapOff.Click += (_, _) => SetWrap(0);
        _wrap40.Click += (_, _) => SetWrap(40);
        _wrap80.Click += (_, _) => SetWrap(80);
        _showLn.Click += (_, _) => { _currentShowLn = _showLn.Checked; _editor.ShowLineNumbers = _currentShowLn; };
        _showWs.Click += (_, _) => { _currentShowWs = _showWs.Checked; _editor.ShowWhitespace = _currentShowWs; };
        _hlLine.Click += (_, _) => { _currentHlLine = _hlLine.Checked; _editor.HighlightCurrentLine = _currentHlLine; };
        viewMenu.DropDownItems.AddRange(new ToolStripItem[] {
            _wrapOff, _wrap40, _wrap80, new ToolStripSeparator(), _showLn, _showWs, _hlLine
        });
        menu.Items.Add(viewMenu);

        MainMenuStrip = menu;

        // ---- ステータス ----
        _status = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("(未読込)") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _status.Items.Add(_statusLabel);

        // Dock 順: 中央(Fill)= Editor は最後に追加=最も後段=残余を占有
        // MenuStrip(Top)/StatusStrip(Bottom)/Editor(Fill) の順に Controls.Add する。
        Controls.Add(_editor);
        Controls.Add(_status);
        Controls.Add(menu);

        if (initialPath is not null && File.Exists(initialPath))
        {
            OpenFilePath(initialPath, Encoding.UTF8, "UTF-8");
        }
    }

    private void SetWrap(int cols)
    {
        _wrapOff.Checked = cols == 0;
        _wrap40.Checked = cols == 40;
        _wrap80.Checked = cols == 80;
        _currentWrap = cols;
        _editor.WrapColumns = cols;
    }

    private void OpenFile(Encoding encoding, string label)
    {
        using var dlg = new OpenFileDialog { Filter = "テキスト (*.txt;*.md;*.csv;*.log)|*.txt;*.md;*.csv;*.log|すべて (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        OpenFilePath(dlg.FileName, encoding, label);
    }

    /// <summary>
    /// 指定パスを指定エンコーディングで読み、TextBuffer に流し込んで新しい EditorControl に差し替える。
    /// EditorControl.SetSource は 1 度きり契約のため、開き直しごとに旧コントロールを Dispose して
    /// 新しく生成する(=フォーカスは新コントロールに再バインドされる)。
    /// </summary>
    private void OpenFilePath(string path, Encoding encoding, string label)
    {
        try
        {
            string text = File.ReadAllText(path, encoding);
            var buf = TextBuffer.FromString(text);

            var old = _editor;
            Controls.Remove(old);
            old.Dispose();

            _editor = new EditorControl { Dock = DockStyle.Fill };
            Controls.Add(_editor);
            // Dock=Fill は Controls コレクション上「先頭(index=0)」に置いた子=最後に dock 処理される=
            // 残余(menu と status を引いた領域)を占有する。Controls コレクションは Add で末尾に
            // 追加されるため、明示的に index=0 へ移して ctor と同じ [editor, status, menu] 順にする。
            Controls.SetChildIndex(_editor, 0);

            _editor.SetSource(buf);
            // 前回の表示設定を復元
            _editor.WrapColumns = _currentWrap;
            _editor.ShowLineNumbers = _currentShowLn;
            _editor.ShowWhitespace = _currentShowWs;
            _editor.HighlightCurrentLine = _currentHlLine;
            _editor.Focus();

            _currentPath = path;
            _encodingLabel = label;
            UpdateStatus(buf.Current.LineCount);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "読み込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateStatus(int lineCount)
    {
        _statusLabel.Text = $"{_currentPath ?? "(無題)"}  |  行数: {lineCount:N0}  |  Encoding: {_encodingLabel}  |  行高: {_editor.LineHeightPx}px";
    }
}
