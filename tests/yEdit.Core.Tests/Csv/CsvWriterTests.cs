using Xunit;
using yEdit.Core.Csv;

namespace yEdit.Core.Tests.Csv;

public class CsvWriterTests
{
    [Fact]
    public void Plain_value_is_unchanged() => Assert.Equal("abc", CsvWriter.EscapeField("abc"));

    [Fact]
    public void Empty_value_is_unchanged() => Assert.Equal("", CsvWriter.EscapeField(""));

    [Fact]
    public void Comma_is_quoted() => Assert.Equal("\"a,b\"", CsvWriter.EscapeField("a,b"));

    [Fact]
    public void Quote_is_doubled_and_wrapped() =>
        Assert.Equal("\"he \"\"q\"\"\"", CsvWriter.EscapeField("he \"q\""));

    [Fact]
    public void Lf_is_quoted() => Assert.Equal("\"a\nb\"", CsvWriter.EscapeField("a\nb"));

    [Fact]
    public void Cr_is_quoted() => Assert.Equal("\"a\rb\"", CsvWriter.EscapeField("a\rb"));

    [Fact]
    public void Leading_space_is_not_quoted() => Assert.Equal(" a ", CsvWriter.EscapeField(" a "));

    [Theory]
    [InlineData("abc")]
    [InlineData("a,b,c")]
    [InlineData("he said \"hi\"")]
    [InlineData("line1\nline2")]
    [InlineData("comma, and \"quote\"")]
    public void Roundtrip_escape_then_parse_preserves_value(string value)
    {
        // 1行1セルの CSV として直列化→パースし、論理値が戻ることを確認。
        string csvText = CsvWriter.EscapeField(value);
        var doc = CsvParser.Parse(csvText);
        Assert.True(doc.Ok);
        Assert.Equal(value, doc.GetField(0, 0)!.Value);
    }

    [Fact]
    public void Empty_value_parses_to_no_rows() =>
        Assert.Empty(CsvParser.Parse(CsvWriter.EscapeField("")).Rows);

    // --- CSV-L-1: formula injection 対策 (OWASP) ---
    // 先頭が = + - @ TAB CR のいずれかなら apostrophe (') を前置して Excel/Sheets の formula 実行を阻止する。

    [Fact]
    public void Formula_prefix_equals_is_apostrophe_prefixed() =>
        Assert.Equal("'=1+1", CsvWriter.EscapeField("=1+1"));

    [Fact]
    public void Formula_prefix_plus_is_apostrophe_prefixed() =>
        Assert.Equal("'+cmd", CsvWriter.EscapeField("+cmd"));

    [Fact]
    public void Formula_prefix_minus_is_apostrophe_prefixed() =>
        Assert.Equal("'-2+3", CsvWriter.EscapeField("-2+3"));

    [Fact]
    public void Formula_prefix_at_is_apostrophe_prefixed() =>
        Assert.Equal("'@SUM", CsvWriter.EscapeField("@SUM"));

    [Fact]
    public void Formula_prefix_tab_is_apostrophe_prefixed() =>
        Assert.Equal("'\tX", CsvWriter.EscapeField("\tX"));

    [Fact]
    public void Formula_prefix_cr_is_apostrophe_prefixed() =>
        // CR を含むため既存の quote 分岐で "..." に包まれる。内部の CR は素通し。
        Assert.Equal("\"'\rX\"", CsvWriter.EscapeField("\rX"));

    [Fact]
    public void Formula_char_in_middle_is_untouched() =>
        Assert.Equal("x=1", CsvWriter.EscapeField("x=1"));

    [Fact]
    public void Formula_prefix_with_comma_is_quoted_after_apostrophe() =>
        // apostrophe 前置後もカンマがあれば既存 quote ロジックが包む。
        Assert.Equal("\"'=SUM(A1,B1)\"", CsvWriter.EscapeField("=SUM(A1,B1)"));

    [Fact]
    public void FormulaPrefixChars_contains_expected_set() =>
        Assert.Equal("=+-@\t\r", CsvWriter.FormulaPrefixChars);
}
