namespace yEdit.Editor;

/// <summary>
/// Ctrl+←→ による単語ナビ(選択拡張中でない場合)が発生したときの通知用 EventArgs(P5 Task 12)。
/// PC-Talker では単語スパン発声のため App 層 Announcer から購読する。
/// UIA プロバイダの選択イベントとは独立=<see cref="EditorControl.RaiseUiaSelectionEvents"/>=false の
/// ときは発火しない(CSV グリッドモード等で選択イベントを抑止する要件に相乗り)。
/// </summary>
public sealed class WordNavigatedEventArgs : System.EventArgs
{
    /// <summary>単語スパンの開始オフセット(UTF-16)。</summary>
    public int WordStart { get; }
    /// <summary>単語スパンの終端オフセット(UTF-16・排他)。</summary>
    public int WordEnd { get; }

    public WordNavigatedEventArgs(int wordStart, int wordEnd)
    {
        WordStart = wordStart;
        WordEnd = wordEnd;
    }
}
