using System.Runtime.InteropServices;
using yEdit.Core.Speech;

namespace yEdit.App.Speech;

/// <summary>
/// PC-Talker（高知システム開発）への直接発声。共有DLL PCTKUsr.dll の
/// PCTKPReadW(LPCWSTR text, int priority, BOOL analyze)（PC-Talker ネイティブの「テキストを読む」関数）を
/// 遅延束縛で呼ぶ。DLL は PC-Talker 本体が System32 に導入するため同梱不要。
/// 非 PC-Talker 機では DLL/関数が見つからず TrySpeak は false（→ ルータが UIA へフォールバック）。
/// 静的 [DllImport("PCTKUsr.dll")] は非 PC-Talker 機で DllNotFoundException を誘発し得るため避け、
/// LoadLibrary/GetProcAddress を使う。
///
/// 注: 95Reader 互換の SoundMessage は現行 PC-Talker(Neo) では TRUE を返すが無音のため使わない。
/// 実機 PC-Talker Neo 12.0.4.0 で PCTKPReadW(text, priority=0, analyze=1) の発話を確認済み
/// （analyze=1 で記号・数字等の読み解析を有効化）。
/// </summary>
internal sealed class PcTalkerSpeech : ISpeechChannel
{
    public string Name => "PC-Talker";

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PcTkpReadDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string text, int priority, int analyze);

    private static PcTkpReadDelegate? _read;
    private static bool _resolved;

    private static PcTkpReadDelegate? Resolve()
    {
        if (_resolved) return _read;
        try
        {
            nint h = LoadLibraryW("PCTKUsr.dll");
            nint p = h == 0 ? 0 : GetProcAddress(h, "PCTKPReadW");
            _read = p == 0 ? null : Marshal.GetDelegateForFunctionPointer<PcTkpReadDelegate>(p);
        }
        catch { _read = null; }
        _resolved = true; // 成功・失敗の両方を解決完了後にキャッシュ（_resolved を先に立てない＝順序ハザード回避）
        return _read;
    }

    public bool TrySpeak(string message)
    {
        var fn = Resolve();
        if (fn is null) return false;
        try { fn(message, 0, 1); return true; }  // priority=0, analyze=1。割り込み等の調整は実機検証で
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string procName);
}
