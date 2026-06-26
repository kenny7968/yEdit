using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class RecentFilesListTests
{
    [Fact]
    public void New_path_goes_to_front()
    {
        var r = RecentFilesList.Add(new[] { @"C:\a.txt", @"C:\b.txt" }, @"C:\c.txt", 10);
        Assert.Equal(new[] { @"C:\c.txt", @"C:\a.txt", @"C:\b.txt" }, r);
    }

    [Fact]
    public void Existing_path_moves_to_front_without_duplicate()
    {
        var r = RecentFilesList.Add(new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt" }, @"C:\b.txt", 10);
        Assert.Equal(new[] { @"C:\b.txt", @"C:\a.txt", @"C:\c.txt" }, r);
    }

    [Fact]
    public void Caps_at_max()
    {
        var r = RecentFilesList.Add(new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt" }, @"C:\d.txt", 2);
        Assert.Equal(new[] { @"C:\d.txt", @"C:\a.txt" }, r);
    }

    [Fact]
    public void Dedup_is_pathkey_normalized_case_and_separators()
    {
        // 同一ファイルの大小・区切り違いは 1 件に集約される（PathKey 正規化）。
        var r = RecentFilesList.Add(new[] { @"C:\Dir\A.TXT" }, @"c:/dir/a.txt", 10);
        Assert.Single(r);
        Assert.Equal(@"c:/dir/a.txt", r[0]); // 新規入力が先頭
    }

    [Fact]
    public void Max_zero_or_negative_returns_empty()
    {
        Assert.Empty(RecentFilesList.Add(new[] { @"C:\a.txt" }, @"C:\b.txt", 0));
        Assert.Empty(RecentFilesList.Add(new[] { @"C:\a.txt" }, @"C:\b.txt", -1));
    }

    [Fact]
    public void Empty_current_yields_single()
        => Assert.Equal(new[] { @"C:\a.txt" }, RecentFilesList.Add(System.Array.Empty<string>(), @"C:\a.txt", 10));
}
