using System;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace yEdit.Accessibility;

/// <summary>
/// UIA テキスト範囲（ITextRangeProvider）。[Start, End) のオフセット対で表現。
/// SR はこの上の GetText / ExpandToEnclosingUnit / Move / MoveEndpointByUnit を多用して読む。
/// デバッグ計装あり（UiaDiag）: どの SR がどのメソッドをどう呼ぶか切り分けるため。
/// </summary>
internal sealed class TextRangeProvider : ITextRangeProvider
{
    private static int _seq;

    private readonly TextProviderImpl _owner;
    private readonly int _id;
    private int _start;
    private int _end;

    public TextRangeProvider(TextProviderImpl owner, int start, int end)
    {
        _owner = owner;
        _id = Interlocked.Increment(ref _seq);
        int len = owner.Host.TextLength;
        start = TextNavigation.Clamp(start, 0, len);
        end = TextNavigation.Clamp(end, 0, len);
        if (start > end) (start, end) = (end, start);
        _start = start;
        _end = end;
    }

    private string Tag => $"TR#{_id}[{_start},{_end}]";
    private static int Tid => Environment.CurrentManagedThreadId;

    public ITextRangeProvider Clone()
    {
        var c = new TextRangeProvider(_owner, _start, _end);
        UiaDiag.Log($"{Tag} Clone -> TR#{((TextRangeProvider)c)._id} tid={Tid}");
        return c;
    }

    public bool Compare(ITextRangeProvider range)
        => range is TextRangeProvider o && o._start == _start && o._end == _end;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        var o = (TextRangeProvider)targetRange;
        int a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int b = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
        UiaDiag.Log($"{Tag} CompareEndpoints({endpoint} vs TR#{o._id}.{targetEndpoint}) -> {a - b} tid={Tid}");
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
        UiaDiag.Log($"TR#{_id} ExpandToEnclosingUnit({unit}) -> [{_start},{_end}] tid={Tid}");
    }

    public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward)
    {
        UiaDiag.Log($"{Tag} FindAttribute(id={attributeId}) tid={Tid}");
        return null;
    }

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        UiaDiag.Log($"{Tag} FindText('{UiaDiag.Trunc(text)}', back={backward}, ic={ignoreCase}) tid={Tid}");
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

    public object GetAttributeValue(int attributeId)
    {
        UiaDiag.Log($"{Tag} GetAttributeValue(id={attributeId}) -> NotSupported tid={Tid}");
        return AutomationElement.NotSupported;
    }

    public double[] GetBoundingRectangles()
    {
        var rects = _owner.Host.GetBoundingRectangles(_start, _end);
        UiaDiag.Log($"{Tag} GetBoundingRectangles -> {rects.Length / 4} rect(s) tid={Tid}");
        return rects;
    }

    public IRawElementProviderSimple GetEnclosingElement() => _owner.RootProvider;

    public string GetText(int maxLength)
    {
        string text = _owner.Host.GetText();
        int s = TextNavigation.Clamp(_start, 0, text.Length);
        int e = TextNavigation.Clamp(_end, 0, text.Length);
        int count = e - s;
        if (count < 0) count = 0;
        if (maxLength >= 0 && count > maxLength) count = maxLength;
        string result = text.Substring(s, count);
        UiaDiag.Log($"{Tag} GetText(max={maxLength}) -> '{UiaDiag.Trunc(result)}' tid={Tid}");
        return result;
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

        int ret = count < 0 ? -moved : moved;
        UiaDiag.Log($"TR#{_id} Move({unit},{count}) -> moved={ret} now[{_start},{_end}] tid={Tid}");
        return ret;
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
        int ret = count < 0 ? -moved : moved;
        UiaDiag.Log($"TR#{_id} MoveEndpointByUnit({endpoint},{unit},{count}) -> moved={ret} now[{_start},{_end}] tid={Tid}");
        return ret;
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
        UiaDiag.Log($"TR#{_id} MoveEndpointByRange({endpoint},TR#{o._id}.{targetEndpoint}) -> now[{_start},{_end}] tid={Tid}");
    }

    public void Select()
    {
        UiaDiag.Log($"{Tag} Select tid={Tid}");
        _owner.Host.SetSelection(_start, _end);
    }

    public void AddToSelection() { /* SupportedTextSelection.Single のため無効 */ }

    public void RemoveFromSelection() { /* SupportedTextSelection.Single のため無効 */ }

    public void ScrollIntoView(bool alignToTop) { /* 実験では未対応 */ }

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
