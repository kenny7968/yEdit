using System.Diagnostics;
using yEdit.Core.Speech;

namespace yEdit.App.Speech;

/// <summary>
/// 起動時に一度だけ確定する SR 経路。受動読み（UIA プロバイダの提供可否 =
/// <see cref="yEdit.Editor.ScintillaHost.ApplySrAdaptation"/>）と能動通知（発声モード =
/// <see cref="AnnouncerFactory"/>）の両経路が、ここで確定した同じ <see cref="Route"/> を消費する。
/// 規則（設計 2026-07-04 sr-route-no-sr）: 検出された SR の経路。両方稼働なら「優先するスクリーンリーダー」
/// 設定が決める。どちらも非検出なら汎用 UIA 経路（SrRoute.Uia）。純ロジックは Core の SrRouteSelector。
/// 起動後の SR 起動/終了には追従しない（起動時確定方針）。「優先するスクリーンリーダー」の変更は再起動後に有効。
/// <see cref="Detect"/> は Program.Main が UI 開始前に1回だけ呼ぶ。
/// </summary>
internal static class SrContext
{
    /// <summary>確定済みの読み上げ経路（未検出時は無害な既定 = 汎用 UIA 経路）。</summary>
    public static SrRoute Route { get; private set; } = SrRoute.Uia;

    /// <summary>受動読みをネイティブ Scintilla に任せるか（ScintillaHost.ApplySrAdaptation へ渡す）。</summary>
    public static bool UseNativeReading => Route == SrRoute.Nvda;

    /// <summary>確定済みの発声モード（AnnouncerFactory・空行発声の判定が消費）。</summary>
    public static SpeechMode Mode => Route == SrRoute.PcTalker ? SpeechMode.PcTalker : SpeechMode.Uia;

    /// <summary>SR 経路を判定して確定する。Program.Main の UI 開始前に1回だけ呼ぶこと。</summary>
    public static void Detect(bool preferNvda)
        => Route = SrRouteSelector.Select(preferNvda, IsNvdaRunning(), PcTalkerSpeech.IsRunning());

    /// <summary>NVDA 本体プロセスが動いているか。</summary>
    private static bool IsNvdaRunning()
    {
        try { return Process.GetProcessesByName("nvda").Length > 0; }
        catch { return false; }
    }
}
