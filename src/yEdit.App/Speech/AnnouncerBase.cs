namespace yEdit.App.Speech;

/// <summary>
/// IAnnouncer 共通の通知コントラクトを1箇所に集約するテンプレート基底。
/// 「視覚表示（label.Text）は発声手段に依らず無条件（空はクリア）」「発声は非空のときだけ」を
/// 全実装で強制し、発声手段だけを派生（<see cref="Speak"/>）に委ねる。
/// これにより呼び出し元ごと・実装ごとの契約乖離（視覚と読み上げの二重更新）を防ぐ。
/// </summary>
internal abstract class AnnouncerBase : IAnnouncer
{
    protected readonly Label _label;

    protected AnnouncerBase(Label label) => _label = label;

    public void Say(string message)
    {
        _label.Text = message ?? ""; // 視覚フィードバックは無条件（晴眼/弱視も第一級）。空はクリア
        if (string.IsNullOrEmpty(message))
            return; // 空は視覚クリアのみ（発声なし）
        Speak(message);
    }

    /// <summary>確定済み（非空）メッセージを派生の発声手段（現状は UiaAnnouncer の UIA 通知のみ）で発声する。視覚表示は基底が済ませている。</summary>
    protected abstract void Speak(string message);
}
