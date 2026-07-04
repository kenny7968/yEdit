namespace yEdit.Core.Speech;

/// <summary>
/// 「優先するスクリーンリーダー」設定と起動時のプロセス検出から読み上げ経路を選ぶ純ロジック
/// （WinForms 非依存・単体テスト可能）。判定は App 層の SrContext が起動時に 1 回行う。
/// 規則（検出フォールバック付き・設計 2026-07-04）:
/// 優先 SR が稼働している、またはどちらも稼働していない → 優先 SR の経路。
/// もう片方だけが稼働 → 検出された方の経路（既定 NVDA のままの PC-Talker ユーザーを壊さない救済）。
/// </summary>
public static class SrRouteSelector
{
    public static SrRoute Select(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning)
    {
        if (preferNvda)
            return (!nvdaRunning && pcTalkerRunning) ? SrRoute.PcTalker : SrRoute.Nvda;
        return (nvdaRunning && !pcTalkerRunning) ? SrRoute.Nvda : SrRoute.PcTalker;
    }
}
