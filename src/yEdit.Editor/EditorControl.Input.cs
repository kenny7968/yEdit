// EditorControl.Input.cs
// Phase 2 (Task 2c) で切り出したキーボード/マウス入力分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3-3c で InputRouter へロジック移譲予定(キーマップ Dictionary 化・_mouseDragging/_wheelAccum
// の所有権も移す)。
using yEdit.Core.Buffers;
using yEdit.Core.Editing;
using yEdit.Core.Layout;
using yEdit.Core.Text;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // OnKeyDown/OnKeyPress/OnMouse* + InsertConfirmedText + Word boundary helpers

    /// <summary>
    /// マウスホイール(P3 Task 12 で精度改善版に更新)。
    /// - Delta は 1 tick = 120 単位。細切れデルタは <see cref="_wheelAccum"/> に蓄積し、閾値に達したら発火。
    /// - 1 tick あたりの行送り量は <see cref="SystemInformation.MouseWheelScrollLines"/> を使う
    ///   (レジストリ未設定 / 「1 ページ」設定=-1 では既定 3 にフォールバック)。
    /// - Delta&gt;0=上方向スクロール=<see cref="TopLine"/> 減。TopLine setter がクランプするため
    ///   上限/下限を跨ぐ蓄積は自然に上端/下端で頭打ちになる。
    /// - 末尾で <see cref="HandledMouseEventArgs.Handled"/> を立てて親コンテナへのバブリングを止める
    ///   (P6 で SplitContainer / ScrollableControl 内に置いた場合の二重スクロール防止)。
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_buffer is null) return;
        _wheelAccum += e.Delta;
        int wheelLines = SystemInformation.MouseWheelScrollLines;
        // SystemInformation.MouseWheelScrollLines は「1 ページスクロール」設定時 -1 を返す仕様。
        // <=0 の全ケースを 3 行(WinForms 標準の既定値)にフォールバックすることで簡潔化する。
        if (wheelLines <= 0) wheelLines = 3;
        while (_wheelAccum >= 120)
        {
            TopLine = _topLine - wheelLines;
            _wheelAccum -= 120;
        }
        while (_wheelAccum <= -120)
        {
            TopLine = _topLine + wheelLines;
            _wheelAccum += 120;
        }
        // WM_MOUSEWHEEL は親コンテナへ再ディスパッチされる仕様のため、Handled=true で
        // 明示的にバブリングを止める(HandledMouseEventArgs は WinForms が MouseWheel 経路で
        // 実際に渡す派生型・is パターンで安全に判定)。
        if (e is HandledMouseEventArgs hme) hme.Handled = true;
    }

    /// <summary>
    /// マウス左ボタン Down(P3 Task 12)。クライアント座標→char offset を計算し、
    /// Shift 併用時は <see cref="MoveCaretWithSelection"/>(アンカー保持=選択拡張)、
    /// 無修飾時は <see cref="SetCaretCharOffset"/>(選択解除)にディスパッチする。
    /// 右ボタン等は無視(将来のコンテキストメニュー配線に予約)。
    /// SetSource 前 / 左以外のボタンは no-op。
    /// </summary>
    /// <remarks>
    /// Focus() を明示的に呼ぶのは、TabStop=true のコントロールでもマウスクリックで自動的に
    /// フォーカスを得ないケース(親 Form が他の子にフォーカスを持たせている等)への保険。
    /// _caretCtrl.DesiredXpx は水平方向の移動系と同じ扱いで -1 リセット(次の Up/Down で再計算)。
    /// BreakUndoCoalescing は「純キャレット移動をまたいだ連続タイピングの分割」で
    /// OnKeyDown の移動系と同じ流儀(Scintilla 互換)。
    /// </remarks>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (IsComposing) CancelCompositionAndDefault();
        base.OnMouseDown(e);
        if (_buffer is null || e.Button != MouseButtons.Left) return;
        Focus();
        int target = OffsetFromClientPoint(e.X, e.Y);
        bool shift = (ModifierKeys & Keys.Shift) != 0;
        if (shift)
            MoveCaretWithSelection(target);
        else
            SetCaretCharOffset(target);
        _mouseDragging = true;
        _caretCtrl.DesiredXpx = -1;
        _buffer.BreakUndoCoalescing();
        BringCaretIntoView();
    }

    /// <summary>
    /// マウス移動(P3 Task 12・ドラッグ選択)。
    /// - <c>_mouseDragging</c>=false または左ボタン非押下時は no-op。
    /// - 押下中ならクライアント座標→char offset を計算し、アンカー保持でキャレットのみ動かす
    ///   (=ドラッグで選択範囲を広げる/縮める)。
    /// </summary>
    /// <remarks>
    /// MouseDown → コントロール外で MouseUp → コントロール内に戻る、といった外部 Capture 経路で
    /// ボタン離しを取り逃すケースへの保険として、e.Button に Left が含まれていない Move が届いたら
    /// フラグを落として抜ける(=次の MouseDown まで移動しない)。
    /// </remarks>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_mouseDragging || _buffer is null) return;
        if ((e.Button & MouseButtons.Left) == 0)
        {
            _mouseDragging = false;
            return;
        }
        int target = OffsetFromClientPoint(e.X, e.Y);
        MoveCaretWithSelection(target);
        BringCaretIntoView();
    }

    /// <summary>
    /// マウス左ボタン Up(P3 Task 12)。ドラッグ選択終了=フラグを落とすのみ。
    /// 座標→char offset の計算はしない(<see cref="OnMouseMove"/> が Up 直前の Move で
    /// すでに最終位置に置いているため)。左ボタン以外は無視。
    /// </summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _mouseDragging = false;
    }

    /// <summary>
    /// マウス左ボタン ダブルクリック(P3 Task 12・単語選択)。
    /// クライアント座標→char offset を計算し、<see cref="PrevWordBoundary"/>/<see cref="NextWordBoundary"/>
    /// で target を含む単語の [start, end) を <see cref="SetSelectionAnchored"/> で設定する。
    /// SetSource 前 / 左以外のボタンは no-op。
    /// </summary>
    /// <remarks>
    /// WinForms は「Down → Up → Click → Down → Up → DoubleClick」の順に発火するため、
    /// OnMouseDown 側で単発キャレット移動が既に走っている。DoubleClick で選択範囲を上書きする形で
    /// 単語選択を確定させる(Notepad と同挙動)。
    /// 空白位置のダブルクリック時の挙動は <see cref="NextWordBoundary"/> の remarks 参照
    /// (Notepad 近似=前単語+空白 run の一部を選択する非対称仕様・実機評価は Task 14 smoke / P7 送り)。
    /// </remarks>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_buffer is null || e.Button != MouseButtons.Left) return;
        int target = OffsetFromClientPoint(e.X, e.Y);
        var snap = _buffer.Current;
        int start = PrevWordBoundary(snap, target);
        int end = NextWordBoundary(snap, target);
        SetSelectionAnchored(start, end);
        _caretCtrl.DesiredXpx = -1;
        _buffer.BreakUndoCoalescing();
        BringCaretIntoView();
    }

    /// <summary>
    /// クライアント座標(px)から論理オフセット(UTF-16 char)を算出する純ヘルパ(P3 Task 12)。
    /// - Y &lt; 0 は <see cref="_topLine"/> の先頭視覚行にクランプ
    /// - 最終視覚行を超えた Y は文書末尾(=<see cref="TextSnapshot.CharLength"/>)にクランプ
    ///   (Notepad と同挙動=末尾行より下の空領域クリックで caret が文書末尾に来る)
    /// - X の行末超過は <see cref="PixelMapper.PxToOffset"/> がセグメント末尾にクランプ
    /// </summary>
    /// <remarks>
    /// 折り返し ON 時は 1 論理行ずつ <see cref="LineLayout.Wrap"/> を呼び直しつつ視覚行を歩く。
    /// Task 14 のベンチで顕在化するようなら Frame 再利用等で最適化(<see cref="ComputeCaretPoint"/>
    /// の Task 9 レビュー M-3 と同じ申し送り)。
    /// </remarks>
    private int OffsetFromClientPoint(int clientX, int clientY)
    {
        if (_buffer is null) return 0;
        var snap = _buffer.Current;
        int lineHeight = _metrics.LineHeightPx;
        int lnWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;

        int visualRowFromTop = clientY / Math.Max(1, lineHeight);
        if (visualRowFromTop < 0) visualRowFromTop = 0;

        int maxWidthPx = _wrapColumns > 0 ? _wrapColumns * _metrics.MeasureRun("0") : 0;

        // TopLine の先頭視覚行から visualRowFromTop 個進む(折り返し ON 時は視覚行=セグメント単位)。
        // 文書末に達した場合(exhausted=true)は文書末尾へクランプする=X による位置決めは行わない。
        int line = _topLine;
        int segIdx = 0;
        int rowsToAdvance = visualRowFromTop;
        int segCount = SegmentCountAtLine(snap, line, maxWidthPx);
        bool exhausted = false;
        while (rowsToAdvance > 0)
        {
            if (segIdx + 1 < segCount)
            {
                segIdx++;
            }
            else
            {
                if (line + 1 >= snap.LineCount) { exhausted = true; break; }
                line++;
                segIdx = 0;
                segCount = SegmentCountAtLine(snap, line, maxWidthPx);
            }
            rowsToAdvance--;
        }

        // 最終視覚行より下 → 文書末尾にクランプ
        if (exhausted) return snap.CharLength;

        // 対象視覚セグメントを取り出し、X → local offset へ
        int lineStart = snap.GetLineStart(line);
        int lineEnd = snap.GetLineEnd(line, includeBreak: false);
        string lineText = lineEnd == lineStart ? string.Empty : snap.GetText(lineStart, lineEnd - lineStart);
        var segs = LineLayout.Wrap(lineText, maxWidthPx, _metrics);
        // WalkVisualRows と同様に防御的にクランプ(通常は segIdx < segs.Count が保たれる)
        int useSeg = Math.Min(segIdx, segs.Count - 1);
        var seg = segs[useSeg];

        // X からセグメント内オフセットを求める。行番号マージンを引き、_scrollX(折り返し OFF 時の
        // 水平シフト)を加算して「セグメント先頭を x=0 とする局所座標」に戻す。
        int xInBody = clientX - lnWidth + _scrollX;
        if (xInBody < 0) xInBody = 0;
        var segSpan = lineText.AsSpan(seg.OffsetInLine, seg.Length);
        int localOffset = PixelMapper.PxToOffset(segSpan, xInBody, _metrics);

        return lineStart + seg.OffsetInLine + localOffset;
    }

    /// <summary>指定論理行の視覚セグメント数(=折り返し個数)。<see cref="OffsetFromClientPoint"/> のヘルパ。</summary>
    private int SegmentCountAtLine(TextSnapshot snap, int line, int maxWidthPx)
    {
        int ls = snap.GetLineStart(line);
        int le = snap.GetLineEnd(line, includeBreak: false);
        string t = le == ls ? string.Empty : snap.GetText(ls, le - ls);
        return LineLayout.Wrap(t, maxWidthPx, _metrics).Count;
    }

    /// <summary>
    /// target 位置を含む単語の先頭を返す(<see cref="OnMouseDoubleClick"/> のヘルパ)。
    /// - target &lt;= 0: 0
    /// - target &gt;= CharLength(EOF): <see cref="WordBoundary.PrevWordStart"/>(CharLength) に委譲=
    ///   末尾が空白なら空白を左スキップして直前の単語まで戻る=末尾に近い単語の頭を返す
    /// - それ以外: <see cref="WordBoundary.PrevWordStart"/>(target+1) を呼ぶことで
    ///   「target 自身を含む単語 class の連続の左端」を得る
    /// </summary>
    /// <remarks>
    /// <see cref="WordBoundary.PrevWordStart"/> は「caret から 1 code-point 左に移動 → 空白後方スキップ
    /// → 同 class 後方スキップ」の設計。従って PrevWordStart(target+1) は「target 自身」が起点になり、
    /// target 位置の文字を含む単語の頭を返す。境界(target=CharLength)では target+1 は不正なので、
    /// 素直に PrevWordStart(target) にフォールバックする(=末尾に近い単語頭に着地する)。
    /// </remarks>
    private static int PrevWordBoundary(TextSnapshot snap, int target)
    {
        if (target <= 0) return 0;
        if (target >= snap.CharLength) return WordBoundary.PrevWordStart(snap, target);
        return WordBoundary.PrevWordStart(snap, target + 1);
    }

    /// <summary>
    /// target 位置の word run の終端を返す(<see cref="OnMouseDoubleClick"/> のヘルパ)。
    /// target が空白/改行/EOF の場合の挙動は下記 remarks 参照。
    /// </summary>
    /// <remarks>
    /// <see cref="WordBoundary.NextWordStart"/> は Ctrl+→ 用に「単語末尾+空白列をスキップして次単語の頭」
    /// を返す設計。ダブルクリック単語選択では末尾空白を含めたくないため、返り値から左に戻して空白/改行
    /// 以外の最初の位置を求める。ただし後方スキャンは <c>nextWordStart &gt; target</c> でガードするため、
    /// target 自身より左には決して戻らない。以下ケース別の挙動:
    /// <list type="bullet">
    /// <item><description>単語内位置: 単語末尾(空白直前)を返す。<see cref="PrevWordBoundary"/> と
    /// 組み合わせると単語の [start, end) が選択される(最も直感的なケース)。</description></item>
    /// <item><description>空白/改行位置: NextWordStart(target) が次単語の頭を返し、そこから左戻しは
    /// target で止まる(=target 自身)。<see cref="PrevWordBoundary"/> と組み合わせると
    /// <c>[前単語頭, target)</c>=「前の単語+target 位置までの空白 run の一部」が選択される。
    /// 空白 run のどこをクリックするかで含まれる空白数が変わる非対称仕様(Notepad の空白
    /// ダブルクリック挙動に近い)。VS Code は空白 run 全体を単独選択する挙動で、これへの
    /// 変更要否は Task 14 smoke / P7 実機検証で最終判断する(申し送り: 計画書 Task 12
    /// レビュー節)。</description></item>
    /// <item><description>CharLength(EOF)位置: CharLength をそのまま返す。</description></item>
    /// </list>
    /// </remarks>
    private static int NextWordBoundary(TextSnapshot snap, int target)
    {
        if (target >= snap.CharLength) return snap.CharLength;
        int nextWordStart = WordBoundary.NextWordStart(snap, target);
        while (nextWordStart > target)
        {
            char c = snap.GetChar(nextWordStart - 1);
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n') break;
            nextWordStart--;
        }
        return nextWordStart;
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
    /// - <c>BringCaretIntoView()</c> は Task 7 で本実装。Task 6 では**呼び出し箇所を確定させる**
    ///   ためスタブ(no-op)で置く。
    /// - <b>case の順序規約</b>: <c>when</c> ガード付き case は同 KeyCode の無修飾 case より
    ///   <b>switch 上に</b>置くこと。C# switch は上から順評価=無修飾 case を先に置くと、
    ///   修飾付き case は永遠にヒットしない。Task 11 の Ctrl+Insert / Shift+Insert /
    ///   Shift+Delete が Task 9 の Keys.Insert / Keys.Delete より上にあるのはこの規約による。
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
                target = ctrl ? WordBoundary.PrevWordStart(snap, _caretCtrl.Caret)
                              : NavigationCommands.MoveLeftChar(snap, _caretCtrl.Caret);
                resetDesired = true;
                break;
            case Keys.Right:
                target = ctrl ? WordBoundary.NextWordStart(snap, _caretCtrl.Caret)
                              : NavigationCommands.MoveRightChar(snap, _caretCtrl.Caret);
                resetDesired = true;
                break;
            case Keys.Home:
                // P8-1a: 折り返し ON では視覚行(折り返し行)の先頭へ(NVDA が視覚行先頭から読むよう App 層挙動を統一)
                target = ctrl ? 0 : NavigationCommands.MoveHomeSmart(snap, _caretCtrl.Caret, _wrapColumns, _metrics);
                resetDesired = true;
                break;
            case Keys.End:
                target = ctrl ? snap.CharLength : NavigationCommands.MoveEnd(snap, _caretCtrl.Caret);
                resetDesired = true;
                break;
            case Keys.Up:
            {
                var (t, d) = VerticalNavigation.MoveUp(snap, _caretCtrl.Caret, _caretCtrl.DesiredXpx, _wrapColumns, _metrics);
                _caretCtrl.DesiredXpx = d;
                target = t;
                break;
            }
            case Keys.Down:
            {
                var (t, d) = VerticalNavigation.MoveDown(snap, _caretCtrl.Caret, _caretCtrl.DesiredXpx, _wrapColumns, _metrics);
                _caretCtrl.DesiredXpx = d;
                target = t;
                break;
            }
            case Keys.PageUp:
            {
                int rows = Math.Max(1, ClientSize.Height / Math.Max(1, _metrics.LineHeightPx));
                var (t, d) = VerticalNavigation.PageUp(snap, _caretCtrl.Caret, _caretCtrl.DesiredXpx, _wrapColumns, rows, _metrics);
                _caretCtrl.DesiredXpx = d;
                target = t;
                break;
            }
            case Keys.PageDown:
            {
                int rows = Math.Max(1, ClientSize.Height / Math.Max(1, _metrics.LineHeightPx));
                var (t, d) = VerticalNavigation.PageDown(snap, _caretCtrl.Caret, _caretCtrl.DesiredXpx, _wrapColumns, rows, _metrics);
                _caretCtrl.DesiredXpx = d;
                target = t;
                break;
            }
            case Keys.A when ctrl:
                SetSelectionAnchored(0, snap.CharLength);
                // 全選択=文書頭〜末尾のジャンプ相当なので、水平移動と同様に desired X をリセット。
                // これを忘れると次の Up/Down が「Ctrl+A 前の古い列」を目指す(Task 6 レビュー S-1)。
                _caretCtrl.DesiredXpx = -1;
                _buffer.BreakUndoCoalescing();
                e.Handled = true;
                return;

            // ===== P3 Task 11: Cut/Copy/Paste + レガシーキー(Ctrl+Insert/Shift+Insert/Shift+Delete) =====
            // Ctrl+C は ReadOnly でも動く(本文不変・Notepad と同挙動)。
            // Cut / Paste は ReadOnly で no-op=case ガードから `!ReadOnly` を落として本体側の
            // 早期 return に任せる案もあるが、case ガードで先に弾いた方が
            // 「switch がキーを消費しない=既定処理へ委譲」の可読性が高い(Undo/Redo と同じ流儀)。
            case Keys.X when ctrl && !ReadOnly:
                Cut();
                e.Handled = true;
                return;
            case Keys.C when ctrl:
                Copy();
                e.Handled = true;
                return;
            case Keys.V when ctrl && !ReadOnly:
                Paste();
                e.Handled = true;
                return;

            // レガシーキー(Windows 3.x 以来の伝統的なクリップボードショートカット)。
            // switch は上から順に評価されるため、Task 9 の case Keys.Insert / Keys.Delete
            // より上に置くことで自動的にそちらを横取りする(=先に return する)。
            // - Ctrl+Insert: Copy(ReadOnly でも動く=Notepad と同挙動)
            // - Shift+Insert: Paste(ReadOnly で no-op)
            // - Shift+Delete: Cut(ReadOnly で no-op)
            case Keys.Insert when ctrl:
                Copy();
                e.Handled = true;
                return;
            case Keys.Insert when shift && !ReadOnly:
                Paste();
                e.Handled = true;
                return;
            case Keys.Delete when shift && !ReadOnly:
                Cut();
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
            // 追従スクロール → Invalidate」の共通後処理。編集経路では _caretCtrl.DesiredXpx = -1 で
            // 垂直位置が変わり得ることを表現する(§0-6 一貫性)。
            // _caretCtrl.SetTo は編集経路の「バッファ変化と一連の副作用を 1 度にまとめる」
            // ために public setter (SetCaretCharOffset) を経由せず直接使う(setter 経由だと
            // 二重 Invalidate/PositionCaret が走る)。
            case Keys.Back when !ReadOnly:
            {
                var (s, en) = GetSelectionCharRange();
                if (s != en)
                {
                    _buffer.Replace(s, en - s, "");
                    _caretCtrl.SetTo(s, _buffer.Current);
                }
                else if (_caretCtrl.Caret > 0)
                {
                    // MoveLeftChar はサロゲートペアを 1 文字として左寄せする(caret-2 になる)。
                    // switch 冒頭で捕獲した snap を使う(UI スレッド専用契約により Delete case と等価)。
                    int start = NavigationCommands.MoveLeftChar(snap, _caretCtrl.Caret);
                    _buffer.Delete(start, _caretCtrl.Caret - start);
                    _caretCtrl.SetTo(start, _buffer.Current);
                }
                _caretCtrl.DesiredXpx = -1;
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
                    _caretCtrl.SetTo(s, _buffer.Current);
                }
                else if (_caretCtrl.Caret < snap.CharLength)
                {
                    // MoveRightChar はサロゲートペアを 1 文字として右寄せする(caret+2 になる)。
                    int next = NavigationCommands.MoveRightChar(snap, _caretCtrl.Caret);
                    _buffer.Delete(_caretCtrl.Caret, next - _caretCtrl.Caret);
                }
                _caretCtrl.DesiredXpx = -1;
                AfterEdit();
                e.Handled = true;
                return;
            }
            case Keys.Enter when !ReadOnly:
            {
                string eol = EolMode.ToEolString();   // "\r\n" / "\n" / "\r"
                var (s, en) = GetSelectionCharRange();
                _buffer.Replace(s, en - s, eol);
                _caretCtrl.SetTo(s + eol.Length, _buffer.Current);
                _caretCtrl.DesiredXpx = -1;
                AfterEdit();
                e.Handled = true;
                return;
            }
            case Keys.Tab when !ReadOnly:
            {
                // TabsToSpaces / TabWidth 対応は P6 送り(YAGNI・Task 9 は素の \t 挿入のみ)。
                var (s, en) = GetSelectionCharRange();
                _buffer.Replace(s, en - s, "\t");
                _caretCtrl.SetTo(s + 1, _buffer.Current);
                _caretCtrl.DesiredXpx = -1;
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
            if (resetDesired) _caretCtrl.DesiredXpx = -1;
            if (shift) MoveCaretWithSelection(t2);
            else SetCaretCharOffset(t2);
            _buffer.BreakUndoCoalescing();          // 純キャレット移動は coalescing 破断
            BringCaretIntoView();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 文字挿入(WM_CHAR)入り口(P3 Task 8 / P4 Task 3 で <see cref="InsertConfirmedText"/> へ委譲)。
    /// 制御文字を弾いてから確定 1 文字を <see cref="InsertConfirmedText"/> に流し
    /// P4 の IME 確定経路(WM_IME_COMPOSITION の GCS_RESULTSTR)と挿入本体を共有する。
    /// </summary>
    /// <remarks>
    /// <b>制御文字は無視</b>: WM_CHAR は Ctrl 修飾の 0x01〜0x1F(Ctrl+A=0x01 等)や
    /// Ctrl+Backspace の 0x7F(ASCII DEL・Task 8 レビュー I-1)も届くため、これらは除外する。
    /// 編集操作(BackSpace/Enter/Tab/Ctrl+A 等)は <c>OnKeyDown</c> 経路(Task 6/9)で処理済み。
    /// 選択/上書き/サロゲートの分岐と <c>_caretCtrl.DesiredXpx</c>/AfterEdit の後処理は
    /// <see cref="InsertConfirmedText"/> に集約(=1 経路)。<see cref="ReadOnly"/> ON では no-op。
    /// </remarks>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_buffer is null || ReadOnly) return;
        char ch = e.KeyChar;

        // 制御文字(0x00〜0x1F, 0x7F)は無視。編集用途は OnKeyDown 経路で処理する(§0-9 温存)。
        if (ch < 0x20 || ch == 0x7F) return;

        InsertConfirmedText(ch.ToString());
        e.Handled = true;
    }

    /// <summary>
    /// 確定文字列を挿入する共通経路(P4)。P3 の <see cref="OnKeyPress"/> と
    /// P4 の <see cref="WM_IME_COMPOSITION"/> (GCS_RESULTSTR) の両方から呼ばれる。
    /// 選択があれば削除→挿入。<see cref="Overtype"/> ON では直後 1 文字を潰す
    /// (改行はスキップ・サロゲートペアは 2 code units を潰す)。
    /// <see cref="ReadOnly"/> ON では no-op。
    /// </summary>
    /// <remarks>
    /// - <paramref name="text"/> の長さは制限なし(IME 確定は通常 1〜数文字だが仕様上長文もあり)
    /// - <paramref name="text"/> が空文字なら no-op(ESC 取消時の GCS_RESULTSTR=空を安全化)
    /// - <c>_caretCtrl.DesiredXpx</c> は -1 リセット(P3 §0-6)、AfterEdit で追従スクロール
    /// </remarks>
    private void InsertConfirmedText(string text)
    {
        if (_buffer is null || ReadOnly) return;
        if (string.IsNullOrEmpty(text)) return;

        var (s, en) = GetSelectionCharRange();
        if (s != en)
        {
            _buffer.Replace(s, en - s, text);
            _caretCtrl.SetTo(s + text.Length, _buffer.Current);
        }
        else if (Overtype)
        {
            var snap = _buffer.Current;
            int overwriteLen = 0;
            int caret = _caretCtrl.Caret;
            if (caret < snap.CharLength)
            {
                char nc = snap.GetChar(caret);
                if (nc != '\r' && nc != '\n')
                {
                    overwriteLen = (char.IsHighSurrogate(nc) && caret + 1 < snap.CharLength
                                    && char.IsLowSurrogate(snap.GetChar(caret + 1))) ? 2 : 1;
                }
            }
            _buffer.Replace(caret, overwriteLen, text);
            _caretCtrl.SetTo(caret + text.Length, _buffer.Current);
        }
        else
        {
            int caret = _caretCtrl.Caret;
            _buffer.Insert(caret, text);
            _caretCtrl.SetTo(caret + text.Length, _buffer.Current);
        }

        _caretCtrl.DesiredXpx = -1;
        AfterEdit();
    }
}
