using System.ComponentModel;
using yEdit.Core.Buffers;
using yEdit.Core.Layout;
// System.Windows.Forms.SelectionRange(MonthCalendar 用)と同名のため別名で解決する。
using SelectionRange = yEdit.Core.Layout.SelectionRange;

namespace yEdit.Editor;

/// <summary>
/// P2 で導入する自作エディットコントロール。P1 の <see cref="TextBuffer"/>/<see cref="TextSnapshot"/>
/// をソースに、Layout 層(<c>ViewportLayout</c>/<c>FrameBuilder</c>)が組み立てた <see cref="Frame"/> を
/// GDI 呼び出しに置換して描画する。P6 で <c>ScintillaHost</c> を置換する予定・現状は並行運用。
/// UI スレッド専用(<see cref="GdiCharMetrics"/>・<c>SetSource</c> は 1 度だけ)。
/// </summary>
public sealed class EditorControl : Control
{
    private readonly Font _font;
    private readonly ICharMetrics _metrics;
    private readonly ViewportStyle _style;
    private readonly VScrollBar _vscroll;
    private TextBuffer? _buffer;
    private int _topLine;
    private bool _showLineNumbers;
    private bool _highlightCurrentLine;

    // キャレット/選択の内部状態(Task 9 で SetCaretCharOffset/SetSelectionCharRange 経由で更新)
    // Invariant: _selStart <= _selEnd は API 側(SetSelectionCharRange)で保証・OnPaint も念のため
    // Min/Max で正規化してから SelectionRange を組み立てる。SetCaret は選択も同 offset に潰す。
    private int _caret;
    private int _selStart;
    private int _selEnd;

    // Task 10: システムキャレットのフォーカス状態フラグ。CreateCaret/DestroyCaret はフォーカスを
    // 持つ間のみ有効なため、SetCaretCharOffset 等から PositionCaret を呼ぶ際にガードに使う。
    private bool _hasFocus;

    // cellHighlight は Task 11(検索ハイライト)で書き込み側実装予定。
    // 現状は読み取り側だけあるので CS0649 を局所抑止(Task 11 で pragma 撤去)。
#pragma warning disable CS0649 // Task 11 で書き込み側を実装する
    private SelectionRange? _cellHighlight;
#pragma warning restore CS0649

