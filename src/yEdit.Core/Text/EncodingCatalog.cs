using System.Text;

namespace yEdit.Core.Text;

/// <summary>
/// .NET9 で Shift_JIS(932)/EUC-JP(51932) を使うには CodePagesEncodingProvider の登録が要る。
/// 本カタログ初回利用時に一度だけ登録する。
/// </summary>
public static class EncodingCatalog
{
    private static bool _registered;
    private static readonly object _sync = new();

    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (_sync)
        {
            if (_registered) return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _registered = true;
        }
    }

    /// <summary>コードページから Encoding を得る（必要なら provider 登録）。</summary>
    public static Encoding Get(int codePage)
    {
        EnsureRegistered();
        return codePage switch
        {
            65001 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            1200 => Encoding.Unicode,
            1201 => Encoding.BigEndianUnicode,
            _ => Encoding.GetEncoding(codePage),
        };
    }
}
