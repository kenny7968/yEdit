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
}
