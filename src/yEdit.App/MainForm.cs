using yEdit.Core.Settings;
using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.App;

public sealed partial class MainForm : Form
{
    private readonly DocumentManager _docs;
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

        _docs = new DocumentManager(CreateEditor);
        _docs.ActiveDocumentChanged += (_, _) => { UpdateTitle(); UpdateStatus(); };
        _docs.ActiveDirtyChanged += (_, _) => UpdateTitle();
        _docs.ActiveCaretChanged += (_, _) => UpdateStatus();

        var menu = BuildMenu();
        var status = BuildStatusBar();

        Controls.Add(_docs.TabHost);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        NewFile(); // 起動時の無題タブ1つ（Q1=B：常に新規タブ）
    }

    /// <summary>タブ毎の ScintillaHost を生成する。SR 適応はハンドル生成前に確定させる（M1 と同順）。</summary>
    private ScintillaHost CreateEditor()
    {
        var e = new ScintillaHost { Dock = DockStyle.Fill };
        e.ConfigureForCurrentScreenReader();                       // ハンドル生成前に SR 適応を確定
        e.Styles[ScintillaNET.Style.Default].Font = _settings.FontName; // size 等は M7
        return e;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _docs.Active?.Editor.Focus();
        UpdateTitle();
        UpdateStatus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 全タブの未保存を順に確認（どれかでキャンセルなら終了中止）。
        foreach (var doc in _docs.Documents.ToArray())
        {
            if (!doc.Editor.Modified) continue;
            _docs.Activate(doc); // どのファイルの確認かを SR/視覚で示す
            if (!ConfirmDiscardIfDirty(doc))
            {
                e.Cancel = true;
                base.OnFormClosing(e);
                return;
            }
        }
        // ウィンドウサイズを設定に保存（最大化中は RestoreBounds を使う・M1 同様）。
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowWidth = b.Width;
        _settings.WindowHeight = b.Height;
        try { SettingsStore.Save(_settingsPath, _settings); } catch { /* 設定保存失敗は致命でない */ }
        base.OnFormClosing(e);
    }

    // ==================== キー操作（タブ切替・クローズ） ====================

    // Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+1..9 は子の Scintilla に食われないよう
    // フォームの ProcessCmdKey で横取りする。Ctrl+W はメニューのショートカットで処理。
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Tab: _docs.SelectNext(+1); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: _docs.SelectNext(-1); return true;
        }
        if ((keyData & (Keys.Control | Keys.Alt | Keys.Shift)) == Keys.Control)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k >= Keys.D1 && k <= Keys.D9)
            {
                if (k == Keys.D9) _docs.SelectAt(_docs.Count - 1); // 9 = 最後のタブ
                else _docs.SelectAt(k - Keys.D1);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ==================== メニュー / ステータス ====================

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("ファイル(&F)");
        AddMenuItem(file, "新規(&N)", (_, _) => NewFile(), Keys.Control | Keys.N);
        AddMenuItem(file, "開く(&O)...", (_, _) => OpenFile(), Keys.Control | Keys.O);
        AddMenuItem(file, "文字コードを指定して開き直す(&R)...", (_, _) => ReopenWithEncoding());
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "上書き保存(&S)", (_, _) => Save(), Keys.Control | Keys.S);
        AddMenuItem(file, "名前を付けて保存(&A)...", (_, _) => SaveAs());
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "タブを閉じる(&W)", (_, _) => CloseActiveTab(), Keys.Control | Keys.W);
        AddMenuItem(file, "終了(&X)", (_, _) => Close());

        var edit = new ToolStripMenuItem("編集(&E)");
        AddMenuItem(edit, "元に戻す(&U)", (_, _) => _docs.Active?.Editor.Undo(), Keys.Control | Keys.Z);
        AddMenuItem(edit, "やり直し(&R)", (_, _) => _docs.Active?.Editor.Redo(), Keys.Control | Keys.Y);
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(edit, "切り取り(&T)", (_, _) => _docs.Active?.Editor.Cut(), Keys.Control | Keys.X);
        AddMenuItem(edit, "コピー(&C)", (_, _) => _docs.Active?.Editor.Copy(), Keys.Control | Keys.C);
        AddMenuItem(edit, "貼り付け(&P)", (_, _) => _docs.Active?.Editor.Paste(), Keys.Control | Keys.V);
        AddMenuItem(edit, "すべて選択(&A)", (_, _) => _docs.Active?.Editor.SelectAll(), Keys.Control | Keys.A);

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
        var doc = _docs.Active;
        if (doc is null) return;
        int line = doc.Editor.CurrentLine + 1;
        int col = doc.Editor.GetColumn(doc.Editor.CurrentPosition) + 1;
        _posLabel.Text = $"行 {line}, 桁 {col}";
        _encLabel.Text = EncodingDisplayName(doc.State.Encoding, doc.State.HasBom);
        _eolLabel.Text = doc.State.LineEnding switch
        {
            LineEnding.Crlf => "CRLF", LineEnding.Lf => "LF", _ => "CR"
        };
    }

    private void UpdateTitle()
    {
        var doc = _docs.Active;
        Text = doc is null ? "yEdit" : $"{(doc.Editor.Modified ? "* " : "")}{doc.State.DisplayName} - yEdit";
    }

    private static string EncodingDisplayName(System.Text.Encoding enc, bool bom)
    {
        // 表示名は Core（EncodingCatalog）に集約。BOM 表記のみ App 側で付与する（UTF-8 のみ）。
        string name = EncodingCatalog.DisplayName(enc.CodePage);
        return enc.CodePage == 65001 && bom ? name + " (BOM)" : name;
    }

    // ==================== ファイル操作（アクティブタブ対象） ====================

    /// <summary>
    /// 変更があれば 保存/破棄/キャンセル を問う。Yes なら保存成否、No なら true、
    /// キャンセルなら false を返す。対象ドキュメントを明示で受ける（終了時の他タブ確認用）。
    /// </summary>
    private bool ConfirmDiscardIfDirty(Document doc)
    {
        if (!doc.Editor.Modified) return true;
        var r = MessageBox.Show(
            $"{doc.State.DisplayName} の変更を保存しますか？",
            "yEdit", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return r switch
        {
            DialogResult.Yes => SaveDocument(doc),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void NewFile()
    {
        var doc = _docs.CreateNew();
        doc.State.Path = null;
        doc.State.Encoding = EncodingCatalog.Get(_settings.DefaultCodePage);
        doc.State.HasBom = false;
        doc.State.LineEnding = (LineEnding)_settings.DefaultLineEnding;

        doc.Editor.Text = string.Empty;
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        doc.Editor.SetSavePoint();
        _docs.UpdateLabel(doc);
        UpdateTitle();
        UpdateStatus();
    }

    private void OpenFile()
    {
        using var dlg = new OpenFileDialog { Filter = "テキスト ファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // 既に同じファイルを開いていればそのタブへ（Q4：二重編集の上書き事故防止）。
        var existing = _docs.FindByPath(dlg.FileName);
        if (existing is not null) { _docs.Activate(existing); return; }

        var prev = _docs.Active; // 読込失敗時に戻る先（直前のアクティブタブ）
        var doc = _docs.CreateNew();
        if (!LoadInto(doc, dlg.FileName, forcedCodePage: null))
        {
            _docs.TryClose(doc, _ => true); // 読込失敗→作りかけタブを破棄
            if (prev is not null) _docs.Activate(prev); // 直前のアクティブへ戻す
        }
    }

    /// <summary>
    /// ファイルを読み込み、本文・文字コード・改行を対象タブへ反映する。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む（開き直し用）。
    /// 成否を返す（失敗は MessageBox 表示・握り潰さない）。
    /// </summary>
    private bool LoadInto(Document doc, string path, int? forcedCodePage)
    {
        try
        {
            var loaded = TextFileService.Load(path, forcedCodePage);
            doc.State.Path = path;
            doc.State.Encoding = loaded.Encoding;
            doc.State.HasBom = loaded.HasBom;
            doc.State.LineEnding = loaded.LineEnding;

            doc.Editor.Text = loaded.Text;
            ApplyEol(doc);
            doc.Editor.EmptyUndoBuffer();
            doc.Editor.SetSavePoint();

            _docs.UpdateLabel(doc);
            UpdateTitle();
            UpdateStatus();

            if (loaded.HadReplacementChar)
            {
                MessageBox.Show(
                    "このファイルには現在の文字コードで表せない文字（置換文字）が含まれています。" +
                    "別の文字コードで開き直してください。",
                    "文字コードの警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            // 想定内の入出力エラーのみ握る。NullReference 等のロジックバグは伝播させる。
            MessageBox.Show($"開けませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>doc.State.LineEnding をそのエディタの EOL モードへ反映する。</summary>
    private static void ApplyEol(Document doc)
        => doc.Editor.EolMode = doc.State.LineEnding switch
        {
            LineEnding.Crlf => ScintillaNET.Eol.CrLf,
            LineEnding.Lf => ScintillaNET.Eol.Lf,
            _ => ScintillaNET.Eol.Cr,
        };

    /// <summary>アクティブタブを指定の文字コードで開き直す。Path 未確定なら案内表示して中止。</summary>
    private void ReopenWithEncoding()
    {
        var doc = _docs.Active;
        if (doc is null) return;
        if (doc.State.Path is null)
        {
            MessageBox.Show("ファイルを開いてから実行してください。", "yEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!ConfirmDiscardIfDirty(doc)) return;
        using var dlg = new EncodingPickDialog(doc.State.Encoding.CodePage);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        LoadInto(doc, doc.State.Path, forcedCodePage: dlg.SelectedCodePage);
    }

    // メニューから呼ぶ（アクティブタブ対象）。
    private bool Save()
    {
        var doc = _docs.Active;
        return doc is not null && SaveDocument(doc);
    }

    private bool SaveAs()
    {
        var doc = _docs.Active;
        return doc is not null && SaveAsDocument(doc);
    }

    /// <summary>指定ドキュメントを保存。Path 未確定なら SaveAs にフォールバック。</summary>
    private bool SaveDocument(Document doc)
        => doc.State.Path is null ? SaveAsDocument(doc) : WriteToPath(doc, doc.State.Path);

    /// <summary>指定ドキュメントを名前を付けて保存。成功で State.Path とラベルを更新する。</summary>
    private bool SaveAsDocument(Document doc)
    {
        using var dlg = new SaveFileDialog { Filter = "テキスト ファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
        if (doc.State.Path is not null) dlg.FileName = System.IO.Path.GetFileName(doc.State.Path);
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        if (!WriteToPath(doc, dlg.FileName)) return false;
        doc.State.Path = dlg.FileName;
        _docs.UpdateLabel(doc);
        UpdateTitle();
        return true;
    }

    /// <summary>
    /// 改行を State.LineEnding に正規化してから本文を取得し、原子的に保存する。
    /// 例外は MessageBox でエラー表示し false を返す。
    /// </summary>
    private bool WriteToPath(Document doc, string path)
    {
        try
        {
            ApplyEol(doc);
            doc.Editor.ConvertEols(doc.Editor.EolMode);
            TextFileService.Save(path, doc.Editor.Text, doc.State.Encoding, doc.State.HasBom);
            doc.Editor.SetSavePoint();
            _docs.UpdateLabel(doc);
            UpdateTitle();
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            MessageBox.Show($"保存できませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>アクティブタブを閉じる。変更確認→クローズ。最後の1つを閉じたらアプリ終了（Q1=B）。</summary>
    private void CloseActiveTab()
    {
        var doc = _docs.Active;
        if (doc is null) return;
        if (!_docs.TryClose(doc, ConfirmDiscardIfDirty)) return;
        if (_docs.Count == 0) Close();
        // 選択タブ削除時の TabControl.Selected 発火は WinForms の仕様上保証されないため、
        // クローズ後の新アクティブへ明示的にフォーカスを移す（SR が新タブ＋現在行を読む）。冪等。
        else _docs.Active?.Editor.Focus();
    }
}
