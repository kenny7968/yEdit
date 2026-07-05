using yEdit.Core.Buffers;
using yEdit.Core.Editing;

namespace yEdit.Core.Tests.Editing;

public class NavigationCommandsTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    [Fact]
    public void MoveLeftChar_SkipsSurrogatePair()
    {
        var s = Snap("a😀b");  // "a" + "😀"(surrogate pair) + "b"、CharLength=4
        Assert.Equal(3, NavigationCommands.MoveLeftChar(s, 4));  // 'b' の前 → surrogate high の後にキャレット
        Assert.Equal(1, NavigationCommands.MoveLeftChar(s, 3));  // surrogate high → 'a' の後
        Assert.Equal(0, NavigationCommands.MoveLeftChar(s, 1));
        Assert.Equal(0, NavigationCommands.MoveLeftChar(s, 0));  // 先頭で no-op
    }

    [Fact]
    public void MoveRightChar_SkipsSurrogatePair()
    {
        var s = Snap("a😀b");
        Assert.Equal(1, NavigationCommands.MoveRightChar(s, 0));
        Assert.Equal(3, NavigationCommands.MoveRightChar(s, 1));  // 'a' の後 → 😀 の後
        Assert.Equal(4, NavigationCommands.MoveRightChar(s, 3));
        Assert.Equal(4, NavigationCommands.MoveRightChar(s, 4));  // 末尾で no-op
    }

    [Fact]
    public void MoveLeftChar_OnEmptyBuffer_ReturnsZero()
    {
        var s = Snap("");
        Assert.Equal(0, NavigationCommands.MoveLeftChar(s, 0));
    }

    [Fact]
    public void MoveRightChar_OnEmptyBuffer_ReturnsZero()
    {
        var s = Snap("");
        Assert.Equal(0, NavigationCommands.MoveRightChar(s, 0));
    }

    [Fact]
    public void MoveHome_ReturnsLineStart()
    {
        var s = Snap("abc\r\ndef");
        Assert.Equal(0, NavigationCommands.MoveHome(s, 2));   // 行0内
        Assert.Equal(0, NavigationCommands.MoveHome(s, 0));   // 行0の先頭
        Assert.Equal(5, NavigationCommands.MoveHome(s, 7));   // "def" の 'e' → 行1の先頭=5
    }

    [Fact]
    public void MoveEnd_ReturnsLineEnd_ExcludingBreak()
    {
        var s = Snap("abc\r\ndef");
        Assert.Equal(3, NavigationCommands.MoveEnd(s, 1));   // 行0の末尾(\r の前)
        Assert.Equal(3, NavigationCommands.MoveEnd(s, 3));   // 既に末尾でも同じ
        Assert.Equal(8, NavigationCommands.MoveEnd(s, 6));   // 行1(EOF・改行なし)
    }

    [Fact]
    public void MoveEnd_LfOnly_ExcludesLf()
    {
        var s = Snap("abc\ndef");
        Assert.Equal(3, NavigationCommands.MoveEnd(s, 1));
    }

    [Fact]
    public void MoveHomeSmart_TogglesBetweenFirstNonWsAndLineStart()
    {
        var s = Snap("  hello");
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 4));   // 本文内(4='l')→ firstNonWs(2)
        Assert.Equal(0, NavigationCommands.MoveHomeSmart(s, 2));   // firstNonWs → lineStart
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 0));   // lineStart → firstNonWs
    }

    [Fact]
    public void MoveHomeSmart_TabsAsWhitespace()
    {
        var s = Snap("\t\thello");
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 4));
    }

    [Fact]
    public void MoveHomeSmart_EmptyLine_ReturnsLineStart()
    {
        var s = Snap("abc\n\nxyz");   // 行1 は空行(lineStart=lineEnd=4)
        Assert.Equal(4, NavigationCommands.MoveHomeSmart(s, 4));   // firstNonWs=lineEnd=4=lineStart相当
    }

    [Fact]
    public void MoveHomeSmart_LineWithOnlyWhitespace_TogglesLineStartLineEnd()
    {
        var s = Snap("   ");   // 空白のみ(firstNonWs=lineEnd=3)
        Assert.Equal(3, NavigationCommands.MoveHomeSmart(s, 0));   // lineStart(0) → firstNonWs(3)
        Assert.Equal(0, NavigationCommands.MoveHomeSmart(s, 3));   // firstNonWs(3) → lineStart(0)
    }
}
