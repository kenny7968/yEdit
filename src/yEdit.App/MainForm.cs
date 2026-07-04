using yEdit.App.Settings;
using yEdit.App.Speech;
using yEdit.Core.Csv;
using yEdit.Core.Reading;
using yEdit.Core.Settings;
using yEdit.Core.Speech;
using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.App;

public sealed partial class MainForm : Form
{
    private readonly DocumentManager _docs;
    private FileController _file = null!;      // コンストラクタで生成
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
    private readonly string _settingsPath = SettingsStore.DefaultPath;
    private AppSettings _settings = new();
    // Alt 等でメニューがアクティブな間は CSV の素キー横取りを止め、矢印/文字キーをメニュー操作へ通す。
    // メニューモードに入っても本文（Scintilla）はフォーカスを保持するため ContainsFocus では判別できず、
    // MenuStrip の Activate/Deactivate イベントで明示的に追跡する。
    private bool _menuActive;

    public MainForm(AppSettings settings)
    {
        _settings = settings;   // Program.Main が読込済み（優先 SR を SR 判定へ渡すため先読みしている）

        Text = "yEdit";
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        StartPosition = FormStartPosition.CenterScreen;

        _docs = new DocumentManager(CreateEditor);
        _docs.ActiveDocumentChanged += (_, _) => { UpdateTitle(); UpdateStatus(); };
        _docs.ActiveDirtyChanged += (_, _) => UpdateTitle();
        _docs.ActiveCaretChanged += (_, _) => UpdateStatus();
        // 空行着地の能動発声: PC-Talker は UIA の長さ0行を無音にするため、こちらから「空行」を読む。
        // NVDA はネイティブに「ブランク」を読むため対象外（発声モードは起動時に確定済み）。
        // CSVモード中はセル読み体系（CsvController）が担うため発声しない。
        _docs.ActiveCaretEnteredEmptyLine += (_, _) =>
        {
            if (SrContext.Mode == SpeechMode.PcTalker && _docs.Active?.State.CsvMode != true)
                _announcer.Say("空行");
        };
        // 設定は OpenSettings で参照が差し替わるため Func で都度解決させる。
        _file = new FileController(_docs, this, () => _settings,
            SaveSettingsSafe, RebuildRecentMenu, () => { UpdateTitle(); UpdateStatus(); },
            AutoEnterCsvMode);
        _search = new SearchController(_docs, this);
        _grep = new GrepController(_docs, this,
            hit => OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength));
        _backup = new BackupCoordinator(_docs, _settings.BackupEnabled, _settings.BackupIntervalSeconds);
        _announcer = AnnouncerFactory.Create(_announceLabel);
        _csv = new CsvController(_docs, _announcer);
        _docs.BeforeActiveChange = () => _csv.AbortEdit(); // タブ切替直前に F2 編集を中断（焦点の引き戻し防止）
        // CSVモード中に Scintilla がフォーカスを得たら（メニュー閉塞後の復帰・マウスクリック等）
        // シンクへ即時退避する。NVDA のネイティブ読み（フォーカス駆動）を Scintilla に向けない。
        // BeginInvoke は GotFocus 中の再入 Focus() を避けるため必須。ToggleMode OFF は
        // CsvMode=false を先に立ててから Editor.Focus() するため、このガードで素通りする。
        // 退避は遅延実行時点でも Scintilla がフォーカスを保持しているときだけ行う
        // （コールバックはモーダルポンプ中にも走るため、ダイアログからフォーカスを奪わない）。
        _docs.EditorGotFocus += doc =>
        {
            if (!doc.State.CsvMode || _csv.IsEditing) return;
            BeginInvoke(() => { if (doc.State.CsvMode && !_csv.IsEditing && doc.Editor.ContainsFocus) doc.CsvSink.Focus(); });
        };

        var menu = BuildMenu();
        var status = BuildStatusBar();

        Controls.Add(_docs.TabHost);
        Controls.Add(status);
        Controls.Add(_announceLabel); // 最下部（status の下）
        Controls.Add(menu);
        MainMenuStrip = menu;

