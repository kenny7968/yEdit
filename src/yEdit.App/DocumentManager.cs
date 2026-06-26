using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// タブ（TabControl）と複数 Document の管理。各 Document は独立した ScintillaHost を持つ。
/// アクティブ由来のイベントのみ上位（MainForm）へ転送し、どのタブでも変更状態は
/// そのタブのラベルへ反映する。
/// </summary>
public sealed class DocumentManager
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<Document> _docs = new();
    private readonly Func<ScintillaHost> _editorFactory;

    public DocumentManager(Func<ScintillaHost> editorFactory)
    {
        _editorFactory = editorFactory;
        _tabs.Selected += (_, _) => OnSelectedTabChanged();
        _tabs.KeyDown += OnTabKeyDown; // タブ列で Enter → エディタへ（編集開始）
    }

    /// <summary>MainForm の Controls へ載せるためのビュー（実体は TabControl）。</summary>
    public Control TabHost => _tabs;

    public Document? Active => _tabs.SelectedTab?.Tag as Document;
    public IReadOnlyList<Document> Documents => _docs;
    public int Count => _docs.Count;

    public event EventHandler? ActiveDocumentChanged; // タブ切替
    public event EventHandler? ActiveDirtyChanged;    // アクティブの変更状態（タイトル更新）
    public event EventHandler? ActiveCaretChanged;    // アクティブの UpdateUI（行・桁更新）

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

        _docs.Add(doc);
        _tabs.TabPages.Add(page);
        UpdateLabel(doc);
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
        if (_tabs.SelectedTab != doc.Page) _tabs.SelectedTab = doc.Page;
        doc.Editor.Focus(); // 開いた/呼び出したタブで即編集できるようにする
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

    public void SelectNext(int dir)
    {
        int n = _tabs.TabPages.Count;
        if (n == 0) return;
        int i = _tabs.SelectedIndex;
        _tabs.SelectedIndex = ((i + dir) % n + n) % n; // 端は巡回
        FocusTabStrip(); // タブ列にフォーカスを留め、SR が選択タブ（ファイル名＋位置）を読む
    }

    public void SelectAt(int index)
    {
        if (index < 0 || index >= _tabs.TabPages.Count) return;
        _tabs.SelectedIndex = index;
        FocusTabStrip();
    }

    public void UpdateLabel(Document doc) => doc.Page.Text = doc.TabLabel;

    // 選択変更そのものはフォーカスを動かさない（フォーカス先は呼び出し側が決める：
    // 新規/開く/閉じる→エディタ、Ctrl+Tab/番号での切替→タブ列）。UI 更新のみ通知。
    private void OnSelectedTabChanged() => ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);

    private void FocusActiveEditor() => Active?.Editor.Focus();

    private void FocusTabStrip() => _tabs.Focus();

    private void OnTabKeyDown(object? sender, KeyEventArgs e)
    {
        // タブ列にフォーカスがある状態で Enter を押したらエディタへ移って編集を開始する
        // （Ctrl+Tab で SR がファイル名を読む→Enter で本文へ、という流れ）。
        if (e.KeyCode == Keys.Enter)
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
