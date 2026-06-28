using yEdit.Core.Backup;
using yEdit.Core.Csv;
using yEdit.Core.Reading;
using yEdit.Core.Search;
using yEdit.Core.Settings;
using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.App;

public sealed partial class MainForm : Form
{
    private readonly DocumentManager _docs;
    private SearchController _search = null!; // コンストラクタで生成
    private GrepController _grep = null!;     // コンストラクタで生成
    private BackupCoordinator _backup = null!; // コンストラクタで生成
    private CsvController _csv = null!;        // コンストラクタで生成
    private bool _restoreOffered;             // 起動時の復元提案を一度だけ行う
    private readonly ToolStripStatusLabel _posLabel = new("行 1, 桁 1");
    private readonly ToolStripStatusLabel _encLabel = new("UTF-8");
    private readonly ToolStripStatusLabel _eolLabel = new("CRLF");
    // SR への能動通知用ラベル（底部・最後の通知を視覚表示）。フォーカス不可なので編集を妨げない。
    private readonly Label _announceLabel = new()
    {
        Dock = DockStyle.Bottom, Height = 22, AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "通知",
    };
    private IAnnouncer _announcer = null!; // AnnouncerFactory で生成（起動時モード確定）
    private ToolStripMenuItem _recentMenu = null!; // BuildMenu で生成
    private const int MaxRecent = 10;
    private readonly string _settingsPath = SettingsStore.DefaultPath;
    private AppSettings _settings = new();
    private int _untitledSeq; // 無題タブの連番（新規作成毎に増加・セッション内で再利用しない）

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
        _search = new SearchController(_docs, this);
        _grep = new GrepController(_docs, this,
            hit => OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength));
        _backup = new BackupCoordinator(_docs, _settings.BackupEnabled, _settings.BackupIntervalSeconds);
        _announcer = AnnouncerFactory.Create(_announceLabel);
        _csv = new CsvController(_docs, _announcer);

        var menu = BuildMenu();
        var status = BuildStatusBar();

        Controls.Add(_docs.TabHost);
        Controls.Add(status);
        Controls.Add(_announceLabel); // 最下部（status の下）
        Controls.Add(menu);
        MainMenuStrip = menu;

        NewFile(); // 起動時の無題タブ1つ（Q1=B：常に新規タブ）
    }

    /// <summary>タブ毎の ScintillaHost を生成する。SR 適応はハンドル生成前に確定させる（M1 と同順）。</summary>
    private ScintillaHost CreateEditor()
    {
        var e = new ScintillaHost { Dock = DockStyle.Fill };
        e.ConfigureForCurrentScreenReader();   // ハンドル生成前に SR 適応を確定
        EditorAppearance.Apply(e, _settings);  // フォント＋配色テーマを適用（M7）
        return e;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _docs.Active?.Editor.Focus();
        UpdateTitle();
        UpdateStatus();

        // 前回の異常終了で残ったバックアップがあれば復元提案（起動時に一度だけ）。
        if (!_restoreOffered)
        {
            _restoreOffered = true;
            _backup.OfferRestoreOnStartup(this, RestoreFromBackup);
        }
    }

    /// <summary>バックアップ記録を新タブへ復元する。本文・メタを載せ、保存点は打たず dirty のままにする。</summary>
    private Document RestoreFromBackup(BackupRecord rec)
    {
        var doc = _docs.CreateNew();
        doc.State.Path = rec.OriginalPath;
        // 無題は元の連番を保ち、ダイアログ表示と復元後タブの番号を一致させる。連番カウンタは
        // 既存の最大値以上へ進め、以後の新規無題と衝突しないようにする。
        if (rec.OriginalPath is null)
        {
            int n = rec.UntitledNumber > 0 ? rec.UntitledNumber : ++_untitledSeq;
            if (n > _untitledSeq) _untitledSeq = n;
            doc.State.UntitledNumber = n;
        }
        else
        {
            doc.State.UntitledNumber = 0;
        }
        doc.State.Encoding = EncodingCatalog.Get(rec.CodePage);
        doc.State.HasBom = rec.HasBom;
        doc.State.LineEnding = (LineEnding)rec.LineEndingId;

        doc.Editor.Text = rec.Content;
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        // SetSavePoint しない → Modified=true のまま（ユーザーが保存できる）。
        _docs.UpdateLabel(doc);
        UpdateTitle();
        UpdateStatus();
        return doc;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 終了開始: 実行中の grep を中止し、終了確認中に結果窓が湧くのを抑止する。
        _grep.BeginClose();
        // 全タブの未保存を順に確認（どれかでキャンセルなら終了中止）。
        foreach (var doc in _docs.Documents.ToArray())
        {
            if (!doc.Editor.Modified) continue;
            _docs.Activate(doc); // どのファイルの確認かを SR/視覚で示す
            if (!ConfirmDiscardIfDirty(doc))
            {
                e.Cancel = true;
                _grep.CancelClose(); // 終了を取りやめたので grep を通常運用へ戻す
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // 閉じが確定した後にバックアップを停止する（OnFormClosing 後に取消される余地を残さない）。
        // 当セッション管理分のバックアップを削除し、孤児（=前回異常終了の印）を残さない。
        _backup.Shutdown();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        // 異常系（OnFormClosed 未経由）でも Timer/背景スレッドを確実に解放する。Shutdown 済みなら冪等で無害。
        if (disposing) _backup?.Dispose();
        base.Dispose(disposing);
    }

    // ==================== キー操作（タブ切替・クローズ） ====================

    // Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+1..9 は子の Scintilla に食われないよう
    // フォームの ProcessCmdKey で横取りする。Ctrl+W はメニューのショートカットで処理。
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // CSV モードのアクティブタブのみ Ctrl+Shift+矢印 / Ctrl+Shift+C を横取り。
        // OFF の時はこのブロックを素通りし、通常の Scintilla 挙動を温存する。
        if (_docs.Active?.State.CsvMode == true)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Shift | Keys.Up: _csv.Move(Direction.Up); return true;
                case Keys.Control | Keys.Shift | Keys.Down: _csv.Move(Direction.Down); return true;
                case Keys.Control | Keys.Shift | Keys.Left: _csv.Move(Direction.Left); return true;
                case Keys.Control | Keys.Shift | Keys.Right: _csv.Move(Direction.Right); return true;
                case Keys.Control | Keys.Shift | Keys.C: _csv.ReadColumnHeader(); return true;
            }
        }

        switch (keyData)
        {
            case Keys.Control | Keys.Tab: _docs.SelectNext(+1); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: _docs.SelectNext(-1); return true;
            case Keys.F3: _search.FindNext(); return true;
            case Keys.Shift | Keys.F3: _search.FindPrev(); return true;
            case Keys.Control | Keys.Alt | Keys.P: AnnouncePosition(); return true;
            case Keys.Control | Keys.Alt | Keys.I: AnnounceCharInfo(); return true;
            case Keys.Control | Keys.G: GoToLine(); return true;
            case Keys.Insert: ToggleOvertype(); return true;
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
        _recentMenu = new ToolStripMenuItem("最近のファイル(&Y)");
        file.DropDownItems.Add(_recentMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "上書き保存(&S)", (_, _) => Save(), Keys.Control | Keys.S);
        AddMenuItem(file, "名前を付けて保存(&A)...", (_, _) => SaveAs());
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "設定(&P)...", (_, _) => OpenSettings());
        AddMenuItem(file, "タブを閉じる(&W)", (_, _) => CloseActiveTab(), Keys.Control | Keys.W);
        AddMenuItem(file, "終了(&X)", (_, _) => Close());
        RebuildRecentMenu();

        var edit = new ToolStripMenuItem("編集(&E)");
        AddMenuItem(edit, "元に戻す(&U)", (_, _) => _docs.Active?.Editor.Undo(), Keys.Control | Keys.Z);
        AddMenuItem(edit, "やり直し(&R)", (_, _) => _docs.Active?.Editor.Redo(), Keys.Control | Keys.Y);
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(edit, "切り取り(&T)", (_, _) => _docs.Active?.Editor.Cut(), Keys.Control | Keys.X);
        AddMenuItem(edit, "コピー(&C)", (_, _) => _docs.Active?.Editor.Copy(), Keys.Control | Keys.C);
        AddMenuItem(edit, "貼り付け(&P)", (_, _) => _docs.Active?.Editor.Paste(), Keys.Control | Keys.V);
        AddMenuItem(edit, "すべて選択(&A)", (_, _) => _docs.Active?.Editor.SelectAll(), Keys.Control | Keys.A);
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(edit, "折り返し整形（禁則処理）(&K)", (_, _) => FormatWithKinsoku(),
            Keys.Control | Keys.Shift | Keys.J);

        // 検索系（旧「編集」メニューから分離。挙動・ショートカットは不変）。
        var search = new ToolStripMenuItem("検索(&S)");
        AddMenuItem(search, "検索(&F)...", (_, _) => _search.OpenFind(), Keys.Control | Keys.F);
        AddMenuItem(search, "置換(&H)...", (_, _) => _search.OpenReplace(), Keys.Control | Keys.H);
        // F3/Shift+F3 は ProcessCmdKey で処理するため、メニューは表示専用（ShortcutKeys 未登録）にして二重発火を避ける。
        var findNext = new ToolStripMenuItem("次を検索(&N)", null, (_, _) => _search.FindNext())
        { ShortcutKeyDisplayString = "F3" };
        var findPrev = new ToolStripMenuItem("前を検索(&B)", null, (_, _) => _search.FindPrev())
        { ShortcutKeyDisplayString = "Shift+F3" };
        search.DropDownItems.Add(findNext);
        search.DropDownItems.Add(findPrev);
        search.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(search, "フォルダ検索(grep)(&G)...", (_, _) => _grep.Open(), Keys.Control | Keys.Shift | Keys.F);

        // 読み上げ（SR 照会）。キーは ProcessCmdKey で処理し、ここは表示のみ（二重発火回避・M3 同方式）。
        var read = new ToolStripMenuItem("読み上げ(&R)");
        read.DropDownItems.Add(new ToolStripMenuItem("現在位置(&P)", null, (_, _) => AnnouncePosition())
        { ShortcutKeyDisplayString = "Ctrl+Alt+P" });
        read.DropDownItems.Add(new ToolStripMenuItem("文字情報(&I)", null, (_, _) => AnnounceCharInfo())
        { ShortcutKeyDisplayString = "Ctrl+Alt+I" });
        read.DropDownItems.Add(new ToolStripMenuItem("行へ移動(&G)...", null, (_, _) => GoToLine())
        { ShortcutKeyDisplayString = "Ctrl+G" });

        var md = new ToolStripMenuItem("マークダウン(&M)");
        var mdPreview = new ToolStripMenuItem(
            "マークダウンプレビュー(&P)", null, (_, _) => ShowMarkdownPreview());
        md.DropDownItems.Add(mdPreview);
        // 開く度に活性状態を更新（アクティブが .md の時だけ有効）。
        md.DropDownOpening += (_, _) =>
            mdPreview.Enabled = MarkdownFile.IsMarkdownPath(_docs.Active?.State.Path);

        var csv = new ToolStripMenuItem("CSV(&C)");
        var csvToggle = new ToolStripMenuItem("CSVモード切替(&T)", null, (_, _) => _csv.ToggleMode());
        var csvUp = new ToolStripMenuItem("上のセル(&U)", null, (_, _) => _csv.Move(Direction.Up))
        { ShortcutKeyDisplayString = "Ctrl+Shift+↑" };
        var csvDown = new ToolStripMenuItem("下のセル(&D)", null, (_, _) => _csv.Move(Direction.Down))
        { ShortcutKeyDisplayString = "Ctrl+Shift+↓" };
        var csvLeft = new ToolStripMenuItem("左のセル(&L)", null, (_, _) => _csv.Move(Direction.Left))
        { ShortcutKeyDisplayString = "Ctrl+Shift+←" };
        var csvRight = new ToolStripMenuItem("右のセル(&R)", null, (_, _) => _csv.Move(Direction.Right))
        { ShortcutKeyDisplayString = "Ctrl+Shift+→" };
        var csvHeader = new ToolStripMenuItem("列見出しを読み上げ(&C)", null, (_, _) => _csv.ReadColumnHeader())
        { ShortcutKeyDisplayString = "Ctrl+Shift+C" };
        csv.DropDownItems.Add(csvToggle);
        csv.DropDownItems.Add(new ToolStripSeparator());
        csv.DropDownItems.AddRange(new ToolStripItem[] { csvUp, csvDown, csvLeft, csvRight, csvHeader });
        // 開く度に活性更新。移動系は CSV モード時のみ有効。トグルは常に有効＋チェック表示。
        csv.DropDownOpening += (_, _) =>
        {
            bool on = _docs.Active?.State.CsvMode == true;
            csvUp.Enabled = csvDown.Enabled = csvLeft.Enabled = csvRight.Enabled = csvHeader.Enabled = on;
            csvToggle.Checked = on;
        };

        var help = new ToolStripMenuItem("ヘルプ(&H)");
        help.DropDownItems.Add("バージョン情報(&A)", null, (_, _) =>
            MessageBox.Show("yEdit v0.1", "バージョン情報", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, search, read, md, csv, help });
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
        doc.State.UntitledNumber = ++_untitledSeq; // 「無題 1」「無題 2」…で区別できるように
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
        using var dlg = new OpenFileDialog { Filter = "対応ファイル (*.txt, *.md, *.csv)|*.txt;*.md;*.csv|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        OpenExistingPath(dlg.FileName);
    }

    /// <summary>既存ファイルを開く（既存タブ再利用・読込失敗時は直前へ復帰）。OpenFile/最近のファイル共通。</summary>
    private void OpenExistingPath(string path)
    {
        // 既に同じファイルを開いていればそのタブへ（Q4：二重編集の上書き事故防止）。最近のファイルは先頭へ繰上げ。
        var existing = _docs.FindByPath(path);
        if (existing is not null) { _docs.Activate(existing); RegisterRecent(path); return; }

        var prev = _docs.Active; // 読込失敗時に戻る先（直前のアクティブタブ）
        var doc = _docs.CreateNew();
        if (!LoadInto(doc, path, forcedCodePage: null))
        {
            _docs.TryClose(doc, _ => true); // 読込失敗→作りかけタブを破棄
            if (prev is not null) _docs.Activate(prev); // 直前のアクティブへ戻す
        }
    }

    // ==================== 最近のファイル / 設定（M7） ====================

    /// <summary>開いたファイルを最近のファイルへ登録し、永続化＆メニュー再生成する。</summary>
    private void RegisterRecent(string path)
    {
        _settings.RecentFiles = RecentFilesList.Add(_settings.RecentFiles, path, MaxRecent);
        try { SettingsStore.Save(_settingsPath, _settings); } catch { /* 設定保存失敗は致命でない */ }
        RebuildRecentMenu();
    }

    private void RebuildRecentMenu()
    {
        // 旧項目を解放（差し替え毎のリーク防止）。Clear 後に Dispose してコレクション変更との競合を避ける。
        var olds = new ToolStripItem[_recentMenu.DropDownItems.Count];
        _recentMenu.DropDownItems.CopyTo(olds, 0);
        _recentMenu.DropDownItems.Clear();
        foreach (var o in olds) o.Dispose();

        if (_settings.RecentFiles.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(なし)") { Enabled = false });
            return;
        }
        int n = 0;
        foreach (string path in _settings.RecentFiles)
        {
            string p = path; // クロージャ捕捉
            n++;
            string body = ($"{System.IO.Path.GetFileName(p)}  〔{System.IO.Path.GetDirectoryName(p)}〕").Replace("&", "&&");
            // 1..9 は &1..&9、10 件目は &0 をアクセスキーに（不揃いを避ける）。
            string text = n <= 9 ? $"&{n} {body}" : n == 10 ? $"&0 {body}" : body;
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => OpenExistingPath(p)));
        }
    }

    /// <summary>設定ダイアログを開き、OK なら全タブへ外観適用＋永続化する。</summary>
    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.FontName = dlg.FontName;
        _settings.FontSize = dlg.FontSize;
        _settings.Theme = dlg.ThemeId;
        _settings.DefaultCodePage = dlg.DefaultCodePage;
        _settings.DefaultLineEnding = dlg.DefaultLineEnding;
        _settings.WrapColumnEnabled = dlg.WrapColumnEnabled;
        _settings.WrapColumn = dlg.WrapColumn;
        _settings.KinsokuLineStartChars = dlg.KinsokuLineStartChars;
        _settings.KinsokuLineEndChars = dlg.KinsokuLineEndChars;
        _settings.KinsokuHangChars = dlg.KinsokuHangChars;
        foreach (var doc in _docs.Documents) EditorAppearance.Apply(doc.Editor, _settings);
        try { SettingsStore.Save(_settingsPath, _settings); } catch { /* 設定保存失敗は致命でない */ }
        _announcer.Say("設定を適用しました");
    }

    /// <summary>
    /// grep ジャンプ用: path を開き（既存タブがあれば再利用）、文字オフセット範囲を選択して
    /// エディタへフォーカスする。選択移動でエディタの UIA が一致行を SR に読ませる。
    /// offset は grep が算出した UTF-16 文字位置で、同じ復号経路（TextFileService）を通るため
    /// エディタのスナップショットと同一空間に揃う。
    /// </summary>
    internal void OpenAndSelect(string path, int offset, int length)
    {
        var doc = _docs.FindByPath(path);
        if (doc is null)
        {
            var prev = _docs.Active; // 読込失敗時に戻る先
            doc = _docs.CreateNew();
            if (!LoadInto(doc, path, forcedCodePage: null))
            {
                _docs.TryClose(doc, _ => true);
                if (prev is not null) _docs.Activate(prev);
                return;
            }
        }
        else
        {
            _docs.Activate(doc);
        }
        doc.Editor.SelectCharRange(offset, length);
        doc.Editor.Focus();
        // ジャンプ先のファイル名と行を明示通知（選択移動の自動読みに加え、別ファイルへ飛んだ文脈を補う）。
        _announcer.Say($"{doc.State.DisplayName} {doc.Editor.CurrentLine + 1} 行目");
    }

    // ==================== 読み上げ照会（SR 利便・M6） ====================

    /// <summary>現在位置（行/総行/桁/文字数/選択数）を読み上げる。</summary>
    private void AnnouncePosition()
    {
        var ed = _docs.Active?.Editor;
        if (ed is null) return;
        int line = ed.CurrentLine + 1;
        int totalLines = ed.Lines.Count;
        int column = ed.GetColumn(ed.CurrentPosition) + 1;
        var (s, e) = ed.GetSelectionCharRange();
        _announcer.Say(PositionFormatter.Format(line, totalLines, column, ed.SnapshotText.Length, e - s, ed.Overtype));
    }

    /// <summary>キャレット位置の文字情報（全角/半角空白の区別など）を読み上げる。末尾なら案内する。</summary>
    private void AnnounceCharInfo()
    {
        var ed = _docs.Active?.Editor;
        if (ed is null) return;
        string text = ed.SnapshotText;
        int caret = ed.CaretCharOffset; // 選択端ではなく実キャレット位置の文字を説明する
        if (caret < 0 || caret >= text.Length) { _announcer.Say("文書の末尾"); return; }
        _announcer.Say(CharacterDescriber.DescribeAt(text, caret));
    }

    /// <summary>行番号を入力して移動する。</summary>
    private void GoToLine()
    {
        var ed = _docs.Active?.Editor;
        if (ed is null) return;
        int max = ed.Lines.Count;
        using var dlg = new GoToLineDialog(ed.CurrentLine + 1, max);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int target = Math.Clamp(dlg.LineNumber, 1, max);
        ed.Lines[target - 1].Goto();
        ed.Focus();
        _announcer.Say($"行 {target}");
    }

    /// <summary>挿入/上書きモードをトグルし読み上げる（Insert キー）。</summary>
    private void ToggleOvertype()
    {
        var ed = _docs.Active?.Editor;
        if (ed is null) return;
        ed.Overtype = !ed.Overtype;
        _announcer.Say(ed.Overtype ? "上書きモード" : "挿入モード");
    }

    /// <summary>アクティブな .md タブの編集中内容を WebView2 プレビューで表示する。</summary>
    private void ShowMarkdownPreview()
    {
        var doc = _docs.Active;
        // メニュー無効化で基本到達しないが、保険として再ガードする。
        if (doc is null || !MarkdownFile.IsMarkdownPath(doc.State.Path)) return;

        string markdown = doc.Editor.SnapshotText;            // 編集中バッファ（未保存も反映）
        string? dir = System.IO.Path.GetDirectoryName(doc.State.Path);
        string html = MarkdownRenderer.Render(markdown, MarkdownRenderer.PreviewBaseHref);

        using var f = new MarkdownPreviewForm(html, dir, doc.State.DisplayName);
        f.ShowDialog(this);
        _docs.Active?.Editor.Focus();                          // 戻り後はエディタへフォーカス
    }

    /// <summary>選択範囲（無ければ全文）を WrapColumn 桁で禁則整形する（実改行挿入・1 Undo）。</summary>
    private void FormatWithKinsoku()
    {
        var doc = _docs.Active;
        var ed = doc?.Editor;
        if (ed is null) return;

        string text = ed.SnapshotText;
        var (selStart, selEnd) = ed.GetSelectionCharRange();
        bool whole = selStart == selEnd;
        int start = whole ? 0 : selStart;
        int len = whole ? text.Length : selEnd - selStart;
        if (len <= 0) return;

        string target = text.Substring(start, len);
        string eol = EolString(doc!.State.LineEnding);
        string formatted = KinsokuFormatter.Format(
            target, _settings.WrapColumn,
            _settings.KinsokuLineStartChars, _settings.KinsokuLineEndChars, _settings.KinsokuHangChars,
            eol);

        if (formatted == target) { _announcer.Say("変更なし"); return; }
        ed.ReplaceCharRange(start, len, formatted);   // SCI_REPLACETARGET = 1 アンドゥ
        // 部分選択なら変化箇所を選択して提示。全文整形では全選択を避け、先頭へキャレットを置く。
        if (whole) ed.SelectCharRange(0, 0);
        else ed.SelectCharRange(start, formatted.Length);
        ed.Focus();
        _announcer.Say("整形しました");
    }

    private static string EolString(LineEnding eol) => eol switch
    {
        LineEnding.Lf => "\n",
        LineEnding.Cr => "\r",
        _ => "\r\n",
    };

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

            doc.State.CsvMode = false;
            if (CsvFile.IsCsvPath(path))
            {
                var csv = CsvParser.Parse(loaded.Text);
                if (csv.Ok) { doc.State.CsvMode = true; _announcer.Say(CsvAnnounceFormatter.ModeOn); }
                else { _announcer.Say(CsvAnnounceFormatter.OpenParseFailed); }
            }

            if (loaded.HadReplacementChar)
            {
                MessageBox.Show(
                    "このファイルには現在の文字コードで表せない文字（置換文字）が含まれています。" +
                    "別の文字コードで開き直してください。",
                    "文字コードの警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RegisterRecent(path); // 開けたファイルを最近のファイルへ
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            // 想定内の入出力エラーのみ握る。NullReference 等のロジックバグは伝播させる。
            MessageBox.Show($"開けませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>パスと本文から CSV モードを再判定して反映する。オンに変わったときだけ通知する。
    /// オープン時の解析失敗通知は LoadInto 側で行うため、ここでは通知しない。</summary>
    private void RedetectCsvMode(Document doc)
    {
        bool was = doc.State.CsvMode;
        doc.State.CsvMode = CsvFile.IsCsvPath(doc.State.Path)
            && CsvParser.Parse(doc.Editor.SnapshotText).Ok;
        if (doc.State.CsvMode && !was) _announcer.Say(CsvAnnounceFormatter.ModeOn);
        if (!doc.State.CsvMode && was) doc.Editor.ClearHighlight(); // OFF へ転じたらセルハイライトを消す
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
        using var dlg = new SaveFileDialog { Filter = "テキスト ファイル (*.txt)|*.txt|マークダウン ファイル (*.md)|*.md|CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*" };
        if (doc.State.Path is not null) dlg.FileName = System.IO.Path.GetFileName(doc.State.Path);
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        if (!WriteToPath(doc, dlg.FileName)) return false;
        doc.State.Path = dlg.FileName;
        _docs.UpdateLabel(doc);
        UpdateTitle();
        RegisterRecent(dlg.FileName); // 保存先も最近のファイルへ
        RedetectCsvMode(doc);         // パス変更に追従して CSV モードを再判定
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
        if (_docs.Count == 0) { Close(); return; }
        // 選択タブ削除時の TabControl.Selected 発火は WinForms の仕様上保証されないため、
        // クローズ後の新アクティブへフォーカス・タイトル・ステータスを明示更新する
        // （Selected 発火に依存しない唯一の更新源）。
        _docs.Active?.Editor.Focus();
        UpdateTitle();
        UpdateStatus();
    }
}
