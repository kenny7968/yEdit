using System.ComponentModel;
using yEdit.Core.Buffers;
using yEdit.Core.Editing;
using yEdit.Core.Layout;
using yEdit.Core.Settings;
using yEdit.Core.Text;
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
    // Task 13 で ApplyAppearance によりフォント差し替え/GdiCharMetrics 再構築/ViewportStyle 差し替えを
    // 行うため readonly を外した(Font 差し替え時は明示的に古い Font.Dispose を呼ぶ責務)。
    private Font _font;
    private ICharMetrics _metrics;
    private ViewportStyle _style;
    private readonly VScrollBar _vscroll;
    private readonly HScrollBar _hscroll;
    private TextBuffer? _buffer;
    private int _topLine;
    private int _wrapColumns;
    private int _scrollX;
    private bool _showLineNumbers;
    private bool _highlightCurrentLine;
    private bool _showWhitespace;

    // キャレット/選択の内部状態(P3 Task 2 でアンカー概念を導入=_selStart/_selEnd から _anchor に置換)。
    // 選択範囲は [Math.Min(_anchor, _caret), Math.Max(_anchor, _caret)]。
    // - _anchor == _caret: 選択なし(単純キャレット位置)
    // - _anchor <  _caret: 右方向に伸びた選択(キャレットが末尾)
    // - _anchor >  _caret: 左方向に伸びた選択(キャレットが先頭・shift+←/Home で作られる)
    // SetCaretCharOffset は選択解除仕様=_anchor = _caret = snapped に潰す。
    // MoveCaretWithSelection はアンカー保持でキャレットのみ動かす共通経路(shift+移動系)。
    private int _caret;
    private int _anchor;

    // Task 10: システムキャレットのフォーカス状態フラグ。CreateCaret/DestroyCaret はフォーカスを
    // 持つ間のみ有効なため、SetCaretCharOffset 等から PositionCaret を呼ぶ際にガードに使う。
    private bool _hasFocus;

    // P3 Task 6: 上下移動(Up/Down/PageUp/PageDown)で保持する desired X(px)。
    // -1 = 未計算=次回の垂直移動時に現在キャレット位置から新規計算する(慣例値)。
    // Left/Right/Home/End など水平方向の移動が起きたらリセット=次の垂直移動で再計算される。
    // Task 8 以降の編集経路(挿入/削除等)でも同様に -1 リセットする(§0-6 の一貫性)。
    private int _desiredXpx = -1;

    // Task 15: システムキャレットの太さ(px)。既定 2・ApplyAppearance で AppSettings.CaretWidth
    // (1〜5)を反映。弱視のキャレット視認性要件(設計原則 yedit-sighted-users-first-class)。
    private int _caretWidthPx = 2;

    // セルハイライト状態(HighlightCharRange で設定・ClearHighlight で null)。
    // テキスト選択(_anchor/_caret)とは独立した装飾で、単一アクティブ。
    private SelectionRange? _cellHighlight;

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
        //
        // Dock 順の注意: WinForms の DefaultLayout は Controls コレクションを逆順で docking する。
        // 「後に Add した子ほど先に dock 処理される=フルエッジを取る」ため、HScrollBar を先に、
        // VScrollBar を後に Add することで:
        //   - VScrollBar が右端全高(Explorer と同じ慣習)
        //   - HScrollBar が下端の残り幅(VScroll の左まで)
        // となる。ここを逆順にすると HScrollBar が下端全幅を取ってしまい、右下の角に
        // VScroll が張り付かない見た目になる。
        _hscroll = new HScrollBar { Dock = DockStyle.Bottom, SmallChange = 10, Visible = false };
        _hscroll.Scroll += (_, e) => ScrollX = e.NewValue;
        Controls.Add(_hscroll);

        _vscroll = new VScrollBar { Dock = DockStyle.Right, SmallChange = 1, Enabled = false };
        _vscroll.Scroll += (_, e) => TopLine = e.NewValue;
        Controls.Add(_vscroll);
    }

    /// <summary>ソースの <see cref="TextBuffer"/> を差し込む(1 度だけ)。</summary>
    /// <remarks>
    /// SetSource 前にフォーカスを得ていた場合(OnGotFocus は buffer null で早期 return するため
    /// キャレット未生成)は、SetSource 末尾でシステムキャレットを生成する。P6 のタブ切替で
    /// 「Controls.Add → 自動 Focus → 遅延 SetSource」の順で組み立てるパターンでもキャレットが
    /// 確実に立つ(Task 15 レビュー I-2)。
    /// </remarks>
    public void SetSource(TextBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_buffer is not null)
            throw new InvalidOperationException("SetSource は 1 度だけ");
        _buffer = buffer;
        _topLine = 0;
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
        if (_hasFocus)
        {
            NativeMethods.CreateCaret(Handle, nint.Zero, _caretWidthPx, _metrics.LineHeightPx);
            PositionCaret();
            NativeMethods.ShowCaret(Handle);
        }
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
    /// <summary>
    /// 折り返し桁数(半角換算)。0 以下で折り返し OFF。負値は 0 に丸める。
    /// ON: 水平スクロールバー非表示・視覚行を <c>WrapColumns × 半角1文字幅</c> で折り返す。
    /// OFF: 水平スクロールバー表示(必要な場合)+ <see cref="ScrollX"/> で表示原点を左右シフト。
    /// 変化時: <see cref="ScrollX"/> を 0 にリセット・HScrollBar 表示切替・キャレット再配置・Invalidate。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int WrapColumns
    {
        get => _wrapColumns;
        set
        {
            int clamped = Math.Max(0, value);
            if (_wrapColumns == clamped) return;
            _wrapColumns = clamped;
            _scrollX = 0;
            UpdateHorizontalScrollbar();
            PositionCaret();
            Invalidate();
        }
    }

    /// <summary>
    /// 水平スクロール位置(px)。<b>折り返し OFF かつ HScrollBar 表示中のみ有効</b>
    /// (ON 時 / 内容が可視領域に収まり HScroll 非表示の間は 0 固定・set は no-op)。
    /// [0, MaxScrollX] にクランプ(MaxScrollX は HScrollBar.Maximum - LargeChange + 1 相当)。
    /// 変化時のみ HScrollBar.Value を追従・キャレット再配置・Invalidate。
    /// </summary>
    /// <remarks>
    /// HScrollBar 非表示時ガードが無いと、直前まで表示されていたときの
    /// <c>_hscroll.Maximum</c> が残存しており ClampScrollX が非ゼロ値を通してしまう
    /// (=本来スクロール不要なのに描画が左シフトする)。<see cref="UpdateHorizontalScrollbar"/>
    /// の hide 分岐で Maximum/LargeChange をリセットすることでも二重に防いでいる。
    /// </remarks>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ScrollX
    {
        get => _scrollX;
        set
        {
            if (_wrapColumns > 0) return;   // 折り返し ON では水平スクロール無効
            if (!_hscroll.Visible) return;  // HScroll 非表示時は水平スクロール意味なし
            int clamped = ClampScrollX(value);
            if (clamped == _scrollX) return;
            _scrollX = clamped;
            if (_hscroll.Value != clamped) _hscroll.Value = clamped;
            PositionCaret();
            Invalidate();
        }
    }
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
            // 行番号マージン幅は本文 X の起点(bodyX)を変えるためシステムキャレット位置と
            // 水平スクロールの content 幅にも効く。TopLine/WrapColumns setter と同じく
            // Update → PositionCaret → Invalidate の順で反映する(Task 15 レビュー I-1)。
            UpdateHorizontalScrollbar();
            PositionCaret();
            Invalidate();
        }
    }
    /// <summary>
    /// 空白/タブ/EOL の可視化グリフ(中点/矢印)を <see cref="ViewportStyle.WhitespaceGlyph"/> 色で
    /// 本文の上から重ね塗りするか。FrameBuilder は本文とは別 op として個別 DrawText を発行し、
    /// GdiCharMetrics が同 Font を使うため座標のズレなく重なる。変化時のみ Invalidate。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowWhitespace
    {
        get => _showWhitespace;
        set
        {
            if (_showWhitespace == value) return;
            _showWhitespace = value;
            Invalidate();
        }
    }

    /// <summary>
    /// 文字オフセット範囲(UTF-16)を「セル」枠 + 半透明背景で強調する(P0 の Scintilla セル装飾を継承)。
    /// テキスト選択とは独立した装飾で、単一アクティブ(次の <see cref="HighlightCharRange"/> で
    /// 置き換え・<see cref="ClearHighlight"/> で消える)。両端は [0, CharLength] にクランプ・
    /// UTF-16 low サロゲート中間位置は前方(high)にスナップ。<paramref name="length"/> が負値のときは
    /// 0 として扱う(空範囲=装飾なし相当)。SetSource 前の呼び出しは no-op。
    /// </summary>
    /// <param name="start">開始 UTF-16 文字オフセット。</param>
    /// <param name="length">長さ(UTF-16 コード単位)。負値は 0 として扱う。</param>
    public void HighlightCharRange(int start, int length)
    {
        if (_buffer is null) return;
        int s = SnapAndClamp(start);
        // start + length は int 加算だとオーバーフローで負値になり s > e = SelectionRange 例外の
        // 経路が残る(実運用の CharLength は int.MaxValue 未満だが公開 API の契約防御として長型経由)。
        long endLong = (long)start + Math.Max(0, length);
        int endInt = endLong > int.MaxValue ? int.MaxValue : (int)endLong;
        int e = SnapAndClamp(endInt);
        // SnapAndClamp は単純クランプ + サロゲート前方スナップで、単調非減少
        // (証明スケッチ: snap(x) ∈ {x, x-1, 0, CharLength} かつ snap(x) <= x。a <= b の両側で成立)。
        // Math.Max(0, length) と上記オーバーフロー処理で e >= s が数学的に保証される
        // (SelectionRange invariant Start <= End にも合致)。
        var range = new SelectionRange(s, e);
        if (_cellHighlight == range) return;
        _cellHighlight = range;
        Invalidate();
    }

    /// <summary>
    /// セルハイライトを消す。現状 null のときは no-op。
    /// </summary>
    public void ClearHighlight()
    {
        if (_cellHighlight is null) return;
        _cellHighlight = null;
        Invalidate();
    }

    /// <summary>
    /// キャレット論理行の背景を <see cref="ViewportStyle.CurrentLineBack"/> で塗るか。
    /// <b>選択がある間(_anchor != _caret)は塗らない</b>=OnPaint で FrameBuilder への
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

    /// <summary>
    /// 上書き入力モード(Overtype)。true のとき文字挿入は直後 1 文字を潰す=Insert キー(修飾なし)で
    /// OnKeyDown が直接トグルする(Task 9)。改行(<c>\r</c>/<c>\n</c>)の直前では潰さず単純挿入
    /// (Scintilla 互換)。サロゲートペアの high 位置にキャレットがあるときは pair 全体(2 code units)を潰す。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Overtype { get; set; }

    /// <summary>
    /// 読み取り専用モード。true のとき編集経路(OnKeyPress・Task 9〜11 の削除/貼り付け系)を
    /// 全て早期 return する。選択状態やキャレット移動は禁止しない(閲覧用途の想定)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Enter 押下時に挿入する改行シーケンス。既定は <see cref="LineEnding.Crlf"/>(Windows 標準)。
    /// App 層 (P6) は開いた文書の実測改行(LineEndingDetector)を反映して設定する想定。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public LineEnding EolMode { get; set; } = LineEnding.Crlf;

    /// <summary>
    /// SavePoint(<see cref="SetSavePoint"/>)以後にバッファが変更されたか。SetSource 前 / 現ルート ==
    /// 保存時ルート の間は false。P6 の <c>ScintillaHost.Modified</c> と同名(移植先での機械的置換用)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Modified => _buffer?.Modified ?? false;

    /// <summary>
    /// Undo 可否(履歴あり)。SetSource 前は false。P6 の <c>ScintillaHost.CanUndo</c> と同名。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanUndo => _buffer?.CanUndo ?? false;

    /// <summary>
    /// Redo 可否(Undo 後で新規編集がまだ無い)。SetSource 前は false。P6 の <c>ScintillaHost.CanRedo</c> と同名。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanRedo => _buffer?.CanRedo ?? false;

    /// <summary>キャレット位置(UTF-16 文字オフセット)。書き込みは <see cref="SetCaretCharOffset"/>。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CaretCharOffset => _caret;

    /// <summary>
    /// 選択アンカー(UTF-16 文字オフセット)。<c>_anchor == _caret</c> のときは選択なし。
    /// 書き込みは <see cref="SetSelectionAnchored(int, int)"/> または <see cref="SetSelectionCharRange(int, int)"/>。
    /// P3 Task 2 で導入(shift+左方向の選択保持=キャレット &lt; アンカーのケース)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectionAnchor => _anchor;

    /// <summary>
    /// キャレット位置を UTF-16 文字オフセットで設定する(選択はクリアされる=_anchor=_caret=snapped)。
    /// サロゲートペア中間位置(low)は前方(high)にスナップ。範囲外は [0, CharLength] にクランプ。
    /// SetSource 前の呼び出しは no-op(_buffer が null のため)。
    /// </summary>
    public void SetCaretCharOffset(int offset)
    {
        if (_buffer is null) return;
        int snapped = SnapAndClamp(offset);
        if (_caret == snapped && _anchor == snapped) return;
        _caret = snapped;
        _anchor = snapped;   // 単純キャレット移動は選択解除
        PositionCaret();
        Invalidate();
    }

    /// <summary>現在の選択範囲(UTF-16 文字オフセット・Start &lt;= End で返す)。</summary>
    /// <remarks>
    /// 内部状態(<c>_anchor</c>/<c>_caret</c>)は非対称=どちらが Min/Max かは選択方向で変わる。
    /// 呼び出し側が「アンカーはどこか」を知りたい場合は <see cref="SelectionAnchor"/> を使う。
    /// </remarks>
    public (int Start, int End) GetSelectionCharRange()
        => (Math.Min(_anchor, _caret), Math.Max(_anchor, _caret));

    /// <summary>
    /// 選択範囲を設定する(対称版=方向を持たない)。<paramref name="start"/> &gt; <paramref name="end"/>
    /// の場合は内部で正規化。両端はサロゲートペア中間位置なら前方スナップ・範囲外はクランプ。
    /// 内部では <c>_anchor = Min(start, end)</c>・<c>_caret = Max(start, end)</c> にマップする
    /// (=キャレットは選択末尾=右方向の選択)。SetSource 前の呼び出しは no-op。
    /// </summary>
    /// <remarks>
    /// 非対称版(キャレット位置を明示指定=shift+左方向の選択)は <see cref="SetSelectionAnchored(int, int)"/>
    /// を使う。既存呼び出し側の挙動を変えないためこの API はキャレット末尾固定のまま維持する。
    /// </remarks>
    public void SetSelectionCharRange(int start, int end)
    {
        if (_buffer is null) return;
        int s = SnapAndClamp(Math.Min(start, end));
        int e = SnapAndClamp(Math.Max(start, end));
        if (_anchor == s && _caret == e) return;
        _anchor = s;
        _caret = e;
        PositionCaret();
        Invalidate();
    }

    /// <summary>
    /// アンカー保持でキャレットのみを <paramref name="newCaret"/> に移動する(shift+移動系の共通経路)。
    /// サロゲートペア中間位置は前方スナップ・範囲外はクランプ。
    /// <c>newCaret == _anchor</c> のとき選択が消える(=アンカーと同位置)。
    /// SetSource 前の呼び出しは no-op。
    /// </summary>
    public void MoveCaretWithSelection(int newCaret)
    {
        if (_buffer is null) return;
        int snapped = SnapAndClamp(newCaret);
        if (_caret == snapped) return;
        _caret = snapped;
        // _anchor は保持
        PositionCaret();
        Invalidate();
    }

    /// <summary>
    /// アンカーとキャレットを個別指定して選択範囲を設定する(非対称版)。
    /// <paramref name="anchor"/> &gt; <paramref name="caret"/> のときはキャレットが Min=選択先頭
    /// (=shift+左方向の選択)。両端はサロゲートペア中間位置なら前方スナップ・範囲外はクランプ。
    /// SetSource 前の呼び出しは no-op。
    /// </summary>
    public void SetSelectionAnchored(int anchor, int caret)
    {
        if (_buffer is null) return;
        int a = SnapAndClamp(anchor);
        int c = SnapAndClamp(caret);
        if (_anchor == a && _caret == c) return;
        _anchor = a;
        _caret = c;
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

    /// <summary>
    /// HScrollBar の表示可否・Maximum / LargeChange を現在の buffer と ClientSize から再計算する。
    /// - 折り返し ON / 未 SetSource / 内容が可視領域に収まる → 非表示・_scrollX=0・
    ///   Maximum/LargeChange を初期値へリセット(残存値でクランプが緩まないように)
    /// - 折り返し OFF で内容がはみ出す → 表示。可視分の視覚行のうち最長 pixel 幅を上限にする
    ///   (1GB でも計算量は O(可視行数))。
    /// 順序: Maximum → LargeChange → SmallChange → Value(<see cref="UpdateVerticalScrollbar"/> と統一。
    /// 逆順だと Maximum が小さいときに LargeChange が内部で clip されるケースがある)。
    /// </summary>
    private void UpdateHorizontalScrollbar()
    {
        if (_buffer is null || _wrapColumns > 0) { HideAndResetHScroll(); return; }
        var snap = _buffer.Current;
        int paintWidth = Math.Max(0, ClientSize.Width - _vscroll.Width);
        // HScroll 表示可否を決めるための計算では、まだ表示していない前提で高さいっぱいを見る
        // (可視行がわずかに多めになるだけで最長幅の推定には害がない)。
        int probeHeight = Math.Max(0, ClientSize.Height);
        var rows = ViewportLayout.Build(snap, _topLine, probeHeight, wrapColumns: 0, _metrics);
        int lnWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;
        int maxLineWidthPx = 0;
        foreach (var row in rows)
        {
            if (row.SegmentLength == 0) continue;
            string lineText = snap.GetText(row.SegmentStartChar, row.SegmentLength);
            int width = _metrics.MeasureRun(lineText.AsSpan());
            if (width > maxLineWidthPx) maxLineWidthPx = width;
        }
        int contentWidth = lnWidth + maxLineWidthPx;
        if (contentWidth <= paintWidth) { HideAndResetHScroll(); return; }

        // 表示に必要
        int largeChange = Math.Max(1, paintWidth);
        // WinForms 慣習に合わせ Maximum → LargeChange の順で設定
        // (逆順だと Maximum が小さいときに LargeChange が内部で clip されるケースがある)。
        _hscroll.Maximum = contentWidth - 1 + Math.Max(0, largeChange - 1);
        _hscroll.LargeChange = largeChange;
        _hscroll.SmallChange = Math.Max(1, _metrics.MeasureRun("0"));
        int maxScrollX = _hscroll.Maximum - Math.Max(0, largeChange - 1);
        if (_scrollX > maxScrollX) _scrollX = Math.Max(0, maxScrollX);
        if (_scrollX < 0) _scrollX = 0;
        _hscroll.Value = _scrollX;
        _hscroll.Visible = true;
    }

    /// <summary>
    /// HScrollBar を非表示にし、Maximum/LargeChange を初期値にリセットする。
    /// <see cref="ScrollX"/> は「HScroll 非表示中は set が no-op」で守られているが、
    /// 直前の表示状態で Maximum が非ゼロのまま残ると、内部から <see cref="ClampScrollX"/> 経由で
    /// 触れた場合に非ゼロ値を通してしまう(RenderFrame の一様シフトで内容が左にズレる)。
    /// リセットしておくことで防御を二重化する。
    /// </summary>
    private void HideAndResetHScroll()
    {
        _hscroll.Visible = false;
        // 縮小方向は WinForms の内部 clip が働いても LargeChange=1 に落ち着くだけなので順序不問。
        _hscroll.LargeChange = 1;
        _hscroll.Maximum = 0;
        _scrollX = 0;
        if (_hscroll.Value != 0) _hscroll.Value = 0;
    }

    private int ClampScrollX(int value)
    {
        int max = _hscroll.Maximum - Math.Max(0, _hscroll.LargeChange - 1);
        if (max < 0) max = 0;
        if (value < 0) return 0;
        if (value > max) return max;
        return value;
    }

    /// <summary>
    /// 編集(Insert/Delete/Replace)後の共通後処理: スクロールバー再計算+キャレット再配置+
    /// 追従スクロール+再描画。<c>_desiredXpx</c> は編集経路では常にリセット(-1)される想定なので
    /// 呼び出し側(OnKeyPress/Task 9〜11 の削除系)で個別に設定する。Task 9〜11 でも共用する
    /// (§0-6 の一貫性)。
    /// </summary>
    /// <remarks>
    /// 順序は「バッファ変化 → スクロールバー再計算(Update*) → キャレット再配置(PositionCaret)
    /// → 追従スクロール(BringCaretIntoView) → 再描画(Invalidate)」。
    /// - Update*Scrollbar が先: 挿入で総行数/最長行が変わっている可能性があるため、Position/追従の
    ///   前に Maximum/LargeChange を反映する必要がある。
    /// - PositionCaret は BringCaretIntoView の内部で必要な OS 側キャレット反映を先出しする
    ///   (BringCaretIntoView 自体は TopLine/ScrollX の setter を経由するときに PositionCaret を
    ///   間接的に呼ぶが、可視範囲内で TopLine/ScrollX が変わらない編集経路では呼ばれないため)。
    /// - BringCaretIntoView は挿入後キャレットが下端/右端を越えたら TopLine/ScrollX を追随させる。
    /// - Invalidate は最後(BringCaretIntoView 経由の setter が変化なしの場合でも本文が変わっている)。
    /// </remarks>
    private void AfterEdit()
    {
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
        PositionCaret();
        BringCaretIntoView();
        Invalidate();
    }

    /// <summary>
    /// Undo 実行(P3 Task 10)。<see cref="TextBuffer.Undo"/> の結果を反映し、キャレットを
    /// 推奨位置(=Pos + RemovedLen=削除内容が復元された末尾)へ移動する。選択は解除
    /// (Task 8/9 と同じ<c>_caret = _anchor = pos</c> パターン)。<c>_desiredXpx</c> は編集経路の
    /// 一貫性で -1 リセット。SetSource 前 / 履歴なし(<see cref="TextBuffer.Undo"/> が null)
    /// / <see cref="ReadOnly"/> は no-op。
    /// P6 の <c>ScintillaHost.Undo</c> と同名(<c>Undo</c> は <see cref="Control"/> の直接メンバではなく
    /// <c>TextBoxBase</c> で導入される名前=本クラスは Control 直接派生のため隠すべき同名メソッドが
    /// 無く <c>new</c> キーワード不要)。
    /// </summary>
    /// <remarks>
    /// ReadOnly ガードはメソッド本体側で行う(<see cref="OnKeyDown"/> の <c>Keys.Z</c> case にも
    /// <c>when !ReadOnly</c> を残しているが、これは二重防御=App 層 <c>MainForm</c> のメニュー
    /// shortcut は <see cref="OnKeyDown"/> を経由せず本メソッドを直接呼ぶため、本体側で弾く必要が
    /// ある)。Scintilla の <c>SCI_UNDO</c> が read-only モードで no-op になる挙動と合わせる=P6 で
    /// <c>ScintillaHost</c> を本コントロールへ機械的置換した際に CSV グリッドモード
    /// (<c>CsvController.Editor.ReadOnly = true</c>)などの ReadOnly 経路で挙動が退行しない。
    /// </remarks>
    public void Undo()
    {
        if (_buffer is null || ReadOnly) return;
        var r = _buffer.Undo();
        if (r is null) return;
        int pos = Math.Clamp(r.Value.CaretPos, 0, _buffer.Current.CharLength);
        _caret = _anchor = pos;
        _desiredXpx = -1;
        AfterEdit();
    }

    /// <summary>
    /// Redo 実行(P3 Task 10)。<see cref="TextBuffer.Redo"/> の結果を反映し、キャレットを
    /// 推奨位置(=Pos + InsertedLen=再挿入内容の末尾)へ移動する。それ以外の副作用は
    /// <see cref="Undo"/> と同じ(選択解除・desiredXpx リセット・AfterEdit で追従スクロール・
    /// SetSource 前 / <see cref="ReadOnly"/> は no-op)。
    /// </summary>
    public void Redo()
    {
        if (_buffer is null || ReadOnly) return;
        var r = _buffer.Redo();
        if (r is null) return;
        int pos = Math.Clamp(r.Value.CaretPos, 0, _buffer.Current.CharLength);
        _caret = _anchor = pos;
        _desiredXpx = -1;
        AfterEdit();
    }

    /// <summary>
    /// SavePoint を打つ(<see cref="TextBuffer.MarkSaved"/> の別名)。以後 <see cref="Modified"/> は
    /// 現ルートとの参照比較で判定される。SetSource 前は no-op。P6 の <c>ScintillaHost.SetSavePoint</c>
    /// と同名(App 層 Save 経路からの機械的置換用)。
    /// </summary>
    public void SetSavePoint() => _buffer?.MarkSaved();

    /// <summary>
    /// EmptyUndoBuffer 相当(<see cref="TextBuffer.ClearUndo"/> の別名)。Undo/Redo 履歴を破棄する。
    /// 保存点は維持されるため <see cref="Modified"/> の値は変わらない。SetSource 前は no-op。
    /// P6 の <c>ScintillaHost.EmptyUndoBuffer</c> と同名。
    /// </summary>
    public void EmptyUndoBuffer() => _buffer?.ClearUndo();

    /// <summary>診断用(テストで文書全体を取得)。SetSource 前は空文字列。</summary>
    internal string GetText() => _buffer?.Current.GetText(0, _buffer.Current.CharLength) ?? string.Empty;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
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
        // SetSource 前は buffer が無く PositionCaret が SetCaretPos を呼ばないため、
        // ShowCaret のみ走ると未定義位置(実装依存)にキャレットが出る。SetSource 前は
        // キャレットを生成しない(次に focus を得るときに再セットアップされる)。
        if (_buffer is null) return;
        NativeMethods.CreateCaret(Handle, nint.Zero, _caretWidthPx, _metrics.LineHeightPx);
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
    /// 与えられた UTF-16 char offset のクライアント座標(px)と可視性を算出する純ロジック。
    /// - Visible=false: TopLine 未到達 / paintHeight を超える論理行 / y &gt;= paintHeight
    /// - Visible=true: (X, Y) は「行番号マージン含む・_scrollX を引く前」の座標
    /// </summary>
    /// <remarks>
    /// 折り返し ON 時は TopLine ～ 対象行までの各論理行に対して <c>LineLayout.Wrap</c>
    /// を呼び直す(1 論理行ずつ GetText + Wrap)。Task 14 のベンチで顕在化するようなら
    /// Frame の再利用等で最適化する(Task 9 レビュー M-3 の申し送り)。
    /// Task 10 レビュー I-1 対応: 積み上げループ内で paintHeight 超えを検出したら早期退避する
    /// (100 万行のような巨大文書でキャレットが末尾方向にあるとき無駄な Wrap を避けるため)。
    /// </remarks>
    private (int X, int Y, bool Visible) ComputeCaretPoint(int offset)
    {
        if (_buffer is null) return (0, 0, false);
        var snap = _buffer.Current;
        int logicalLine = snap.GetLineIndexOfChar(offset);

        // TopLine 未到達なら不可視(スクロールで対象行が上にはみ出している)
        if (logicalLine < _topLine) return (0, 0, false);

        int lineStart = snap.GetLineStart(logicalLine);
        int lineEnd = snap.GetLineEnd(logicalLine, includeBreak: false);
        int lineLen = lineEnd - lineStart;
        string lineText = lineLen == 0 ? string.Empty : snap.GetText(lineStart, lineLen);
        int maxWidthPx = _wrapColumns > 0 ? _wrapColumns * _metrics.MeasureRun("0") : 0;
        var segments = LineLayout.Wrap(lineText, maxWidthPx, _metrics);

        int caretInLine = offset - lineStart;

        // 対象がどの視覚セグメントに属するかを決める。
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

        int lineHeight = _metrics.LineHeightPx;
        int paintHeight = Math.Max(0, ClientSize.Height - (_hscroll.Visible ? _hscroll.Height : 0));

        // TopLine の先頭視覚行を Y=0 として、対象視覚行までの積み上げ視覚行数を算出。
        // paintHeight を超えたら以降の Wrap は無駄なので早期退避(Task 10 I-1)。
        int visualRowsBeforeThisLine = 0;
        for (int line = _topLine; line < logicalLine; line++)
        {
            int lStart = snap.GetLineStart(line);
            int lEnd = snap.GetLineEnd(line, includeBreak: false);
            int lLen = lEnd - lStart;
            string lText = lLen == 0 ? string.Empty : snap.GetText(lStart, lLen);
            var segs = LineLayout.Wrap(lText, maxWidthPx, _metrics);
            visualRowsBeforeThisLine += segs.Count;
            if (visualRowsBeforeThisLine * lineHeight >= paintHeight)
                return (0, 0, false);
        }
        int totalVisualRow = visualRowsBeforeThisLine + segIdx;

        int lnWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;
        int x = lnWidth + xInSeg;
        int y = totalVisualRow * lineHeight;

        // 下端超過(paint 領域の高さ以上)なら不可視
        if (y >= paintHeight) return (0, 0, false);

        return (x, y, true);
    }

    /// <summary>
    /// <c>_caret</c>(UTF-16 char offset)からクライアント座標(px)を算出し、
    /// システムキャレット位置に反映する。可視外(TopLine 未到達 / 下端超過)は
    /// 見えない位置 (-1000, -1000) へ退避。フォーカス無し・buffer 未設定時は何もしない。
    /// 折り返し OFF 時は最終位置から <see cref="ScrollX"/> を引いてから SetCaretPos する。
    /// </summary>
    private void PositionCaret()
    {
        if (!_hasFocus || _buffer is null) return;
        var (x, y, visible) = ComputeCaretPoint(_caret);
        if (visible) NativeMethods.SetCaretPos(x - _scrollX, y);
        else NativeMethods.SetCaretPos(-1000, -1000);
    }

    /// <summary>
    /// UTF-16 文字オフセットのクライアント座標(px)を返す(P2 計画 §1 の公開 API)。
    /// SetSource 前 / 可視外(TopLine 未到達 / 下端超過)は <see cref="Point.Empty"/> を返す。
    /// 返す座標は <see cref="ScrollX"/> 反映後の実描画位置(折り返し OFF 時は -_scrollX された値)。
    /// サロゲート中間位置・範囲外は内部で <see cref="SnapAndClamp"/> により正規化する。
    /// </summary>
    public Point PointFromCharOffset(int offset)
    {
        if (_buffer is null) return Point.Empty;
        int snapped = SnapAndClamp(offset);
        var (x, y, visible) = ComputeCaretPoint(snapped);
        if (!visible) return Point.Empty;
        return new Point(x - _scrollX, y);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_buffer is null) return;
        const int scrollLines = 3;  // 1 tick = 3 論理行(WinForms 既定を採らず固定で開始・P3 で調整余地)
        int delta = -Math.Sign(e.Delta) * scrollLines;  // Delta>0=上方向 → TopLine 減
        TopLine = _topLine + delta;
    }

    /// <summary>
    /// 移動系キー(Arrow/Home/End/PageUp/PageDown)+ Tab を「入力キー」として扱わせ、
    /// フォームレベルのフォーカス遷移やダイアログの既定ボタン発火に持っていかれないようにする。
    /// Tab は Task 9 で OnKeyDown 側に <c>\t</c> 挿入 case を追加したため、ここに含めないと
    /// WinForms のフォーカス遷移(次コントロールへの Tab 移動)に持って行かれ、
    /// <c>OnKeyDown</c> まで届かなくなる(Task 6 申し送り S-2 決着)。
    /// </summary>
    protected override bool IsInputKey(Keys keyData)
    {
        Keys code = keyData & Keys.KeyCode;
        return code switch
        {
            Keys.Left or Keys.Right or Keys.Up or Keys.Down
                or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
                or Keys.Tab => true,
            _ => base.IsInputKey(keyData),
        };
    }

    /// <summary>
    /// 移動系キーバインド配線(P3 Task 6)。編集系(BackSpace/Delete/Enter/文字挿入/Cut/Copy/Paste/
    /// Undo/Redo/Tab)は Task 8〜11 で追加する=ここでは配線しない。
    /// </summary>
    /// <remarks>
    /// 実装方針(統一経路):
    /// - Shift 押下時は <see cref="MoveCaretWithSelection"/>=アンカー保持でキャレットのみ動かす。
    /// - Shift 無押下時は <see cref="SetCaretCharOffset"/>=アンカー = キャレット(選択解除)。
    /// - Ctrl+A のみ <see cref="SetSelectionAnchored(int, int)"/> を直接呼ぶ(anchor=0, caret=CharLength)。
    /// - Left/Right/Home/End は水平方向の移動なので desiredXpx をリセット(次の Up/Down で再計算)。
    /// - Up/Down/PageUp/PageDown は <c>VerticalNavigation</c> が返した desiredPx を必ず保存する
    ///   (新規計算経路=-1 → 有効値、有効値継続経路=同じ値)。
    /// - 純キャレット移動でも <see cref="TextBuffer.BreakUndoCoalescing"/> を呼び、
    ///   移動をまたいだ連続タイピングを分割する(Scintilla 版と同挙動)。
    /// - <c>BringCaretIntoView()</c> / <c>RaiseCaretEnteredEmptyLineIfNeeded()</c> は Task 7/13 で
    ///   本実装。Task 6 では**呼び出し箇所を確定させる**ためスタブ(no-op)で置く。
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_buffer is null) return;
        var snap = _buffer.Current;
        bool shift = (e.Modifiers & Keys.Shift) != 0;
        bool ctrl = (e.Modifiers & Keys.Control) != 0;

        int? target = null;
        bool resetDesired = false;

        switch (e.KeyCode)
        {
            case Keys.Left:
                target = ctrl ? WordBoundary.PrevWordStart(snap, _caret)
                              : NavigationCommands.MoveLeftChar(snap, _caret);
                resetDesired = true;
                break;
            case Keys.Right:
                target = ctrl ? WordBoundary.NextWordStart(snap, _caret)
                              : NavigationCommands.MoveRightChar(snap, _caret);
                resetDesired = true;
                break;
            case Keys.Home:
                target = ctrl ? 0 : NavigationCommands.MoveHomeSmart(snap, _caret);
                resetDesired = true;
                break;
            case Keys.End:
                target = ctrl ? snap.CharLength : NavigationCommands.MoveEnd(snap, _caret);
                resetDesired = true;
                break;
            case Keys.Up:
            {
                var (t, d) = VerticalNavigation.MoveUp(snap, _caret, _desiredXpx, _wrapColumns, _metrics);
                _desiredXpx = d;
                target = t;
                break;
            }
            case Keys.Down:
            {
                var (t, d) = VerticalNavigation.MoveDown(snap, _caret, _desiredXpx, _wrapColumns, _metrics);
                _desiredXpx = d;
                target = t;
                break;
            }
            case Keys.PageUp:
            {
                int rows = Math.Max(1, ClientSize.Height / Math.Max(1, _metrics.LineHeightPx));
                var (t, d) = VerticalNavigation.PageUp(snap, _caret, _desiredXpx, _wrapColumns, rows, _metrics);
                _desiredXpx = d;
                target = t;
                break;
            }
            case Keys.PageDown:
            {
                int rows = Math.Max(1, ClientSize.Height / Math.Max(1, _metrics.LineHeightPx));
                var (t, d) = VerticalNavigation.PageDown(snap, _caret, _desiredXpx, _wrapColumns, rows, _metrics);
                _desiredXpx = d;
                target = t;
                break;
            }
            case Keys.A when ctrl:
                SetSelectionAnchored(0, snap.CharLength);
                // 全選択=文書頭〜末尾のジャンプ相当なので、水平移動と同様に desired X をリセット。
                // これを忘れると次の Up/Down が「Ctrl+A 前の古い列」を目指す(Task 6 レビュー S-1)。
                _desiredXpx = -1;
                _buffer.BreakUndoCoalescing();
                e.Handled = true;
                return;

            // ===== P3 Task 10: Undo / Redo =====
            // ReadOnly 時は Undo/Redo も no-op(App 側で Save 直後に ReadOnly にした後、
            // 誤操作 Ctrl+Z で内容が変わるのを防ぐ)。Undo/Redo 本体はメソッドに集約=
            // App 層メニュー/ツールバー等からの直接呼び出しと Ctrl+Z/Y の両経路を統一する。
            case Keys.Z when ctrl && !ReadOnly:
                Undo();
                e.Handled = true;
                return;
            case Keys.Y when ctrl && !ReadOnly:
                Redo();
                e.Handled = true;
                return;

            // ===== P3 Task 9: 削除/改行/Tab/Insert =====
            // AfterEdit は「バッファ変化 → スクロールバー再計算 → キャレット再配置 →
            // 追従スクロール → Invalidate」の共通後処理。編集経路では _desiredXpx = -1 で
            // 垂直位置が変わり得ることを表現する(§0-6 一貫性)。
            // _caret / _anchor の直接代入は編集経路の「バッファ変化と一連の副作用を 1 度にまとめる」
            // ために許容(setter 経由だと二重 Invalidate/PositionCaret が走る)。
            case Keys.Back when !ReadOnly:
            {
                var (s, en) = GetSelectionCharRange();
                if (s != en)
                {
                    _buffer.Replace(s, en - s, "");
                    _caret = _anchor = s;
                }
                else if (_caret > 0)
                {
                    // MoveLeftChar はサロゲートペアを 1 文字として左寄せする(_caret-2 になる)。
                    // switch 冒頭で捕獲した snap を使う(UI スレッド専用契約により Delete case と等価)。
                    int start = NavigationCommands.MoveLeftChar(snap, _caret);
                    _buffer.Delete(start, _caret - start);
                    _caret = _anchor = start;
                }
                _desiredXpx = -1;
                AfterEdit();
                e.Handled = true;
                return;
            }
            case Keys.Delete when !ReadOnly:
            {
                var (s, en) = GetSelectionCharRange();
                if (s != en)
                {
                    _buffer.Replace(s, en - s, "");
                    _caret = _anchor = s;
                }
                else if (_caret < snap.CharLength)
                {
                    // MoveRightChar はサロゲートペアを 1 文字として右寄せする(_caret+2 になる)。
                    int next = NavigationCommands.MoveRightChar(snap, _caret);
                    _buffer.Delete(_caret, next - _caret);
                }
                _desiredXpx = -1;
                AfterEdit();
                e.Handled = true;
                return;
            }
            case Keys.Enter when !ReadOnly:
            {
                string eol = EolMode.ToEolString();   // "\r\n" / "\n" / "\r"
                var (s, en) = GetSelectionCharRange();
                _buffer.Replace(s, en - s, eol);
                _caret = _anchor = s + eol.Length;
                _desiredXpx = -1;
                AfterEdit();
                e.Handled = true;
                return;
            }
            case Keys.Tab when !ReadOnly:
            {
                // TabsToSpaces / TabWidth 対応は P6 送り(YAGNI・Task 9 は素の \t 挿入のみ)。
                var (s, en) = GetSelectionCharRange();
                _buffer.Replace(s, en - s, "\t");
                _caret = _anchor = s + 1;
                _desiredXpx = -1;
                AfterEdit();
                e.Handled = true;
                return;
            }
            case Keys.Insert:
                // 修飾なしで Overtype トグル(Ctrl/Shift 修飾時はモードトグルせず、
                // e.Handled=true で消費して Task 11 の case 追加=Ctrl+Insert=Copy /
                // Shift+Insert=Paste 待ちにする=switch 上で修飾付き case を上に置けば
                // 自動的にそちらが横取りする設計)。
                // Alt 修飾は通常 WinForms のメニュー mnemonic 側に捕捉されるため到達確率は
                // 低いが、到達した場合は現行実装では Overtype がトグルされる(Alt を判定
                // していないため)。挙動を厳密化したければ `!alt` を条件に追加する
                // (現状は簡潔さを優先=Alt はメニュー予約経路として非対応)。
                if (!ctrl && !shift) Overtype = !Overtype;
                e.Handled = true;
                return;
        }

        if (target is int t2)
        {
            if (resetDesired) _desiredXpx = -1;
            if (shift) MoveCaretWithSelection(t2);
            else SetCaretCharOffset(t2);
            _buffer.BreakUndoCoalescing();          // 純キャレット移動は coalescing 破断
            BringCaretIntoView();                    // Task 7 で本実装(現在はスタブ=no-op)
            RaiseCaretEnteredEmptyLineIfNeeded();    // Task 13 で本実装(現在はスタブ=no-op)
            e.Handled = true;
        }
    }

    /// <summary>
    /// 文字挿入(WM_CHAR)入り口(P3 Task 8)。<see cref="Overtype"/> ON では直後 1 文字を潰す
    /// (改行はスキップ・サロゲートペアは 2 code units を潰す)。<see cref="ReadOnly"/> ON では no-op。
    /// </summary>
    /// <remarks>
    /// 実装方針:
    /// - <b>制御文字は無視</b>: WM_CHAR は Ctrl 修飾の 0x01〜0x1F も来る(Ctrl+A=0x01 等)。
    ///   加えて Ctrl+Backspace は Windows のキーボード変換仕様上 wParam=0x7F(ASCII DEL)を
    ///   届けるため、これも除外対象(Task 8 レビュー I-1)。編集操作は全て <c>OnKeyDown</c>
    ///   経路(Task 6 の Ctrl+A・Task 9 の BackSpace/Delete/Enter/Tab/Insert)で処理する。
    ///   BackSpace(0x08)/Enter(0x0D)/Tab(0x09) は Task 9 で OnKeyDown 経由で処理済み=ここでは素通り
    ///   (Task 8 申し送り S-1 決着=選択肢 (A))。
    /// - <b>選択があれば無条件 Replace</b>: Overtype 影響なし(選択範囲を完全に置換)。
    /// - <b>Overtype で改行スキップ</b>: <c>\r</c>/<c>\n</c> の直前では潰さず単純挿入(Scintilla 互換)。
    /// - <b>サロゲート考慮</b>: <c>_caret</c> が high surrogate 位置なら pair 全体を潰す。
    ///   BMP 外の文字を新たに挿入するケース(WM_CHAR 2 回発火)は Task 8 対象外=将来の IME/貼付で対応。
    /// - <b>_desiredXpx リセット</b>: 挿入で垂直位置が変わる可能性があるため、水平移動系と同じく -1 に。
    /// - <b>AfterEdit へ集約</b>: スクロールバー再計算 → キャレット再配置 → 追従スクロール → Invalidate
    ///   を編集系共通の後処理に集約(Task 9〜11 でも再利用)。
    /// - <b>e.Handled = true</b>: WinForms のフォーカス処理や親フォームへのバブリングを抑止。
    /// </remarks>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_buffer is null || ReadOnly) return;
        char ch = e.KeyChar;

        // 制御文字(BackSpace 0x08 / Enter 0x0D / Tab 0x09 含む 0x00〜0x1F および
        // Ctrl+Backspace の 0x7F=ASCII DEL)は無視。編集用途はすべて OnKeyDown 経路で
        // 処理する(Task 9 で BackSpace/Enter/Tab 配線予定)。
        if (ch < 0x20 || ch == 0x7F) return;

        var (s, en) = GetSelectionCharRange();
        string ins = ch.ToString();

        if (s != en)
        {
            // 選択があるときは無条件で置換(Overtype 影響なし)。
            _buffer.Replace(s, en - s, ins);
            _caret = _anchor = s + ins.Length;
        }
        else if (Overtype)
        {
            // 上書き: 直後 1 文字を潰す。ただし改行(CR/LF)は潰さない。
            // 単一 Replace(=Splice) で「削除+挿入」を 1 Undo 単位で表現する。
            var snap = _buffer.Current;
            int overwriteLen = 0;
            if (_caret < snap.CharLength)
            {
                char nc = snap.GetChar(_caret);
                if (nc != '\r' && nc != '\n')
                {
                    // サロゲートペアは 2 code units 分潰す(high + low を pair として扱う)。
                    overwriteLen = (char.IsHighSurrogate(nc) && _caret + 1 < snap.CharLength
                                    && char.IsLowSurrogate(snap.GetChar(_caret + 1))) ? 2 : 1;
                }
            }
            _buffer.Replace(_caret, overwriteLen, ins);
            _caret = _anchor = _caret + ins.Length;
        }
        else
        {
            _buffer.Insert(_caret, ins);
            _caret = _anchor = _caret + ins.Length;
        }

        _desiredXpx = -1;
        AfterEdit();
        e.Handled = true;
    }

    /// <summary>
    /// 現在のキャレット位置(<c>_caret</c>)を可視領域内に収める(P3 Task 7)。
    /// - 垂直: キャレットの論理行が [TopLine, TopLine + visibleRows) の外なら <see cref="TopLine"/> を調整。
    /// - 水平: 折り返し OFF かつ <see cref="_hscroll"/> 表示中で、キャレット X が
    ///   [ScrollX, ScrollX + paintWidth) の外なら <see cref="ScrollX"/> を調整。
    /// - SetSource 前 / 折り返し ON では水平調整は no-op。
    /// - Task 6 で仕込んだ OnKeyDown 経路の呼び出しはそのまま本実装へ流れる(Ctrl+A は
    ///   慣例に合わせて意図的に呼び出さない=Task 6 レビュー I-1 の判断)。
    /// - App 層 Task 7 送り(検索ジャンプ・GoTo)から <see cref="EnsureVisibleCharRange"/>
    ///   経由で呼ばれることを想定して public 昇格。
    /// </summary>
    /// <remarks>
    /// <see cref="ComputeCaretPoint"/> は <c>_scrollX</c> 適用前の X を返す(=行番号マージン含む
    /// 描画原点座標)。現在の可視ウィンドウは [<c>_scrollX</c>, <c>_scrollX + paintWidth</c>)。
    /// 右端に張り付かせるときは caret を可視領域末尾から半角 1 文字幅分内側に置く
    /// (キャレットが右端ぎりぎりで隠れるのを防ぐ)。paintWidth が半角 1 文字幅より狭い極小
    /// ウィンドウでは、この 1 サイクル余分にスクロールしても届かず ScrollX がクランプで
    /// 頭打ちになるが、そのケースは表示自体が破綻しているので実害なし(S-3 レビュー)。
    /// 垂直方向の可視行数は <c>paintHeight = ClientSize.Height - hscroll.Height</c> ベースで
    /// 計算する(<see cref="ComputeCaretPoint"/> の可視性判定と一致させる=Task 7 レビュー I-1)。
    /// 一致していないと、最下論理行にキャレットが来たとき「垂直はまだ可視」と誤判断され
    /// hscroll の下に張り付いたまま TopLine が動かないケースが発生する。
    /// </remarks>
    public void BringCaretIntoView()
    {
        if (_buffer is null) return;
        var snap = _buffer.Current;

        int logicalLine = snap.GetLineIndexOfChar(_caret);
        // I-1 対応: paintHeight ベースで可視行数を算出(ComputeCaretPoint の可視性判定と一致)。
        int paintHeight = Math.Max(0, ClientSize.Height - (_hscroll.Visible ? _hscroll.Height : 0));
        int visibleRows = Math.Max(1, paintHeight / Math.Max(1, _metrics.LineHeightPx));

        // 垂直: caret 論理行が [TopLine, TopLine+visibleRows) に入るように TopLine 調整
        if (logicalLine < _topLine)
        {
            TopLine = logicalLine;
        }
        else if (logicalLine >= _topLine + visibleRows)
        {
            TopLine = logicalLine - visibleRows + 1;
        }

        // 水平: 折り返し OFF かつ HScroll 表示中のみ有効
        // (ScrollX setter 自体も _wrapColumns>0 / !_hscroll.Visible で no-op だが、
        //  ここで先に判定することで ComputeCaretPoint の無駄呼びを回避する)
        if (_wrapColumns == 0 && _hscroll.Visible)
        {
            var (x, _, visible) = ComputeCaretPoint(_caret);
            if (visible)
            {
                int paintWidth = Math.Max(0, ClientSize.Width - _vscroll.Width);
                if (x < _scrollX)
                {
                    ScrollX = x;
                }
                else if (x >= _scrollX + paintWidth)
                {
                    // 右端に張り付かせる: caret を可視領域末尾から 1 半角幅分内側に置く
                    ScrollX = x - paintWidth + _metrics.MeasureRun("0");
                }
            }
        }
    }

    /// <summary>
    /// 指定範囲 <c>[start, start+length)</c> の末尾を可視化する(検索ジャンプ・GoTo・
    /// EnsureVisible 相当)。内部でキャレットを一時的に end に置いて
    /// <see cref="BringCaretIntoView"/> を呼ぶが、呼び出し前のキャレット位置と
    /// アンカーは try/finally で必ず復元する(=装飾スクロールなので選択・
    /// キャレット状態は変えない)。SetSource 前は no-op。
    /// </summary>
    /// <param name="start">開始 UTF-16 文字オフセット。範囲末尾 (<c>start+length</c>) の
    /// 計算にのみ使い、start 単独位置の可視化は行わない(=start は <see cref="SnapAndClamp"/>
    /// を通さない)。</param>
    /// <param name="length">長さ(UTF-16 コード単位)。負値は 0 として扱う。</param>
    /// <remarks>
    /// C-1 対応(Task 7 レビュー): finally で <see cref="PositionCaret"/> を呼び、
    /// OS 側システムキャレット位置を savedCaret に戻す。これをやらないと
    /// <see cref="TopLine"/>/<see cref="ScrollX"/> setter が内部で呼ぶ
    /// <see cref="PositionCaret"/> が <c>_caret = end</c> のまま SetCaretPos を発火し、
    /// field 復元後も blinking caret / IME 変換開始位置 / UIA フォーカスキャレット位置が
    /// end に取り残される(P5 の UIA 接続で顕在化する)。
    /// try/finally 化は I-2(将来 <see cref="BringCaretIntoView"/> に throw を混ぜ込んだ
    /// ときの state 残留防止)も兼ねる。
    /// </remarks>
    public void EnsureVisibleCharRange(int start, int length)
    {
        if (_buffer is null) return;
        // 範囲末尾のみ可視化する仕様(start 側は SnapAndClamp 不要=BringCaretIntoView は
        // end のみに反応する)。start + length は int 加算オーバーフロー対策で long 経由。
        long endLong = (long)start + Math.Max(0, length);
        int endInt = endLong > int.MaxValue ? int.MaxValue : (int)endLong;
        int end = SnapAndClamp(endInt);

        int savedCaret = _caret;
        int savedAnchor = _anchor;
        try
        {
            _caret = end;
            _anchor = end;
            BringCaretIntoView();
        }
        finally
        {
            _caret = savedCaret;
            _anchor = savedAnchor;
            // OS 側キャレットを savedCaret の座標に戻す(TopLine/ScrollX setter が
            // 内部で _caret=end のまま SetCaretPos を発火した副作用を上書きする)。
            // 末尾 Invalidate は削除: setter が Invalidate 発行済み、no-op ケースなら不要。
            PositionCaret();
        }
    }

    /// <summary>
    /// 純キャレット移動でキャレットが空行に入ったとき、UIA / SR にイベントを上げる
    /// (Task 13 で本実装。yEdit の空行能動発声要件)。P3 Task 6 では呼び出し箇所を確定させる
    /// ためのスタブ(no-op)。
    /// </summary>
    private void RaiseCaretEnteredEmptyLineIfNeeded() { }

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
        }
        // 本コントロールの描画を確定させた後に Paint イベント購読者に描かせる
        // (App 層の overlay 拡張余地を残す)。base.OnPaint は Paint イベントを発火する。
        base.OnPaint(e);
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
    /// <see cref="AppSettings"/> からフォント/テーマ/表示設定を反映する。App 層の
    /// <c>EditorAppearance.Apply</c>(Scintilla ホスト向け)の自作コントロール版で、
    /// P6 で App 層から呼ばれることを想定している(P2 時点では未接続=Task 14 の smoke で目視確認)。
    ///
    /// 挙動:
    /// - フォント: 既存 Font を Dispose して新 Font に差し替え、<see cref="GdiCharMetrics"/> も再構築する
    ///   (LineHeightPx が変わるため後段の VScroll/HScroll 再計算とキャレット再配置が必須)。
    /// - テーマ: <see cref="AppearanceThemes.ById"/> で解決し、<see cref="ViewportStyle"/> を算出。
    ///   現在行/行番号/空白グリフの色は fore/back のブレンドで導出(現行 App 層 Blend の移植)。
    ///   BackColor は <see cref="Graphics.Clear"/> 用に Background と一致させる(<see cref="RenderFrame"/>
    ///   の一様シフト時に右側の隙間が同色で埋まる不変を維持)。
    /// - 表示設定: <see cref="ShowLineNumbers"/>/<see cref="ShowWhitespace"/>/<see cref="HighlightCurrentLine"/>
    ///   /<see cref="WrapColumns"/> をフィールドへ直接反映(setter の Invalidate/HScroll 再計算に頼らず、
    ///   末尾でまとめて Update*Scrollbar/PositionCaret/Invalidate を 1 回ずつ呼ぶ)。
    ///   <see cref="ScrollX"/> は 0 にリセット(折り返し設定が変わっても不整合を残さないため)。
    /// - Task 13 では <c>TabWidth</c>/<c>TabsToSpaces</c> は反映しない(P3=編集入力タスクの担当・YAGNI)。
    /// </summary>
    public void ApplyAppearance(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // フォント差し替え + GdiCharMetrics 再構築(古い Font は明示的に Dispose して GDI HFONT リーク回避)。
        // 例外安全: newFont / newMetrics を両方作り切ってから旧 Font を Dispose する。
        // GdiCharMetrics のコンストラクタが throw した場合は newFont も破棄して呼び出し元へ propagate
        // (旧 _font / _metrics は生きたまま=次回 OnPaint も従前の高さで安全に描画できる)。
        var newFont = new Font(
            string.IsNullOrEmpty(settings.FontName) ? "ＭＳ ゴシック" : settings.FontName,
            settings.FontSize > 0 ? settings.FontSize : 12f);
        GdiCharMetrics newMetrics;
        try
        {
            newMetrics = new GdiCharMetrics(newFont);
        }
        catch
        {
            newFont.Dispose();
            throw;
        }
        _font.Dispose();
        _font = newFont;
        _metrics = newMetrics;

        // テーマから ViewportStyle 算出 + Graphics.Clear 用 BackColor 同期
        var theme = AppearanceThemes.ById(settings.Theme);
        _style = BuildStyle(theme, settings.HighlightCurrentLine);
        BackColor = FromRgb(theme.BackRgb);

        // 表示設定はフィールドへ直接反映(末尾でまとめて Invalidate/Update するため setter を経由しない)
        _showLineNumbers = settings.ShowLineNumbers;
        _showWhitespace = settings.ShowWhitespace;
        _highlightCurrentLine = settings.HighlightCurrentLine;
        // キャレット太さ(弱視のキャレット視認性・yedit-sighted-users-first-class)
        _caretWidthPx = Math.Clamp(settings.CaretWidth, 1, 5);
        // WrapColumns の実値が変わったときだけ ScrollX をリセットする(フォント色だけ変更等で
        // 横スクロール位置が不用意にホームへ戻る副作用を避ける)。折り返し ON への遷移では
        // ScrollX=0 が必要=UpdateHorizontalScrollbar 内の HideAndResetHScroll でも 0 にされるが、
        // ここでも先に落としておくことで PositionCaret が過渡的な旧 _scrollX を参照するのを防ぐ。
        int oldWrapColumns = _wrapColumns;
        _wrapColumns = Math.Max(0, settings.WrapColumnEnabled ? settings.WrapColumn : 0);
        if (_wrapColumns != oldWrapColumns) _scrollX = 0;

        // LineHeightPx / 折り返し設定が変わった可能性があるので両スクロールバーを再計算 →
        // キャレット再配置。
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();

        // フォーカス保持中に LineHeightPx が変わったら system caret を作り直す
        // (前回 OnGotFocus 時の古い高さのままだと視覚的にキャレットが行高と合わない)。
        // フォーカス無し時は次回 OnGotFocus で新しい _metrics.LineHeightPx を使って作られる。
        if (_hasFocus)
        {
            NativeMethods.DestroyCaret();
            NativeMethods.CreateCaret(Handle, nint.Zero, _caretWidthPx, _metrics.LineHeightPx);
            NativeMethods.ShowCaret(Handle);
        }
        PositionCaret();
        Invalidate();
    }

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
