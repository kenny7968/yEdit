namespace yEdit.Editor;

internal static class NativeMethods
{
    public const int WM_GETOBJECT = 0x003D;

    /// <summary>UIA がプロバイダを要求するときの WM_GETOBJECT lParam。</summary>
    public const int UiaRootObjectId = -25;

    /// <summary>MSAA クライアント領域オブジェクト（OBJID_CLIENT）。</summary>
    public const int OBJID_CLIENT = -4;
}
