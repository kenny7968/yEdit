using yEdit.Core.Settings;
using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.App;

public sealed partial class MainForm : Form
{
    private readonly ScintillaHost _editor;
    private readonly DocumentState _doc = new();
    private readonly ToolStripStatusLabel _posLabel = new("行 1, 桁 1");
    private readonly ToolStripStatusLabel _encLabel = new("UTF-8");
    private readonly ToolStripStatusLabel _eolLabel = new("CRLF");
    private readonly string _settingsPath = SettingsStore.DefaultPath;
    private AppSettings _settings = new();

    public MainForm()
    {
        _settings = SettingsStore.Load(_settingsPath);

        Text = "yEdit";
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        StartPosition = FormStartPosition.CenterScreen;

        _editor = new ScintillaHost { Dock = DockStyle.Fill };
        _editor.ConfigureForCurrentScreenReader(); // ハンドル生成前に SR 適応を確定
        ApplyFont();

        var menu = BuildMenu();
        var status = BuildStatusBar();

        Controls.Add(_editor);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        _editor.UpdateUI += (_, _) => UpdateStatus();
        // 変更/保存点で件名（dirty）更新
        _editor.SavePointLeft += (_, _) => UpdateTitle();
        _editor.SavePointReached += (_, _) => UpdateTitle();

        UpdateTitle();
        UpdateStatus();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _editor.Focus();
    }

