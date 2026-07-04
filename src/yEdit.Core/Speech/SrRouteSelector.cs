namespace yEdit.Core.Speech;

/// <summary>
/// 「優先するスクリーンリーダー」設定と起動時のプロセス検出から読み上げ経路を選ぶ純ロジック
/// （WinForms 非依存・単体テスト可能）。判定は App 層の SrContext が起動時に 1 回行う。
/// 規則（設計 2026-07-04 sr-route-no-sr）:
/// 検出された SR の経路。両方稼働なら「優先するスクリーンリーダー」設定が決める。
/// どちらも非検出なら汎用 UIA 経路（SR なし・ナレーター/JAWS 等の UIA 系 SR で安全）。
/// </summary>
public static class SrRouteSelector
{
    public static SrRoute Select(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning)
    {
        if (nvdaRunning && pcTalkerRunning) return preferNvda ? SrRoute.Nvda : SrRoute.PcTalker;
        if (nvdaRunning) return SrRoute.Nvda;
        if (pcTalkerRunning) return SrRoute.PcTalker;
        return SrRoute.Uia;
    }
}