        _file.NewFile(); // 起動時の無題タブ1つ（Q1=B：常に新規タブ）
    }

    /// <summary>タブ毎の ScintillaHost を生成する。SR 適応はハンドル生成前に確定させる（M1 と同順）。</summary>
    private ScintillaHost CreateEditor()
    {
        var e = new ScintillaHost { Dock = DockStyle.Fill };
        e.ApplySrAdaptation(useNativeReading: SrContext.UseNativeReading); // ハンドル生成前に起動時確定の SR 適応を反映
        EditorAppearance.Apply(e, _settings);  // フォント＋配色テーマを適用（M7）
        return e;
    }

    /// <summary>開く系経路（開く/最近/開き直し）で新規ロードした直後の .csv 自動 CSV モード進入（設定 ON のときのみ）。</summary>
    private void AutoEnterCsvMode(Document doc)
    {
        if (!_settings.CsvAutoModeOnOpen) return;
        if (!string.Equals(System.IO.Path.GetExtension(doc.State.Path), ".csv", StringComparison.OrdinalIgnoreCase)) return;
        _csv.TryEnterMode(doc);   // 解析不可なら TryEnterMode が通知して通常モードのまま
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _docs.Active?.FocusTarget.Focus();
        UpdateTitle();
        UpdateStatus();

        // 前回の異常終了で残ったバックアップがあれば復元提案（起動時に一度だけ）。
        if (!_restoreOffered)
        {
            _restoreOffered = true;
            _backup.OfferRestoreOnStartup(this, _file.RestoreFromBackup);
        }
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
            if (!_file.ConfirmDiscardIfDirty(doc))
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
        SaveSettingsSafe();
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
        // CSVモードのアクティブタブのみ、素のキーをグリッドナビ用に横取りする。
        // F2 編集オーバーレイ表示中（_csv.IsEditing）は素通しし、TextBox に通常編集させる。
        // 横取りはフォーカスシンクにフォーカスがある時に行う（CSVモード中は通常シンクが
        // フォーカスを保持する）。エディタ側の条件も残すのは、シンクへ移る遷移瞬間の
        // 取りこぼし防止。タブ列（Ctrl+Tab でフォーカスが移る）に居るときは矢印/Home/End 等を
        // タブ操作へ通す。メニューがアクティブ（Alt 等）な間は横取りせず、矢印/文字キーを
        // メニュー操作へ通す。
        var activeDoc = _docs.Active;
        if (activeDoc?.State.CsvMode == true && !_csv.IsEditing && !_menuActive &&
            (activeDoc.Editor.ContainsFocus || activeDoc.CsvSink.Focused) &&
            CsvCommands.ByKey.TryGetValue(keyData, out var csvCmd))
        {
            csvCmd(_csv);
            return true;
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
        // メニューモード中は CSV の素キー横取りを止める（ProcessCmdKey の _menuActive ガード参照）。
        menu.MenuActivate += (_, _) => _menuActive = true;
        menu.MenuDeactivate += (_, _) => _menuActive = false;

        var file = new ToolStripMenuItem("ファイル(&F)");
        AddMenuItem(file, "新規(&N)", (_, _) => _file.NewFile(), Keys.Control | Keys.N);
        AddMenuItem(file, "開く(&O)...", (_, _) => _file.OpenFileWithDialog(), Keys.Control | Keys.O);
        AddMenuItem(file, "文字コードを指定して開き直す(&R)...", (_, _) => _file.ReopenWithEncoding());
        _recentMenu = new ToolStripMenuItem("最近のファイル(&Y)");
        file.DropDownItems.Add(_recentMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "上書き保存(&S)", (_, _) => _file.Save(), Keys.Control | Keys.S);
        AddMenuItem(file, "名前を付けて保存(&A)...", (_, _) => _file.SaveAs());
        file.DropDownItems.Add(new ToolStripSeparator());
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

        // モード（マークダウンプレビュー / CSVモード）。CSV 操作系はメニューに出さず
        // キー専用（CsvCommands・キー一覧は将来のヘルプに記載する）。
        var mode = new ToolStripMenuItem("モード(&M)");
        var mdPreview = new ToolStripMenuItem(
            "マークダウンプレビュー(&P)", null, (_, _) => ShowMarkdownPreview());
        mode.DropDownItems.Add(mdPreview);
        mode.DropDownItems.Add(new ToolStripSeparator());
        var csvToggle = new ToolStripMenuItem("CSVモード(&C)", null, (_, _) => _csv.ToggleMode());
        mode.DropDownItems.Add(csvToggle);
        // 開く度に活性状態を更新（プレビューはアクティブタブがあれば拡張子を問わず有効、
        // CSVトグルは現在のモードを Checked で表示）。
        mode.DropDownOpening += (_, _) =>
        {
            mdPreview.Enabled = _docs.Active is not null;
            csvToggle.Checked = _docs.Active?.State.CsvMode == true;
        };

        var options = new ToolStripMenuItem("オプション(&O)");
        AddMenuItem(options, "設定(&P)...", (_, _) => OpenSettings());

        var help = new ToolStripMenuItem("ヘルプ(&H)");
        help.DropDownItems.Add("バージョン情報(&A)", null, (_, _) =>
            MessageBox.Show("yEdit v0.1", "バージョン情報", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, search, read, mode, options, help });
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
        _eolLabel.Text = doc.State.LineEnding.ToDisplayString();
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

    // ==================== 最近のファイル / 設定（M7） ====================

    /// <summary>設定を永続化する（保存失敗は致命でないため握る）。</summary>
    private void SaveSettingsSafe()
    {
        try { SettingsStore.Save(_settingsPath, _settings); } catch { /* 設定保存失敗は致命でない */ }
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
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => _file.TryOpenOrActivate(p)));
        }
    }

    /// <summary>設定ダイアログを開き、OK なら全タブへ外観適用＋永続化する。
    /// 項目→コントロールの対応はダイアログに閉じ、ここは Result を差し替えるだけにする。</summary>
    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings = dlg.Result;
        foreach (var doc in _docs.Documents) EditorAppearance.Apply(doc.Editor, _settings);
        SaveSettingsSafe();
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
        var doc = _file.TryOpenOrActivate(path, suppressAutoCsv: true);
        if (doc is null) return;
        doc.Editor.SelectCharRange(offset, length);
        doc.FocusTarget.Focus();
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
        // CSVモード中は行ジャンプをセル指定に読み替える（Ctrl+G のキーボード経路と統一）。
        if (_docs.Active?.State.CsvMode == true) { _csv.GoToCell(); return; }
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

    /// <summary>アクティブタブの編集中内容を WebView2 プレビューで表示する（拡張子は問わない）。</summary>
    private void ShowMarkdownPreview()
    {
        var doc = _docs.Active;
        if (doc is null) return;

        string markdown = doc.Editor.SnapshotText;            // 編集中バッファ（未保存も反映）
        string? dir = System.IO.Path.GetDirectoryName(doc.State.Path);
        string html = MarkdownRenderer.Render(markdown, MarkdownRenderer.PreviewBaseHref);

        using var f = new MarkdownPreviewForm(html, dir, doc.State.DisplayName);
        f.ShowDialog(this);
        _docs.Active?.FocusTarget.Focus();                     // 戻り後は編集領域へフォーカス
    }

    /// <summary>選択範囲（無ければ全文）を WrapColumn 桁で禁則整形する（実改行挿入・1 Undo）。</summary>
    private void FormatWithKinsoku()
    {
        var doc = _docs.Active;
        var ed = doc?.Editor;
        if (ed is null) return;
        // CSVモード中は本文が読取専用で整形が無反映になるため抑止（誤成功通知を防ぐ）。
        if (doc!.State.CsvMode) { _announcer.Say(CsvAnnounceFormatter.BlockedInCsvMode); return; }

        string text = ed.SnapshotText;
        var (selStart, selEnd) = ed.GetSelectionCharRange();
        bool whole = selStart == selEnd;
        int start = whole ? 0 : selStart;
        int len = whole ? text.Length : selEnd - selStart;
        if (len <= 0) return;

        string target = text.Substring(start, len);
        string eol = doc!.State.LineEnding.ToEolString();
        string formatted = KinsokuFormatter.Format(
            target, _settings.WrapColumn,
            _settings.KinsokuLineStartChars, _settings.KinsokuLineEndChars, _settings.KinsokuHangChars,
            eol, _settings.TabWidth);   // タブ幅は表示設定と連動（画面の見た目どおりに整形する。従来は既定 8 固定）

        if (formatted == target) { _announcer.Say("変更なし"); return; }
        ed.ReplaceCharRange(start, len, formatted);   // SCI_REPLACETARGET = 1 アンドゥ
        // 部分選択なら変化箇所を選択して提示。全文整形では全選択を避け、先頭へキャレットを置く。
        if (whole) ed.SelectCharRange(0, 0);
        else ed.SelectCharRange(start, formatted.Length);
        ed.Focus();
        _announcer.Say("整形しました");
    }

    /// <summary>アクティブタブを閉じる。変更確認→クローズ。最後の1つを閉じたらアプリ終了（Q1=B）。</summary>
    private void CloseActiveTab()
    {
        _csv.AbortEdit(); // F2 編集中ならタブ破棄前にオーバーレイを除去（IsEditing 固着防止）
        var doc = _docs.Active;
        if (doc is null) return;
        if (!_docs.TryClose(doc, _file.ConfirmDiscardIfDirty)) return;
        if (_docs.Count == 0) { Close(); return; }
        // 選択タブ削除時の TabControl.Selected 発火は WinForms の仕様上保証されないため、
        // クローズ後の新アクティブへフォーカス・タイトル・ステータスを明示更新する
        // （Selected 発火に依存しない唯一の更新源）。
        _docs.Active?.FocusTarget.Focus();
        UpdateTitle();
        UpdateStatus();
    }
}
