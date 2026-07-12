namespace yEdit.App.Speech;

/// <summary>
/// 呼び出し元 Label に束縛した IAnnouncer を、起動時の PC-Talker 稼働判定に応じて生成する。
/// PC-Talker 稼働中なら PCTKUsr.dll 直叩き（PcTalkerAnnouncer）、それ以外は UIA 通知（UiaAnnouncer）。
/// 起動時確定方針だが判定自体は <see cref="PcTalkerSpeech.IsRunning"/> の軽量呼び出し（内部で
/// DLL 解決結果はキャッシュされる）に一本化し、複数ダイアログの生成タイミングでも同一手段が選ばれる。
/// </summary>
internal static class AnnouncerFactory
{
    /// <summary>指定 Label に束縛した IAnnouncer を、PC-Talker 稼働判定に応じて生成する。</summary>
    public static IAnnouncer Create(Label label) => PcTalkerSpeech.IsRunning()
        ? new PcTalkerAnnouncer(label)
        : new UiaAnnouncer(label);
}
