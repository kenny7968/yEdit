using yEdit.Core.Settings;
using Xunit;

namespace yEdit.Core.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void Clone_copies_values()
    {
        var s = new AppSettings
        {
            FontName = "テスト", FontSize = 14f, Theme = "white-on-black",
            DefaultCodePage = 932, WrapColumn = 40,
            RecentFiles = new List<string> { @"C:\a.txt", @"C:\b.txt" },
        };
        var c = s.Clone();
        Assert.Equal("テスト", c.FontName);
        Assert.Equal(14f, c.FontSize);
        Assert.Equal("white-on-black", c.Theme);
        Assert.Equal(932, c.DefaultCodePage);
        Assert.Equal(40, c.WrapColumn);
        Assert.Equal(s.RecentFiles, c.RecentFiles);
    }

    [Fact]
    public void Clone_is_independent_of_original()
    {
        var s = new AppSettings { RecentFiles = new List<string> { @"C:\a.txt" } };
        var c = s.Clone();
        c.FontName = "変更後";
        c.RecentFiles.Add(@"C:\b.txt");
        Assert.NotEqual("変更後", s.FontName);
        Assert.Single(s.RecentFiles); // クローン側の追加が元へ波及しない
    }
}
