namespace yEdit.App.Speech;

/// <summary>
/// 呼び出し元 Label に束縛した IAnnouncer(UIA 通知)を生成する。
/// PC-Talker サポート廃止(docs/plans/2026-07-13-pctalker-removal-design.md)により経路分岐は撤去し、
/// 常に UiaAnnouncer を返す。static 解消等の構造整理はテスト戦略 Phase 2 Stage 2(縮小版)で判断する。
/// </summary>
internal static class AnnouncerFactory
{
    /// <summary>指定 Label に束縛した UiaAnnouncer を生成する。</summary>
    public static IAnnouncer Create(Label label) => new UiaAnnouncer(label);
}
