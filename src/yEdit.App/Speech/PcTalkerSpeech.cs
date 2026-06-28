using System.Runtime.InteropServices;
using yEdit.Core.Speech;

namespace yEdit.App.Speech;

/// <summary>
/// PC-Talker（高知システム開発／AOK）への直接発声。共有DLL PCTKUsr.dll（PC-Talker本体が System32 に導入）の
/// ネイティブ関数を遅延束縛で呼ぶ。同梱不要。
/// - 稼働判定: PCTKStatus()（PC-Talker稼働中=非0、停止中=0）。プロセス名は Neo でブランドが「AOK」となり
///   PCTK* 名のプロセスが存在せず使えないため、DLL のステータス関数で判定する（バージョン・ブランド非依存）。
/// - 発話: PCTKPReadW(text, priority, analyze)。実機 PC-Talker Neo 12.0.4.0 で PCTKPReadW(text,0,1) の発話を確認済み
///   （analyze=1 で記号・数字等の読み解析を有効化）。95Reader互換 SoundMessage は TRUE を返すが無音のため使わない。
/// 非PC-Talker機ではDLL/関数が見つからず IsRunning=false・TrySpeak=false（→ ルータが UIA へフォールバック）。
/// 静的 [DllImport("PCTKUsr.dll")] は非PC-Talker機で DllNotFoundException を誘発し得るため避け、LoadLibrary/GetProcAddress を使う。
/// PCTKStatus はライブ評価のため、yEdit 起動後に PC-Talker を起動/終了しても追従する（DLLは一度ロードしたまま）。
/// </summary>
internal sealed class PcTalkerSpeech : ISpeechChannel
{
    public string Name => "PC-Talker";

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
        _resolved = true; // 成功・失敗の両方を解決完了後にキャッシュ（_resolved を先に立てない＝順序ハザード回避）
    }

    /// <summary>PC-Talker が現在稼働中か（PCTKStatus が非0）。DLL/関数が無ければ false。</summary>
    public static bool IsRunning()
    {
        EnsureResolved();
        if (_status is null) return false;
        try { return _status() != 0; }
        catch { return false; }
    }

    public bool TrySpeak(string message)
    {
        EnsureResolved();
        if (_read is null) return false;
        try { _read(message, 0, 1); return true; }  // priority=0, analyze=1。割り込み等の調整は実機検証で
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string procName);
}
