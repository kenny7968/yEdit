namespace yEdit.Core.Speech;

/// <summary>
/// 起動時に確定する読み上げ経路。受動読み（UIA プロバイダ可否 = ScintillaHost.ApplySrAdaptation）と
/// 能動発声（Announcer 選択）は常にペアで同じ経路に従う。
/// </summary>
public enum SrRoute
{
    /// <summary>NVDA 経路: UIA プロバイダを出さずネイティブ Scintilla 読みに任せ、能動発声は UIA 通知。</summary>
    Nvda,
    /// <summary>PC-Talker 経路: 自前 UIA プロバイダ提供、能動発声は PCTKPReadW 直叩き。</summary>
    PcTalker,
    /// <summary>汎用 UIA 経路: SR 非検出時。自前 UIA プロバイダ提供、能動発声は UIA 通知（SR なし・ナレーター/JAWS 等の UIA 系 SR で安全）。</summary>
    Uia,
}
