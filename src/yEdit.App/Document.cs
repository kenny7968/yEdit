using yEdit.Core.Csv;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 開いている1ドキュメント。独立した編集コントロール・タブ面・メタ状態を束ねる。
/// 変更フラグは Editor.Modified（Scintilla のセーブポイント）を唯一の真実とする。
/// </summary>
public sealed class Document
{
    public ScintillaHost Editor { get; }
    public TabPage Page { get; }
    public DocumentState State { get; } = new();

    /// <summary>CSVモード中のフォーカス退避先。生成時に Page へ追加され、
    /// Dock=Fill のエディタの背面に隠れる（視覚影響なし）。</summary>
    public CsvFocusSink CsvSink { get; }

    /// <summary>「編集領域」へフォーカスを戻すときの正しい行き先。CSVモード中はシンク、
    /// 通常時はエディタ。編集領域への Focus() 呼び出しは必ずこれを経由すること。
    /// モード遷移の内部（CsvController.ToggleMode）と F2 編集の復帰先注入（CsvCellEditor.Begin）は
    /// 行き先を明示するため直接指定する。</summary>
    public Control FocusTarget => State.CsvMode ? CsvSink : Editor;

    public Document(ScintillaHost editor, TabPage page)
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
