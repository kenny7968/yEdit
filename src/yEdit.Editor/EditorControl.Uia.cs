// EditorControl.Uia.cs
// Phase 2 (Task 2d) で切り出した UIA (IUiaTextHost) 分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3-3d で UiaTextHostAdapter へロジック移譲予定
// (_bufferSnapshot/_bounds/_boundsSync/_lastLineSegs/_hwnd の所有権も移す)。
using yEdit.Core.Buffers;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // ==================== UIA テストフック(Editor.Tests から観測) ====================

    // Editor.Tests から観測するためのヒットカウンタ(internal・テスト以外の呼び出しは想定しない)。
    internal long TestHook_LastLineSegsHitCount { get; private set; }
    internal long TestHook_LastLineSegsMissCount { get; private set; }
    internal void TestHook_ResetLastLineSegsCounters()
    {
        TestHook_LastLineSegsHitCount = 0;
        TestHook_LastLineSegsMissCount = 0;
    }

    // Task 6 テスト用フック: WndProc 経路と self-served 判定を Editor.Tests から観察する。
    internal static void TestHook_WndProc(EditorControl c, ref Message m) => c.WndProc(ref m);
    internal static bool TestHook_LastGetObjectServed(EditorControl c) => c._testHook_LastGetObjectServed;

    // ==================== WM_GETOBJECT からの provider 生成 helper ====================
    // Phase 2 (Task 2d) 分割時に切り出し: WndProc 本体の WM_GETOBJECT case から呼ばれる。
    // 分岐そのものは WndProc 本体側 (§C.4) に残し、生成のみ Uia 側へ集約する。
    private void EnsureUiaProvider()
    {
        _provider ??= new yEdit.Accessibility.TextControlProviderV2(this);
    }

    // ==================== P5 Task 5: IUiaTextHost v2 実装 ====================
    // UIA プロバイダ(TextControlProviderV2)のバックエンド。RPC スレッドから呼ばれ得るため
    // 全メンバは不変スナップショット参照 + キャッシュ値で応答する。SetSelection / SetFocus のみ
    // UI スレッドへマーシャリングする。座標 API 2 個(GetBoundingRectangles / OffsetFromScreenPoint)は
    // 本 Task ではスタブで、Task 10/11 で本実装する。

    private void CacheSnapshot()
    {
        if (_buffer is not null) _bufferSnapshot = _buffer.Current;
    }

    private void UpdateBoundsCache()
    {
        if (!IsHandleCreated) return;
        var r = RectangleToScreen(ClientRectangle);
        lock (_boundsSync) _bounds = new System.Windows.Rect(r.Left, r.Top, r.Width, r.Height);
        // P5 Task 10: client→screen オフセットも同時に更新
        var origin = PointToScreen(new System.Drawing.Point(0, 0));
        _clientToScreenX = origin.X;
        _clientToScreenY = origin.Y;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hwnd = Handle;   // P5 Task 14 (I-2): RPC スレッドが安全に読める hwnd キャッシュ
        UpdateBoundsCache();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _hwnd = IntPtr.Zero;
        base.OnHandleDestroyed(e);
    }

    string yEdit.Accessibility.IUiaTextHost.GetTextRange(int start, int length)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return "";
        int s = Math.Clamp(start, 0, snap.CharLength);
        int l = Math.Clamp(length, 0, snap.CharLength - s);
        return snap.GetText(s, l);
    }

    int yEdit.Accessibility.IUiaTextHost.TextLength => _bufferSnapshot?.CharLength ?? 0;

    (int Start, int End) yEdit.Accessibility.IUiaTextHost.GetSelection()
    {
        int c = _caret, a = _anchor;
        return (Math.Min(a, c), Math.Max(a, c));
    }

    void yEdit.Accessibility.IUiaTextHost.SetSelection(int start, int end)
    {
        // P5 Task 14 (I-3): 破棄後 / Handle 未生成での BeginInvoke による InvalidOperationException を防ぐ
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ((yEdit.Accessibility.IUiaTextHost)this).SetSelection(start, end)));
            return;
        }
        SetSelectionCharRange(start, end);
    }

    int yEdit.Accessibility.IUiaTextHost.NextChar(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        if (o >= snap.CharLength) return snap.CharLength;
        char c = snap.GetChar(o);
        if (char.IsHighSurrogate(c) && o + 1 < snap.CharLength && char.IsLowSurrogate(snap.GetChar(o + 1)))
            return o + 2;
        return o + 1;
    }

    int yEdit.Accessibility.IUiaTextHost.PrevChar(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        if (o <= 0) return 0;
        if (char.IsLowSurrogate(snap.GetChar(o - 1)) && o - 2 >= 0 && char.IsHighSurrogate(snap.GetChar(o - 2)))
            return o - 2;
        return o - 1;
    }

    int yEdit.Accessibility.IUiaTextHost.LineStartOf(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        int line = snap.GetLineIndexOfChar(o);
        int logicalStart = snap.GetLineStart(line);
        // P8-1c: 折り返し ON の視覚行=キャレット位置が属する視覚セグメントの先頭を返す。
        // wrap OFF は論理行先頭(既存挙動)。
        var visual = TryFindVisualSegment(snap, line, o - logicalStart);
        return visual is { } vs ? logicalStart + vs.OffsetInLine : logicalStart;
    }

    int yEdit.Accessibility.IUiaTextHost.LineEnd(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        int line = snap.GetLineIndexOfChar(o);
        int logicalStart = snap.GetLineStart(line);
        int logicalEnd = snap.GetLineEnd(line, includeBreak: false);
        // P8-1c: 折り返し ON の視覚行=次の視覚行の先頭(=現在視覚行の直後)。
        // 継続セグメント(=論理行末に達しないセグメント)は次 seg 先頭=改行を跨がない。
        var visual = TryFindVisualSegment(snap, line, o - logicalStart);
        if (visual is { } vs)
        {
            int visualEndInLine = vs.OffsetInLine + vs.Length;
            if (logicalStart + visualEndInLine < logicalEnd)
                return logicalStart + visualEndInLine;   // 継続 seg=改行手前で終了
        }
        // 論理行最終視覚行(または wrap OFF)=改行を含めて次論理行先頭 or TextLength
        if (line + 1 < snap.LineCount) return snap.GetLineStart(line + 1);
        return snap.CharLength;
    }

    int yEdit.Accessibility.IUiaTextHost.LineEndNoBreakOf(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int e = ((yEdit.Accessibility.IUiaTextHost)this).LineEnd(offset);
        // CRLF 混在対応: LF → CR の順で剥がす(継続セグメントの場合 e は改行前=剥がすものが無いので no-op)
        if (e > 0 && snap.GetChar(e - 1) == '\n') e--;
        if (e > 0 && snap.GetChar(e - 1) == '\r') e--;
        return e;
    }

    /// <summary>
    /// P8-1c: 論理行 <paramref name="line"/> 内の <paramref name="offsetInLine"/> が属する視覚セグメントを返す。
    /// wrap OFF(<c>_wrapColumns &lt;= 0</c>)または空行のときは null=呼び出し側で論理行フォールバック。
    /// </summary>
    /// <remarks>
    /// P8 レビュー Important-1 対応: <see cref="GdiCharMetrics.MeasureRun"/> は非 ASCII(日本語含む)で
    /// <see cref="TextRenderer.MeasureText"/>(GDI)へ落ちる=UI スレッド専用。UIA RPC スレッドから
    /// 直接呼ぶと契約違反+<see cref="ApplyAppearance"/> による <see cref="Font"/> 差替時の
    /// disposed reference レースが発生する。RPC スレッドからは <see cref="Control.Invoke(Delegate)"/> で
    /// UI スレッドへマーシャリングして両問題を解決する(SR の Line 単位読みは典型的に秒あたり数回=
    /// Invoke レイテンシ数 ms は許容)。Handle 未生成時(SetSource 前)は null=論理行フォールバック。
    /// </remarks>
    private yEdit.Core.Layout.WrapSegment? TryFindVisualSegment(TextSnapshot snap, int line, int offsetInLine)
    {
        int wrap = _wrapColumns;
        if (wrap <= 0) return null;
        if (!IsHandleCreated) return null;   // UI スレッドが束縛されていない=論理行フォールバック
        if (InvokeRequired)
        {
            try
            {
                return (yEdit.Core.Layout.WrapSegment?)Invoke(new Func<yEdit.Core.Layout.WrapSegment?>(
                    () => TryFindVisualSegmentCore(snap, line, offsetInLine, wrap)));
            }
            catch (ObjectDisposedException) { return null; }
            catch (InvalidOperationException) { return null; }   // Handle 破棄との race
        }
        return TryFindVisualSegmentCore(snap, line, offsetInLine, wrap);
    }

    /// <summary>UI スレッド上での視覚セグメント検索本体(<see cref="TryFindVisualSegment"/> から Invoke マーシャリング後)。</summary>
    private yEdit.Core.Layout.WrapSegment? TryFindVisualSegmentCore(TextSnapshot snap, int line, int offsetInLine, int wrap)
    {
        System.Collections.Generic.IReadOnlyList<yEdit.Core.Layout.WrapSegment> segs;

        if (_lastLineSegs is { } c &&
            ReferenceEquals(c.Snap, snap) && c.Line == line && c.Wrap == wrap)
        {
            segs = c.Segs;
            TestHook_LastLineSegsHitCount++;
        }
        else
        {
            var metrics = _metrics;
            int logicalStart = snap.GetLineStart(line);
            int logicalEnd = snap.GetLineEnd(line, includeBreak: false);
            if (logicalStart == logicalEnd) return null;
            string lineText = snap.GetText(logicalStart, logicalEnd - logicalStart);
            int maxWidthPx = wrap * metrics.MeasureRun("0".AsSpan());
            segs = yEdit.Core.Layout.LineLayout.Wrap(lineText.AsSpan(), maxWidthPx, metrics);
            _lastLineSegs = (snap, line, wrap, segs);
            TestHook_LastLineSegsMissCount++;
        }

        return yEdit.Core.Layout.VisualSegments.FindContaining(segs, offsetInLine).Segment;
    }

    int yEdit.Accessibility.IUiaTextHost.WordStart(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return WordBoundary_WordStart(snap, o);
    }

    int yEdit.Accessibility.IUiaTextHost.WordEnd(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return WordBoundary_WordEnd(snap, o);
    }

    int yEdit.Accessibility.IUiaTextHost.NextWordStart(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return yEdit.Core.Editing.WordBoundary.NextWordStart(snap, o);
    }

    int yEdit.Accessibility.IUiaTextHost.PrevWordStart(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return yEdit.Core.Editing.WordBoundary.PrevWordStart(snap, o);
    }

    // WordStart/WordEnd は Core WordBoundary に直接メンバがないため、
    // 「offset を含む単語の左/右端(空白でない連続の左/右端)」を素朴実装する
    // (計画書 §5-5: v1 の TextNavigation.WordStart と同じ流儀=空白区切りだけ)。
    private static int WordBoundary_WordStart(TextSnapshot snap, int pos)
    {
        if (pos <= 0) return 0;
        int p = pos;
        while (p > 0)
        {
            int prev = p - 1;
            if (prev > 0 && char.IsLowSurrogate(snap.GetChar(prev)) && char.IsHighSurrogate(snap.GetChar(prev - 1)))
                prev--;
            char pc = snap.GetChar(prev);
            if (char.IsWhiteSpace(pc) || pc == '\r' || pc == '\n') break;
            p = prev;
        }
        return p;
    }

    private static int WordBoundary_WordEnd(TextSnapshot snap, int pos)
    {
        int p = pos;
        while (p < snap.CharLength)
        {
            char c = snap.GetChar(p);
            if (char.IsWhiteSpace(c) || c == '\r' || c == '\n') break;
            if (char.IsHighSurrogate(c) && p + 1 < snap.CharLength && char.IsLowSurrogate(snap.GetChar(p + 1)))
                p += 2;
            else
                p++;
        }
        return p;
    }

    System.Windows.Rect yEdit.Accessibility.IUiaTextHost.BoundingRectangle
    {
        get { lock (_boundsSync) return _bounds; }
    }

    // P5 Task 10: 座標 API 本実装
    // [start, end) を含む各行のスクリーン矩形を UIA 形式 (x,y,w,h, ...) で返す。
    // ComputeCaretPoint は UI スレッド専用の状態(_topLine 等)を参照するため、
    // RPC スレッドから呼ばれた場合は Invoke で UI スレッドへマーシャリングする。
    // ハンドル未生成 / 非可視範囲は空配列。
    double[] yEdit.Accessibility.IUiaTextHost.GetBoundingRectangles(int start, int end)
    {
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return Array.Empty<double>();
            return (double[])Invoke(new Func<double[]>(() => ComputeBoundingRectangles(start, end)));
        }
        return ComputeBoundingRectangles(start, end);
    }

    private double[] ComputeBoundingRectangles(int start, int end)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return Array.Empty<double>();
        int s = Math.Clamp(start, 0, snap.CharLength);
        int en = Math.Clamp(end, 0, snap.CharLength);
        if (s >= en) return Array.Empty<double>();

        int csx = _clientToScreenX, csy = _clientToScreenY;
        int lineHeight = _metrics.LineHeightPx;
        var rects = new System.Collections.Generic.List<double>(16);

        int pos = s;
        int safety = 0;
        while (pos < en && safety++ < 100_000)
        {
            int line = snap.GetLineIndexOfChar(pos);
            int lineEndNoBreak = snap.GetLineEnd(line, includeBreak: false);
            int rangeEnd = Math.Min(en, lineEndNoBreak);

            var (x1, y1, visible) = ComputeCaretPoint(pos);
            var (x2, _, _) = ComputeCaretPoint(rangeEnd);
            if (visible)
            {
                double w = Math.Max(1, x2 - x1);
                rects.Add(csx + x1);
                rects.Add(csy + y1);
                rects.Add(w);
                rects.Add(lineHeight);
            }

            int nextLineStart = (line + 1 < snap.LineCount)
                ? snap.GetLineStart(line + 1)
                : snap.CharLength;
            if (nextLineStart <= pos) break;
            pos = nextLineStart;
        }
        return rects.ToArray();
    }

    // P5 Task 11: OffsetFromScreenPoint 本実装
    // スクリーン座標 (x, y) 直下の文字オフセットを返す(HitTest 相当)。範囲外は clamp。
    // 既存の OffsetFromClientPoint(UI スレッド専用)を再利用し、
    // RPC スレッドから呼ばれた場合は Invoke で UI スレッドへマーシャリングする。
    int yEdit.Accessibility.IUiaTextHost.OffsetFromScreenPoint(double x, double y)
    {
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return 0;
            return (int)Invoke(new Func<int>(() => ComputeOffsetFromScreenPoint(x, y)));
        }
        return ComputeOffsetFromScreenPoint(x, y);
    }

    private int ComputeOffsetFromScreenPoint(double x, double y)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        // スクリーン→クライアント変換(client 原点は _clientToScreenX/Y)。範囲外は
        // OffsetFromClientPoint 側で「Y<0=先頭視覚行の X」「exhausted=文書末尾」に丸める。
        int clientX = (int)(x - _clientToScreenX);
        int clientY = (int)(y - _clientToScreenY);
        // 負座標はゼロ扱い(文書先頭 0 に落ちる=clamp)。上限は OffsetFromClientPoint が自然に処理。
        if (clientX < 0) clientX = 0;
        if (clientY < 0) clientY = 0;
        int pos = OffsetFromClientPoint(clientX, clientY);
        return Math.Clamp(pos, 0, snap.CharLength);
    }

    // P5 Task 10/11: テスト用フック(Editor.Tests から _lastFrame を観察できるように)
    internal static yEdit.Core.Layout.Frame? TestHook_GetLastFrame(EditorControl c) => c._lastFrame;

    // P5 Task 14 (I-2): live プロパティ Handle は RPC で CreateHandle を誘発し得るためキャッシュ返し。
    nint yEdit.Accessibility.IUiaTextHost.Handle => _hwnd;

    // P5 Task 14 (I-1): Focused は内部で GetFocus() を呼ぶ=RPC スレッドから読むと常に false に落ちる。
    // OnGotFocus/OnLostFocus で管理している _hasFocus キャッシュを返す(v1 ScintillaHost 同形)。
    bool yEdit.Accessibility.IUiaTextHost.HasFocus => _hasFocus;

    int yEdit.Accessibility.IUiaTextHost.ControlTypeId => System.Windows.Automation.ControlType.Document.Id;

    string yEdit.Accessibility.IUiaTextHost.Name => "本文";

    string yEdit.Accessibility.IUiaTextHost.AutomationId => "editor";

    void yEdit.Accessibility.IUiaTextHost.SetFocus()
    {
        // P5 Task 14 (I-3): 破棄後 / Handle 未生成での BeginInvoke による InvalidOperationException を防ぐ
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Focus()));
            return;
        }
        Focus();
    }

    // ==================== P5 Task 8: UIA イベント発火配線 ====================
    // TextChangedEvent / TextSelectionChangedEvent / AutomationFocusChangedEvent を
    // 編集経路(AfterEdit)/移動経路(Set*/MoveCaret*)/フォーカス経路(OnGotFocus・Task 9)
    // の末尾から発火する。UIA プロバイダが未生成のとき(=SR 未接続)や
    // AutomationInteropProvider.ClientsAreListening=false の環境では早期 return。
    // テスト用に強制発火フラグ (TestHook_ForceUiaListen) を持つ。

    internal static bool TestHook_ForceUiaListen { get; set; }
    internal static void TestHook_ResetUiaEventCounts(EditorControl c)
    {
        c._uiaTextChangedCount = c._uiaSelectionChangedCount = c._uiaFocusChangedCount = 0;
    }
    internal static (int textChanged, int selChanged, int focusChanged) TestHook_UiaEventCounts(EditorControl c)
        => (c._uiaTextChangedCount, c._uiaSelectionChangedCount, c._uiaFocusChangedCount);

    /// <summary>UIA イベントを発火する共通ヘルパ。プロバイダ未生成・SR 未リッスン時はスキップ。</summary>
    private void RaiseUia(System.Windows.Automation.AutomationEvent ev)
    {
        if (_provider is null) return;
        if (!TestHook_ForceUiaListen &&
            !System.Windows.Automation.Provider.AutomationInteropProvider.ClientsAreListening) return;
        try
        {
            System.Windows.Automation.Provider.AutomationInteropProvider.RaiseAutomationEvent(
                ev, _provider, new System.Windows.Automation.AutomationEventArgs(ev));
            if (ev == System.Windows.Automation.TextPatternIdentifiers.TextChangedEvent) _uiaTextChangedCount++;
            else if (ev == System.Windows.Automation.TextPatternIdentifiers.TextSelectionChangedEvent) _uiaSelectionChangedCount++;
            else if (ev == System.Windows.Automation.AutomationElementIdentifiers.AutomationFocusChangedEvent) _uiaFocusChangedCount++;
        }
        catch { /* UIA サーバ側の失敗は本体に影響させない */ }
    }
}
