namespace yEdit.Core.Speech;

/// <summary>
/// 1つの音声出力経路。TrySpeak は「この経路で発声を完結できたら true、
/// 未対応/失敗なら false」を返す。false のときルータが次の経路へフォールバックする。
/// </summary>
public interface ISpeechChannel
{
    string Name { get; }
    bool TrySpeak(string message);
}
