using System.Windows.Automation.Text;
using Xunit;
using yEdit.Accessibility;

namespace yEdit.Core.Tests.Accessibility;

public class TextRangeProviderV2Tests
{
    private sealed class InMemoryHost : IUiaTextHost
    {
        private readonly string _text;

        public InMemoryHost(string text)
        {
            _text = text;
        }

        public string GetTextRange(int start, int length)
        {
            start = System.Math.Clamp(start, 0, _text.Length);
            length = System.Math.Clamp(length, 0, _text.Length - start);
            return _text.Substring(start, length);
        }

        public int TextLength => _text.Length;

        public (int Start, int End) GetSelection() => (0, 0);

        public void SetSelection(int s, int e) { }

        public int NextChar(int o) => System.Math.Min(o + 1, _text.Length);

        public int PrevChar(int o) => System.Math.Max(o - 1, 0);

        public int LineStartOf(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i > 0 && _text[i - 1] != '\n')
                i--;
            return i;
        }

        public int LineEnd(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i < _text.Length && _text[i] != '\n')
                i++;
            if (i < _text.Length)
                i++;
            return i;
        }

        public int LineEndNoBreakOf(int o)
        {
            int e = LineEnd(o);
            if (e > 0 && _text[e - 1] == '\n')
                e--;
            return e;
        }

        public int WordStart(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i > 0 && !char.IsWhiteSpace(_text[i - 1]))
                i--;
            return i;
        }

        public int WordEnd(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i < _text.Length && !char.IsWhiteSpace(_text[i]))
                i++;
            return i;
        }

        public int NextWordStart(int o)
        {
            int i = WordEnd(o);
            while (i < _text.Length && char.IsWhiteSpace(_text[i]))
                i++;
            return i;
        }

        public int PrevWordStart(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i > 0 && char.IsWhiteSpace(_text[i - 1]))
                i--;
            while (i > 0 && !char.IsWhiteSpace(_text[i - 1]))
                i--;
            return i;
        }

        public System.Windows.Rect BoundingRectangle => System.Windows.Rect.Empty;

        public double[] GetBoundingRectangles(int s, int e) => System.Array.Empty<double>();

        public int OffsetFromScreenPoint(double x, double y) => 0;

        public nint Handle => System.IntPtr.Zero;
        public bool HasFocus => false;
        public int ControlTypeId => System.Windows.Automation.ControlType.Document.Id;
        public string Name => "本文";
        public string AutomationId => "editor";

        public void SetFocus() { }
    }

    private static TextProviderImplV2 MakeProvider(string text)
    {
        var host = new InMemoryHost(text);
        var root = new TextControlProviderV2(host);
        return new TextProviderImplV2(host, root);
    }

    [Fact]
    public void ExpandToEnclosingUnit_Character_ReturnsOneCodePoint()
    {
        var p = MakeProvider("abcdef");
        var r = new TextRangeProviderV2(p, 2, 2);
        r.ExpandToEnclosingUnit(TextUnit.Character);
        Assert.Equal("c", r.GetText(int.MaxValue));
    }

    [Fact]
    public void ExpandToEnclosingUnit_Word_ExpandsToWordSpan()
    {
        var p = MakeProvider("hello world");
        var r = new TextRangeProviderV2(p, 3, 3);
        r.ExpandToEnclosingUnit(TextUnit.Word);
        Assert.Equal("hello", r.GetText(int.MaxValue));
    }

    [Fact]
    public void ExpandToEnclosingUnit_Line_ExcludesLineBreak()
    {
        var p = MakeProvider("aaa\nbbb\nccc");
        var r = new TextRangeProviderV2(p, 5, 5);
        r.ExpandToEnclosingUnit(TextUnit.Line);
        Assert.Equal("bbb", r.GetText(int.MaxValue));
    }

    [Fact]
    public void ExpandToEnclosingUnit_Line_EmptyLineHasZeroLength()
    {
        var p = MakeProvider("aaa\n\nbbb");
        var r = new TextRangeProviderV2(p, 4, 4);
        r.ExpandToEnclosingUnit(TextUnit.Line);
        Assert.Equal("", r.GetText(int.MaxValue));
    }

    [Fact]
    public void Move_CharForward_PreservesUnitSpan()
    {
        // PC-Talker の文字歩き挙動: Expand(Char) → Move(Char, 1) → GetText を繰り返し
        var p = MakeProvider("abc");
        var r = new TextRangeProviderV2(p, 0, 0);
        r.ExpandToEnclosingUnit(TextUnit.Character); // "a"
        int moved = r.Move(TextUnit.Character, 1);
        Assert.Equal(1, moved);
        Assert.Equal("b", r.GetText(int.MaxValue)); // b が読める(退化させない)
    }

    [Fact]
    public void GetText_RangeIsClamped()
    {
        var p = MakeProvider("abc");
        var r = new TextRangeProviderV2(p, 0, 3);
        Assert.Equal("abc", r.GetText(int.MaxValue));
        Assert.Equal("ab", r.GetText(2));
    }

    [Fact]
    public void FindText_LocalSearch_ReturnsSubrange()
    {
        var p = MakeProvider("aabbcc");
        var r = new TextRangeProviderV2(p, 0, 6);
        var found = r.FindText("bb", false, false) as TextRangeProviderV2;
        Assert.NotNull(found);
        Assert.Equal("bb", found!.GetText(int.MaxValue));
    }
}
