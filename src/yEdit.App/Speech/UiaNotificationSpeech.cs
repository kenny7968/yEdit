using System.Windows.Forms.Automation;
using yEdit.Core.Speech;

namespace yEdit.App.Speech;

/// <summary>
/// 既存の UIA 通知（RaiseAutomationNotification）を channel 化したもの。
/// NVDA・その他SR、および PC-Talker フォールバックを担う常設の最終手段。
/// 呼び出し毎に通知元 Label に束縛して生成する。
/// </summary>
internal sealed class UiaNotificationSpeech : ISpeechChannel
{
    private readonly Label _label;
    public UiaNotificationSpeech(Label label) => _label = label;

    public string Name => "UIA";

    public bool TrySpeak(string message)
    {
        try
        {
            _label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message);
            return true;
        }
        catch { return false; } // 通知非対応環境では false（視覚表示にフォールバック）
    }
}
