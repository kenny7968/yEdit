using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace yEdit.Accessibility;

/// <summary>
/// UIA テキスト範囲（ITextRangeProvider）。[Start, End) のオフセット対で表現。
/// SR はこの上の GetText / ExpandToEnclosingUnit / Move / MoveEndpointByUnit を多用して読む
/// （PC-Talker の文字/行歩き読みのホットパス。余計なアロケーションを持ち込まないこと）。
/// </summary>
internal sealed class TextRangeProvider : ITextRangeProvider
{
    private readonly TextProviderImpl _owner;
    private int _start;
    private int _end;

    public TextRangeProvider(TextProviderImpl owner, int start, int end)
    {
        _owner = owner;
        int len = owner.Host.TextLength;
        start = TextNavigation.Clamp(start, 0, len);
        end = TextNavigation.Clamp(end, 0, len);
        if (start > end) (start, end) = (end, start);
        _start = start;
        _end = end;
    }

    public ITextRangeProvider Clone() => new TextRangeProvider(_owner, _start, _end);

    public bool Compare(ITextRangeProvider range)
        => range is TextRangeProvider o && o._start == _start && o._end == _end;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        var o = (TextRangeProvider)targetRange;
        int a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int b = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
        return a - b;
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        string text = _owner.Host.GetText();
        int len = text.Length;
        int pos = TextNavigation.Clamp(_start, 0, len);
        switch (unit)
        {
            case TextUnit.Character:
                _start = pos;
                _end = TextNavigation.NextChar(text, pos);
                break;
            case TextUnit.Word:
            case TextUnit.Format:
                _start = TextNavigation.WordStart(text, pos);
                _end = TextNavigation.WordEnd(text, _start);
                if (_end == _start) _end = TextNavigation.NextChar(text, _start);
                break;
            case TextUnit.Line:
            case TextUnit.Paragraph:
                // 行の読み取りは改行を含めない（空行を長さ0で公開）。
                _start = TextNavigation.LineStart(text, pos);
                _end = TextNavigation.LineEndNoBreak(text, pos);
                break;
            default: // Page / Document
                _start = 0;
                _end = len;
                break;
        }
    }

    public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) => null;

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        string content = _owner.Host.GetText();
        int s = TextNavigation.Clamp(_start, 0, content.Length);
        int e = TextNavigation.Clamp(_end, 0, content.Length);
        if (string.IsNullOrEmpty(text) || s >= e) return null;
        string hay = content.Substring(s, e - s);
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int idx = backward ? hay.LastIndexOf(text, cmp) : hay.IndexOf(text, cmp);
        if (idx < 0) return null;
        return new TextRangeProvider(_owner, s + idx, s + idx + text.Length);
    }

    public object GetAttributeValue(int attributeId) => AutomationElement.NotSupported;

    public double[] GetBoundingRectangles() => _owner.Host.GetBoundingRectangles(_start, _end);

    public IRawElementProviderSimple GetEnclosingElement() => _owner.RootProvider;

    public string GetText(int maxLength)
    {
        string text = _owner.Host.GetText();
        int s = TextNavigation.Clamp(_start, 0, text.Length);
        int e = TextNavigation.Clamp(_end, 0, text.Length);
        int count = e - s;
        if (count < 0) count = 0;
        if (maxLength >= 0 && count > maxLength) count = maxLength;
        return text.Substring(s, count);
    }

    public int Move(TextUnit unit, int count)
    {
        string text = _owner.Host.GetText();
        bool wasDegenerate = _start == _end;

        // 開始端へ畳んでから count 単位ぶん移動。
        int pos = _start;
        int moved = 0;
        if (count > 0)
            for (int i = 0; i < count; i++) { int n = StepForward(text, pos, unit); if (n == pos) break; pos = n; moved++; }
        else if (count < 0)
            for (int i = 0; i < -count; i++) { int p = StepBackward(text, pos, unit); if (p == pos) break; pos = p; moved++; }
        _start = _end = pos;

        // 元が単位スパンを持っていた範囲は、移動先で同じ1単位ぶん再展開してスパンを保つ。
        // （PC-Talker は Expand(Char)→Move(Char,1)→GetText を繰り返して各文字を読むため、
        //  ここで退化させると2文字目以降が空になり読めなくなる。UIA の Move 仕様準拠。）
        if (!wasDegenerate)
            ExpandToEnclosingUnit(unit);

        return count < 0 ? -moved : moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        string text = _owner.Host.GetText();
        int pos = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int moved = 0;
        if (count > 0)
            for (int i = 0; i < count; i++) { int n = StepForward(text, pos, unit); if (n == pos) break; pos = n; moved++; }
        else if (count < 0)
            for (int i = 0; i < -count; i++) { int p = StepBackward(text, pos, unit); if (p == pos) break; pos = p; moved++; }

        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = pos;
            if (_end < _start) _end = _start;
        }
        else
        {
            _end = pos;
            if (_start > _end) _start = _end;
        }
        return count < 0 ? -moved : moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        var o = (TextRangeProvider)targetRange;
        int target = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = target;
            if (_end < _start) _end = _start;
        }
        else
        {
            _end = target;
            if (_start > _end) _start = _end;
        }
    }

    public void Select() => _owner.Host.SetSelection(_start, _end);

    public void AddToSelection() { /* SupportedTextSelection.Single のため無効 */ }

    public void RemoveFromSelection() { /* SupportedTextSelection.Single のため無効 */ }

    public void ScrollIntoView(bool alignToTop) { /* 未対応（PC-Talker はテキスト歩きで読めるため省略） */ }

    public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();

    // ---------- 単位ステップ ----------

    private static int StepForward(string text, int pos, TextUnit unit) => unit switch
    {
        TextUnit.Character => TextNavigation.NextChar(text, pos),
        TextUnit.Word or TextUnit.Format => TextNavigation.NextWord(text, pos),
        TextUnit.Line or TextUnit.Paragraph => TextNavigation.LineEnd(text, pos),
        _ => text.Length,
    };

    private static int StepBackward(string text, int pos, TextUnit unit) => unit switch
    {
        TextUnit.Character => TextNavigation.PrevChar(text, pos),
        TextUnit.Word or TextUnit.Format => TextNavigation.PrevWord(text, pos),
        TextUnit.Line or TextUnit.Paragraph => LineBackward(text, pos),
        _ => 0,
    };

    private static int LineBackward(string text, int pos)
    {
        int ls = TextNavigation.LineStart(text, pos);
        if (ls < pos) return ls;        // 行頭でなければ行頭へ
        if (ls == 0) return 0;
        return TextNavigation.LineStart(text, ls - 1); // 行頭なら前行の行頭へ
    }
}
