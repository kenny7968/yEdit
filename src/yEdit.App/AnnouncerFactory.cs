using yEdit.App.Speech;
using yEdit.Core.Speech;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 起動時に一度だけ SR を判定して発声モードを確定し、各呼び出し元 Label 用の IAnnouncer を生成する。
/// 起動後の SR 起動/終了には追従しない（受動読みパスと一貫した起動時確定方針）。
/// <para>呼び出しは WinForms UI スレッドからのみを前提とする（_mode の ??= キャッシュは非アトミック）。
/// 将来バックグラウンド検出やライブ追従を足す場合は同期＋キャッシュ無効化口が必要。</para>
/// </summary>
internal static class AnnouncerFactory
{
    private static SpeechMode? _mode;

    /// <summary>確定済みの発声モード（初回アクセス時に1回だけ評価しキャッシュ）。</summary>
    public static SpeechMode Mode =>
        _mode ??= SrSpeechSelector.Select(ScreenReaders.IsNvdaRunning(), PcTalkerSpeech.IsRunning());

    /// <summary>指定 Label に束縛した IAnnouncer を、確定済みモードに応じて生成する。</summary>
    public static IAnnouncer Create(Label label) => Mode switch
    {
        SpeechMode.PcTalker => new PcTalkerAnnouncer(label),
        _ => new UiaAnnouncer(label),
    };
}
