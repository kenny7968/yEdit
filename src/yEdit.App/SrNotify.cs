using yEdit.App.Speech;
using yEdit.Core.Speech;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// SR への能動通知の共通実装。Label を視覚表示しつつ、SR適応の音声出力層へ委譲する。
/// PC-Talker 稼働時は PCTKUSR.dll で直接発声し、それ以外/失敗時は UIA 通知へフォールバック。
/// 実機 SR 調整時の変更点を 1 箇所に集約する（Announcer / FindReplaceDialog / GrepDialog から共有）。
/// </summary>
internal static class SrNotify
{
    private static readonly SpeechRouter _router =
        new(new PcTalkerSpeech(), ScreenReaders.IsPcTalkerRunning);

    public static void Raise(Label label, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        label.Text = message; // 視覚フィードバックは無条件（晴眼/弱視も第一級）
        _router.Speak(message, new UiaNotificationSpeech(label));
    }
}
