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
    private TextBuffer? _buffer;

    // 内部状態(Task 9/11 で本実装・現状は既定値のまま OnPaint に渡す)
#pragma warning disable CS0649 // Task 9/11 で書き込み側を実装する
    private int _caret;
    private int _selStart;
    private int _selEnd;
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
    }

    /// <summary>ソースの <see cref="TextBuffer"/> を差し込む(1 度だけ)。</summary>
    public void SetSource(TextBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_buffer is not null)
            throw new InvalidOperationException("SetSource は 1 度だけ");
        _buffer = buffer;
        Invalidate();
    }

    /// <summary>行の高さ(px)。<see cref="ICharMetrics.LineHeightPx"/> の透過。</summary>
    public int LineHeightPx => _metrics.LineHeightPx;

    // 後続タスク受け口(バッキングは auto-property・本実装は該当タスクで)
    // [Browsable(false)] + [DesignerSerializationVisibility(Hidden)] は
    // Control 派生の public プロパティに対する WFO1000 を回避する意図(デザイナ非対応の宣言)。
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TopLine { get; set; }               // Task 7 で本実装
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int WrapColumns { get; set; }           // Task 12 で本実装
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLineNumbers { get; set; }      // Task 8 で本実装
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowWhitespace { get; set; }       // Task 11 で本実装
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HighlightCurrentLine { get; set; } // Task 9 で本実装

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_buffer is not null)
        {
            var snap = _buffer.Current;
            var rows = ViewportLayout.Build(snap, TopLine, ClientSize.Height, WrapColumns, _metrics);
            int lnWidth = ShowLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;
            var frame = FrameBuilder.Build(
                snap, rows, ClientSize.Width, ClientSize.Height,
                lnWidth,
                HighlightCurrentLine ? snap.GetLineIndexOfChar(_caret) : -1,
                _selStart != _selEnd
                    ? new SelectionRange(Math.Min(_selStart, _selEnd), Math.Max(_selStart, _selEnd))
                    : null,
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
