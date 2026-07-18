using yEdit.App.Settings;
using yEdit.App.Speech;
using yEdit.Core.Backup;
using yEdit.Core.Csv;
using yEdit.Core.Reading;
using yEdit.Core.Settings;
using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.App;

public sealed partial class MainForm : Form
{
    private readonly DocumentManager _docs;
    private readonly FileController _file; // コンストラクタで生成
    private readonly SearchController _search; // コンストラクタで生成
    private readonly GrepController _grep; // コンストラクタで生成
    private readonly BackupCoordinator _backup; // コンストラクタで生成
    private readonly CsvController _csv; // コンストラクタで生成
    private readonly KinsokuFormatController _kinsoku; // コンストラクタで生成(FormatWithKinsoku を委譲)
    private bool _restoreOffered; // 起動時の復元提案を一度だけ行う
    private readonly ToolStripStatusLabel _posLabel = new("行 1, 桁 1");
    private readonly ToolStripStatusLabel _encLabel = new("UTF-8");
    private readonly ToolStripStatusLabel _eolLabel = new("CRLF");

    // SR への能動通知用ラベル（底部・最後の通知を視覚表示）。フォーカス不可なので編集を妨げない。
    private readonly Label _announceLabel = new()
    {
        Dock = DockStyle.Bottom,
        Height = 22,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        AccessibleName = "通知",
    };

    // CA1859: 実体は常に UiaAnnouncer(65 行下 ctor で直接生成)。
    // downstream (SearchController / GrepDialog / KinsokuFormatController 等) には
    // 依然 IAnnouncer として渡される(implicit conversion)ため公開契約は不変。
    private readonly UiaAnnouncer _announcer;
    private ToolStripMenuItem _recentMenu = null!; // BuildMenu で生成
    private readonly string _settingsPath;
    private AppSettings _settings = new();

    // Alt 等でメニューがアクティブな間は CSV の素キー横取りを止め、矢印/文字キーをメニュー操作へ通す。
    // メニューモードに入っても本文(EditorControl)はフォーカスを保持するため ContainsFocus では判別できず、
    // MenuStrip の Activate/Deactivate イベントで明示的に追跡する。
    private bool _menuActive;

    public MainForm(AppSettings settings)
        : this(settings, SettingsStore.DefaultPath) { }

