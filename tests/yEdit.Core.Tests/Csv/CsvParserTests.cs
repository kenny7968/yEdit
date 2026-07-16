using yEdit.Core.Csv;
using Xunit;

namespace yEdit.Core.Tests.Csv;

public class CsvParserTests
{
    [Fact]
    public void Empty_text_has_no_rows()
    {
        var d = CsvParser.Parse("");
        Assert.True(d.Ok);
        Assert.Empty(d.Rows);
    }

    [Fact]
    public void Single_cell_no_delimiter()
    {
        var d = CsvParser.Parse("hello");
        Assert.True(d.Ok);
        Assert.Single(d.Rows);
        Assert.Single(d.Rows[0]);
        Assert.Equal("hello", d.Rows[0][0].Value);
        Assert.Equal(0, d.Rows[0][0].Start);
        Assert.Equal(5, d.Rows[0][0].Length);
    }

    [Fact]
    public void Two_rows_two_cols_offsets_are_raw_spans()
    {
        var d = CsvParser.Parse("a,bb\nccc,d");
        Assert.True(d.Ok);
        Assert.Equal(2, d.Rows.Count);
        Assert.Equal(new[] { "a", "bb" }, new[] { d.Rows[0][0].Value, d.Rows[0][1].Value });
        Assert.Equal(new[] { "ccc", "d" }, new[] { d.Rows[1][0].Value, d.Rows[1][1].Value });
        Assert.Equal((0, 1), (d.Rows[0][0].Start, d.Rows[0][0].Length));
        Assert.Equal((2, 2), (d.Rows[0][1].Start, d.Rows[0][1].Length));
        Assert.Equal((5, 3), (d.Rows[1][0].Start, d.Rows[1][0].Length));
        Assert.Equal((9, 1), (d.Rows[1][1].Start, d.Rows[1][1].Length));
    }

    [Fact]
    public void Trailing_newline_does_not_make_empty_record()
    {
        var d = CsvParser.Parse("a,b\r\n");
        Assert.Single(d.Rows);
        Assert.Equal(new[] { "a", "b" }, new[] { d.Rows[0][0].Value, d.Rows[0][1].Value });
    }

    [Fact]
    public void Trailing_empty_field_on_content_line()
    {
        var d = CsvParser.Parse("a,");
        Assert.Single(d.Rows);
        Assert.Equal(2, d.Rows[0].Count);
        Assert.Equal("", d.Rows[0][1].Value);
        Assert.Equal((2, 0), (d.Rows[0][1].Start, d.Rows[0][1].Length));
    }

    [Fact]
    public void Quoted_field_with_comma_and_escaped_quote()
    {
        var d = CsvParser.Parse("a,\"b,\"\"c\"\"\",d");
        Assert.True(d.Ok);
        Assert.Single(d.Rows);
        Assert.Equal(3, d.Rows[0].Count);
        Assert.Equal("a", d.Rows[0][0].Value);
        Assert.Equal("b,\"c\"", d.Rows[0][1].Value);
        Assert.Equal("d", d.Rows[0][2].Value);
        Assert.Equal('"', "a,\"b,\"\"c\"\"\",d"[d.Rows[0][1].Start]);
        // 生スパンは外側の引用符を含む丸ごとのトークン "b,""c""" を覆う（9文字）。
        Assert.Equal(9, d.Rows[0][1].Length);
    }

    [Fact]
    public void Crlf_advances_offset_by_two()
    {
        var d = CsvParser.Parse("a\r\nbb");
        Assert.True(d.Ok);
        Assert.Equal(2, d.Rows.Count);
        Assert.Equal(3, d.Rows[1][0].Start);
    }

    [Fact]
    public void Quoted_field_with_embedded_newline_is_one_logical_row()
    {
        var d = CsvParser.Parse("\"line1\nline2\",x");
        Assert.True(d.Ok);
        Assert.Single(d.Rows);
        Assert.Equal(2, d.Rows[0].Count);
        Assert.Equal("line1\nline2", d.Rows[0][0].Value);
        Assert.Equal("x", d.Rows[0][1].Value);
    }

    [Fact]
    public void Unterminated_quote_is_not_ok()
    {
        var d = CsvParser.Parse("a,\"unterminated");
        Assert.False(d.Ok);
    }

    [Fact]
    public void Parse_TextSnapshotOverload_ProducesSameResultAsString()
    {
        string csv = "a,b,c\n1,\"quoted, comma\",3\n\"multi\nline\",x,y\n";
        var expected = CsvParser.Parse(csv);

        var buffer = yEdit.Core.Buffers.TextBuffer.FromString(csv);
        var actual = CsvParser.Parse(buffer.Current);   // 新規オーバーロード

        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int i = 0; i < expected.Rows.Count; i++)
        {
            Assert.Equal(expected.Rows[i].Count, actual.Rows[i].Count);
            for (int j = 0; j < expected.Rows[i].Count; j++)
                Assert.Equal(expected.Rows[i][j].Value, actual.Rows[i][j].Value);
        }
        Assert.Equal(expected.Ok, actual.Ok);
    }

    [Fact]
    public void Parse_TextSnapshotOverload_HandlesQuotedFieldAcrossPieceBoundary()
    {
        // SnapshotReader は piece 境界を Read/Peek で透過的に跨ぐ(Ensure() 経由)。
        // TextBufferBuilder に複数 Add で強制的に piece を分割し、quoted field / \r\n / "" が
        // 境界を跨いでも状態機械が崩れないことを機械固定する。
        var b = new yEdit.Core.Buffers.TextBufferBuilder();
        // piece 1: "head,\"" (開き引用符でフィールド開始)
        b.Add(System.Text.Encoding.UTF8.GetBytes("head,\""));
        // piece 2: "with , comma\r\nand newline," (quoted 内に comma と CRLF)
        b.Add(System.Text.Encoding.UTF8.GetBytes("with , comma\r\nand newline\","));
        // piece 3: "tail\n" (次の field と行終端)
        b.Add(System.Text.Encoding.UTF8.GetBytes("tail\n"));
        var buffer = b.Build();

        var parsed = CsvParser.Parse(buffer.Current);
        Assert.Single(parsed.Rows);
        Assert.Equal(3, parsed.Rows[0].Count);
        Assert.Equal("head", parsed.Rows[0][0].Value);
        Assert.Equal("with , comma\r\nand newline", parsed.Rows[0][1].Value);
        Assert.Equal("tail", parsed.Rows[0][2].Value);
        Assert.True(parsed.Ok);
    }
}