    private void ApplyFont()
        => _editor.Styles[ScintillaNET.Style.Default].Font = _settings.FontName; // size 等は M7 で詳細化

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        // DropDownItems.Add(string, Image, EventHandler) は ToolStripItem を返すが、
        // ドロップダウンの既定アイテムは ToolStripMenuItem なので cast して ShortcutKeys を設定する。
        var file = new ToolStripMenuItem("ファイル(&F)");
        AddMenuItem(file, "新規(&N)", (_, _) => NewFile(), Keys.Control | Keys.N);
        AddMenuItem(file, "開く(&O)...", (_, _) => OpenFile(), Keys.Control | Keys.O);
        AddMenuItem(file, "文字コードを指定して開き直す(&R)...", (_, _) => ReopenWithEncoding());
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "上書き保存(&S)", (_, _) => Save(), Keys.Control | Keys.S);
        AddMenuItem(file, "名前を付けて保存(&A)...", (_, _) => SaveAs());
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "終了(&X)", (_, _) => Close());

        var edit = new ToolStripMenuItem("編集(&E)");
        AddMenuItem(edit, "元に戻す(&U)", (_, _) => _editor.Undo(), Keys.Control | Keys.Z);
        AddMenuItem(edit, "やり直し(&R)", (_, _) => _editor.Redo(), Keys.Control | Keys.Y);
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(edit, "切り取り(&T)", (_, _) => _editor.Cut(), Keys.Control | Keys.X);
        AddMenuItem(edit, "コピー(&C)", (_, _) => _editor.Copy(), Keys.Control | Keys.C);
        AddMenuItem(edit, "貼り付け(&P)", (_, _) => _editor.Paste(), Keys.Control | Keys.V);
        AddMenuItem(edit, "すべて選択(&A)", (_, _) => _editor.SelectAll(), Keys.Control | Keys.A);

        var help = new ToolStripMenuItem("ヘルプ(&H)");
        help.DropDownItems.Add("バージョン情報(&A)", null, (_, _) =>
            MessageBox.Show("yEdit v0.1", "バージョン情報", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, help });
        return menu;
    }

    /// <summary>ドロップダウンに ToolStripMenuItem を追加し、任意でショートカットキーを設定する。</summary>
    private static void AddMenuItem(ToolStripMenuItem parent, string text, EventHandler onClick, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(text, null, onClick);
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        parent.DropDownItems.Add(item);
    }

    private StatusStrip BuildStatusBar()
    {
        var strip = new StatusStrip();
        _posLabel.Spring = true;
        _posLabel.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.AddRange(new ToolStripItem[] { _posLabel, _encLabel, _eolLabel });
        return strip;
    }

    private void UpdateStatus()
    {
        int line = _editor.CurrentLine + 1;
        int col = _editor.GetColumn(_editor.CurrentPosition) + 1;
        _posLabel.Text = $"行 {line}, 桁 {col}";
        _encLabel.Text = EncodingDisplayName(_doc.Encoding, _doc.HasBom);
        _eolLabel.Text = _doc.LineEnding switch
        {
            LineEnding.Crlf => "CRLF", LineEnding.Lf => "LF", _ => "CR"
        };
    }

    private void UpdateTitle()
        => Text = $"{(_editor.Modified ? "* " : "")}{_doc.DisplayName} - yEdit";

    private static string EncodingDisplayName(System.Text.Encoding enc, bool bom) => enc.CodePage switch
    {
        65001 => bom ? "UTF-8 (BOM)" : "UTF-8",
        932 => "Shift_JIS",
        51932 => "EUC-JP",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        _ => enc.WebName,
    };

    // ==================== ファイル操作 ====================

    /// <summary>
    /// 変更があれば 保存/破棄/キャンセル を問う。Yes なら Save() の結果（保存成否）を、
    /// No なら true（破棄して続行）、キャンセルなら false（操作中止）を返す。
    /// </summary>
    private bool ConfirmDiscardIfDirty()
    {
        if (!_editor.Modified) return true;
        var r = MessageBox.Show(
            $"{_doc.DisplayName} の変更を保存しますか？",
            "yEdit", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return r switch
        {
            DialogResult.Yes => Save(),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void NewFile()
    {
        if (!ConfirmDiscardIfDirty()) return;
        _editor.Text = string.Empty;
        _doc.Path = null;
        _doc.Encoding = EncodingCatalog.Get(_settings.DefaultCodePage);
        _doc.HasBom = false;
        _doc.LineEnding = (LineEnding)_settings.DefaultLineEnding;
        ApplyEol();
        _editor.EmptyUndoBuffer();
        _editor.SetSavePoint();
        UpdateTitle();
        UpdateStatus();
    }

    private void OpenFile()
    {
        if (!ConfirmDiscardIfDirty()) return;
        using var dlg = new OpenFileDialog { Filter = "テキスト ファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        LoadPath(dlg.FileName, forcedCodePage: null);
    }

    /// <summary>
    /// ファイルを読み込み、本文・文字コード・改行をエディタとドキュメント状態へ反映する。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む（開き直し用）。
    /// 例外は MessageBox でエラー表示する（握り潰さない）。
    /// </summary>
    private void LoadPath(string path, int? forcedCodePage)
    {
        try
        {
            var doc = TextFileService.Load(path, forcedCodePage);
            _doc.Path = path;
            _doc.Encoding = doc.Encoding;
            _doc.HasBom = doc.HasBom;
            _doc.LineEnding = doc.LineEnding;

            _editor.Text = doc.Text;
            ApplyEol();
            _editor.EmptyUndoBuffer();
            _editor.SetSavePoint();

            UpdateTitle();
            UpdateStatus();

            if (doc.HadReplacementChar)
            {
                MessageBox.Show(
                    "このファイルには現在の文字コードで表せない文字（置換文字）が含まれています。" +
                    "別の文字コードで開き直してください。",
                    "文字コードの警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"開けませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>_doc.LineEnding をエディタの EOL モードへ反映する。</summary>
    private void ApplyEol()
        => _editor.EolMode = _doc.LineEnding switch
        {
            LineEnding.Crlf => ScintillaNET.Eol.CrLf,
            LineEnding.Lf => ScintillaNET.Eol.Lf,
            _ => ScintillaNET.Eol.Cr,
        };

    private void ReopenWithEncoding() { }
    private bool Save() { return false; }
    private bool SaveAs() { return false; }
}
