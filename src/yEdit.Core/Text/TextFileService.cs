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

    // in-place フォールバックを許す唯一の条件（Win32 共有/ロック違反）。
    // これ以外（ディスクフル等）でフォールバックすると原本を破壊し得るため不可。
    private const int HResultSharingViolation = unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION
    private const int HResultLockViolation = unchecked((int)0x80070021);    // ERROR_LOCK_VIOLATION

    /// <summary>
    /// 原子的にテキストを保存する。同ディレクトリの temp に書いてから File.Replace で差し替える
    /// （新規は File.Move）。差し替えが「共有違反/ロック競合」で失敗した場合に限り in-place 上書きへ
    /// フォールバックする。tmp 書き込み自体の失敗やそれ以外の I/O 失敗（ディスクフル等）は、原本に
    /// 一切触れずそのまま例外を伝播する（= 原子的保存の目的＝原本喪失の回避を守る）。
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

        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Path.GetRandomFileName() + ".tmp");

        // ① tmp へステージング書き込み。ここで失敗（ディスクフル・権限・パス長等）したら
        //    原本に一切触れず、tmp 残骸の掃除だけ試みて例外を伝播する。
        try
        {
            File.WriteAllBytes(tmp, payload);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        // ② tmp は完全に書けている。原子的に差し替える。
        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null); // ACL/属性を保持・バックアップ無し
            else
                File.Move(tmp, path);
        }
        catch (IOException ex) when (ex.HResult is HResultSharingViolation or HResultLockViolation)
        {
            // 共有違反/ロック競合（AV・同期ソフト等が一時的に掴んでいる）に限り in-place 上書きへ。
            // FileMode.Create はハンドルを開けてからしか切り詰めないため、ロックで open に失敗した
            // 場合は原本を 0 バイトにせず例外が伝播する（= 原本を喪失しない）。完成済み tmp は finally で掃除。
            try
            {
                File.WriteAllBytes(path, payload);
            }
            finally
            {
                TryDelete(tmp);
            }
        }
        catch
        {
            // 共有違反以外（ディスクフル等）は原本を壊さないよう、フォールバックせず tmp を消して伝播。
            TryDelete(tmp);
            throw;
        }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* 残骸は実害小 */ }
    }
}