    public EditorControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable,
            true);
        TabStop = true;
        BackColor = Color.White;
        ForeColor = Color.Black;
        _font = new Font("MS ゴシック", 12f);
        _metrics = new GdiCharMetrics(_font);
        _style = DefaultStyle();
        Cursor = Cursors.IBeam;

        // 空文書想定で初期は Enabled=false。SetSource で有効化される。
        // Scroll イベントは「ユーザー操作(ドラッグ/ホイール/キー)」でのみ発火。
        // TopLine setter からの `_vscroll.Value = ...` では発火しないため、
        // TopLine ↔ VScrollBar 間の無限ループは起こらない(セッター側の != チェックは念のため)。
        _vscroll = new VScrollBar { Dock = DockStyle.Right, SmallChange = 1, Enabled = false };
        _vscroll.Scroll += (_, e) => TopLine = e.NewValue;
        Controls.Add(_vscroll);
    }

    /// <summary>ソースの <see cref="TextBuffer"/> を差し込む(1 度だけ)。</summary>
    public void SetSource(TextBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_buffer is not null)
            throw new InvalidOperationException("SetSource は 1 度だけ");
        _buffer = buffer;
        _topLine = 0;
        UpdateVerticalScrollbar();
        Invalidate();
    }

    /// <summary>行の高さ(px)。<see cref="ICharMetrics.LineHeightPx"/> の透過。</summary>
    public int LineHeightPx => _metrics.LineHeightPx;

    // 後続タスク受け口(バッキングは auto-property・本実装は該当タスクで)
    // [Browsable(false)] + [DesignerSerializationVisibility(Hidden)] は
    // Control 派生の public プロパティに対する WFO1000 を回避する意図(デザイナ非対応の宣言)。

    /// <summary>
    /// 可視領域の先頭に置く論理行(0 始まり)。set 時は [0, LineCount-1] にクランプ、
    /// 変化時のみ VScrollBar.Value を追従させて Invalidate。折り返し ON でも TopLine の
    /// 先頭視覚行から描画する(§0-3=論理行の途中から始めない)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TopLine
    {
        get => _topLine;
        set
        {
            int clamped = ClampTopLine(value);
            if (clamped == _topLine) return;
            _topLine = clamped;
            if (_vscroll.Value != clamped) _vscroll.Value = clamped;
            PositionCaret();
            Invalidate();
        }
    }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int WrapColumns { get; set; }           // Task 12 で本実装
    /// <summary>
    /// 行番号マージンを表示するか。true にすると <see cref="MeasureLineNumberWidth"/> 幅のマージンを確保し、
    /// FrameBuilder が右寄せで行番号を発行する(現在行のみ <see cref="ViewportStyle.Foreground"/> で強調)。
    /// 変化時のみ Invalidate。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set
        {
            if (_showLineNumbers == value) return;
            _showLineNumbers = value;
            Invalidate();
        }
    }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowWhitespace { get; set; }       // Task 11 で本実装

    /// <summary>
    /// キャレット論理行の背景を <see cref="ViewportStyle.CurrentLineBack"/> で塗るか。
    /// <b>選択がある間(_selStart != _selEnd)は塗らない</b>=OnPaint で FrameBuilder への
    /// currentLineLogical に -1 を渡す(選択矩形との視覚的競合を避けるため)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HighlightCurrentLine
    {
        get => _highlightCurrentLine;
        set
        {
            if (_highlightCurrentLine == value) return;
            _highlightCurrentLine = value;
            Invalidate();
        }
    }

    /// <summary>キャレット位置(UTF-16 文字オフセット)。書き込みは <see cref="SetCaretCharOffset"/>。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CaretCharOffset => _caret;

    /// <summary>
    /// キャレット位置を UTF-16 文字オフセットで設定する(選択はクリアされる=_selStart=_selEnd=offset)。
    /// サロゲートペア中間位置(low)は前方(high)にスナップ。範囲外は [0, CharLength] にクランプ。
    /// SetSource 前の呼び出しは no-op(_buffer が null のため)。
    /// </summary>
    public void SetCaretCharOffset(int offset)
    {
        if (_buffer is null) return;
        int snapped = SnapAndClamp(offset);
        if (_caret == snapped && _selStart == snapped && _selEnd == snapped) return;
        _caret = snapped;
        _selStart = snapped;
        _selEnd = snapped;
        PositionCaret();
        Invalidate();
    }

    /// <summary>現在の選択範囲(UTF-16 文字オフセット・Start &lt;= End で返す)。</summary>
    public (int Start, int End) GetSelectionCharRange()
        => (Math.Min(_selStart, _selEnd), Math.Max(_selStart, _selEnd));

    /// <summary>
    /// 選択範囲を設定する。<paramref name="start"/> &gt; <paramref name="end"/> の場合は
    /// 内部で正規化(Start &lt;= End)。両端はサロゲートペア中間位置なら前方スナップ・
    /// 範囲外はクランプ。キャレットは選択の末尾(正規化後の End)に置く。
    /// SetSource 前の呼び出しは no-op。
    /// </summary>
    /// <remarks>
    /// キャレットが常に選択末尾に置かれるため、shift+左矢印方向の選択(キャレット=Min・
    /// アンカー=Max)は現行 API で表現できない。P3 で入力ハンドラを追加する際にアンカー
    /// 概念を導入する API(非対称版)を追加予定。
    /// </remarks>
    public void SetSelectionCharRange(int start, int end)
    {
        if (_buffer is null) return;
        int s = SnapAndClamp(Math.Min(start, end));
        int e = SnapAndClamp(Math.Max(start, end));
        if (_selStart == s && _selEnd == e && _caret == e) return;
        _selStart = s;
        _selEnd = e;
        _caret = e;
        PositionCaret();
        Invalidate();
    }

    /// <summary>
    /// [0, CharLength] にクランプし、UTF-16 low サロゲート位置なら 1 前方(high 側)へスナップ。
    /// CharLength 位置(=EOF)はキャレットが立てる境界なのでクランプ後もそのまま許可。
    /// </summary>
    private int SnapAndClamp(int offset)
    {
        if (_buffer is null) return 0;
        var snap = _buffer.Current;
        if (offset <= 0) return 0;
        if (offset >= snap.CharLength) return snap.CharLength;
        // offset > 0 は L187 の早期 return で保証済み
        char c = snap.GetChar(offset);
        if (char.IsLowSurrogate(c))
        {
            char prev = snap.GetChar(offset - 1);
            if (char.IsHighSurrogate(prev)) return offset - 1;
        }
        return offset;
    }

    private int ClampTopLine(int value)
    {
        int max = _buffer is null ? 0 : Math.Max(0, _buffer.Current.LineCount - 1);
        if (value < 0) return 0;
        if (value > max) return max;
        return value;
    }

    /// <summary>
    /// VScrollBar の Maximum / LargeChange を現在の buffer と ClientSize から再計算する。
    /// WinForms VScrollBar の到達可能な最大 Value は "Maximum - LargeChange + 1" のため、
    /// TopLine=maxLine を到達させるには Maximum = maxLine + (LargeChange - 1) と置く必要がある。
    /// 順序: Maximum → LargeChange の順に設定(逆順だと Maximum が小さいときに LargeChange が
    /// 内部で clip されて意図した値にならないケースがある)。
    /// </summary>
    private void UpdateVerticalScrollbar()
    {
        if (_buffer is null) return;
        var snap = _buffer.Current;
        int maxLine = Math.Max(0, snap.LineCount - 1);
        int visibleLines = Math.Max(1, ClientSize.Height / Math.Max(1, _metrics.LineHeightPx));
        _vscroll.Maximum = maxLine + Math.Max(0, visibleLines - 1);
        _vscroll.LargeChange = visibleLines;
        _vscroll.SmallChange = 1;
        _vscroll.Value = _topLine;  // _topLine は常に [0, maxLine] にクランプ済み → Value 範囲内
        _vscroll.Enabled = maxLine > 0;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateVerticalScrollbar();
        PositionCaret();
    }

    /// <summary>
    /// フォーカスを受けたときにシステムキャレット(幅 2px・高さ LineHeightPx)を作成し、
    /// 現在の <c>_caret</c> オフセットへ位置決めして表示する。1 ウィンドウにつき Windows は
    /// 1 個のキャレットしか保持しないため、必ず OnLostFocus で DestroyCaret すること。
    /// </summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        NativeMethods.CreateCaret(Handle, nint.Zero, 2, _metrics.LineHeightPx);
        PositionCaret();
        NativeMethods.ShowCaret(Handle);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        NativeMethods.DestroyCaret();
    }

    /// <summary>
    /// <c>_caret</c>(UTF-16 char offset)からクライアント座標(px)を算出し、
    /// システムキャレット位置に反映する。可視外(TopLine 未到達 / 下端超過)は
    /// 見えない位置 (-1000, -1000) へ退避。フォーカス無し・buffer 未設定時は何もしない。
    /// </summary>
    /// <remarks>
    /// 折り返し ON 時は TopLine ～ キャレット行までの各論理行に対して <c>LineLayout.Wrap</c>
    /// を呼び直す(1 論理行ずつ GetText + Wrap)。Task 14 のベンチで顕在化するようなら
    /// Frame の再利用等で最適化する(Task 9 レビュー M-3 の申し送り)。
    /// </remarks>
    private void PositionCaret()
    {
        if (!_hasFocus || _buffer is null) return;
        var snap = _buffer.Current;
        int logicalLine = snap.GetLineIndexOfChar(_caret);

        // TopLine 未到達なら不可視(スクロールでキャレット行が上にはみ出している)
        if (logicalLine < _topLine)
        {
            NativeMethods.SetCaretPos(-1000, -1000);
            return;
        }

        int lineStart = snap.GetLineStart(logicalLine);
        int lineEnd = snap.GetLineEnd(logicalLine, includeBreak: false);
        int lineLen = lineEnd - lineStart;
        string lineText = lineLen == 0 ? string.Empty : snap.GetText(lineStart, lineLen);
        int maxWidthPx = WrapColumns > 0 ? WrapColumns * _metrics.MeasureRun("0") : 0;
        var segments = LineLayout.Wrap(lineText, maxWidthPx, _metrics);

        int caretInLine = _caret - lineStart;

        // caret がどの視覚セグメントに属するかを決める。
        // - 通常は「seg.OffsetInLine + seg.Length で終わる直前」まで
        // - 最終セグメントに限り「末尾ちょうど」も許容(EOL キャレット位置)
        int segIdx = segments.Count - 1;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            int segEnd = seg.OffsetInLine + seg.Length;
            if (caretInLine < segEnd || (i == segments.Count - 1 && caretInLine == segEnd))
            {
                segIdx = i;
                break;
            }
        }
        var chosenSeg = segments[segIdx];
        int localOffset = caretInLine - chosenSeg.OffsetInLine;
        var segSpan = lineText.AsSpan(chosenSeg.OffsetInLine, chosenSeg.Length);
        int xInSeg = PixelMapper.OffsetToPx(segSpan, localOffset, _metrics);

        // TopLine の先頭視覚行を Y=0 として、キャレット視覚行までの積み上げ視覚行数を算出。
        int visualRowsBeforeThisLine = 0;
        for (int line = _topLine; line < logicalLine; line++)
        {
            int lStart = snap.GetLineStart(line);
            int lEnd = snap.GetLineEnd(line, includeBreak: false);
            int lLen = lEnd - lStart;
            string lText = lLen == 0 ? string.Empty : snap.GetText(lStart, lLen);
            var segs = LineLayout.Wrap(lText, maxWidthPx, _metrics);
            visualRowsBeforeThisLine += segs.Count;
        }
        int totalVisualRow = visualRowsBeforeThisLine + segIdx;

        int lnWidth = ShowLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;
        int x = lnWidth + xInSeg;
        int y = totalVisualRow * _metrics.LineHeightPx;

        // 下端超過(クライアント高さ以上)なら不可視位置へ退避
        if (y >= ClientSize.Height)
        {
            NativeMethods.SetCaretPos(-1000, -1000);
            return;
        }

        NativeMethods.SetCaretPos(x, y);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_buffer is null) return;
        const int scrollLines = 3;  // 1 tick = 3 論理行(WinForms 既定を採らず固定で開始・P3 で調整余地)
        int delta = -Math.Sign(e.Delta) * scrollLines;  // Delta>0=上方向 → TopLine 減
        TopLine = _topLine + delta;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_buffer is not null)
        {
            var snap = _buffer.Current;
            // Control.ClientSize は docked 子コントロールを引かないため、VScrollBar 幅を明示的に減算。
            // (ScrollableControl と違って Control は DisplayRectangle でも同じ挙動)
            int paintWidth = Math.Max(0, ClientSize.Width - _vscroll.Width);
            int paintHeight = ClientSize.Height;
            var rows = ViewportLayout.Build(snap, _topLine, paintHeight, WrapColumns, _metrics);
            int lnWidth = ShowLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;

            // 選択がある間は現在行強調 FillRect を抑止する(選択矩形と重ねると
            // ハイライトが二重になり視覚的に読みにくいため=EditorControl 層の責務)。
            bool hasSelection = _selStart != _selEnd;
            int currentLineLogical = (_highlightCurrentLine && !hasSelection)
                ? snap.GetLineIndexOfChar(_caret)
                : -1;
            SelectionRange? selection = hasSelection
                ? new SelectionRange(Math.Min(_selStart, _selEnd), Math.Max(_selStart, _selEnd))
                : null;

            var frame = FrameBuilder.Build(
                snap, rows, paintWidth, paintHeight,
                lnWidth,
                currentLineLogical,
                selection,
                _cellHighlight, ShowWhitespace, _style, _metrics);
            RenderFrame(g, frame);
        }
        // 本コントロールの描画を確定させた後に Paint イベント購読者に描かせる
        // (App 層の overlay 拡張余地を残す)。base.OnPaint は Paint イベントを発火する。
        base.OnPaint(e);
    }

    private void RenderFrame(Graphics g, Frame frame)
    {
        foreach (var op in frame.Ops)
        {
            switch (op.Kind)
            {
                case PaintOpKind.FillRect:
                    using (var b = new SolidBrush(ToColor(op.Back)))
                        g.FillRectangle(b, op.X, op.Y, op.Width, op.Height);
                    break;
                case PaintOpKind.DrawText:
                    TextRenderer.DrawText(
                        g, op.Text ?? string.Empty, _font,
                        new Rectangle(op.X, op.Y, op.Width, op.Height),
                        ToColor(op.Fore),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
                    break;
                case PaintOpKind.DrawLine:
                    using (var p = new Pen(ToColor(op.Fore)))
                        g.DrawLine(p, op.X, op.Y, op.X + op.Width, op.Y + op.Height);
                    break;
            }
        }
    }

    // Task 8 で本実装。現状はダミー: 桁数(下限 3) × '9' 幅 + 4px 余白
    private int MeasureLineNumberWidth(int lineCount)
    {
        int digits = Math.Max(3, lineCount.ToString().Length);
        return _metrics.MeasureRun(new string('9', digits)) + 4;
    }

    private static Color ToColor(PaintColor c)
        => Color.FromArgb(c.Alpha, (c.Rgb >> 16) & 0xFF, (c.Rgb >> 8) & 0xFF, c.Rgb & 0xFF);

    private static ViewportStyle DefaultStyle() => new(
        Foreground:       new PaintColor(0x000000),
        Background:       new PaintColor(0xFFFFFF),
        CurrentLineBack:  new PaintColor(0xF0F0F0),
        SelectionBack:    new PaintColor(0xADD8E6),
        LineNumberFore:   new PaintColor(0x777777),
        HighlightOutline: new PaintColor(0xD77800),
        WhitespaceGlyph:  new PaintColor(0xCCCCCC));

    /// <summary>
    /// GDI ハンドル(Font)を解放する。P6 でタブ毎にインスタンス生成/破棄する運用のため、
    /// 生存中に確保した Font が Control 破棄時に必ず解放されるようにする。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
