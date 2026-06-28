namespace yEdit.Core.Speech;

/// <summary>
/// 起動中SRから発声モードを選ぶ純ロジック（WinForms非依存・単体テスト可能）。
/// NVDA 優先（受動読みパス ConfigureForCurrentScreenReader と同じ鉄則）。
/// PC-Talker 専用直叩きは「PC-Talker 稼働かつ NVDA 非稼働」のときのみ。それ以外は無害な既定 Uia。
/// </summary>
public static class SrSpeechSelector
{
    public static SpeechMode Select(bool nvdaRunning, bool pcTalkerRunning)
        => (pcTalkerRunning && !nvdaRunning) ? SpeechMode.PcTalker : SpeechMode.Uia;
}
