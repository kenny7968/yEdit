using yEdit.Core.Reading;
using Xunit;

namespace yEdit.Core.Tests.Reading;

public class PositionFormatterTests
{
    [Fact]
    public void Formats_without_selection()
        => Assert.Equal("行 12 / 全 340、桁 5、文字数 89012",
            PositionFormatter.Format(line: 12, totalLines: 340, column: 5, totalChars: 89012, selectionLength: 0));

    [Fact]
    public void Appends_selection_when_present()
        => Assert.Equal("行 1 / 全 1、桁 1、文字数 0、選択 7 文字",
            PositionFormatter.Format(1, 1, 1, 0, 7));
}
