using System.Windows.Forms.Automation;

namespace yEdit.App.Speech;

/// <summary>
/// UIA 通知（RaiseAutomationNotification）で読ませる Announcer。NVDA・その他SR・既定。
/// 空ガード・視覚表示は <see cref="AnnouncerBase"/> が担う。
/// </summary>
internal sealed class UiaAnnouncer : AnnouncerBase
{
    public UiaAnnouncer(Label label) : base(label) { }

    protected override void Speak(string message) => Raise(_label, message);

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
