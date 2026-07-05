using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using yEdit.Accessibility;
using WpfRect = System.Windows.Rect;

namespace yEdit.UiaProbe;

/// <summary>
/// 完全自作描画のテキストコントロール。UIA TextPattern を生プロバイダで公開し、
/// 「PC-Talker が自作 UIA テキストを Tier 2 で読めるか」を実機検証するための最小実装。
///
/// 診断のため、SR が位置追従に使う 2 系統（システムキャレット / UIA イベント）を
/// 個別に ON/OFF でき、報告 ControlType（Document/Edit）も切り替えられる。
/// </summary>
public sealed class UiaTextControl : Control, IUiaTextHost
{
    private string _text = string.Empty;     // 不変参照（編集ごとに差し替え）
    private volatile int _caret;             // キャレット位置（オフセット）
    private volatile int _anchor;            // 選択アンカー
    private volatile bool _hasFocus;
    private volatile int _controlTypeId = ControlType.Document.Id;
    private nint _hwnd;
    private int _lineHeight;

    private TextControlProvider? _provider;

    private readonly object _boundsSync = new();
    private WpfRect _bounds;

    // ---- 診断トグル（デザイナ非使用なのでシリアライズ対象外） ----
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UseSystemCaret { get; set; } = true;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RaiseUiaSelectionEvents { get; set; } = true;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RaiseUiaTextEvents { get; set; } = true;

    /// <summary>状態ログ出力（UI スレッドから呼ばれる）。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<string>? Log { get; set; }

