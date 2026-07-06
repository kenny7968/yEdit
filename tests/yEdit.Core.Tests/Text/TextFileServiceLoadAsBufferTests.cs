using System.Text;
using yEdit.Core.Buffers;
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class TextFileServiceLoadAsBufferTests
{
    private const string Jp = "一行目\r\n二行目\r\n";

    [Fact]
    public void LoadAsBuffer_Utf8_NoBom_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Jp));
            var buf = TextFileService.LoadAsBuffer(path, new UTF8Encoding(false), hasBom: false);
            Assert.Equal(Jp, buf.Current.GetText(0, buf.Current.CharLength));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadAsBuffer_Utf8_WithBom_StripsPreamble()
    {
        string path = Path.GetTempFileName();
        try
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            File.WriteAllBytes(path, bom.Concat(Encoding.UTF8.GetBytes(Jp)).ToArray());
            var buf = TextFileService.LoadAsBuffer(path, new UTF8Encoding(false), hasBom: true);
            Assert.Equal(Jp, buf.Current.GetText(0, buf.Current.CharLength));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadAsBuffer_ShiftJis_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var enc = EncodingCatalog.Get(932);
            File.WriteAllBytes(path, enc.GetBytes(Jp));
            var buf = TextFileService.LoadAsBuffer(path, enc);
            Assert.Equal(Jp, buf.Current.GetText(0, buf.Current.CharLength));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadAsBuffer_EucJp_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var enc = EncodingCatalog.Get(51932);
            File.WriteAllBytes(path, enc.GetBytes(Jp));
            var buf = TextFileService.LoadAsBuffer(path, enc);
            Assert.Equal(Jp, buf.Current.GetText(0, buf.Current.CharLength));
        }
        finally { File.Delete(path); }
    }
}
