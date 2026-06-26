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
        _tabs.SelectedTab = page; // 既存タブがあれば Selected 発火→フォーカス＋ActiveDocumentChanged
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
        if (_tabs.SelectedTab != doc.Page) _tabs.SelectedTab = doc.Page; // Selected 経由でフォーカス
        else doc.Editor.Focus();
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
    }

    public void SelectAt(int index)
    {
        if (index >= 0 && index < _tabs.TabPages.Count) _tabs.SelectedIndex = index;
    }

    public void UpdateLabel(Document doc) => doc.Page.Text = doc.TabLabel;

    private void OnSelectedTabChanged()
    {
        // NVDA は「フォーカスした要素」を読む。まずタブ列へフォーカスして標準のタブ読み上げ
        // （ファイル名＋位置）を促し、続けてエディタへフォーカスを戻して編集を継続できるようにする
        // （案2: 一瞬タブ→エディタ）。BeginInvoke でタブのフォーカスイベントを先に処理させる。
        _tabs.Focus();
        // 連続切替に備え、遅延実行時点で選択中のエディタへフォーカスする（古いタブを掴まない）。
        if (Active is not null) _tabs.BeginInvoke(new Action(() => Active?.Editor.Focus()));
        ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDirtyChanged(Document doc)
    {
        UpdateLabel(doc);
        if (ReferenceEquals(doc, Active)) ActiveDirtyChanged?.Invoke(this, EventArgs.Empty);
    }
}
