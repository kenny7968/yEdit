using System.Runtime.InteropServices;

namespace yEdit.UiaProbe;

internal static class NativeMethods
{
    public const int WM_GETOBJECT = 0x003D;

    /// <summary>UIA がプロバイダを要求するときの WM_GETOBJECT lParam。</summary>
    public const int UiaRootObjectId = -25;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateCaret(nint hWnd, nint hBitmap, int nWidth, int nHeight);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCaretPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowCaret(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyCaret();
}
