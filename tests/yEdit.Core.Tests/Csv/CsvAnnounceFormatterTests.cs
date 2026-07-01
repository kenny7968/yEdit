using yEdit.Core.Csv;
using Xunit;

namespace yEdit.Core.Tests.Csv;

public class CsvAnnounceFormatterTests
{
    [Fact]
    public void Cell_reads_content_then_position()
        => Assert.Equal("田中 2行2列", CsvAnnounceFormatter.Cell("田中", row: 2, col: 2));

    [Fact]
    public void Cell_empty_value_says_blank()
        => Assert.Equal("ブランク 2行2列", CsvAnnounceFormatter.Cell("", 2, 2));

    [Fact]
    public void Header_returns_value_or_blank()
    {
        Assert.Equal("氏名", CsvAnnounceFormatter.Header("氏名"));
        Assert.Equal("ブランク", CsvAnnounceFormatter.Header(""));
    }
}
