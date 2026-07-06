using System.Text;
using yEdit.Core.Buffers;
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class TextFileServiceSaveTextBufferTests
{
    private const string Jp = "一行目\r\n二行目\r\n";

    [Fact]
    public void SaveBuffer_Utf8_NoBom_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buf = TextBuffer.FromString(Jp);
            TextFileService.Save(path, buf, new UTF8Encoding(false), hasBom: false);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(Encoding.UTF8.GetBytes(Jp), bytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveBuffer_Utf8_WithBom_WritesPreamble()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buf = TextBuffer.FromString(Jp);
            TextFileService.Save(path, buf, new UTF8Encoding(false), hasBom: true);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveBuffer_ShiftJis_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buf = TextBuffer.FromString(Jp);
            var enc = EncodingCatalog.Get(932);
            TextFileService.Save(path, buf, enc, hasBom: false);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(enc.GetBytes(Jp), bytes);
        }
        finally { File.Delete(path); }
    }
}
