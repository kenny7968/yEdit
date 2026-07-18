using Xunit;
using yEdit.Core.Text;

namespace yEdit.Core.Tests.Text;

public class PathKeyTests
{
    [Fact]
    public void Same_path_different_case_yields_same_key() =>
        Assert.Equal(PathKey.For(@"C:\Temp\Memo.txt"), PathKey.For(@"c:\temp\memo.TXT"));

    [Fact]
    public void Forward_and_back_slashes_normalize_equal() =>
        Assert.Equal(PathKey.For(@"C:\Temp\a\b.txt"), PathKey.For("C:/Temp/a/b.txt"));

    [Fact]
    public void Relative_segments_collapse() =>
        Assert.Equal(PathKey.For(@"C:\Temp\b.txt"), PathKey.For(@"C:\Temp\x\..\b.txt"));

    [Fact]
    public void Empty_returns_empty() => Assert.Equal(string.Empty, PathKey.For(""));
}
