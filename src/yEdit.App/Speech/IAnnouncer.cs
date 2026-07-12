namespace yEdit.App.Speech;

/// <summary>
/// SR への能動通知。底部/ステータス Label を視覚表示しつつ、起動時に確定した SR 別手段で発声する。
/// 空メッセージは「視覚クリアのみ・発声なし」（表示と読み上げを1呼び出しで扱えるようにする契約）。
/// 実体は AnnouncerFactory が PC-Talker 稼働判定に応じて生成（UiaAnnouncer / PcTalkerAnnouncer）。
/// </summary>
public interface IAnnouncer
{
    void Say(string message);
}
