using System.Runtime.InteropServices;

namespace yEdit.Editor;

internal static class NativeMethods
{
    public const int WM_GETOBJECT = 0x003D;

    /// <summary>UIA がプロバイダを要求するときの WM_GETOBJECT lParam。</summary>
    public const int UiaRootObjectId = -25;

    // Win32 システムキャレット API(UI スレッド専用)。P2 Task 10 で EditorControl から使用。
    // フォーカスを持つウィンドウ毎に 1 個だけ。CreateCaret 後 ShowCaret で表示・DestroyCaret で破棄。

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateCaret(nint hWnd, nint hBitmap, int nWidth, int nHeight);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCaretPos(int X, int Y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowCaret(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyCaret();

    // GetCaretPos は Task 7 レビュー C-1 の回帰テストで使用(EnsureVisibleCharRange 後に
    // OS 側キャレット位置が savedCaret に戻っていることを検証する)。
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCaretPos(out System.Drawing.Point lpPoint);
}
