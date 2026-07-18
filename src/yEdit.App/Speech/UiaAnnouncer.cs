using System.Windows.Forms.Automation;

namespace yEdit.App.Speech;

/// <summary>
/// UIA 通知(RaiseAutomationNotification)で読ませる Announcer。
/// 「視覚表示(label.Text)は無条件・発声は非空メッセージだけ」の契約を担保する。
/// 空メッセージは視覚クリアのみ(発声なし)=SR に空通知を撃たない。
/// UIA 非対応環境では <see cref="Raise"/> の catch で握りつぶし、視覚のみに縮退する。
/// </summary>
internal class UiaAnnouncer : IAnnouncer
{
    protected readonly Label _label;

    public UiaAnnouncer(Label label) => _label = label;

    public void Say(string message)
    {
        _label.Text = message ?? ""; // 視覚フィードバックは無条件(晴眼/弱視も第一級)。空はクリア
        if (string.IsNullOrEmpty(message))
            return; // 空は視覚クリアのみ(発声なし)
        Raise(message);
    }

    /// <summary>確定済み(非空)メッセージを Label の UIA プロバイダから通知として上げる。
    /// 空ガードと視覚表示は <see cref="Say"/> が済ませているため、ここでは message が非空であることが前提。
    /// テストではオーバーライドして発声呼び出しを観測する(発声手段の seam)。</summary>
    protected virtual void Raise(string message)
    {
        try
        {
            _label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message
            );
        }
        catch
        { /* 通知非対応環境では視覚表示のみ */
        }
    }
}
