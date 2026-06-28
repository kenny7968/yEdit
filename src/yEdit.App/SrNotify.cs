using System.Windows.Forms.Automation;

namespace yEdit.App;

internal static class SrNotify
{
    public static void Raise(Label label, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        label.Text = message;
        if (Speech.PcTalkerSpeech.IsRunning() && Speech.PcTalkerSpeech.Speak(message)) return;
        try
        {
            label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent, message);
        }
        catch { }
    }
}
