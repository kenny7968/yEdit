using System.Runtime.InteropServices;

namespace yEdit.Editor;

internal static class NativeMethods
{
    public const int WM_GETOBJECT = 0x003D;

    /// <summary>UIA がプロバイダを要求するときの WM_GETOBJECT lParam。</summary>
    public const int UiaRootObjectId = -25;

    /// <summary>MSAA クライアント領域オブジェクト（OBJID_CLIENT）。</summary>
    public const int OBJID_CLIENT = -4;

    public const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    public const uint CS_GLOBALCLASS = 0x4000;

    // ---- ウィンドウクラスのクローン用（NVDA の "Scintilla" 正規化回避）----
    // NVDA は WindowsForms10.<X>.app.0.NNN を <X> に正規化し、X=="Scintilla" だと
    // ネイティブ Scintilla オーバーレイを UIA オブジェクトに被せて競合させ無音になる。
    // そこで Scintilla の登録済みクラスを別名でクローンし、WinForms にそれを
    // スーパークラス化させて X を非 "Scintilla" にする。

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;   // IntPtr のまま（マーシャリング回避）
        public nint lpszClassName;  // IntPtr のまま（クローン時に差し替え）
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetClassInfoExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClassInfoExW(nint hInstance, string lpClassName, ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClassExW")]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    public static extern nint GetModuleHandleW(string? lpModuleName);
}
