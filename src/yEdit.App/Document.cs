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

    /// <summary>「編集領域」へフォーカスを戻すときの正しい行き先。P6 以降は常に Editor
    /// (P5 まで CSV モード中は CsvFocusSink へ退避していたが、UIA v2 単一経路への統一に合わせ Editor 固定・
    /// P7 で CsvFocusSink 自体を完全撤去=§0-8 猶予を解消)。</summary>
    public Control FocusTarget => Editor;

    public Document(EditorControl editor, TabPage page)
    {
        Editor = editor;
        Page = page;
    }

    /// <summary>タブに表示するラベル（変更マーク＋ファイル名）。位置はタイトルバーと揃える。</summary>
    public string TabLabel => (Editor.Modified ? "* " : "") + State.DisplayName;

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
