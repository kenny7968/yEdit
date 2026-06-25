using System.Text;
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class TextFileServiceSaveTests
{
    [Fact]
    public void Save_then_load_roundtrips_shift_jis()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            const string text = "保存テスト\r\nおわり\r\n";
            TextFileService.Save(path, text, EncodingCatalog.Get(932), hasBom: false);
            var doc = TextFileService.Load(path);
            Assert.Equal(text, doc.Text);
            Assert.Equal(932, doc.Encoding.CodePage);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_overwrites_existing_atomically()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "old");
            TextFileService.Save(path, "new", EncodingCatalog.Get(65001), hasBom: false);
            Assert.Equal("new", File.ReadAllText(path));
            // temp 残骸が無いこと
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*tmp*"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_utf8_with_bom_writes_preamble()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, "x", EncodingCatalog.Get(65001), hasBom: true);
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
