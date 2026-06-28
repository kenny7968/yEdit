namespace yEdit.Core.Speech;

/// <summary>
/// SR適応の音声出力ルータ。PC-Talker 稼働中のみ専用経路を先に試し、
/// 未発声なら UIA 通知（fallback）へ。判定は注入された delegate で行い、単体テスト可能にする。
/// </summary>
public sealed class SpeechRouter
{
    private readonly ISpeechChannel _pcTalker;
    private readonly Func<bool> _isPcTalkerRunning;

    public SpeechRouter(ISpeechChannel pcTalker, Func<bool> isPcTalkerRunning)
    {
        _pcTalker = pcTalker;
        _isPcTalkerRunning = isPcTalkerRunning;
    }

    /// <summary>message を発声する。fallback は呼び出し時の Label に束縛された UIA 経路。</summary>
    public void Speak(string message, ISpeechChannel fallback)
    {
        if (_isPcTalkerRunning() && _pcTalker.TrySpeak(message)) return;
        fallback.TrySpeak(message);
    }
}