    public UiaTextControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable,
            true);
        TabStop = true;
        BackColor = Color.White;
        ForeColor = Color.Black;
        Font = new Font("MS ゴシック", 14f);
        _lineHeight = Font.Height;
        Cursor = Cursors.IBeam;
    }

    public void SetInitialText(string text)
    {
        _text = text ?? string.Empty;
        _caret = _anchor = 0;
        Invalidate();
    }

    /// <summary>報告する ControlType を切り替え、UIA に property-changed を通知する。</summary>
    public void SetReportedControlType(int controlTypeId)
    {
        int old = _controlTypeId;
        if (old == controlTypeId) return;
        _controlTypeId = controlTypeId;
        if (_provider != null && AutomationInteropProvider.ClientsAreListening)
        {
            try
            {
                AutomationInteropProvider.RaiseAutomationPropertyChangedEvent(
                    _provider,
                    new AutomationPropertyChangedEventArgs(
                        AutomationElementIdentifiers.ControlTypeProperty, old, controlTypeId));
            }
            catch { /* 実験中は握りつぶす */ }
        }
        RaiseUia(AutomationElementIdentifiers.AutomationFocusChangedEvent);
        LogState("ctlType");
    }

    /// <summary>システムキャレットのトグル反映（メニューから呼ぶ）。</summary>
    public void ApplySystemCaretToggle()
    {
        if (!_hasFocus) return;
        if (UseSystemCaret)
        {
            NativeMethods.CreateCaret(_hwnd, nint.Zero, 2, _lineHeight);
            PositionCaret();
            NativeMethods.ShowCaret(_hwnd);
        }
        else
        {
            NativeMethods.DestroyCaret();
        }
        Invalidate();
    }

    // ==================== UIA: WM_GETOBJECT ====================

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_GETOBJECT && m.LParam.ToInt64() == NativeMethods.UiaRootObjectId)
        {
            _provider ??= new TextControlProvider(this);
            m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, m.WParam, m.LParam, _provider);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hwnd = Handle;
        _provider ??= new TextControlProvider(this);
        UpdateBoundsCache();
    }

    // ==================== フォーカス ====================

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        if (UseSystemCaret)
        {
            NativeMethods.CreateCaret(_hwnd, nint.Zero, 2, _lineHeight);
            PositionCaret();
            NativeMethods.ShowCaret(_hwnd);
        }
        RaiseUia(AutomationElementIdentifiers.AutomationFocusChangedEvent);
        Invalidate();
        LogState("focus");
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        NativeMethods.DestroyCaret();
        Invalidate();
    }

    // ==================== キーボード ====================

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.Home:
            case Keys.End:
            case Keys.PageUp:
            case Keys.PageDown:
            case Keys.Enter:
                return true;
        }
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool shift = e.Shift;
        bool ctrl = e.Control;
        switch (e.KeyCode)
        {
            case Keys.Left:
                MoveCaret(ctrl ? TextNavigation.PrevWord(_text, _caret) : TextNavigation.PrevChar(_text, _caret), shift);
                e.Handled = true; break;
            case Keys.Right:
                MoveCaret(ctrl ? TextNavigation.NextWord(_text, _caret) : TextNavigation.NextChar(_text, _caret), shift);
                e.Handled = true; break;
            case Keys.Up:
                MoveCaret(CaretByLine(-1), shift); e.Handled = true; break;
            case Keys.Down:
                MoveCaret(CaretByLine(+1), shift); e.Handled = true; break;
            case Keys.Home:
                MoveCaret(TextNavigation.LineStart(_text, _caret), shift); e.Handled = true; break;
            case Keys.End:
                MoveCaret(LineEndNoBreak(_caret), shift); e.Handled = true; break;
            case Keys.Back:
                DeleteBackward(); e.Handled = true; break;
            case Keys.Delete:
                DeleteForward(); e.Handled = true; break;
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        char c = e.KeyChar;
        if (c == '\r' || c == '\n') { InsertText("\n"); e.Handled = true; return; }
        if (!char.IsControl(c)) { InsertText(c.ToString()); e.Handled = true; }
    }

    // ==================== マウス ====================

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        MoveCaret(HitTest(e.Location), (ModifierKeys & Keys.Shift) != 0);
    }

    private int HitTest(Point p)
    {
        string[] lines = _text.Split('\n');
        int lineIndex = Math.Max(0, (p.Y - 2) / _lineHeight);
        if (lineIndex >= lines.Length) lineIndex = lines.Length - 1;

        int lineStart = 0;
        for (int i = 0; i < lineIndex; i++) lineStart += lines[i].Length + 1; // +1 = '\n'

        string line = lines[lineIndex];
        int col = line.Length;
        for (int i = 1; i <= line.Length; i++)
        {
            int w = TextRenderer.MeasureText(
                line.Substring(0, i), Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            if (2 + w > p.X) { col = i - 1; break; }
        }
        return Clamp(lineStart + col);
    }

    // ==================== 編集／キャレット ====================

    private void MoveCaret(int newPos, bool extend)
    {
        newPos = Clamp(newPos);
        _caret = newPos;
        if (!extend) _anchor = newPos;
        OnCaretChanged();
    }

    private void InsertText(string s)
    {
        int selS = Math.Min(_anchor, _caret), selE = Math.Max(_anchor, _caret);
        _text = _text.Substring(0, selS) + s + _text.Substring(selE);
        _caret = _anchor = selS + s.Length;
        OnTextMutated();
    }

    private void DeleteBackward()
    {
        int selS = Math.Min(_anchor, _caret), selE = Math.Max(_anchor, _caret);
        if (selS != selE) { _text = _text.Remove(selS, selE - selS); _caret = _anchor = selS; OnTextMutated(); return; }
        if (_caret == 0) return;
        int prev = TextNavigation.PrevChar(_text, _caret);
        _text = _text.Remove(prev, _caret - prev);
        _caret = _anchor = prev;
        OnTextMutated();
    }

    private void DeleteForward()
    {
        int selS = Math.Min(_anchor, _caret), selE = Math.Max(_anchor, _caret);
        if (selS != selE) { _text = _text.Remove(selS, selE - selS); _caret = _anchor = selS; OnTextMutated(); return; }
        if (_caret >= _text.Length) return;
        int next = TextNavigation.NextChar(_text, _caret);
        _text = _text.Remove(_caret, next - _caret);
        OnTextMutated();
    }

    private void OnCaretChanged()
    {
        PositionCaret();
        if (RaiseUiaSelectionEvents) RaiseUia(TextPatternIdentifiers.TextSelectionChangedEvent);
        Invalidate();
        LogState("caret");
    }

    private void OnTextMutated()
    {
        if (RaiseUiaTextEvents) RaiseUia(TextPatternIdentifiers.TextChangedEvent);
        PositionCaret();
        if (RaiseUiaSelectionEvents) RaiseUia(TextPatternIdentifiers.TextSelectionChangedEvent);
        Invalidate();
        LogState("edit");
    }

    private void RaiseUia(AutomationEvent ev)
    {
        if (_provider == null || !AutomationInteropProvider.ClientsAreListening) return;
        try { AutomationInteropProvider.RaiseAutomationEvent(ev, _provider, new AutomationEventArgs(ev)); }
        catch { /* 実験中は握りつぶす */ }
    }

    // ==================== 行・座標ヘルパ ====================

    private int Clamp(int v) => v < 0 ? 0 : (v > _text.Length ? _text.Length : v);

    private int LineEndNoBreak(int offset)
    {
        int e = TextNavigation.LineEnd(_text, offset);
        if (e > 0 && e <= _text.Length && _text[e - 1] == '\n') e--;
        return e;
    }

    private int LineLengthNoBreak(int lineStart) => LineEndNoBreak(lineStart) - lineStart;

    private int CaretByLine(int dir)
    {
        int ls = TextNavigation.LineStart(_text, _caret);
        int col = _caret - ls;
        if (dir < 0)
        {
            if (ls == 0) return _caret; // 先頭行
            int prevStart = TextNavigation.LineStart(_text, ls - 1);
            return prevStart + Math.Min(col, LineLengthNoBreak(prevStart));
        }
        else
        {
            int nextStart = TextNavigation.LineEnd(_text, _caret);
            if (nextStart < _text.Length)
                return nextStart + Math.Min(col, LineLengthNoBreak(nextStart));
            // nextStart == _text.Length
            if (_text.Length > 0 && _text[_text.Length - 1] == '\n')
                return _text.Length; // 末尾が改行 → 仮想の空行へ
            return _caret;           // 最終行、移動なし
        }
    }

    private int CountNewlines(int from, int to)
    {
        int n = 0;
        for (int i = from; i < to && i < _text.Length; i++)
            if (_text[i] == '\n') n++;
        return n;
    }

    private Point CaretPixel(int offset)
    {
        offset = Clamp(offset);
        int ls = TextNavigation.LineStart(_text, offset);
        int lineIndex = CountNewlines(0, ls);
        string prefix = _text.Substring(ls, offset - ls);
        int x = TextRenderer.MeasureText(
            prefix, Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        int y = lineIndex * _lineHeight;
        return new Point(2 + x, 2 + y);
    }

    private void PositionCaret()
    {
        if (!_hasFocus || !UseSystemCaret) return;
        var p = CaretPixel(_caret);
        bool ok = NativeMethods.SetCaretPos(p.X, p.Y);
        UiaDiag.Log($"[sysCaret] offset={_caret} px=({p.X},{p.Y}) setOk={ok} tid={Environment.CurrentManagedThreadId}");
    }

    // ==================== 描画 ====================

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);

        // 選択ハイライト（行ごとの簡易版）
        DrawSelection(g);

        // 本文
        string[] lines = _text.Split('\n');
        int y = 2;
        foreach (string line in lines)
        {
            TextRenderer.DrawText(g, line, Font, new Point(2, y), ForeColor,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            y += _lineHeight;
        }

        // 自前キャレット（システムキャレット非使用時の視覚フィードバック）
        if (_hasFocus && !UseSystemCaret)
        {
            var p = CaretPixel(_caret);
            using var pen = new Pen(ForeColor, 2);
            g.DrawLine(pen, p.X, p.Y, p.X, p.Y + _lineHeight);
        }

        UpdateBoundsCache();
    }

    private void DrawSelection(Graphics g)
    {
        int s = Math.Min(_anchor, _caret), en = Math.Max(_anchor, _caret);
        if (s == en) return;
        using var brush = new SolidBrush(Color.FromArgb(90, Color.SteelBlue));
        int pos = s;
        while (pos < en)
        {
            int segEnd = Math.Min(en, LineEndNoBreak(pos));
            var p1 = CaretPixel(pos);
            var p2 = CaretPixel(segEnd);
            g.FillRectangle(brush, p1.X, p1.Y, Math.Max(3, p2.X - p1.X), _lineHeight);
            int nextLine = TextNavigation.LineEnd(_text, pos);
            if (nextLine <= pos) break;
            pos = nextLine;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateBoundsCache();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        UpdateBoundsCache();
    }

    private void UpdateBoundsCache()
    {
        if (!IsHandleCreated) return;
        var r = RectangleToScreen(ClientRectangle);
        lock (_boundsSync) _bounds = new WpfRect(r.Left, r.Top, r.Width, r.Height);
    }

    private void LogState(string tag)
    {
        int s = Math.Min(_anchor, _caret), en = Math.Max(_anchor, _caret);
        string ct = _controlTypeId == ControlType.Edit.Id ? "Edit" : "Document";
        Log?.Invoke(
            $"[{tag}] caret={_caret} sel=[{s},{en}] len={_text.Length} " +
            $"ctlType={ct} sysCaret={(UseSystemCaret ? 1 : 0)} " +
            $"uiaSel={(RaiseUiaSelectionEvents ? 1 : 0)} uiaText={(RaiseUiaTextEvents ? 1 : 0)}");
    }

    // ==================== IUiaTextHost ====================

    string IUiaTextHost.GetText() => _text;

    int IUiaTextHost.TextLength => _text.Length;

    (int Start, int End) IUiaTextHost.GetSelection()
    {
        int c = _caret, a = _anchor;
        return (Math.Min(a, c), Math.Max(a, c));
    }

    void IUiaTextHost.SetSelection(int start, int end)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => ((IUiaTextHost)this).SetSelection(start, end))); return; }
        _anchor = Clamp(start);
        _caret = Clamp(end);
        OnCaretChanged();
    }

    WpfRect IUiaTextHost.BoundingRectangle { get { lock (_boundsSync) return _bounds; } }

    double[] IUiaTextHost.GetBoundingRectangles(int start, int end)
        => Array.Empty<double>(); // 計装ビルドでは空のまま（挙動を変えない）。本番/修正時に実装。

    nint IUiaTextHost.Handle => _hwnd;

    bool IUiaTextHost.HasFocus => _hasFocus;

    int IUiaTextHost.ControlTypeId => _controlTypeId;

    void IUiaTextHost.SetFocus()
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Focus())); return; }
        Focus();
    }
}
