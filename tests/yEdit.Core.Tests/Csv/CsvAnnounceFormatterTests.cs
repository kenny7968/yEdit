using yEdit.Core.Csv;
using Xunit;

namespace yEdit.Core.Tests.Csv;

public class CsvAnnounceFormatterTests
{
    [Fact]
    public void Cell_reads_content_then_position()
        => Assert.Equal("田中 2行2列", CsvAnnounceFormatter.Cell("田中", row: 2, col: 2));

    [Fact]
    public void Cell_empty_value_says_kuu()
        => Assert.Equal("空 2行2列", CsvAnnounceFormatter.Cell("", 2, 2));

    [Fact]
    public void Header_returns_value_or_kuu()
    {
        Assert.Equal("氏名", CsvAnnounceFormatter.Header("氏名"));
        Assert.Equal("空", CsvAnnounceFormatter.Header(""));
    }
}
