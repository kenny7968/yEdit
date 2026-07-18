using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace yEdit.Accessibility;

/// <summary>
/// UIA テキスト範囲(v2)。[Start, End) のオフセット対で表現。
/// v1 の Move スパン保持ロジックを踏襲(PC-Talker の文字歩きが動く条件)。
/// テキストアクセスは全て <see cref="_owner"/>.Host の v2 メンバ経由。
/// </summary>
internal sealed class TextRangeProviderV2 : ITextRangeProvider
{
    private readonly TextProviderImplV2 _owner;
    private int _start;
    private int _end;

    public TextRangeProviderV2(TextProviderImplV2 owner, int start, int end)
    {
        _owner = owner;
        int len = owner.Host.TextLength;
        start = System.Math.Clamp(start, 0, len);
        end = System.Math.Clamp(end, 0, len);
        if (start > end)
            (start, end) = (end, start);
        _start = start;
        _end = end;
    }

    public ITextRangeProvider Clone() => new TextRangeProviderV2(_owner, _start, _end);

    public bool Compare(ITextRangeProvider range) =>
        range is TextRangeProviderV2 o && o._start == _start && o._end == _end;

    public int CompareEndpoints(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider targetRange,
        TextPatternRangeEndpoint targetEndpoint
    )
    {
        var o = (TextRangeProviderV2)targetRange;
        int a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int b = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
        return a - b;
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        var host = _owner.Host;
        int len = host.TextLength;
        int pos = System.Math.Clamp(_start, 0, len);
        switch (unit)
        {
            case TextUnit.Character:
                _start = pos;
                _end = host.NextChar(pos);
                break;
            case TextUnit.Word:
            case TextUnit.Format:
                _start = host.WordStart(pos);
                _end = host.WordEnd(_start);
                if (_end == _start)
                    _end = host.NextChar(_start);
                break;
            case TextUnit.Line:
            case TextUnit.Paragraph:
                _start = host.LineStartOf(pos);
                _end = host.LineEndNoBreakOf(pos);
                break;
            default: // Page / Document
                _start = 0;
                _end = len;
                break;
        }
    }

    public ITextRangeProvider FindAttribute(int attribute, object value, bool backward) => null!;

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        var host = _owner.Host;
        int s = System.Math.Clamp(_start, 0, host.TextLength);
        int e = System.Math.Clamp(_end, 0, host.TextLength);
        if (string.IsNullOrEmpty(text) || s >= e)
            return null!;
        string hay = host.GetTextRange(s, e - s);
        var cmp = ignoreCase
            ? System.StringComparison.OrdinalIgnoreCase
            : System.StringComparison.Ordinal;
        int idx = backward ? hay.LastIndexOf(text, cmp) : hay.IndexOf(text, cmp);
        if (idx < 0)
            return null!;
        return new TextRangeProviderV2(_owner, s + idx, s + idx + text.Length);
    }

    public object GetAttributeValue(int attribute) => AutomationElement.NotSupported;

    public double[] GetBoundingRectangles() => _owner.Host.GetBoundingRectangles(_start, _end);

    public IRawElementProviderSimple GetEnclosingElement() => _owner.RootProvider;

    public string GetText(int maxLength)
    {
        var host = _owner.Host;
        int s = System.Math.Clamp(_start, 0, host.TextLength);
        int e = System.Math.Clamp(_end, 0, host.TextLength);
        int count = e - s;
        if (count < 0)
            count = 0;
        if (maxLength >= 0 && count > maxLength)
            count = maxLength;
        return host.GetTextRange(s, count);
    }

    public int Move(TextUnit unit, int count)
    {
        var host = _owner.Host;
        bool wasDegenerate = _start == _end;

        int pos = _start;
        int moved = 0;
        if (count > 0)
            for (int i = 0; i < count; i++)
            {
                int n = StepForward(host, pos, unit);
                if (n == pos)
                    break;
                pos = n;
                moved++;
            }
        else if (count < 0)
            for (int i = 0; i < -count; i++)
            {
                int p = StepBackward(host, pos, unit);
                if (p == pos)
                    break;
                pos = p;
                moved++;
            }
        _start = _end = pos;

        // v1 実装の Move スパン保持を踏襲(PC-Talker の文字歩き=Expand(Char)→Move(Char,1)→GetText で
        // 2 文字目以降が空にならないように、非退化だった元の状態を復元)
        if (!wasDegenerate)
            ExpandToEnclosingUnit(unit);

        return count < 0 ? -moved : moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        var host = _owner.Host;
        int pos = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int moved = 0;
        if (count > 0)
            for (int i = 0; i < count; i++)
            {
                int n = StepForward(host, pos, unit);
                if (n == pos)
                    break;
                pos = n;
                moved++;
            }
        else if (count < 0)
            for (int i = 0; i < -count; i++)
            {
                int p = StepBackward(host, pos, unit);
                if (p == pos)
                    break;
                pos = p;
                moved++;
            }

        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = pos;
            if (_end < _start)
                _end = _start;
        }
        else
        {
            _end = pos;
            if (_start > _end)
                _start = _end;
        }
        return count < 0 ? -moved : moved;
    }

    public void MoveEndpointByRange(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider targetRange,
        TextPatternRangeEndpoint targetEndpoint
    )
    {
        var o = (TextRangeProviderV2)targetRange;
        int target = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = target;
            if (_end < _start)
                _end = _start;
        }
        else
        {
            _end = target;
            if (_start > _end)
                _start = _end;
        }
    }

    public void Select() => _owner.Host.SetSelection(_start, _end);

    public void AddToSelection() { /* SupportedTextSelection.Single のため無効 */
    }

    public void RemoveFromSelection() { /* SupportedTextSelection.Single のため無効 */
    }

    public void ScrollIntoView(
        bool alignToTop
    ) { /* PC-Talker はテキスト歩きで読めるため省略(v1 挙動踏襲) */
    }

    public IRawElementProviderSimple[] GetChildren() =>
        System.Array.Empty<IRawElementProviderSimple>();

    // ---------- 単位ステップ(host v2 メンバ経由) ----------

    private static int StepForward(IUiaTextHost host, int pos, TextUnit unit) =>
        unit switch
        {
            TextUnit.Character => host.NextChar(pos),
            TextUnit.Word or TextUnit.Format => host.NextWordStart(pos),
            TextUnit.Line or TextUnit.Paragraph => host.LineEnd(pos),
            _ => host.TextLength,
        };

    private static int StepBackward(IUiaTextHost host, int pos, TextUnit unit) =>
        unit switch
        {
            TextUnit.Character => host.PrevChar(pos),
            TextUnit.Word or TextUnit.Format => host.PrevWordStart(pos),
            TextUnit.Line or TextUnit.Paragraph => LineBackward(host, pos),
            _ => 0,
        };

    private static int LineBackward(IUiaTextHost host, int pos)
    {
        int ls = host.LineStartOf(pos);
        if (ls < pos)
            return ls; // 行頭でなければ行頭へ
        if (ls == 0)
            return 0;
        return host.LineStartOf(ls - 1); // 行頭なら前行の行頭へ
    }
}
