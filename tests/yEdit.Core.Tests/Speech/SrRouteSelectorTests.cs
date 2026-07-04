using yEdit.Core.Speech;
using Xunit;

namespace yEdit.Core.Tests.Speech;

/// <summary>
/// 決定表（設計 2026-07-04 sr-route-no-sr）:
/// 検出された SR の経路。両方稼働なら「優先するスクリーンリーダー」設定が決める。
/// どちらも非検出なら汎用 UIA 経路。
/// </summary>
public class SrRouteSelectorTests
{
    [Theory]
    // 優先 = NVDA（既定）
    [InlineData(true,  true,  false, SrRoute.Nvda)]     // NVDA のみ → NVDA
    [InlineData(true,  false, true,  SrRoute.PcTalker)] // PC-Talker のみ → PC-Talker（検出優先）
    [InlineData(true,  true,  true,  SrRoute.Nvda)]     // 両方 → 優先 NVDA
    [InlineData(true,  false, false, SrRoute.Uia)]      // どちらも非稼働 → 汎用 UIA（SR なし・他 UIA 系 SR で安全）
    // 優先 = PC-Talker
    [InlineData(false, true,  false, SrRoute.Nvda)]     // NVDA のみ → NVDA（検出優先）
    [InlineData(false, false, true,  SrRoute.PcTalker)] // PC-Talker のみ → PC-Talker
    [InlineData(false, true,  true,  SrRoute.PcTalker)] // 両方 → 優先 PC-Talker（設定が勝つ）
    [InlineData(false, false, false, SrRoute.Uia)]      // どちらも非稼働 → 汎用 UIA
    public void Select_resolves_route(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning, SrRoute expected)
        => Assert.Equal(expected, SrRouteSelector.Select(preferNvda, nvdaRunning, pcTalkerRunning));
}
