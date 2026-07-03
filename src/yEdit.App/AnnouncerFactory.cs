using yEdit.App.Speech;
using yEdit.Core.Speech;

namespace yEdit.App;

/// <summary>
/// 起動時に確定した発声モード（<see cref="SrContext.Mode"/>）に応じて、
/// 呼び出し元 Label に束縛した IAnnouncer を生成する。
/// </summary>
internal static class AnnouncerFactory
{
    /// <summary>指定 Label に束縛した IAnnouncer を、確定済みモードに応じて生成する。</summary>
    public static IAnnouncer Create(Label label) => SrContext.Mode switch
    {
        SpeechMode.PcTalker => new PcTalkerAnnouncer(label),
        _ => new UiaAnnouncer(label),
    };
}
