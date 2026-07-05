using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using yEdit.Accessibility;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace yEdit.UiaProbe;

/// <summary>
/// プローブ専用: UIA プロバイダ呼び出しトレース。SR(特に PC-Talker)がキャレット移動時に
/// どのメソッド/TextUnit で本文を読むかを実機ログで判別するためのデコレータ。
/// yEdit.Accessibility(本番コード)には手を触れず、プローブ側で TextControlProvider を包む。
/// 全メソッドが UIA の RPC スレッドから呼ばれ得るため、trace デリゲートはスレッド安全であること。
/// </summary>
internal sealed class TracingRootProvider :
    IRawElementProviderSimple,
    IRawElementProviderFragment,
    IRawElementProviderFragmentRoot
{
    private readonly TextControlProvider _inner;
    private readonly Action<string> _trace;

    public TracingRootProvider(TextControlProvider inner, Action<string> trace)
    {
        _inner = inner;
        _trace = trace;
    }

    public ProviderOptions ProviderOptions => _inner.ProviderOptions;

    public object? GetPatternProvider(int patternId)
    {
        var p = _inner.GetPatternProvider(patternId);
        if (p is ITextProvider tp)
        {
            _trace($"GetPatternProvider(TextPattern) tid={Environment.CurrentManagedThreadId}");
            return new TracingTextProvider(tp, this, _trace);
        }
        return p;
    }

    public object? GetPropertyValue(int propertyId) => _inner.GetPropertyValue(propertyId);

    public IRawElementProviderSimple? HostRawElementProvider => _inner.HostRawElementProvider;

    // ---------- IRawElementProviderFragment ----------

    public WpfRect BoundingRectangle => _inner.BoundingRectangle;

    public IRawElementProviderFragmentRoot FragmentRoot => this;

    public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => _inner.GetEmbeddedFragmentRoots();

    public int[] GetRuntimeId() => _inner.GetRuntimeId();

    public IRawElementProviderFragment? Navigate(NavigateDirection direction) => _inner.Navigate(direction);

    public void SetFocus()
    {
        _trace("SetFocus");
        _inner.SetFocus();
    }

    // ---------- IRawElementProviderFragmentRoot ----------

    public IRawElementProviderFragment ElementProviderFromPoint(double x, double y)
    {
        _trace($"ElementProviderFromPoint({x:F0},{y:F0})");
        return this;
    }

    public IRawElementProviderFragment? GetFocus() => _inner.GetFocus() is null ? null : this;
}

/// <summary>ITextProvider のトレースラッパ。返す範囲を TracingTextRange で包む。</summary>
internal sealed class TracingTextProvider : ITextProvider
{
    private readonly ITextProvider _inner;
    private readonly IRawElementProviderSimple _root;
    private readonly Action<string> _trace;

    public TracingTextProvider(ITextProvider inner, IRawElementProviderSimple root, Action<string> trace)
    {
        _inner = inner;
        _root = root;
        _trace = trace;
    }

    private TracingTextRange Wrap(ITextRangeProvider r) => new(r, _root, _trace);

    public ITextRangeProvider[] GetSelection()
    {
        var rs = _inner.GetSelection();
        var wrapped = Array.ConvertAll(rs, Wrap);
        _trace($"GetSelection -> {string.Join(",", Array.ConvertAll(wrapped, w => w.Describe()))}");
        return wrapped;
    }

    public ITextRangeProvider[] GetVisibleRanges()
    {
        var wrapped = Array.ConvertAll(_inner.GetVisibleRanges(), Wrap);
        _trace($"GetVisibleRanges -> {wrapped.Length} ranges");
        return wrapped;
    }

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
    {
        var w = Wrap(_inner.RangeFromChild(childElement));
        _trace($"RangeFromChild -> {w.Describe()}");
        return w;
    }

    public ITextRangeProvider RangeFromPoint(WpfPoint screenLocation)
    {
        var w = Wrap(_inner.RangeFromPoint(screenLocation));
        _trace($"RangeFromPoint({screenLocation.X:F0},{screenLocation.Y:F0}) -> {w.Describe()}");
        return w;
    }

    public ITextRangeProvider DocumentRange
    {
        get
        {
            var w = Wrap(_inner.DocumentRange);
            _trace($"DocumentRange -> {w.Describe()}");
            return w;
        }
    }

    public SupportedTextSelection SupportedTextSelection => _inner.SupportedTextSelection;
}

/// <summary>
/// ITextRangeProvider のトレースラッパ。呼び出しと応答(範囲スナップショット)を1行ずつ記録。
/// 引数に来る範囲はサーバー実装が内部型へキャストするため Unwrap して委譲する。
/// </summary>
internal sealed class TracingTextRange : ITextRangeProvider
{
    private static int s_nextId;

