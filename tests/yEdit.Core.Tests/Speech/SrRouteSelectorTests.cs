using yEdit.Core.Speech;
using Xunit;

namespace yEdit.Core.Tests.Speech;

/// <summary>
/// 決定表（設計 2026-07-04 §読み上げタブ）:
/// 優先 SR が稼働 or どちらも非稼働 → 優先 SR の経路。もう片方のみ稼働 → 検出された方（救済）。
/// </summary>
public class SrRouteSelectorTests
{
    [Theory]
    // 優先 = NVDA（既定）
    [InlineData(true,  true,  false, SrRoute.Nvda)]     // NVDA のみ → NVDA（現行同）
    [InlineData(true,  false, true,  SrRoute.PcTalker)] // PC-Talker のみ → PC-Talker（救済・現行同）
    [InlineData(true,  true,  true,  SrRoute.Nvda)]     // 両方 → 優先 NVDA（現行同）
    [InlineData(true,  false, false, SrRoute.Nvda)]     // どちらも非稼働 → NVDA（後から起動に対応）
    // 優先 = PC-Talker
    [InlineData(false, true,  false, SrRoute.Nvda)]     // NVDA のみ → NVDA（救済）
    [InlineData(false, false, true,  SrRoute.PcTalker)] // PC-Talker のみ → PC-Talker
    [InlineData(false, true,  true,  SrRoute.PcTalker)] // 両方 → 優先 PC-Talker（設定が勝つ・新規挙動）
    [InlineData(false, false, false, SrRoute.PcTalker)] // どちらも非稼働 → PC-Talker（後から起動に対応）
    public void Select_resolves_route(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning, SrRoute expected)
        => Assert.Equal(expected, SrRouteSelector.Select(preferNvda, nvdaRunning, pcTalkerRunning));
}
