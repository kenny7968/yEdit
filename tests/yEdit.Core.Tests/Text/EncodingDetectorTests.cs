using System.Text;
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class EncodingDetectorTests
{
    private const string Jp = "日本語のテスト。ABC 123 半角と　全角。";

    [Fact]
    public void Detects_utf8_bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Jp)).ToArray();
        var r = EncodingDetector.Detect(bytes);
        Assert.Equal(65001, r.CodePage);
        Assert.True(r.HasBom);
    }

    [Fact]
    public void Detects_utf8_no_bom()
    {
        var r = EncodingDetector.Detect(Encoding.UTF8.GetBytes(Jp));
        Assert.Equal(65001, r.CodePage);
        Assert.False(r.HasBom);
    }

    [Fact]
    public void Detects_shift_jis()
    {
        var sjis = EncodingCatalog.Get(932);
        var r = EncodingDetector.Detect(sjis.GetBytes(Jp));
        Assert.Equal(932, r.CodePage);
    }

    [Fact]
    public void Detects_euc_jp()
    {
        var euc = EncodingCatalog.Get(51932);
        var r = EncodingDetector.Detect(euc.GetBytes(Jp));
        Assert.Equal(51932, r.CodePage);
    }

    [Fact]
    public void Utf16_le_bom_no_longer_detected_as_utf16()
    {
        // P6 Task 6: UTF-16 は非対応=BOM も検出しない。fallback 経路(UTF-8 strict → charset detect → SJIS)へ落ちる。
        var r = EncodingDetector.Detect(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Jp)).ToArray());
        Assert.NotEqual(1200, r.CodePage);
        Assert.NotEqual(1201, r.CodePage);
    }

    [Fact]
    public void Empty_defaults_to_utf8()
    {
        var r = EncodingDetector.Detect(Array.Empty<byte>());
        Assert.Equal(65001, r.CodePage);
    }
}
