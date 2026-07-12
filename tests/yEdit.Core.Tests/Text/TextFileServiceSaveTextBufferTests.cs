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

    [Fact]
    public void SaveBuffer_EucJp_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buf = TextBuffer.FromString(Jp);
            var enc = EncodingCatalog.Get(51932);
            TextFileService.Save(path, buf, enc, hasBom: false);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(enc.GetBytes(Jp), bytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveTextBuffer_Utf8_LargeContent_WritesExactBytes()
    {
        // 5MB(TextBufferBuilder のチャンク境界 4MB を跨ぐ)
        var body = new string('あ', 5 * 1024 * 1024 / 3);  // UTF-8 で 5MB 近辺
        var buffer = TextBuffer.FromString(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: false);
            byte[] actual = File.ReadAllBytes(path);
            byte[] expected = Encoding.UTF8.GetBytes(body);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected, actual);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveTextBuffer_Utf8_WithBom_EmitsPreamble()
    {
        var buffer = TextBuffer.FromString("hello");
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: true);
            byte[] actual = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, actual.Take(3).ToArray());
            Assert.Equal("hello", Encoding.UTF8.GetString(actual, 3, actual.Length - 3));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveTextBuffer_Sjis_LargeContent_WritesExactBytes()
    {
        EncodingCatalog.EnsureRegistered();
        var body = new string('あ', 100_000);  // SJIS で 200KB
        var buffer = TextBuffer.FromString(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, EncodingCatalog.Get(932), hasBom: false);
            byte[] actual = File.ReadAllBytes(path);
            byte[] expected = Encoding.GetEncoding(932).GetBytes(body);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected, actual);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveTextBuffer_EucJp_WritesExactBytes()
    {
        EncodingCatalog.EnsureRegistered();
        string body = "日本語テキスト EUC-JP\nsecond line\n";
        var buffer = TextBuffer.FromString(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, EncodingCatalog.Get(51932), hasBom: false);
            byte[] actual = File.ReadAllBytes(path);
            byte[] expected = Encoding.GetEncoding(51932).GetBytes(body);
            Assert.Equal(expected, actual);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveTextBuffer_EmptyBuffer_WritesZeroBytes()
    {
        var buffer = TextBuffer.FromString("");
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: false);
            Assert.Equal(0, new FileInfo(path).Length);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveTextBuffer_EmptyBuffer_WithBom_WritesOnlyPreamble()
    {
        var buffer = TextBuffer.FromString("");
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: true);
            byte[] actual = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, actual);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
