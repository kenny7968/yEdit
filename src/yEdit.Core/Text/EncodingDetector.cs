using System.Text;
using UtfUnknown;

namespace yEdit.Core.Text;

/// <summary>判定結果。CodePage と BOM 有無を持つ。</summary>
public readonly record struct DetectedEncoding(int CodePage, bool HasBom);

public static class EncodingDetector
{
    /// <summary>バイト列から文字コードを推定する（design §4.2 の手順）。</summary>
    public static DetectedEncoding Detect(byte[] bytes)
    {
        EncodingCatalog.EnsureRegistered();

        // ① BOM 確定
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new DetectedEncoding(65001, true);

        // 空は UTF-8 既定
        if (bytes.Length == 0)
            return new DetectedEncoding(65001, false);

        // ② 厳格 UTF-8 デコード成功なら UTF-8（BOM無し）
        if (IsStrictUtf8(bytes))
            return new DetectedEncoding(65001, false);

        // ③ UTF.Unknown へ委譲
        var result = CharsetDetector.DetectFromBytes(bytes);
        int? cp = MapCharset(result?.Detected?.EncodingName, result?.Detected?.Confidence ?? 0f);
        if (cp is int detected)
            return new DetectedEncoding(detected, false);

        // ④ フォールバック: Shift_JIS
        return new DetectedEncoding(932, false);
    }

    private static bool IsStrictUtf8(byte[] bytes)
    {
        try
        {
            var strict = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true
            );
            strict.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// UTF.Unknown の名前→コードページ。採用は utf-8 / shift_jis / euc-jp のみ。
    /// 低信頼（&lt;0.5）と未対応の名前は不採用（null を返し、呼び出し側で Shift_JIS フォールバック）。
    /// </summary>
    private static int? MapCharset(string? name, float confidence)
    {
        if (name is null || confidence < 0.5f)
            return null;
        return name.ToLowerInvariant() switch
        {
            "utf-8" => 65001,
            "shift-jis" or "shift_jis" => 932,
            "euc-jp" => 51932,
            _ => null,
        };
    }
}
