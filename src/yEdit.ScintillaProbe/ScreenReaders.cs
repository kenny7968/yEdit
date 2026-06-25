using System.Diagnostics;

namespace yEdit.ScintillaProbe;

/// <summary>
/// 起動中スクリーンリーダーの判定。確定アーキテクチャ:
/// NVDA 起動中はネイティブ Scintilla に任せ（UIA/MSAA を出さない）、それ以外（PC-Talker
/// など）は我々の UIA プロバイダを適用する。判定の要は「NVDA が動いているか」だけ。
/// </summary>
internal static class ScreenReaders
{
    /// <summary>NVDA 本体プロセスが動いているか。</summary>
    public static bool IsNvdaRunning()
    {
        try { return Process.GetProcessesByName("nvda").Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// PC-Talker（高知システム開発）らしきプロセスが動いているか。
    /// プロセス名は環境差があるため前方一致で広めに拾う（判定の主役ではなく情報目的）。
    /// </summary>
    public static bool IsPcTalkerRunning()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                string n = p.ProcessName;
                if (n.StartsWith("PCTK", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("PCTalker", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* 列挙失敗は無視 */ }
        return false;
    }
}
