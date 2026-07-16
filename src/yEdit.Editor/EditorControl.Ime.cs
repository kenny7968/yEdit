// EditorControl.Ime.cs
// Phase 2 (Task 2a) で切り出した IME 分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3-3a で ImeController へロジック移譲予定。
using System.Runtime.InteropServices;
using yEdit.Core.Editing;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    /// <summary>
    /// WM_IME_ENDCOMPOSITION: 未確定期間の終端。<c>_ime</c> を Empty へリセットし、overlay を
    /// 再描画で消す(P4 Task 8)。ApplyResult(=GCS_RESULTSTR)ですでに空へ落ちている場合でも
    /// 冗長 no-op になるだけで害はない=このメッセージは「未確定期間の終端イベント」であり、
    /// 取消経路(ESC=空 RESULTSTR + ENDCOMPOSITION)からも呼ばれるため無条件クリアが正しい。
    /// </summary>
    private void OnImeEndComposition()
    {
        _ime = ImeCompositionState.Empty;
        Invalidate();
    }

    /// <summary>
    /// 未確定期間を強制取消しし、IME 側にも <see cref="NativeMethods.CPS_CANCEL"/> を通知する
    /// (P4 Task 8・§4-2 縁ケース共通経路)。<see cref="ReadOnly"/> の true 切替が現時点の
    /// 主な呼び出し元。<see cref="NativeMethods.ImmNotifyIME"/> が失敗する環境(IME 無効/
    /// 取得失敗)でも overlay(<c>_ime</c>)は必ずクリアする=UI 側で浮きっぱなしの未確定が
    /// 残らない。<see cref="IsComposing"/> が false ならただちに戻る(=既存の閲覧テストで
    /// ReadOnly=true に切り替えても副作用が発生しない)。
    /// </summary>
    private void CancelCompositionAndDefault()
    {
        if (!IsComposing) return;
        nint hIMC = NativeMethods.ImmGetContext(Handle);
        if (hIMC != IntPtr.Zero)
        {
            try
            {
                NativeMethods.ImmNotifyIME(hIMC, NativeMethods.NI_COMPOSITIONSTR,
                                           NativeMethods.CPS_CANCEL, 0);
            }
            finally { NativeMethods.ImmReleaseContext(Handle, hIMC); }
        }
        _ime = ImeCompositionState.Empty;
        Invalidate();
    }

    /// <summary>
    /// WM_IME_SETCONTEXT: IME の既定 composition ウィンドウ描画を止めて自前描画に切り替える(P4)。
    /// lParam の ISC_SHOWUICOMPOSITIONWINDOW ビットを落として base.WndProc に流す。
    /// </summary>
    /// <remarks>
    /// ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000。int のままだと符号ビットになるため
    /// long に一旦上げてビットマスクを適用し、nint に戻す(nint(long) は truncate ではなく
    /// プラットフォームサイズへ符号拡張/縮小=x86/x64 双方で意図どおり)。
    /// </remarks>
    private void OnImeSetContext(ref Message m)
    {
        long lp = m.LParam.ToInt64();
        lp &= ~(long)NativeMethods.ISC_SHOWUICOMPOSITIONWINDOW;
        m.LParam = new nint(lp);
        base.WndProc(ref m);
    }

    /// <summary>
    /// WM_IME_STARTCOMPOSITION: 未確定期間の開始。選択があれば削除して先頭位置をキャレットに寄せ、
    /// <c>_ime.Start</c> を現在のキャレットで初期化する(P4 Task 5)。
    /// </summary>
    /// <remarks>
    /// - 選択削除は 1 Splice=1 Undo=以後の未確定文字列(Task 6 で overlay 描画)は Undo に積まれない
    ///   ことを守るため、Start より前の状態でここまでにバッファへ確定させておく。
    /// - <see cref="ReadOnly"/> / SetSource 前は no-op(WM_IME_COMPOSITION は来ない前提だが、
    ///   WM_IME_STARTCOMPOSITION は IME 側の都合で先に来ることがあるため防御的にガードする)。
    /// - <c>_desiredXpx = -1</c> / <see cref="AfterEdit"/> は選択削除が起きたときだけ呼ぶ
    ///   (本文不変ならスクロールバー/追従スクロールは動かす必要が無い)。
    /// - <c>ImmSetCandidateWindow</c> の初期位置設定は Task 12 に集約するため、ここでは触らない。
    /// </remarks>
    private void OnImeStartComposition()
    {
        if (_buffer is null || ReadOnly) return;
        var (s, en) = GetSelectionCharRange();
        if (s != en)
        {
            // 選択削除(1 Splice=1 Undo・以後の未確定は overlay=Undo に積まれない)
            _buffer.Replace(s, en - s, "");
            _caret = _anchor = s;
            _desiredXpx = -1;
            AfterEdit();
        }
        _ime = ImeCompositionState.Empty with { Start = _caret };
        // Task 11 レビュー I-1: SetCaretPos を IME 経路から発火する。ここで PositionCaret を
        // 呼ばないと、_ime.CursorPos の変更に OS 側キャレットが追従しない=SR が IME 内の
        // 移動を読めない。Task 12 で NotifyCandidateWindow が PositionCaret に相乗りするため
        // ここでの明示 NotifyCandidateWindow は不要(Task 12 レビュー I-1: この時点は
        // IsComposing=false=候補窓通知はガードで早期 return する上、初回 ApplyComposition
        // 時に PositionCaret 経由で発火する=初期位置は取れる)。
        PositionCaret();
        Invalidate();
    }

    /// <summary>
    /// WM_IME_COMPOSITION: lParam の GCS_ フラグ束を見て未確定/確定文字列を反映する
    /// (P4 Task 6/7)。GCS_COMPSTR/GCS_COMPATTR/GCS_COMPCLAUSE/GCS_CURSORPOS の 4 種は
    /// 未確定期間の更新=<see cref="ApplyComposition"/> 経由で <c>_ime</c> に載せる。
    /// GCS_RESULTSTR(確定文字列)は <see cref="ApplyResult"/> 経由で
    /// <see cref="InsertConfirmedText"/> に流し、単発 <see cref="TextBuffer.Insert"/> によって
    /// 1 Splice=1 Undo になる。節ハイライトの描画本体は Task 10・キャレット位置反映は
    /// Task 11 で扱う=本タスクは <c>_ime</c> フィールドへの取り込み + 確定挿入まで。
    /// </summary>
    /// <remarks>
    /// - <see cref="ReadOnly"/> / SetSource 前は no-op(<see cref="OnImeStartComposition"/> と同じ防御)。
    /// - <see cref="NativeMethods.ImmGetContext"/> の返却は必ず try/finally で
    ///   <see cref="NativeMethods.ImmReleaseContext"/> する(§0-6=ハンドルリーク防止)。
    /// - GCS_COMPSTR と GCS_RESULTSTR が同一 lParam に同時に立つケース(IME 実装による)は
    ///   仕様上あり得るが、Task 7 では独立に評価する=順序は COMPSTR → RESULTSTR
    ///   (RESULTSTR が来ていれば ApplyResult で <c>_ime</c> がクリアされて overlay は落ちる)。
    /// </remarks>
    private void OnImeComposition(long gcsFlags)
    {
        if (_buffer is null || ReadOnly) return;
        nint hIMC = NativeMethods.ImmGetContext(Handle);
        if (hIMC == IntPtr.Zero) return;
        try
        {
            if ((gcsFlags & NativeMethods.GCS_COMPSTR) != 0)
            {
                string compStr = ReadImeString(hIMC, NativeMethods.GCS_COMPSTR);
                byte[] attrs = (gcsFlags & NativeMethods.GCS_COMPATTR) != 0
                    ? ImeCompositionState.ParseAttrs(ReadImeBytes(hIMC, NativeMethods.GCS_COMPATTR))
                    : [];
                int[] clauses = (gcsFlags & NativeMethods.GCS_COMPCLAUSE) != 0
                    ? ImeCompositionState.ParseClauses(ReadImeBytes(hIMC, NativeMethods.GCS_COMPCLAUSE))
                    : [];
                int cursor = (gcsFlags & NativeMethods.GCS_CURSORPOS) != 0
                    ? ImeCompositionState.SnapCursorPos(compStr, ReadImeInt(hIMC, NativeMethods.GCS_CURSORPOS))
                    : 0;
                ApplyComposition(compStr, cursor, attrs, clauses);
            }
            if ((gcsFlags & NativeMethods.GCS_RESULTSTR) != 0)
            {
                string resultStr = ReadImeString(hIMC, NativeMethods.GCS_RESULTSTR);
                ApplyResult(resultStr);
            }
        }
        finally
        {
            NativeMethods.ImmReleaseContext(Handle, hIMC);
        }
    }

    /// <summary>
    /// 確定文字列の適用本体(Task 7)。テストは <see cref="__TestApplyResult"/> 経由でここを呼ぶ。
    /// </summary>
    /// <remarks>
    /// 順序(§0-2 の 1 Splice=1 Undo 契約):
    /// - <b>先に</b> <c>_ime = ImeCompositionState.Empty</c> で overlay を落とす
    ///   =この直後の <see cref="InsertConfirmedText"/> が <see cref="AfterEdit"/> 内で
    ///   Invalidate/OnPaint を誘発しても、overlay の旧 Start に基づいて確定済みの本文の上へ
    ///   偽の未確定描画が重ならないようにする。
    /// - 空文字列(<c>text.Length == 0</c>=ESC 取消時の GCS_RESULTSTR)は何も挿入しない
    ///   (仕様は length ベース=<see cref="string.IsNullOrEmpty"/> と結果は同じだが契約は
    ///   「長さ 0」で表す)。InsertConfirmedText 内にも同旨のガードがあるが、
    ///   ここで早期返却することで「空文字なら AfterEdit も走らせない=履歴に副作用なし」
    ///   を明示する。
    /// - <see cref="InsertConfirmedText"/> は単一 <see cref="TextBuffer.Insert"/>(または
    ///   <see cref="TextBuffer.Replace"/>=選択削除時のみ)で 1 Splice=1 Undo になる。
    ///   Coalescing を分割する <see cref="TextBuffer.BreakUndoCoalescing"/> は呼ばない
    ///   (=次以降の GCS_RESULTSTR やユーザータイピングとの結合可否は TextBuffer 側の規約に任せる)。
    /// - 最後の <see cref="Control.Invalidate()"/> は overlay を消した再描画。
    ///   InsertConfirmedText 経路が非空文字時は <see cref="AfterEdit"/> 内で Invalidate 済みだが、
    ///   空文字経路でも overlay 消去分の再描画が必要なため、無条件で呼ぶ。
    /// </remarks>
    private void ApplyResult(string text)
    {
        _ime = ImeCompositionState.Empty;   // overlay を先に外して Insert 経路と競合させない
        if (text.Length > 0) InsertConfirmedText(text);
        Invalidate();
    }

    /// <summary>
    /// 未確定文字列の適用本体(Task 6)。テストは <see cref="__TestApplyComposition"/> 経由でここを呼ぶ。
    /// </summary>
    /// <remarks>
    /// 多重メッセージ自己防衛(§0 4-9): <see cref="ImeCompositionState.IsActive"/> が true(=既に
    /// 未確定期間中)なら既存の <c>Start</c> を維持し、そうでなければ現在のキャレット
    /// (<see cref="OnImeStartComposition"/> が事前に凍結済み)から取り直す。これにより
    /// STARTCOMPOSITION → 複数回の COMPOSITION の途中で Start が上書きされない。
    /// </remarks>
    private void ApplyComposition(string text, int cursorPos, byte[] attrs, int[] clauses)
    {
        _ime = new ImeCompositionState(
            Start: _ime.IsActive ? _ime.Start : _caret,
            Text: text,
            CursorPos: cursorPos,
            Attrs: attrs,
            Clauses: clauses);
        // Task 11 レビュー I-1: _ime.CursorPos の反映=IME 内キャレット追従。
        // NotifyCandidateWindow は PositionCaret の IME 分岐末尾で発火する経路に集約する
        // (Task 12 レビュー I-2: 明示的な二重発火を避けて 1 経路に統一)。
        PositionCaret();
        Invalidate();
    }

    // Imm* からの文字列/バイト列読み出しヘルパ(Task 6)。
    // ImmGetCompositionStringW は「lpBuf=IntPtr.Zero, dwBufLen=0」で必要バイト数を返し、
    // 二回目の呼び出しで実データをコピーする 2 段階 API。
    private static string ReadImeString(nint hIMC, int gcsFlag)
    {
        int byteLen = NativeMethods.ImmGetCompositionStringW(hIMC, gcsFlag, IntPtr.Zero, 0);
        if (byteLen <= 0) return "";
        nint buf = Marshal.AllocHGlobal(byteLen);
        try
        {
            NativeMethods.ImmGetCompositionStringW(hIMC, gcsFlag, buf, byteLen);
            // byteLen は UTF-16 のバイト数=char 数は byteLen / 2。
            return Marshal.PtrToStringUni(buf, byteLen / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static byte[] ReadImeBytes(nint hIMC, int gcsFlag)
    {
        int byteLen = NativeMethods.ImmGetCompositionStringW(hIMC, gcsFlag, IntPtr.Zero, 0);
        if (byteLen <= 0) return [];
        nint buf = Marshal.AllocHGlobal(byteLen);
        try
        {
            NativeMethods.ImmGetCompositionStringW(hIMC, gcsFlag, buf, byteLen);
            var arr = new byte[byteLen];
            Marshal.Copy(buf, arr, 0, byteLen);
            return arr;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static int ReadImeInt(nint hIMC, int gcsFlag)
    {
        // GCS_CURSORPOS は「戻り値そのものが値」(バッファは使わない)。
        return NativeMethods.ImmGetCompositionStringW(hIMC, gcsFlag, IntPtr.Zero, 0);
    }

    // P4 Task 5〜8: IME 未確定状態を検査するためのテスト用アクセサ(internal・
    // EditorControlImeTests から呼ぶ)。__Test* 規約で命名。
    // 本文の取得は既存の internal GetText()(line ~854)をテスト側で呼ぶ規約に統一する
    // (P3 まで 55 箇所超で使用中の規約=__TestSnapshotText を新設せず既存に合流)。
    internal int __TestImeStart() => _ime.Start;
    internal bool __TestIsComposing() => IsComposing;
    internal string __TestImeText() => _ime.Text;

    // P4 Task 14: smoke(yEdit.Editor.Smoke)専用アクセサ。__Test* と挙動は同じだが、
    // 「テストからの検査経路」と「smoke タイトルバー表示用の状態ポーリング経路」を
    // 名前で分けておくと、将来テスト用 __Test* の意味付けを変えても smoke が壊れない。
    // Smoke MainForm の 200ms Timer からポーリング(UpdateTitle)で呼ぶ。
    internal bool __SmokeIsComposing() => IsComposing;
    internal string __SmokeImeText() => _ime.Text;

    // Task 6: 実 IME を介さずに ApplyComposition の状態遷移だけを検証するための受け口。
    // Windows API を経由する経路(WndProc → OnImeComposition → Imm* → ApplyComposition)は
    // Task 14 の smoke で扱う。
    internal void __TestApplyComposition(string text, int cursorPos, byte[] attrs, int[] clauses)
        => ApplyComposition(text, cursorPos, attrs, clauses);

    // Task 7: 実 IME を介さずに ApplyResult(=GCS_RESULTSTR 確定通知)の適用だけを検証する受け口。
    // OnImeComposition → Imm* → ApplyResult の全経路は Task 14 の smoke で扱う。
    internal void __TestApplyResult(string text) => ApplyResult(text);

    /// <summary>
    /// P4 Task 12: 未確定文字列のキャレット行下端座標を <c>ImmSetCandidateWindow</c> で IME に
    /// 通知し、候補ウィンドウの表示位置をキャレット直下に追従させる。呼び出し 3 タイミング:
    /// <list type="bullet">
    ///   <item><see cref="OnImeStartComposition"/> 末尾(未確定開始時=初期位置)</item>
    ///   <item><see cref="ApplyComposition"/> 末尾(未確定更新時=キャレット行が変わる可能性)</item>
    ///   <item><see cref="PositionCaret"/> IME 分岐末尾(スクロール/文書更新で座標が動いたとき)</item>
    /// </list>
    /// 座標は <see cref="ComputeCaretPoint"/>(未確定 Start 位置)の client 座標 -
    /// <c>_scrollX</c> に <c>_metrics.LineHeightPx</c> を足した点(=キャレット行の直下)。
    /// <c>_scrollX</c> の減算は <see cref="DrawImeOverlay"/>/<see cref="PositionCaret"/> と対称に。
    /// 可視外なら no-op(旧位置に置きっぱなしにする=候補窓のゴースト移動を避ける)。
    /// IME 無効環境(<see cref="NativeMethods.ImmGetContext"/> が NULL を返す)は無害に no-op。
    /// </summary>
    private void NotifyCandidateWindow()
    {
        if (!IsComposing || _buffer is null || !_hasFocus) return;
        nint hIMC = NativeMethods.ImmGetContext(Handle);
        if (hIMC == IntPtr.Zero) return;
        try
        {
            var (x, y, visible) = ComputeCaretPoint(_ime.Start);
            if (!visible) return;
            var form = new NativeMethods.CANDIDATEFORM
            {
                dwIndex = 0,
                dwStyle = NativeMethods.CFS_CANDIDATEPOS,
                ptCurrentPos = new NativeMethods.POINT { x = x - _scrollX, y = y + _metrics.LineHeightPx },
            };
            NativeMethods.ImmSetCandidateWindow(hIMC, ref form);
        }
        finally { NativeMethods.ImmReleaseContext(Handle, hIMC); }
    }

    /// <summary>
    /// P4 Task 12: 未確定文字列に使うフォントを <c>ImmSetCompositionFontW</c> で IME に通知する
    /// (候補窓の座標整合=本文フォントと候補窓/未確定文字列のメトリクスを揃える)。
    /// 呼び出し 2 タイミング: <see cref="SetSource"/> 末尾(初期化時)/
    /// <see cref="ApplyAppearance"/> 末尾(フォント変更後)。
    /// </summary>
    /// <remarks>
    /// <see cref="Font.ToLogFont(object)"/> は引数を boxing 経由でしか変異させないため、
    /// ローカル struct を直接渡すと参照ではなく boxed コピーだけが書かれてローカルは 0 のまま
    /// =フォント伝達が壊れる(Task 1 レビュー watchpoint)。明示的に box → 変異 → unbox する。
    /// </remarks>
    private void NotifyCompositionFont()
    {
        nint hIMC = NativeMethods.ImmGetContext(Handle);
        if (hIMC == IntPtr.Zero) return;
        try
        {
            object boxed = new NativeMethods.LOGFONT();
            _font.ToLogFont(boxed);
            var lf = (NativeMethods.LOGFONT)boxed;
            NativeMethods.ImmSetCompositionFontW(hIMC, ref lf);
        }
        finally { NativeMethods.ImmReleaseContext(Handle, hIMC); }
    }
}
