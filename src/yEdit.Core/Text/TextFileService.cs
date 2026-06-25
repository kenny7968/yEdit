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
        EncodingCatalog.EnsureRegistered();
        byte[] bytes = File.ReadAllBytes(path);

        DetectedEncoding det = forcedCodePage is int cp
            ? new DetectedEncoding(cp, HasBomFor(bytes, cp))
            : EncodingDetector.Detect(bytes);

        Encoding enc = EncodingCatalog.Get(det.CodePage);

        // 置換文字検出のため、不正バイトを U+FFFD（置換文字）に落とすデコーダを用意。
        // ※ 静的な DecoderFallback.ReplacementFallback は '?'(U+003F) を出すため、
        //   取り違え検出が効くよう明示的に U+FFFD のフォールバックを指定する。
        var decoder = Encoding.GetEncoding(det.CodePage,
            EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("�"));

        // BOM を除いた本文部分をデコード。
        // 注意: EncodingCatalog.Get(65001) は BOM 無し UTF8Encoding を返し GetPreamble() が空になる。
        //       BOM 付き UTF-8 でも先頭 3 バイトを確実に剥がすため、preamble 長は BOM を出す
        //       codepage 既定の Encoding（decoder）から取得する。
        int preambleLen = det.HasBom ? decoder.GetPreamble().Length : 0;
        string text = decoder.GetString(bytes, preambleLen, bytes.Length - preambleLen);

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
    /// 原子的にテキストを保存する。同ディレクトリの temp に書いてから File.Replace で差し替え。
    /// 新規は File.Move。対象がロック中で差し替え不能なら in-place 上書きへフォールバック。
    /// </summary>
    public static void Save(string path, string text, Encoding encoding, bool hasBom)
    {
        EncodingCatalog.EnsureRegistered();

        // BOM 制御: hasBom に応じて preamble を出す Encoding を用意。
        Encoding enc = encoding.CodePage switch
        {
            65001 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom),
            1200 or 1201 => encoding, // UTF-16 は preamble 既定で出る
            _ => encoding,
        };
        byte[] preamble = hasBom ? enc.GetPreamble() : Array.Empty<byte>();
        byte[] body = enc.GetBytes(text);

        byte[] payload = preamble.Length == 0
            ? body
            : preamble.Concat(body).ToArray();

        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Path.GetRandomFileName() + ".tmp");

        try
        {
            File.WriteAllBytes(tmp, payload);
            if (File.Exists(path))
            {
                // 既存の ACL・属性を保持して差し替え（バックアップ無し）。
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch (IOException)
        {
            // ロック等で差し替え不能 → in-place 上書きにフォールバック。
            TryDelete(tmp);
            File.WriteAllBytes(path, payload);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* 残骸は実害小 */ }
    }
}
