namespace yEdit.Core.Speech;

/// <summary>SR適応の発声モード。起動時に一度だけ確定する。</summary>
public enum SpeechMode
{
    /// <summary>UIA 通知（RaiseAutomationNotification）。NVDA・その他SR・既定。</summary>
    Uia,
    /// <summary>PC-Talker 直叩き（PCTKUsr.dll）。</summary>
    PcTalker,
}
