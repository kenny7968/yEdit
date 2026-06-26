using yEdit.Core.Settings;
using Xunit;

namespace yEdit.Core.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_returns_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var s = SettingsStore.Load(path);
        Assert.Equal(new AppSettings().FontName, s.FontName);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings { FontName = "BIZ UDゴシック", FontSize = 14, WindowWidth = 1000 };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.Equal("BIZ UDゴシック", loaded.FontName);
            Assert.Equal(14, loaded.FontSize);
            Assert.Equal(1000, loaded.WindowWidth);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Corrupt_file_returns_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            var s = SettingsStore.Load(path);
            Assert.Equal(new AppSettings().FontSize, s.FontSize);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_normalizes_corrupt_numeric_and_null_fields()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            // 有効な JSON だが値が壊れている（未対応コードページ・範囲外改行・0サイズ・null参照型）。
            File.WriteAllText(path,
                "{\"DefaultCodePage\":99999,\"DefaultLineEnding\":7,\"FontSize\":0,\"WindowWidth\":1,\"RecentFiles\":null,\"Theme\":null}");
            var s = SettingsStore.Load(path);
            var def = new AppSettings();
            Assert.Equal(def.DefaultCodePage, s.DefaultCodePage);   // 未対応CP→既定
            Assert.Equal(def.DefaultLineEnding, s.DefaultLineEnding); // 範囲外→既定
            Assert.True(s.FontSize > 0);                             // 0→既定
            Assert.True(s.WindowWidth >= 200);                       // 極小→既定
            Assert.NotNull(s.RecentFiles);                           // null→空リスト
            Assert.False(string.IsNullOrEmpty(s.Theme));             // null→default
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
