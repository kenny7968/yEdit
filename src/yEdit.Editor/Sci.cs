namespace yEdit.Editor;

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
    public const int SCI_SCROLLCARET = 2169;        // キャレットを可視範囲へスクロール

    // 置換ターゲット（検索・置換で使用。バイト位置）。
    public const int SCI_SETTARGETSTART = 2190;     // 置換ターゲット開始（バイト）
    public const int SCI_SETTARGETEND = 2192;       // 置換ターゲット終了（バイト）
    public const int SCI_REPLACETARGET = 2194;      // (length, char*) ターゲットを置換（UTF-8・1アンドゥ）

    // 座標系（§6 で実装）。lParam = byte position。
    public const int SCI_POINTXFROMPOSITION = 2164;
    public const int SCI_POINTYFROMPOSITION = 2165;
    public const int SCI_CHARPOSITIONFROMPOINT = 2561;

    // 状態取得（ステータスバー行桁・EOL・保存点）。
    public const int SCI_LINEFROMPOSITION = 2166;   // byte pos → 行番号(0始まり)
    public const int SCI_GETCOLUMN = 2129;          // byte pos → 桁(0始まり, タブ考慮)
    public const int SCI_SETEOLMODE = 2031;         // 0=CRLF,1=CR,2=LF
    public const int SCI_GETMODIFY = 2159;          // 変更フラグ
    public const int SCI_SETSAVEPOINT = 2014;       // 保存点を設定（clean 化）
    public const int SCI_EMPTYUNDOBUFFER = 2175;    // Undo バッファ消去

    // 表示折り返し（指定桁）。UI スレッドからのみ送る。
    public const int SCI_SETMARGINRIGHT = 2157;   // (unused, pixelWidth) テキスト右側の空白マージン
    public const int SCI_GETMARGINLEFT = 2156;    // → 左テキストマージン(px)
    public const int SCI_GETMARGINWIDTHN = 2243;  // (margin) → 当該マージン幅(px)
    public const int SCI_TEXTWIDTH = 2276;        // (style, const char* utf8) → 文字列のピクセル幅
    public const int SCI_TEXTHEIGHT = 2279;       // (line) → 当該行の表示高さ(px)
    public const int STYLE_DEFAULT = 32;

    // インジケータ（CSV 現在セルの視覚ハイライト等。テキスト選択とは独立した装飾）。
    public const int SCI_INDICSETSTYLE = 2080;        // (indicator, style)
    public const int SCI_INDICSETFORE = 2082;         // (indicator, colour 0x00BBGGRR)
    public const int SCI_INDICSETALPHA = 2523;        // (indicator, alpha 0-255) 塗りの不透明度
    public const int SCI_INDICSETOUTLINEALPHA = 2558; // (indicator, alpha 0-255) 枠線の不透明度
    public const int SCI_SETINDICATORCURRENT = 2500;  // (indicator) 以降の FILL/CLEAR の対象
    public const int SCI_INDICATORFILLRANGE = 2504;   // (bytePos, byteLen) 現インジケータで塗る
    public const int SCI_INDICATORCLEARRANGE = 2505;  // (bytePos, byteLen) 現インジケータを消す
    public const int INDIC_STRAIGHTBOX = 8;           // 半透明の塗り＋矩形枠
}
