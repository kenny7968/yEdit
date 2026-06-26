using System.Windows.Forms.Automation;

namespace yEdit.App;

/// <summary>
/// SR への能動通知の共通実装。Label を視覚表示しつつ、その UIA プロバイダから
/// RaiseAutomationNotification を上げて SR に読ませる。実機 SR 調整時の変更点を 1 箇所に集約する
/// （Announcer / FindReplaceDialog / GrepDialog から共有）。
/// </summary>
internal static class SrNotify
{
    public static void Raise(Label label, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        label.Text = message; // 視覚フィードバック
        try
        {
            label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message);
        }
        catch { /* 通知非対応環境では視覚表示にフォールバック */ }
    }
}