    /// <summary>
    /// テストで実設定ファイルを汚さないため internal 経由で settingsPath を注入可能に
    /// (既存の public コンストラクタ経路は不変=Program.Main は DefaultPath へチェーン)。
    /// </summary>
    internal MainForm(AppSettings settings, string settingsPath)
    {
        _settingsPath = settingsPath;
        _settings = settings; // Program.Main が読込済み

        Text = "yEdit";
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        StartPosition = FormStartPosition.CenterScreen;

        _docs = new DocumentManager(CreateEditor);
        // Announcer は KeyBasedSwitch のラムダで参照されるため、event 購読より前に確定させる
        // (readonly 化に伴い null! 初期化を廃止 → definite assignment を先に済ませる)。
        _announcer = new UiaAnnouncer(_announceLabel);
        _docs.ActiveDocumentChanged += (_, _) =>
        {
            UpdateTitle();
            UpdateStatus();
        };
        _docs.KeyBasedSwitch += (_, doc) => _announcer.Say(doc.TabLabel);
        _docs.ActiveDirtyChanged += (_, _) => UpdateTitle();
        _docs.ActiveCaretChanged += (_, _) => UpdateStatus();
        // 設定は OpenSettings で参照が差し替わるため Func で都度解決させる。
        // Stage 8 A.3: delegate 4 個(saveSettings/recentChanged/metaChanged/openedFresh)が同型 Action で
        // 入れ替わっても検出不能なため、名前付き引数化で自己ドキュメント化(Stage 4 の教訓)。
        _file = new FileController(
            docs: _docs,
            owner: this,
            settings: () => _settings,
            saveSettings: SaveSettingsSafe,
            recentChanged: RebuildRecentMenu,
            metaChanged: () =>
            {
                UpdateTitle();
                UpdateStatus();
            },
            openedFresh: AutoEnterCsvMode,
            prompt: new MessageBoxUserPrompt(),
            fileDialogs: new WinFormsFileDialogService()
        );
        _search = new SearchController(_docs, this, _announcer, cb => new FindReplaceDialog(cb));
        _grep = new GrepController(
            docs: _docs,
            owner: this,
            // Batch D Task 12: GrepDialog は new UiaAnnouncer(_status) の直生成を廃止し
            // 共有 _announcer(SearchController と同型経路)を注入する。
            viewFactory: cb => new GrepDialog(cb, _announcer),
            resultsFactory: () =>
                new GrepResultsWindow(
                    new GrepResultsCallbacks(hit =>
                        OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength)
                    )
                )
        );
        _backup = new BackupCoordinator(
            _docs,
            _settings.BackupEnabled,
            _settings.BackupIntervalSeconds,
            TimeProvider.System,
            () => new SerialBackupWriter(BackupStore.DefaultDirectory),
            new WinFormsRestorePrompt()
        );
        _csv = new CsvController(
            docs: _docs,
            announcer: _announcer,
            cellPicker: new WinFormsCellPicker()
        );
        _kinsoku = new KinsokuFormatController(_docs, _announcer);
        _docs.BeforeActiveChange = () => _csv.AbortEdit(); // タブ切替直前に F2 編集を中断（焦点の引き戻し防止）
        // P6 で編集エンジンが自作 EditorControl (v2 UIA 単一経路) に統一されたため、
        // CSVモード中に Editor がフォーカスを得た瞬間にシンクへ強制退避していた仕組みは撤去。
        // 誤読み抑止は CsvController.TryEnterMode の RaiseUiaSelectionEvents=false が担う。
        // _docs.EditorGotFocus 自体は §0-8 の撤退安全性のため残す(購読ゼロで実質死・P7 で撤去)。

        var menu = BuildMenu();
        var status = BuildStatusBar();

        Controls.Add(_docs.TabHost);
        Controls.Add(status);
        Controls.Add(_announceLabel); // 最下部（status の下）
        Controls.Add(menu);
        MainMenuStrip = menu;

