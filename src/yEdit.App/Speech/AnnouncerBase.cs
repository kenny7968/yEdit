namespace yEdit.App.Speech;

/// <summary>
/// IAnnouncer 共通の通知コントラクトを1箇所に集約するテンプレート基底。
/// 「空メッセージは無視」「視覚表示（label.Text）は SR 手段に依らず無条件」を全実装で強制し、
/// 発声手段だけを派生（<see cref="Speak"/>）に委ねる。これにより呼び出し元ごと・実装ごとの契約乖離を防ぐ。
/// </summary>
internal abstract class AnnouncerBase : IAnnouncer
{
    protected readonly Label _label;
    protected AnnouncerBase(Label label) => _label = label;

    public void Say(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _label.Text = message; // 視覚フィードバックは無条件（晴眼/弱視も第一級）
        Speak(message);
    }

    /// <summary>確定済み（非空）メッセージを SR 別手段で発声する。視覚表示は基底が済ませている。</summary>
    protected abstract void Speak(string message);
}
