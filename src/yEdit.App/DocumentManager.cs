using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// タブ（TabControl）と複数 Document の管理。各 Document は独立した EditorControl を持つ。
/// アクティブ由来のイベントのみ上位（MainForm）へ転送し、どのタブでも変更状態は
/// そのタブのラベルへ反映する。
/// </summary>
public sealed class DocumentManager
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<Document> _docs = new();
    private readonly Func<EditorControl> _editorFactory;

    public DocumentManager(Func<EditorControl> editorFactory)
    {
        _editorFactory = editorFactory;
        _tabs.Selected += (_, _) => OnSelectedTabChanged();
        _tabs.Deselecting += (_, _) => BeforeActiveChange?.Invoke(); // 切替直前に通知（マウス操作を含む）
        _tabs.KeyDown += OnTabKeyDown; // タブ列で Enter → エディタへ（編集開始）
    }

    /// <summary>MainForm の Controls へ載せるためのビュー（実体は TabControl）。</summary>
    public Control TabHost => _tabs;

    /// <summary>アクティブタブが切り替わる直前のフック（F2 編集中なら中断させる等）。
    /// マウス操作は Deselecting で、キーボード/プログラム経路は各選択メソッドから発火する。</summary>
    public Action? BeforeActiveChange { get; set; }

    public Document? Active => _tabs.SelectedTab?.Tag as Document;
    public IReadOnlyList<Document> Documents => _docs;
    public int Count => _docs.Count;

    public event EventHandler? ActiveDocumentChanged; // タブ切替
    public event EventHandler? ActiveDirtyChanged;    // アクティブの変更状態（タイトル更新）
    public event EventHandler? ActiveCaretChanged;    // アクティブの UpdateUI（行・桁更新）
    public event EventHandler? ActiveCaretEnteredEmptyLine; // アクティブの空行着地（PC-Talker 能動発声）

    /// <summary>アクティブ Document のエディタが Win32 フォーカスを得た。CSVモード中の
    /// シンク退避判断は上位（MainForm）が行う（_csv.IsEditing を参照できるのが上位のため）。</summary>
    public event Action<Document>? EditorGotFocus;

    /// <summary>新しい空タブを生成しアクティブ化する。State の中身は呼び出し側が設定する。</summary>
    public Document CreateNew()
    {
        var editor = _editorFactory();
        editor.Dock = DockStyle.Fill;
        var page = new TabPage();
        page.Controls.Add(editor);

        var doc = new Document(editor, page);
        page.Tag = doc;

        // どのタブでも保存点変化でそのタブのラベルを更新（アクティブなら上位へ転送）。
        editor.SavePointLeft += (_, _) => OnDirtyChanged(doc);
        editor.SavePointReached += (_, _) => OnDirtyChanged(doc);
        // キャレット移動はアクティブ分のみ上位へ。
        editor.UpdateUI += (_, _) =>
        {
            if (ReferenceEquals(doc, Active)) ActiveCaretChanged?.Invoke(this, EventArgs.Empty);
        };
        editor.CaretEnteredEmptyLine += (_, _) =>
        {
            if (ReferenceEquals(doc, Active)) ActiveCaretEnteredEmptyLine?.Invoke(this, EventArgs.Empty);
        };
        editor.GotFocus += (_, _) =>
        {
            if (ReferenceEquals(doc, Active)) EditorGotFocus?.Invoke(doc);
        };

        _docs.Add(doc);
        _tabs.TabPages.Add(page);
        UpdateLabel(doc);
        BeforeActiveChange?.Invoke();  // 既存タブから切り替わる前に F2 編集等を後始末
        _tabs.SelectedTab = page;  // 既存タブがあれば Selected 発火→ActiveDocumentChanged
        FocusActiveEditor();       // 新規/開く直後はエディタで即編集できるようにする
        return doc;
    }

    /// <summary>保存済みの同一パスを開いているタブを探す（未保存タブは対象外）。</summary>
    public Document? FindByPath(string path)
    {
        string key = PathKey.For(path);
        foreach (var d in _docs)
            if (d.State.Path is not null && PathKey.For(d.State.Path) == key)
                return d;
        return null;
    }

    public void Activate(Document doc)
    {
        if (_tabs.SelectedTab != doc.Page) { BeforeActiveChange?.Invoke(); _tabs.SelectedTab = doc.Page; }
        doc.FocusTarget.Focus(); // 開いた/呼び出したタブで即編集できるようにする
    }

    /// <summary>confirm が続行可を返したら閉じてネイティブ資源を解放する。閉じたら true。</summary>
    public bool TryClose(Document doc, Func<Document, bool> confirm)
    {
        if (!confirm(doc)) return false;
        _docs.Remove(doc);
        _tabs.TabPages.Remove(doc.Page);
        doc.Editor.Dispose();
        doc.Page.Dispose();
        return true;
    }

    /// <summary>タブを相対移動し、フォーカスをタブ列へ移す（SR が選択タブ＝ファイル名を読むため）。</summary>
    public void SelectNext(int dir)
    {
        int n = _tabs.TabPages.Count;
        if (n == 0) return;
        int i = _tabs.SelectedIndex;
        BeforeActiveChange?.Invoke();   // 切替前に F2 編集等を後始末（キーボード経路）
        _tabs.SelectedIndex = ((i + dir) % n + n) % n; // 端は巡回
        FocusTabStrip(); // タブ列にフォーカスを留め、SR が選択タブ（ファイル名＋位置）を読む
    }

    /// <summary>指定位置のタブを選択し、フォーカスをタブ列へ移す（SR が選択タブ＝ファイル名を読むため）。</summary>
    public void SelectAt(int index)
    {
        if (index < 0 || index >= _tabs.TabPages.Count) return;
        BeforeActiveChange?.Invoke();   // 切替前に F2 編集等を後始末（キーボード経路）
        _tabs.SelectedIndex = index;
        FocusTabStrip();
    }

    public void UpdateLabel(Document doc) => doc.Page.Text = doc.TabLabel;

    // 選択変更そのものはフォーカスを動かさない（フォーカス先は呼び出し側が決める：
    // 新規/開く/閉じる→エディタ、Ctrl+Tab/番号での切替→タブ列）。UI 更新のみ通知。
    private void OnSelectedTabChanged() => ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);

    private void FocusActiveEditor() => Active?.FocusTarget.Focus();

    private void FocusTabStrip() => _tabs.Focus();

    private void OnTabKeyDown(object? sender, KeyEventArgs e)
    {
        // タブ列にフォーカスがある状態で Enter を押したらエディタへ移って編集を開始する
        // （Ctrl+Tab で SR がファイル名を読む→Enter で本文へ、という流れ）。
        //
        // 重要: TabControl.ProcessKeyPreview は子孫（エディタ）にフォーカスがある編集中でも
        // プレビュー経路でこの KeyDown を発火させる。_tabs.Focused でタブ列自身がフォーカスを
        // 持つ時だけに限定しないと、編集中の Enter＝改行を横取りして native Scintilla へ
        // 渡らなくなる（改行が入力できなくなる）。タブ列フォーカス時のみ処理すること。
        if (e.KeyCode == Keys.Enter && _tabs.Focused)
        {
            FocusActiveEditor();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnDirtyChanged(Document doc)
    {
        UpdateLabel(doc);
        if (ReferenceEquals(doc, Active)) ActiveDirtyChanged?.Invoke(this, EventArgs.Empty);
    }
}