        _file.NewFile(); // 起動時の無題タブ1つ（Q1=B：常に新規タブ）
    }

    /// <summary>タブ毎の EditorControl を生成する。受動読みは EditorControl 単一経路（UIA v2）に一本化済み。</summary>
    private EditorControl CreateEditor()
    {
        var e = new EditorControl { Dock = DockStyle.Fill };
        EditorAppearance.Apply(e, _settings); // フォント＋配色テーマ＋表示設定を EditorControl.ApplyAppearance へ委譲
        return e;
    }

    /// <summary>開く系経路（開く/最近/開き直し）で新規ロードした直後の .csv 自動 CSV モード進入（設定 ON のときのみ）。</summary>
    private void AutoEnterCsvMode(Document doc)
    {
        if (!_settings.CsvAutoModeOnOpen)
            return;
        if (
            !string.Equals(
                System.IO.Path.GetExtension(doc.State.Path),
                ".csv",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return;
        _csv.TryEnterMode(doc); // 解析不可なら TryEnterMode が通知して通常モードのまま
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _docs.Active?.FocusTarget.Focus();
        UpdateTitle();
        UpdateStatus();

        // 前回の異常終了で残ったバックアップがあれば復元提案（起動時に一度だけ）。確認 OFF では無確認で全復元。
        if (!_restoreOffered)
        {
            _restoreOffered = true;
            int restored = _backup.OfferRestoreOnStartup(
                this,
                _file.RestoreFromBackup,
                _settings.ConfirmRestoreOnStartup
            );
            if (restored > 0)
                _announcer.Say($"バックアップを {restored} 件復元しました");
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 他ウィンドウから戻ったとき、SR 常駐環境ではフォーカスがメニューバー等タブ配下以外に
        // 残ったまま復元されないことがあり（実機事象）、WinForms の ActiveControl 復元では
        // 回復しない。タブ配下（エディタ／CSVシンク／F2ボックス／タブ列）に正当な保持者が
        // 居なければ編集領域へ戻して即編集可能にする。
        // BeginInvoke: 活性化時の WinForms 側フォーカス復元が済んだ後に判定するため。
        // ActiveForm 判定: 判定時点で既に別窓（自前のモーダル含む）へ移っていたら奪わない。
        // _menuActive 判定: 非アクティブ状態からメニュークリックで活性化した直後に
        // メニューを閉じてしまわないため。
        BeginInvoke(() =>
        {
            if (IsDisposed || ActiveForm != this || _menuActive)
                return;
            if (_docs.TabHost.ContainsFocus)
                return;
            _docs.Active?.FocusTarget.Focus();
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 終了開始: 実行中の grep を中止し、終了確認中に結果窓が湧くのを抑止する。
        _grep.BeginClose();
        // 全タブの未保存を順に確認（どれかでキャンセルなら終了中止）。
        foreach (var doc in _docs.Documents.ToArray())
        {
            if (!doc.Editor.Modified)
                continue;
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
        // Sub 3.4-B(CA1001): _docs(DocumentManager) と _csv(CsvController) が IDisposable 化されたが、
        // _docs は TabHost 経由で本 Form.Controls ツリーに接続済みのため base.Dispose(disposing) で
        // _tabs → TabPages → EditorControl まで解放される(=DocumentManager.Dispose を明示呼び出しても
        // 冪等で無害だが、既存の解放経路を尊重して二重呼び出しを増やさない)。
        // _csv は Form の Controls ツリーに載らないため明示 Dispose する(CsvCellEditor 内 TextBox の
        // リーク防止=編集中に強制終了する異常系のセーフティ)。
        // _docs?.Dispose() は現状 no-op(内部 field は全て Control で base.Dispose が回収する)だが、
        // 将来 DocumentManager が non-Control disposable を保持した際の silent leak 防止で明示呼び出し。
        // Dispose は冪等契約のため二重呼び出しでも無害。
        if (disposing)
        {
            _backup?.Dispose();
            _csv?.Dispose();
            _docs?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ==================== キー操作（タブ切替・クローズ） ====================

    // Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+1..9 は子の EditorControl に食われないよう
    // フォームの ProcessCmdKey で横取りする。Ctrl+W はメニューのショートカットで処理。
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // CSVモードのアクティブタブのみ、素のキーをグリッドナビ用に横取りする。
        // F2 編集オーバーレイ表示中（_csv.IsEditing）は素通しし、TextBox に通常編集させる。
        // P7 で CsvFocusSink を撤去し FocusTarget=Editor 固定になったため、
        // 横取り条件は Editor へのフォーカス保持のみで判定する。タブ列（Ctrl+Tab でフォーカスが移る）
        // に居るときは矢印/Home/End 等をタブ操作へ通す。メニューがアクティブ（Alt 等）な間は
        // 横取りせず、矢印/文字キーをメニュー操作へ通す。
        var activeDoc = _docs.Active;
        if (
            activeDoc?.State.CsvMode == true
            && !_csv.IsEditing
            && !_menuActive
            && activeDoc.Editor.ContainsFocus
            && CsvCommands.ByKey.TryGetValue(keyData, out var csvCmd)
        )
        {
            csvCmd(_csv);
            return true;
        }

        switch (keyData)
        {
            case Keys.Control | Keys.Tab:
                _docs.SelectNext(+1);
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
                _docs.SelectNext(-1);
                return true;
            case Keys.F3:
                _search.FindNext();
                return true;
            case Keys.Shift | Keys.F3:
                _search.FindPrev();
                return true;
            case Keys.Control | Keys.Alt | Keys.P:
                AnnouncePosition();
                return true;
            case Keys.Control | Keys.G:
                GoToLine();
                return true;
            case Keys.Insert:
                ToggleOvertype();
                return true;
        }
        if ((keyData & (Keys.Control | Keys.Alt | Keys.Shift)) == Keys.Control)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k >= Keys.D1 && k <= Keys.D9)
            {
                if (k == Keys.D9)
                    _docs.SelectAt(_docs.Count - 1); // 9 = 最後のタブ
                else
                    _docs.SelectAt(k - Keys.D1);
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
        AddMenuItem(
            file,
            "開く(&O)...",
            (_, _) => _file.OpenFileWithDialog(),
            Keys.Control | Keys.O
        );
        AddMenuItem(
            file,
            "文字コードを指定して開き直す(&R)...",
            (_, _) => _file.ReopenWithEncoding()
        );
        _recentMenu = new ToolStripMenuItem("最近のファイル(&Y)");
        file.DropDownItems.Add(_recentMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "上書き保存(&S)", (_, _) => _file.Save(), Keys.Control | Keys.S);
        AddMenuItem(
            file,
            "名前を付けて保存(&A)...",
            (_, _) => _file.SaveAs(),
            Keys.Control | Keys.Shift | Keys.S
        );
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "タブを閉じる(&W)", (_, _) => CloseActiveTab(), Keys.Control | Keys.W);
        AddMenuItem(file, "終了(&X)", (_, _) => Close());
        RebuildRecentMenu();

        var edit = new ToolStripMenuItem("編集(&E)");
        AddMenuItem(
            edit,
            "元に戻す(&U)",
            (_, _) => _docs.Active?.Editor.Undo(),
            Keys.Control | Keys.Z
        );
        AddMenuItem(
            edit,
            "やり直し(&R)",
            (_, _) => _docs.Active?.Editor.Redo(),
            Keys.Control | Keys.Y
        );
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(
            edit,
            "切り取り(&T)",
            (_, _) => _docs.Active?.Editor.Cut(),
            Keys.Control | Keys.X
        );
        AddMenuItem(
            edit,
            "コピー(&C)",
            (_, _) => _docs.Active?.Editor.Copy(),
            Keys.Control | Keys.C
        );
        AddMenuItem(
            edit,
            "貼り付け(&P)",
            (_, _) => _docs.Active?.Editor.Paste(),
            Keys.Control | Keys.V
        );
        AddMenuItem(
            edit,
            "すべて選択(&A)",
            (_, _) => _docs.Active?.Editor.SelectAll(),
            Keys.Control | Keys.A
        );
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(
            edit,
            "折り返し整形（禁則処理）(&K)",
            (_, _) => FormatWithKinsoku(),
            Keys.Control | Keys.Shift | Keys.J
        );

        // 検索系（旧「編集」メニューから分離。挙動・ショートカットは不変）。
        var search = new ToolStripMenuItem("検索(&S)");
        AddMenuItem(search, "検索(&F)...", (_, _) => _search.OpenFind(), Keys.Control | Keys.F);
        AddMenuItem(search, "置換(&H)...", (_, _) => _search.OpenReplace(), Keys.Control | Keys.H);
        // F3/Shift+F3 は ProcessCmdKey で処理するため、メニューは表示専用（ShortcutKeys 未登録）にして二重発火を避ける。
        var findNext = new ToolStripMenuItem("次を検索(&N)", null, (_, _) => _search.FindNext())
        {
            ShortcutKeyDisplayString = "F3",
        };
        var findPrev = new ToolStripMenuItem("前を検索(&B)", null, (_, _) => _search.FindPrev())
        {
            ShortcutKeyDisplayString = "Shift+F3",
        };
        search.DropDownItems.Add(findNext);
        search.DropDownItems.Add(findPrev);
        search.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(
            search,
            "フォルダ検索(grep)(&G)...",
            (_, _) => _grep.Open(),
            Keys.Control | Keys.Shift | Keys.F
        );

        // 読み上げ（SR 照会）。キーは ProcessCmdKey で処理し、ここは表示のみ（二重発火回避・M3 同方式）。
        var read = new ToolStripMenuItem("読み上げ(&R)");
        read.DropDownItems.Add(
            new ToolStripMenuItem("現在位置(&P)", null, (_, _) => AnnouncePosition())
            {
                ShortcutKeyDisplayString = "Ctrl+Alt+P",
            }
        );
        read.DropDownItems.Add(
            new ToolStripMenuItem("行へ移動(&G)...", null, (_, _) => GoToLine())
            {
                ShortcutKeyDisplayString = "Ctrl+G",
            }
        );

        // モード（マークダウンプレビュー / CSVモード）。CSV 操作系はメニューに出さず
        // キー専用（CsvCommands・キー一覧は将来のヘルプに記載する）。
        var mode = new ToolStripMenuItem("モード(&M)");
        var mdPreview = new ToolStripMenuItem(
            "マークダウンプレビュー(&P)",
            null,
            (_, _) => ShowMarkdownPreview()
        );
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
        help.DropDownItems.Add(
            "バージョン情報(&A)",
            null,
            (_, _) =>
                MessageBox.Show(
                    "yEdit v0.1",
                    "バージョン情報",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                )
        );

        menu.Items.AddRange(new ToolStripItem[] { file, edit, search, read, mode, options, help });
        return menu;
    }

    /// <summary>ドロップダウンに ToolStripMenuItem を追加し、任意でショートカットキーを設定する。</summary>
    private static void AddMenuItem(
        ToolStripMenuItem parent,
        string text,
        EventHandler onClick,
        Keys shortcut = Keys.None
    )
    {
        var item = new ToolStripMenuItem(text, null, onClick);
        if (shortcut != Keys.None)
            item.ShortcutKeys = shortcut;
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
        if (doc is null)
            return;
        int line = doc.Editor.CurrentLine + 1;
        int col = doc.Editor.GetColumn(doc.Editor.CurrentPosition) + 1;
        _posLabel.Text = $"行 {line}, 桁 {col}";
        _encLabel.Text = EncodingDisplayName(doc.State.Encoding, doc.State.HasBom);
        _eolLabel.Text = doc.State.LineEnding.ToDisplayString();
    }

    private void UpdateTitle()
    {
        var doc = _docs.Active;
        Text = doc is null
            ? "yEdit"
            : $"{(doc.Editor.Modified ? "* " : "")}{doc.State.DisplayName} - yEdit";
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
        try
        {
            SettingsStore.Save(_settingsPath, _settings);
        }
        catch
        { /* 設定保存失敗は致命でない */
        }
    }

    private void RebuildRecentMenu()
    {
        // 旧項目を解放（差し替え毎のリーク防止）。Clear 後に Dispose してコレクション変更との競合を避ける。
        var olds = new ToolStripItem[_recentMenu.DropDownItems.Count];
        _recentMenu.DropDownItems.CopyTo(olds, 0);
        _recentMenu.DropDownItems.Clear();
        foreach (var o in olds)
            o.Dispose();

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
            string body = (
                $"{System.IO.Path.GetFileName(p)}  〔{System.IO.Path.GetDirectoryName(p)}〕"
            ).Replace("&", "&&");
            // 1..9 は &1..&9、10 件目は &0 をアクセスキーに（不揃いを避ける）。
            string text =
                n <= 9 ? $"&{n} {body}"
                : n == 10 ? $"&0 {body}"
                : body;
            _recentMenu.DropDownItems.Add(
                new ToolStripMenuItem(text, null, (_, _) => _file.TryOpenOrActivate(p))
            );
        }
    }

    /// <summary>設定ダイアログを開き、OK なら全タブへ外観適用＋バックアップ設定の即時反映＋永続化する。
    /// 項目→コントロールの対応はダイアログに閉じ、ここは Result を差し替えるだけにする。</summary>
    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        _settings = dlg.Result; // Result は取得のたびに組み立てるため一度だけ読む
        foreach (var doc in _docs.Documents)
            EditorAppearance.Apply(doc.Editor, _settings);
        _backup.UpdateSettings(_settings.BackupEnabled, _settings.BackupIntervalSeconds);
        SaveSettingsSafe();
        _announcer.Say("設定を適用しました");
    }

    /// <summary>
    /// スモークテストの導線=Active 経由の TryOpenOrActivate/Save を Test から叩くため
    /// (MainForm 内では _file を直接使い、テスト側は FileForTest を通す)。
    /// </summary>
    internal FileController FileForTest => _file;

    /// <summary>
    /// grep ジャンプ用: path を開き（既存タブがあれば再利用）、文字オフセット範囲を選択して
    /// エディタへフォーカスする。選択移動でエディタの UIA が一致行を SR に読ませる。
    /// offset は grep が算出した UTF-16 文字位置で、同じ復号経路（TextFileService）を通るため
    /// エディタのスナップショットと同一空間に揃う。
    /// </summary>
    internal void OpenAndSelect(string path, int offset, int length)
    {
        var doc = _file.TryOpenOrActivate(path, suppressAutoCsv: true);
        if (doc is null)
            return;
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
        if (ed is null)
            return;
        int line = ed.CurrentLine + 1;
        int totalLines = ed.LineCount;
        int column = ed.GetColumn(ed.CurrentPosition) + 1;
        var (s, e) = ed.GetSelectionCharRange();
        _announcer.Say(
            PositionFormatter.Format(
                line,
                totalLines,
                column,
                ed.SnapshotText.Length,
                e - s,
                ed.Overtype
            )
        );
    }

    /// <summary>行番号を入力して移動する。</summary>
    private void GoToLine()
    {
        // CSVモード中は行ジャンプをセル指定に読み替える（Ctrl+G のキーボード経路と統一）。
        if (_docs.Active?.State.CsvMode == true)
        {
            _csv.GoToCell();
            return;
        }
        var ed = _docs.Active?.Editor;
        if (ed is null)
            return;
        int max = ed.LineCount;
        using var dlg = new GoToLineDialog(ed.CurrentLine + 1, max);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        int target = Math.Clamp(dlg.LineNumber, 1, max);
        ed.GoToLine(target - 1);
        ed.Focus();
        _announcer.Say($"行 {target}");
    }

    /// <summary>挿入/上書きモードをトグルし読み上げる（Insert キー）。</summary>
    private void ToggleOvertype()
    {
        var ed = _docs.Active?.Editor;
        if (ed is null)
            return;
        ed.Overtype = !ed.Overtype;
        _announcer.Say(ed.Overtype ? "上書きモード" : "挿入モード");
    }

    /// <summary>アクティブタブの編集中内容を WebView2 プレビューで表示する（拡張子は問わない）。</summary>
    private void ShowMarkdownPreview()
    {
        var doc = _docs.Active;
        if (doc is null)
            return;

        string markdown = doc.Editor.SnapshotText; // 編集中バッファ（未保存も反映）
        string? dir = System.IO.Path.GetDirectoryName(doc.State.Path);
        string html = MarkdownRenderer.Render(markdown, MarkdownRenderer.PreviewBaseHref);

        using var f = new MarkdownPreviewForm(html, dir, doc.State.DisplayName);
        f.ShowDialog(this);
        _docs.Active?.FocusTarget.Focus(); // 戻り後は編集領域へフォーカス
    }

    /// <summary>選択範囲（無ければ全文）を WrapColumn 桁で禁則整形する（Stage 8 で <see cref="KinsokuFormatController"/> へ委譲）。
    /// AppSettings は OpenSettings で参照が差し替わるため Run 引数(呼び出し時解決)で渡す。</summary>
    private void FormatWithKinsoku() => _kinsoku.Run(_settings);

    /// <summary>アクティブタブを閉じる。変更確認→クローズ。最後の1つを閉じたらアプリ終了（Q1=B）。</summary>
    private void CloseActiveTab()
    {
        _csv.AbortEdit(); // F2 編集中ならタブ破棄前にオーバーレイを除去（IsEditing 固着防止）
        var doc = _docs.Active;
        if (doc is null)
            return;
        if (!_docs.TryClose(doc, _file.ConfirmDiscardIfDirty))
            return;
        if (_docs.Count == 0)
        {
            Close();
            return;
        }
        // 選択タブ削除時の TabControl.Selected 発火は WinForms の仕様上保証されないため、
        // クローズ後の新アクティブへフォーカス・タイトル・ステータスを明示更新する
        // （Selected 発火に依存しない唯一の更新源）。
        _docs.Active?.FocusTarget.Focus();
        UpdateTitle();
        UpdateStatus();
    }
}
