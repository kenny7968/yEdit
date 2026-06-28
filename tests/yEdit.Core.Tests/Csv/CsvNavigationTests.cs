using yEdit.Core.Csv;
using Xunit;

namespace yEdit.Core.Tests.Csv;

public class CsvNavigationTests
{
    private static CsvDocument Doc(string t) => CsvParser.Parse(t);

    [Fact]
    public void FindCell_caret_inside_field()
    {
        var d = Doc("a,bb\nccc,d");      // bb=[2,2)
        Assert.Equal((0, 1), d.FindCell(3));   // 'b' の上
    }

    [Fact]
    public void FindCell_caret_on_delimiter_belongs_to_left_field()
    {
        var d = Doc("a,bb");             // a=[0,1) , カンマ=1
        Assert.Equal((0, 0), d.FindCell(1));
    }

    [Fact]
    public void FindCell_caret_on_second_row()
    {
        var d = Doc("a,bb\nccc,d");      // ccc=[5,3)
        Assert.Equal((1, 0), d.FindCell(6));
    }

    [Fact]
    public void FindCell_empty_doc_returns_origin()
        => Assert.Equal((0, 0), Doc("").FindCell(0));
}
