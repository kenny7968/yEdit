using System.Windows.Forms.Automation;

namespace yEdit.App;

/// <summary>
/// SR への能動的な通知。Label を視覚表示しつつ、その UIA プロバイダから
/// RaiseAutomationNotification を上げて SR に読ませる（M3/M4 のステータス通知と同じ実証済み流儀）。
/// 照会ホットキー・モード切替・grep ジャンプ等から呼ぶ。
/// </summary>
public sealed class Announcer
{
    private readonly Label _label;

    public Announcer(Label label) => _label = label;

    public void Say(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _label.Text = message; // 視覚フィードバック（最後の通知を底部に表示）
        try
        {
            _label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message);
        }
        catch { /* 通知非対応環境では視覚表示にフォールバック */ }
    }
}
