namespace yEdit.ScintillaProbe;

/// <summary>
/// 使用する SCI_* メッセージ定数（Scintilla.h より）。
/// 本ホストは UI スレッドからのみ DirectMessage を呼ぶ（RPC スレッドはスナップショット応答）。
/// </summary>
internal static class Sci
{
    public const int SCI_SETCODEPAGE = 2037;
    public const int SC_CP_UTF8 = 65001;

    public const int SCI_GETLENGTH = 2006;          // 本文のバイト長
    public const int SCI_GETTEXT = 2182;            // (length, char* buf) 全文取得（UTF-8）

    public const int SCI_GETSELECTIONSTART = 2143;  // 選択開始（バイト）
    public const int SCI_GETSELECTIONEND = 2145;    // 選択終了（バイト）
    public const int SCI_GETCURRENTPOS = 2008;      // キャレット（バイト）
    public const int SCI_SETSEL = 2160;             // (anchor, caret) 選択設定（バイト）

    // 座標系（§6 で実装）。lParam = byte position。
    public const int SCI_POINTXFROMPOSITION = 2164;
    public const int SCI_POINTYFROMPOSITION = 2165;
    public const int SCI_CHARPOSITIONFROMPOINT = 2561;
}
