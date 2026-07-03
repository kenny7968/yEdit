namespace yEdit.App.Speech;

/// <summary>
/// SR への能動通知。底部/ステータス Label を視覚表示しつつ、起動時に確定した SR 別手段で発声する。
/// 実体は AnnouncerFactory が SpeechMode に応じて生成（UiaAnnouncer / PcTalkerAnnouncer）。
/// </summary>
public interface IAnnouncer
{
    void Say(string message);
}
