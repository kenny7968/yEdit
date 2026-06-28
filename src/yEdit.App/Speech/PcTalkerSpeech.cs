using System.Runtime.InteropServices;
using yEdit.Core.Speech;

namespace yEdit.App.Speech;

/// <summary>
/// PC-Talker（高知システム開発）への直接発声。共有DLL PCTKUSR.dll の
/// SoundMessage(LPCWSTR, int) を遅延束縛で呼ぶ。DLLはPC-Talker本体がWindowsフォルダに導入するため
/// 同梱不要。非PC-Talker機ではDLL/関数が見つからず TrySpeak は false（→ルータがUIAへフォールバック）。
/// 静的 [DllImport] は非PC-Talker機で DllNotFoundException を誘発し得るため避け、LoadLibrary/GetProcAddress を使う。
/// </summary>
public sealed class PcTalkerSpeech : ISpeechChannel
{
    public string Name => "PC-Talker";

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SoundMessageDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string text, int flags);

    private static SoundMessageDelegate? _soundMessage;
    private static bool _resolved;

    private static SoundMessageDelegate? Resolve()
    {
        if (_resolved) return _soundMessage;
        _resolved = true;
        try
        {
            nint h = LoadLibraryW("PCTKUsr.dll");
            if (h == 0) return _soundMessage = null;
            nint p = GetProcAddress(h, "SoundMessage");
            if (p == 0) return _soundMessage = null;
            _soundMessage = Marshal.GetDelegateForFunctionPointer<SoundMessageDelegate>(p);
        }
        catch { _soundMessage = null; }
        return _soundMessage;
    }

    public bool TrySpeak(string message)
    {
        var fn = Resolve();
        if (fn is null) return false;
        try { fn(message, 0); return true; }  // flags=0。割り込み等の調整は実機検証で
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string procName);
}
