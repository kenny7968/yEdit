// EditorControl.Paint.cs
// Phase 2 (Task 2e) で切り出した描画分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3 では Controller 移譲予定なし(render 専任・§C.5 に基づき state は Ime 側が保持)。
// DrawImeOverlay も IME 状態 (_ime) を読むだけの純描画関数のため Paint 側に置く (§C.5)。
using yEdit.Core.Editing;
using yEdit.Core.Layout;
using yEdit.Core.Settings;
using SelectionRange = yEdit.Core.Layout.SelectionRange;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // OnPaint + RenderFrame + IME overlay 描画 + style/color helpers

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_buffer is not null)
        {
            var snap = _buffer.Current;
            // Control.ClientSize は docked 子コントロールを引かないため、VScrollBar 幅・
            // HScrollBar 高さを明示的に減算。(ScrollableControl と違って Control は
            // DisplayRectangle でも同じ挙動)
            int paintWidth = Math.Max(0, ClientSize.Width - _vscroll.Width);
            int paintHeight = Math.Max(0, ClientSize.Height - (_hscroll.Visible ? _hscroll.Height : 0));
            var rows = ViewportLayout.Build(snap, _topLine, paintHeight, _wrapColumns, _metrics);
            int lnWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;

            // 選択がある間は現在行強調 FillRect を抑止する(選択矩形と重ねると
            // ハイライトが二重になり視覚的に読みにくいため=EditorControl 層の責務)。
            bool hasSelection = _anchor != _caret;
            int currentLineLogical = (_highlightCurrentLine && !hasSelection)
                ? snap.GetLineIndexOfChar(_caret)
                : -1;
            SelectionRange? selection = null;
            if (hasSelection)
            {
                var (selS, selE) = GetSelectionCharRange();
                selection = new SelectionRange(selS, selE);
            }

            var frame = FrameBuilder.Build(
                snap, rows, paintWidth, paintHeight,
                lnWidth,
                currentLineLogical,
                selection,
                _cellHighlight, ShowWhitespace, _style, _metrics);
            RenderFrame(g, frame);

            // P4 Task 9: 未確定文字列 overlay(本文 → cellHighlight → キャレット行強調 →
            // ここ → システムキャレット の順序=設計 §3-3)。IsComposing=false(未確定期間外)は
            // 呼ばない=空描画のコストゼロ。節ハイライト(反転)は Task 10・
            // IME 内キャレット位置反映は Task 11 で扱う。
            if (IsComposing) DrawImeOverlay(g);

            // P5 Task 10: 描画完了時点の Frame を UIA 座標 API 用に公開(不変参照)。
            _lastFrame = frame;
            // client→screen オフセットも念のため refresh(スクロールでウィンドウ位置が
            // 動かなくても、DPI 変化・親コントロール移動などで値が変わり得る)。
            var origin = PointToScreen(new System.Drawing.Point(0, 0));
            _clientToScreenX = origin.X;
            _clientToScreenY = origin.Y;
        }
        // 本コントロールの描画を確定させた後に Paint イベント購読者に描かせる
        // (App 層の overlay 拡張余地を残す)。base.OnPaint は Paint イベントを発火する。
        base.OnPaint(e);
    }

    /// <summary>
    /// IME 未確定文字列を本文の上に inline 合成する(P4 Task 9・Task 10 で節ごと描画に拡張)。
    /// 節(<c>_ime.Clauses[i]..[i+1]</c>)ごとに Attrs を見て、変換対象節
    /// (<see cref="ImeAttribute.TargetConverted"/>)は背景反転(<see cref="ViewportStyle.SelectionBack"/>)
    /// + Underline|Bold で強調、それ以外は Underline のみで通常前景色で描く(設計 §3-3)。
    /// Clauses が空 or 節境界が 2 未満(=1 節扱い)なら全体を通常下線で 1 度描く(Task 9 と同挙動)。
    /// 描画位置は <c>_ime.Start</c> のクライアント座標(<see cref="ComputeCaretPoint"/> は
    /// <c>_scrollX</c> 適用前を返すため、ここで差し引いてから <see cref="TextRenderer.DrawText"/>
    /// に渡す=<see cref="PositionCaret"/>/<see cref="PointFromCharOffset"/> と同じ規約)。
    /// 折り返しなし(右端を越えても 1 行に描画=Scintilla 同挙動)。可視外は no-op。
    /// </summary>
    /// <remarks>
    /// <see cref="TextRenderer"/> を使う理由: 本文描画(<see cref="RenderFrame"/>)と同じ GDI 経路で
    /// 描き、ClearType/背景合成のずれを避ける(<see cref="Graphics.DrawString"/> は GDI+ 経路で
    /// 微妙にレンダリングが異なる)。<see cref="TextFormatFlags.NoPadding"/> と
    /// <see cref="TextFormatFlags.NoPrefix"/> で本文の <see cref="RenderFrame"/> と同じ寸法規約に合わせる。
    ///
    /// Attrs の長さ不整合防御: 節先頭の <c>_ime.Attrs[s]</c> を代表 Attr として採用するが、
    /// Attrs が Text より短い場合は <see cref="ImeAttribute.Input"/> で埋める(通常下線扱い)。
    /// これは Task 2 レビュー M-5 の防御方針を踏襲したもの。
    /// </remarks>
    private void DrawImeOverlay(Graphics g)
    {
        if (_buffer is null || _ime.Text.Length == 0) return;
        var (x, y, visible) = ComputeCaretPoint(_ime.Start);
        if (!visible) return;   // 可視範囲外は no-op

        int curX = x - _scrollX;   // 水平スクロール反映(Task 9 と同方針)

        // Clauses が空 or 節境界が 2 未満なら 1 節扱い(全体を通常下線)=Task 9 と同挙動
        if (_ime.Clauses.Length < 2)
        {
            TextRenderer.DrawText(g, _ime.Text, _underlineFontCache, new Point(curX, y), ForeColor,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return;
        }

        for (int i = 0; i < _ime.Clauses.Length - 1; i++)
        {
            int s = _ime.Clauses[i], e = _ime.Clauses[i + 1];
            if (s < 0) continue;                                  // 悪意/誤動作 IME の負値防御(Task 10 レビュー M-2)
            if (e > _ime.Text.Length) e = _ime.Text.Length;
            if (s >= e) continue;
            string clause = _ime.Text[s..e];

            // 節先頭の Attr を代表値として採用(Attrs 長不整合防御=Task 2 レビュー M-5)
            byte attr = s < _ime.Attrs.Length ? _ime.Attrs[s] : ImeAttribute.Input;
            bool isTarget = attr == ImeAttribute.TargetConverted;

            // 描画フォントで測って背景 rect 幅と curX 進み幅を一致させる(Task 10 レビュー I-1)。
            // Bold のグリフ幅は Underline のみより広くなり得るため、target 節は _targetFontCache で測る。
            Font drawFont = isTarget ? _targetFontCache : _underlineFontCache;
            Size sz = TextRenderer.MeasureText(g, clause, drawFont, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            if (isTarget)
            {
                using var brush = new SolidBrush(ToColor(_style.SelectionBack));
                g.FillRectangle(brush, curX, y, sz.Width, _metrics.LineHeightPx);
                TextRenderer.DrawText(g, clause, drawFont, new Point(curX, y), ForeColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            else
            {
                TextRenderer.DrawText(g, clause, drawFont, new Point(curX, y), ForeColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            curX += sz.Width;
        }
    }

    /// <summary>
    /// Frame の Ops を GDI 呼び出しに変換する。折り返し OFF 時の水平スクロール(<see cref="_scrollX"/>)は
    /// <b>全 op の X から一様に差し引く</b>形で反映する(_wrapColumns&gt;0 時は _scrollX=0 で実質シフトなし)。
    /// 先頭 op(背景全域 FillRect)も一緒にシフトされるが、OnPaint 冒頭で
    /// <c>g.Clear(BackColor)</c> が全 client 領域を BackColor で塗っており、DefaultStyle.Background
    /// と BackColor が一致している(共に White)ため、シフトで生じる右側の隙間は視覚的にクリアの
    /// BackColor と同色になり結果は同じ。行番号マージンも一緒にシフトされる(仕様=YAGNI)。
    /// </summary>
    private void RenderFrame(Graphics g, Frame frame)
    {
        foreach (var op in frame.Ops)
        {
            int x = op.X - _scrollX;   // 一様シフト
            switch (op.Kind)
            {
                case PaintOpKind.FillRect:
                    using (var b = new SolidBrush(ToColor(op.Back)))
                        g.FillRectangle(b, x, op.Y, op.Width, op.Height);
                    break;
                case PaintOpKind.DrawText:
                    TextRenderer.DrawText(
                        g, op.Text ?? string.Empty, _font,
                        new Rectangle(x, op.Y, op.Width, op.Height),
                        ToColor(op.Fore),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
                    break;
                case PaintOpKind.DrawLine:
                    using (var p = new Pen(ToColor(op.Fore)))
                        g.DrawLine(p, x, op.Y, x + op.Width, op.Y + op.Height);
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
    /// <see cref="AppearanceTheme"/> の Fore/Back RGB から派生色を算出して <see cref="ViewportStyle"/> を組む。
    /// App 層 <c>EditorAppearance</c> の <c>Blend</c> の手法(Back と Fore の線形補間)を流用しつつ、
    /// 各成分の混色比は以下の内訳:
    /// - CurrentLineBack (ratio=0.12): App 層と厳密一致(移植)
    /// - LineNumberFore (ratio=0.5) / WhitespaceGlyph (ratio=0.3): 自作コントロール独自の派生
    ///   (App 層は Scintilla の既定色を使うため直接の対応値なし)
    /// 強調 OFF 時の CurrentLineBack は Alpha=0 で「未使用」を明示。
    /// 選択背景と枠色は現行 App 層と同じ固定値(P6 でテーマ拡張が入るなら再検討=Task 15 の申し送り参照)。
    /// </summary>
    private static ViewportStyle BuildStyle(AppearanceTheme theme, bool highlightCurrentLine)
    {
        var currentLineBack = highlightCurrentLine
            ? new PaintColor(BlendRgb(theme.BackRgb, theme.ForeRgb, 0.12))
            : new PaintColor(0, 0);
        return new ViewportStyle(
            Foreground:       new PaintColor(theme.ForeRgb),
            Background:       new PaintColor(theme.BackRgb),
            CurrentLineBack:  currentLineBack,
            SelectionBack:    new PaintColor(0xADD8E6),
            LineNumberFore:   new PaintColor(BlendRgb(theme.BackRgb, theme.ForeRgb, 0.5)),
            HighlightOutline: new PaintColor(0xD77800),
            WhitespaceGlyph:  new PaintColor(BlendRgb(theme.BackRgb, theme.ForeRgb, 0.3)));
    }

    /// <summary>
    /// 0xRRGGBB 形式の 2 色を各チャネルで線形補間する(ratio=0 で baseRgb・1 で accentRgb)。
    /// App 層 <c>EditorAppearance.Blend</c> のロジック移植(あちらは Color 型・こちらは int 型)。
    /// </summary>
    private static int BlendRgb(int baseRgb, int accentRgb, double ratio)
    {
        int r = BlendChannel((baseRgb >> 16) & 0xFF, (accentRgb >> 16) & 0xFF, ratio);
        int g = BlendChannel((baseRgb >> 8) & 0xFF, (accentRgb >> 8) & 0xFF, ratio);
        int b = BlendChannel(baseRgb & 0xFF, accentRgb & 0xFF, ratio);
        return (r << 16) | (g << 8) | b;
        static int BlendChannel(int a, int c, double r) => (int)Math.Round(a + (c - a) * r);
    }

    /// <summary>0xRRGGBB の int から Alpha=255 の <see cref="Color"/> を組む(BackColor 設定用)。</summary>
    private static Color FromRgb(int rgb)
        => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
