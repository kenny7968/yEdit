using System.Windows.Forms.Automation;

namespace yEdit.App.Speech;

/// <summary>
/// UIA 通知（RaiseAutomationNotification）で読ませる Announcer。NVDA・その他SR・既定。
/// 視覚表示（label.Text）は無条件。PC-Talker のハード失敗退避でも Raise を再利用する。
/// </summary>
internal sealed class UiaAnnouncer : IAnnouncer
{
    private readonly Label _label;
    public UiaAnnouncer(Label label) => _label = label;

    public void Say(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _label.Text = message; // 視覚フィードバックは無条件（晴眼/弱視も第一級）
        Raise(_label, message);
    }

    /// <summary>指定 Label の UIA プロバイダから通知を上げる。非対応環境では握りつぶし（視覚のみ）。</summary>
    internal static void Raise(Label label, string message)
    {
        try
        {
            label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message);
        }
        catch { /* 通知非対応環境では視覚表示のみ */ }
    }
}
