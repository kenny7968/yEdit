namespace yEdit.App.Speech;

/// <summary>
/// PC-Talker 直叩きで発声する Announcer（起動時に PC-Talker 稼働＆NVDA非稼働で選択）。
/// 空ガード・視覚表示は <see cref="AnnouncerBase"/> が担う。発声のハード失敗（false/例外）時のみ
/// UIA 通知へ退避する（無音は false にならないため audibility フォールバックではない）。
/// </summary>
internal sealed class PcTalkerAnnouncer : AnnouncerBase
{
    public PcTalkerAnnouncer(Label label) : base(label) { }

    protected override void Speak(string message)
    {
        if (!PcTalkerSpeech.Speak(message))
            UiaAnnouncer.Raise(_label, message); // ハード失敗時のみ退避
    }
}
