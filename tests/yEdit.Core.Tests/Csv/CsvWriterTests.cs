using yEdit.Core.Csv;
using Xunit;

namespace yEdit.Core.Tests.Csv;

public class CsvWriterTests
{
    [Fact] public void Plain_value_is_unchanged() => Assert.Equal("abc", CsvWriter.EscapeField("abc"));
    [Fact] public void Empty_value_is_unchanged() => Assert.Equal("", CsvWriter.EscapeField(""));
    [Fact] public void Comma_is_quoted() => Assert.Equal("\"a,b\"", CsvWriter.EscapeField("a,b"));
    [Fact] public void Quote_is_doubled_and_wrapped() => Assert.Equal("\"he \"\"q\"\"\"", CsvWriter.EscapeField("he \"q\""));
    [Fact] public void Lf_is_quoted() => Assert.Equal("\"a\nb\"", CsvWriter.EscapeField("a\nb"));
    [Fact] public void Cr_is_quoted() => Assert.Equal("\"a\rb\"", CsvWriter.EscapeField("a\rb"));
    [Fact] public void Leading_space_is_not_quoted() => Assert.Equal(" a ", CsvWriter.EscapeField(" a "));
}
