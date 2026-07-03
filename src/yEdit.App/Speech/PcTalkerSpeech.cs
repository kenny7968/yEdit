using System.Runtime.InteropServices;

namespace yEdit.App.Speech;

/// <summary>
/// PC-Talker（高知システム開発／AOK）への直接発声。共有DLL PCTKUsr.dll（PC-Talker本体が System32 に導入）の
/// ネイティブ関数を遅延束縛で呼ぶ。同梱不要。
/// - 稼働判定: PCTKStatus()（PC-Talker稼働中=非0、停止中=0）。ブランド・バージョン非依存。
/// - 発話: 既定 PCTKPReadW(text, 1, 1)（priority=1 割り込み・analyze=1）。プローブアプリでは実機可聴確認済み
///   （docs/report-pctalker-speech/2026-06-29-pctk-speech-manual-verification.md キー3）。yEdit 内での可聴は
///   未検証のため、実機で無音なら Speak() の呼び出し行のみを差し替えて再検証する（下記コメント参照）。
/// 非PC-Talker機ではDLL/関数が見つからず IsRunning=false・Speak=false（→ 呼び出し側が UIA へ退避）。
/// 静的 [DllImport("PCTKUsr.dll")] は非PC-Talker機で DllNotFoundException を誘発し得るため避け、LoadLibrary/GetProcAddress を使う。
/// </summary>
internal static class PcTalkerSpeech
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PcTkpReadDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string text, int priority, int analyze);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PcTkStatusDelegate();

    private static PcTkpReadDelegate? _read;
    private static PcTkStatusDelegate? _status;
    private static bool _resolved;

    private static void EnsureResolved()
    {
        if (_resolved) return;
        try
        {
            nint h = LoadLibraryW("PCTKUsr.dll");
            if (h != 0)
            {
                nint pr = GetProcAddress(h, "PCTKPReadW");
                nint ps = GetProcAddress(h, "PCTKStatus");
                if (pr != 0) _read = Marshal.GetDelegateForFunctionPointer<PcTkpReadDelegate>(pr);
                if (ps != 0) _status = Marshal.GetDelegateForFunctionPointer<PcTkStatusDelegate>(ps);
            }
        }
        catch { _read = null; _status = null; }
        _resolved = true; // 成功・失敗の両方を解決完了後にキャッシュ（順序ハザード回避）
    }

    /// <summary>PC-Talker が現在稼働中か（PCTKStatus が非0）。DLL/関数が無ければ false。</summary>
    public static bool IsRunning()
    {
        EnsureResolved();
        if (_status is null) return false;
        try { return _status() != 0; }
        catch { return false; }
    }

    /// <summary>
    /// PC-Talker で発声する。発声手段はここ1箇所に集約。戻り値はハード成否（true=呼べた／false=DLL未解決・例外）。
    /// 無音でも false にはならない点に注意（DLL は可聴を通知しない）。実機で無音なら下記の呼び出し行を差し替える。
    /// </summary>
    public static bool Speak(string message)
    {
        EnsureResolved();
        if (_read is null) return false;
        try
        {
            _read(message, 1, 1); // 既定: priority=1（割り込み）, analyze=1
            // 差し替え候補（実機で無音なら上行をいずれかに変更して再検証）:
            //   _read(message, 0, 1);   // 旧実装（非割り込み・キュー）
            //   PCTKCGuide 等のガイド系（別途 GetProcAddress("PCTKCGuide") の解決と delegate 定義が必要）
            return true;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string procName);
}
