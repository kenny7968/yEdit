using System.Text;

namespace yEdit.Core.Text;

public static partial class TextFileService
{
    /// <summary>
    /// ファイルを読み込み本文・文字コード・改行を確定する。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む（開き直し用）。
    /// </summary>
    public static LoadedDocument Load(string path, int? forcedCodePage = null)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return DecodeBytes(bytes, forcedCodePage);
    }

    /// <summary>
    /// 既に読み込んだバイト列を本文・文字コード・改行へ復号する（grep がバイトを 1 回だけ読んで
    /// 流用するための抽出点。Load と完全に同じ判定・復号ロジック）。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む。
    /// </summary>
    public static LoadedDocument DecodeBytes(byte[] bytes, int? forcedCodePage = null)
    {
        EncodingCatalog.EnsureRegistered();

        DetectedEncoding det = forcedCodePage is int cp
            ? new DetectedEncoding(cp, HasBomFor(bytes, cp))
            : EncodingDetector.Detect(bytes);

        Encoding enc = EncodingCatalog.Get(det.CodePage);

        // 置換文字検出のため、不正バイトを U+FFFD（置換文字）に落とすデコーダを用意。
        // ※ 既定の Encoding.UTF8 インスタンスはデコード置換に U+FFFD を使うが、ここで使う
        //   Encoding.GetEncoding(cp, ...) に静的 DecoderFallback.ReplacementFallback を渡すと
        //   置換が '?'(U+003F) になる（DecoderReplacementFallback の既定文字は '?'＝実測確認済）。
        //   取り違え検出を確実にするため、明示的に U+FFFD のフォールバックを指定する。
        var decoder = Encoding.GetEncoding(det.CodePage,
            EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("�"));

        // BOM を除いた本文部分をデコード。
        // 注意: EncodingCatalog.Get(65001) は BOM 無し UTF8Encoding を返し GetPreamble() が空になる。
        //       BOM 付き UTF-8 でも先頭 3 バイトを確実に剥がすため、preamble 長は BOM を出す
        //       codepage 既定の Encoding（decoder）から取得する。
        int preambleLen = det.HasBom ? decoder.GetPreamble().Length : 0;
        string text = decoder.GetString(bytes, preambleLen, bytes.Length - preambleLen);

        // 注: 本文中に元から U+FFFD が含まれる場合とデコード失敗の置換とを区別しない近似。
        //     文字コード取り違えの「示唆」用途であり、厳密な判定ではない。
        bool hadReplacement = text.Contains('�'); // U+FFFD REPLACEMENT CHARACTER
        LineEnding eol = LineEndingDetector.Detect(text);

        return new LoadedDocument
        {
            Text = text,
            Encoding = enc,
            HasBom = det.HasBom,
            LineEnding = eol,
            HadReplacementChar = hadReplacement,
        };
    }

    private static bool HasBomFor(byte[] bytes, int codePage)
    {
        var pre = Encoding.GetEncoding(codePage).GetPreamble();
        if (pre.Length == 0 || bytes.Length < pre.Length) return false;
        for (int i = 0; i < pre.Length; i++) if (bytes[i] != pre[i]) return false;
        return true;
    }

    /// <summary>
    /// 原子的にテキストを保存する（実体は <see cref="IO.AtomicFile"/>：temp へ書いてから
    /// File.Replace／新規は File.Move）。「共有違反/ロック競合」で失敗した場合に限り in-place
    /// 上書きへフォールバックする。それ以外の I/O 失敗（ディスクフル等）は、原本に一切触れず
    /// そのまま例外を伝播する（= 原子的保存の目的＝原本喪失の回避を守る）。
    /// </summary>
    public static void Save(string path, string text, Encoding encoding, bool hasBom)
    {
        EncodingCatalog.EnsureRegistered();

        // BOM 制御: UTF-8 のみ hasBom に応じて preamble 有無を切替。それ以外（UTF-16 等）は
        // 渡された Encoding をそのまま使い、preamble は hasBom 指定時に GetPreamble() で手前に付ける
        // （body には含めないため二重付与しない）。
        Encoding enc = encoding.CodePage == 65001
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom)
            : encoding;
        byte[] preamble = hasBom ? enc.GetPreamble() : Array.Empty<byte>();
        byte[] body = enc.GetBytes(text);

        byte[] payload;
        if (preamble.Length == 0)
        {
            payload = body;
        }
        else
        {
            payload = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, payload, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, payload, preamble.Length, body.Length);
        }

        try
        {
            IO.AtomicFile.Write(path, payload);
        }
        catch (IOException ex) when (IO.AtomicFile.IsShareOrLockViolation(ex))
        {
            // 共有違反/ロック競合（AV・同期ソフト等が一時的に掴んでいる）に限り in-place 上書きへ。
            // FileMode.Create はハンドルを開けてからしか切り詰めないため、ロックで open に失敗した
            // 場合は原本を 0 バイトにせず例外が伝播する（= 原本を喪失しない）。tmp は AtomicFile が掃除済み。
            // 共有違反以外（ディスクフル等）はフォールバックせず伝播する（原本を壊さない）。
            // 注: 旧実装は差替段階の共有違反のみ救済していたが、乱数名 tmp のステージング書込で
            //     共有違反は事実上起きないため、段階を区別せず単純化している（どの段階由来でも
            //     in-place は上記の非切詰め特性により安全）。
            File.WriteAllBytes(path, payload);
        }
    }
}