    private readonly ITextRangeProvider _inner;
    private readonly IRawElementProviderSimple _root;
    private readonly Action<string> _trace;
    private readonly int _id;

    public TracingTextRange(ITextRangeProvider inner, IRawElementProviderSimple root, Action<string> trace)
    {
        _inner = inner;
        _root = root;
        _trace = trace;
        _id = Interlocked.Increment(ref s_nextId);
    }

    private static ITextRangeProvider Unwrap(ITextRangeProvider r) => r is TracingTextRange t ? t._inner : r;

    private static string Esc(string s)
    {
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length <= 60 ? s : s.Substring(0, 60) + "...";
    }

    /// <summary>範囲の現在内容の覗き読み(トレース専用・inner 直呼びなのでログには出ない)。</summary>
    private string Peek()
    {
        try { return Esc(_inner.GetText(60)); }
        catch { return "<err>"; }
    }

    public string Describe() => $"R{_id}'{Peek()}'";

    public ITextRangeProvider Clone()
    {
        var c = new TracingTextRange(_inner.Clone(), _root, _trace);
        _trace($"R{_id}.Clone -> R{c._id}");
        return c;
    }

    public bool Compare(ITextRangeProvider range)
    {
        bool r = _inner.Compare(Unwrap(range));
        _trace($"R{_id}.Compare -> {r}");
        return r;
    }

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        int r = _inner.CompareEndpoints(endpoint, Unwrap(targetRange), targetEndpoint);
        _trace($"R{_id}.CompareEndpoints({endpoint},{targetEndpoint}) -> {r}");
        return r;
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        _inner.ExpandToEnclosingUnit(unit);
        _trace($"R{_id}.Expand({unit}) -> '{Peek()}'");
    }

    public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward)
    {
        var f = _inner.FindAttribute(attributeId, value, backward);
        _trace($"R{_id}.FindAttribute({attributeId}) -> {(f is null ? "null" : "range")}");
        return f is null ? null : new TracingTextRange(f, _root, _trace);
    }

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        var f = _inner.FindText(text, backward, ignoreCase);
        var w = f is null ? null : new TracingTextRange(f, _root, _trace);
        _trace($"R{_id}.FindText('{Esc(text)}') -> {(w is null ? "null" : $"R{w._id}")}");
        return w;
    }

    public object GetAttributeValue(int attributeId)
    {
        var v = _inner.GetAttributeValue(attributeId);
        _trace($"R{_id}.GetAttributeValue({attributeId})");
        return v;
    }

    public double[] GetBoundingRectangles()
    {
        var v = _inner.GetBoundingRectangles();
        _trace($"R{_id}.GetBoundingRectangles -> len={v?.Length ?? 0}");
        return v ?? Array.Empty<double>();
    }

    public IRawElementProviderSimple GetEnclosingElement()
    {
        _trace($"R{_id}.GetEnclosingElement");
        return _root;
    }

    public string GetText(int maxLength)
    {
        string s = _inner.GetText(maxLength);
        _trace($"R{_id}.GetText({maxLength}) -> '{Esc(s)}' len={s.Length}");
        return s;
    }

    public int Move(TextUnit unit, int count)
    {
        int moved = _inner.Move(unit, count);
        _trace($"R{_id}.Move({unit},{count}) -> {moved} span='{Peek()}'");
        return moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        int moved = _inner.MoveEndpointByUnit(endpoint, unit, count);
        _trace($"R{_id}.MoveEndpointByUnit({endpoint},{unit},{count}) -> {moved} span='{Peek()}'");
        return moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        _inner.MoveEndpointByRange(endpoint, Unwrap(targetRange), targetEndpoint);
        string src = targetRange is TracingTextRange t ? $"R{t._id}" : "ext";
        _trace($"R{_id}.MoveEndpointByRange({endpoint} <- {src}.{targetEndpoint}) span='{Peek()}'");
    }

    public void Select()
    {
        _inner.Select();
        _trace($"R{_id}.Select '{Peek()}'");
    }

    public void AddToSelection()
    {
        _trace($"R{_id}.AddToSelection");
        _inner.AddToSelection();
    }

    public void RemoveFromSelection()
    {
        _trace($"R{_id}.RemoveFromSelection");
        _inner.RemoveFromSelection();
    }

    public void ScrollIntoView(bool alignToTop)
    {
        _trace($"R{_id}.ScrollIntoView({alignToTop})");
        _inner.ScrollIntoView(alignToTop);
    }

    public IRawElementProviderSimple[] GetChildren() => _inner.GetChildren();
}
