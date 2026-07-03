using System.Diagnostics;
using yEdit.Core.Speech;

namespace yEdit.App.Speech;

/// <summary>
/// 起動時に一度だけ確定する SR 環境。受動読み（UIA プロバイダの提供可否 =
/// <see cref="yEdit.Editor.ScintillaHost.ApplySrAdaptation"/>）と能動通知（発声モード =
/// <see cref="AnnouncerFactory"/>）の両経路が、ここで確定した同じ判定結果を消費する。
/// 確定アーキテクチャ: NVDA 起動中はネイティブ Scintilla に任せ（UIA/MSAA を出さない）、
/// それ以外（PC-Talker など）は我々の UIA プロバイダ＋SR 別発声を適用する。
/// 起動後の SR 起動/終了には追従しない（起動時確定方針）。
/// <see cref="Detect"/> は Program.Main が UI 開始前に1回だけ呼ぶ。
/// </summary>
internal static class SrContext
{
    /// <summary>NVDA 本体プロセスが起動時に動いていたか（受動読みパスの SR 適応に使う）。</summary>
    public static bool NvdaRunning { get; private set; }

    /// <summary>確定済みの発声モード（未検出時は無害な既定 Uia）。</summary>
    public static SpeechMode Mode { get; private set; } = SpeechMode.Uia;

    /// <summary>SR 環境を判定して確定する。Program.Main の UI 開始前に1回だけ呼ぶこと。</summary>
    public static void Detect()
    {
        NvdaRunning = IsNvdaRunning();
        Mode = SrSpeechSelector.Select(NvdaRunning, PcTalkerSpeech.IsRunning());
    }

    /// <summary>NVDA 本体プロセスが動いているか。判定の要は「NVDA が動いているか」だけ。</summary>
    private static bool IsNvdaRunning()
    {
        try { return Process.GetProcessesByName("nvda").Length > 0; }
        catch { return false; }
    }
}
