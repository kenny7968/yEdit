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

    public Document(ScintillaHost editor, TabPage page)
    {
        Editor = editor;
        Page = page;
    }

    /// <summary>タブに表示するラベル（ファイル名＋変更マーク）。</summary>
    public string TabLabel => State.DisplayName + (Editor.Modified ? " *" : "");
}
