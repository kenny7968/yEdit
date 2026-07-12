using yEdit.Core.Csv;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 開いている1ドキュメント。独立した編集コントロール・タブ面・メタ状態を束ねる。
/// 変更フラグは Editor.Modified（TextBuffer のセーブポイント）を唯一の真実とする。
/// </summary>
public sealed class Document
{
    public EditorControl Editor { get; }
    public TabPage Page { get; }
    public DocumentState State { get; } = new();

    /// <summary>CSVモード中のフォーカス退避先(P5 まで)。P6 では EditorControl 単一 SR 経路
    /// (UIA v2)に統一するため、実効的にフォーカスは常に Editor に向かう。CsvSink 生成自体は
    /// 残し(P7 の完全撤去まで温存=Page.Controls への追加が消えることでレイアウトが揺れない
    /// ようにする)、FocusTarget からは切り離す(§0-8「無効化のみで残す」)。</summary>
    public CsvFocusSink CsvSink { get; }

    /// <summary>「編集領域」へフォーカスを戻すときの正しい行き先。P6 では常に Editor。
    /// P5 まで CSV モード中はシンクへ退避していたが、Task 15 で UseNativeReading=false 固定に
    /// 揃えるため、Task 13 の段階で FocusTarget を Editor 固定にしておく(実効的な意味論変更は
    /// UIA v2 単一経路への統一のみ)。</summary>
    public Control FocusTarget => Editor;

    public Document(EditorControl editor, TabPage page)
    {
        Editor = editor;
        Page = page;
        CsvSink = new CsvFocusSink();
        page.Controls.Add(CsvSink);   // editor(Dock=Fill) より後に追加 → Z順で背面
        CsvSink.SendToBack();         // 呼び出し順に依存せず背面を自己保証
    }

    /// <summary>タブに表示するラベル（ファイル名＋変更マーク）。</summary>
    public string TabLabel => State.DisplayName + (Editor.Modified ? " *" : "");

    // ---- CSV パースのメモ化（文書単位） ----
    // コントローラ単位で持つとタブ横断で直近文書の全文＋パース結果が滞留し、
    // 複数 CSV タブの行き来で毎回再パースになるため、文書と同寿命でここに持つ。
    private string? _csvCachedText;
    private CsvDocument? _csvCachedDoc;

    /// <summary>SnapshotText の参照同一性でメモ化した CSV パース。編集でスナップショットが
    /// 差し替わると自動失効する。キャッシュは文書と同寿命（タブを閉じれば解放）。</summary>
    public CsvDocument ParseCsv()
    {
        string text = Editor.SnapshotText;
        if (!ReferenceEquals(text, _csvCachedText))
        {
            _csvCachedDoc = CsvParser.Parse(text);
            _csvCachedText = text;
        }
        return _csvCachedDoc!;
    }

    /// <summary>CSV パースキャッシュを明示解放する（CSVモード解除・開き直し時のメモリ解放）。</summary>
    public void ClearCsvCache()
    {
        _csvCachedText = null;
        _csvCachedDoc = null;
    }
}
