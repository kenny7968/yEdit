using System.ComponentModel;
using System.Runtime.InteropServices;
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
public sealed class EditorControl : Control, yEdit.Accessibility.IUiaTextHost
{
    // Task 13 で ApplyAppearance によりフォント差し替え/GdiCharMetrics 再構築/ViewportStyle 差し替えを
    // 行うため readonly を外した(Font 差し替え時は明示的に古い Font.Dispose を呼ぶ責務)。
    private Font _font;
    // Task 9 レビュー I-1: IME overlay 用の下線フォントは打鍵毎の OnPaint で使う=
    // 毎回 new すると GDI HFONT 割当が積む。_font と寿命同期でキャッシュ(ApplyAppearance で再構築)。
    private Font _underlineFontCache;
    // Task 10: 変換対象節(TargetConverted)用の Underline|Bold フォント。_underlineFontCache と対称に
    // ctor/ApplyAppearance で寿命同期する(GDI HFONT リーク回避=§0-6 リソース管理)。
    private Font _targetFontCache;
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

    // P4 Task 5: IME 未確定文字列の状態。WM_IME_STARTCOMPOSITION 受信で Start=現在キャレットに
    // 初期化し、Task 6 以降の WM_IME_COMPOSITION / WM_IME_ENDCOMPOSITION で Text/Attrs/Clauses を
    // 更新する。IsActive(=Text.Length > 0)の間は「未確定期間」で、外部から <see cref="IsComposing"/>
    // で判定できる。純ロジックは <see cref="ImeCompositionState"/>(P4 Task 2)側。
    private ImeCompositionState _ime = ImeCompositionState.Empty;

    /// <summary>
    /// IME 未確定期間中か。Task 6 以降の描画/イベント発火の分岐に使う(現状 Task 5 では
    /// 常に false=Start だけ立てて Text が空の状態から始まる)。
    /// </summary>
    private bool IsComposing => _ime.IsActive;

    // Task 10: システムキャレットのフォーカス状態フラグ。CreateCaret/DestroyCaret はフォーカスを
    // 持つ間のみ有効なため、SetCaretCharOffset 等から PositionCaret を呼ぶ際にガードに使う。
    private bool _hasFocus;

    // P3 Task 6: 上下移動(Up/Down/PageUp/PageDown)で保持する desired X(px)。
    // -1 = 未計算=次回の垂直移動時に現在キャレット位置から新規計算する(慣例値)。
    // Left/Right/Home/End など水平方向の移動が起きたらリセット=次の垂直移動で再計算される。
    // Task 8 以降の編集経路(挿入/削除等)でも同様に -1 リセットする(§0-6 の一貫性)。
    private int _desiredXpx = -1;

    // P3 Task 13: 前回のキャレット論理行(SetSource で 0 リセット)。
    // <see cref="RaiseCaretEnteredEmptyLineIfNeeded"/> が「行が変わった」判定に使う。
    // 更新経路:
    // - <see cref="RaiseCaretEnteredEmptyLineIfNeeded"/> 内(行遷移検出時)
    // - 公開キャレット/選択 setter(<see cref="SetCaretCharOffset"/> /
    //   <see cref="MoveCaretWithSelection"/> / <see cref="SetSelectionAnchored"/> /
    //   <see cref="SetSelectionCharRange"/>)=App 層 programmatic ジャンプ(検索ジャンプ・
    //   GoTo 等)で空行に飛んだあと、ユーザーが同位置に留まるキーを押しても
    //   spurious fire しないよう強制同期(Task 13 レビュー I-1)。
    // - <see cref="SetSource"/>=0 初期化
    // 編集経路(OnKeyPress / BackSpace/Delete/Enter/Tab/Undo/Redo/Cut/Paste)からは
    // 呼ばないため、編集直後は stale になり得るが、直後の純キャレット移動で新しい行が
    // 空行に着地したときの発火は「新しい行が空か」だけで決まるため stale は害無し。
    private int _lastCaretLine;

    // Task 15: システムキャレットの太さ(px)。既定 2・ApplyAppearance で AppSettings.CaretWidth
    // (1〜5)を反映。弱視のキャレット視認性要件(設計原則 yedit-sighted-users-first-class)。
    private int _caretWidthPx = 2;

    // セルハイライト状態(HighlightCharRange で設定・ClearHighlight で null)。
    // テキスト選択(_anchor/_caret)とは独立した装飾で、単一アクティブ。
    private SelectionRange? _cellHighlight;

    // P3 Task 12: マウスドラッグ選択中フラグ(MouseDown で true・MouseUp / ボタン離した drift で false)。
    // このフラグが立っている間だけ MouseMove がキャレット位置を更新する(=非押下時の drift 無視)。
    private bool _mouseDragging;

    // P3 Task 12: ホイールデルタ蓄積(1 tick = 120)。
    // トラックパッド等の細切れ発火で 40+40+40=120 のように 1 tick を溜めるため、
    // 発火閾値 (>=120 / <=-120) に達したら SystemInformation.MouseWheelScrollLines 行送りを 1 回発動する。
    private int _wheelAccum;

    // P5 Task 5: UIA v2 用 RPC スレッド安全キャッシュ。
    // _bufferSnapshot は不変(TextSnapshot は immutable)なので UI スレッドで参照を差し替えるだけで
    // RPC スレッドは自己整合なスナップショットを読める。編集経路(SetSource/AfterEdit)で更新する。
    // _bounds は WPF Rect のためロック越しで読み書き(参照差替不可の struct)。
    private volatile TextSnapshot? _bufferSnapshot;
    private readonly object _boundsSync = new();
    private System.Windows.Rect _bounds;

    // P5 Task 10: 座標 API 用の描画スナップショット + client→screen オフセットキャッシュ。
    // _lastFrame は OnPaint 完了時点の Frame(描画に使われたもの)。BB 計算では直接使わず、
    // 「最新描画のあと」の判定と Task 11 テスト用フックに使う。
    // _clientToScreenX/Y は OnPaint / UpdateBoundsCache で更新した client 原点のスクリーン座標。
    private volatile yEdit.Core.Layout.Frame? _lastFrame;
    private int _clientToScreenX, _clientToScreenY;

    // P5 Task 14 (I-2): UIA プロバイダは RPC スレッドから Handle を取得する。Control.Handle は
    // Handle 未生成時に CreateHandle を誘発し得るため、OnHandleCreated で捕捉した値をキャッシュ。
    // v1 ScintillaHost._hwnd と同形。
    private nint _hwnd;

    // P6 Task 10 レビュー M-2: CurrentBuffer の null 経路で毎回 new すると
    // Assert.Same(ctrl.CurrentBuffer, ctrl.CurrentBuffer) が SetSource 前で失敗する反直観挙動になる。
    // 空 TextBuffer は immutable な使い方に留める前提(=呼び出し側は Save 読み出し等の read-only 用途)
    // のため、プロセス寿命の静的キャッシュで参照同一性を保証する。
    private static readonly TextBuffer s_emptyBuffer = TextBuffer.FromString(string.Empty);

    // P6 Task 4: 直前の Modified 状態(SavePointLeft 検出用)。AfterEdit で Modified=false→true
    // 遷移を検出して SavePointLeft を発火する。SetSource/ReplaceSource/SetSavePoint で
    // 初期同期する(初回編集後の spurious fire 回避=SetSource 直後のバッファが Modified=true
    // だった場合に AfterEdit なしで SavePointLeft が「打たれてないのに」焚かれないよう、
    // 初期化時点で Modified に合わせる)。
    private bool _wasModified;

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
        _underlineFontCache = new Font(_font, _font.Style | FontStyle.Underline);
        _targetFontCache = new Font(_font, _font.Style | FontStyle.Underline | FontStyle.Bold);   // Task 10
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
        // Task 13: 空行遷移検知の内部状態を初期化(SetSource 直後は _caret=0=行0 なので同期)。
        _lastCaretLine = 0;
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
        if (_hasFocus)
        {
            NativeMethods.CreateCaret(Handle, nint.Zero, _caretWidthPx, _metrics.LineHeightPx);
            PositionCaret();
            NativeMethods.ShowCaret(Handle);
        }
        Invalidate();
        // Task 12: 初期化時に未確定文字列用フォントを IME に通知(候補窓/未確定描画のメトリクス整合)。
        NotifyCompositionFont();
        // P5 Task 5: RPC スレッド用スナップショットキャッシュを初期化
        CacheSnapshot();
        // P6 Task 4: SavePointLeft 検出用の直前状態をバッファに同期
        // (FromString で生まれるバッファは Modified=false 前提だが、既に Modified=true な
        //  バッファを差し込まれた場合に初回 AfterEdit で SavePointLeft が spurious 発火するのを防ぐ)。
        _wasModified = _buffer.Modified;
    }

    /// <summary>
    /// P6 Task 1: 本文全体を string で読み書きする互換 API。
    /// getter は現在の TextBuffer スナップショットから全文を返す(内部 GetText と同じ経路)。
    /// setter は新規 TextBuffer を組み立てて <see cref="SetOrReplaceSource"/> に流す
    /// (Task 10 レビュー M-1: SetSource/ReplaceSource 分岐は 1 箇所に集約)。
    /// SetSource 前 / _buffer=null で getter は空文字列を返し、setter は初回 SetSource として扱う。
    /// </summary>
    /// <remarks>
    /// Control.Text と同名だが、Control.Text は本文非公開原則(§0-6 / P5 Task 7)により
    /// WM_GETTEXT/WM_GETTEXTLENGTH で応答しない=シャドウ new が必要。
    /// </remarks>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new string Text
    {
        get => _buffer?.Current.GetText(0, _buffer.Current.CharLength) ?? string.Empty;
        set => SetOrReplaceSource(TextBuffer.FromString(value ?? string.Empty));
    }

    /// <summary>
    /// P6 Task 1: 既存 TextBuffer を新しいものに差し替える(ファイル開き直し・バックアップ復元用)。
    /// SetSource が 1 度限りなのに対し、これは任意回呼べる=Document ごとに EditorControl を
    /// 作り直すのを避ける。キャレット/選択/スクロール/セル強調/IME 未確定/マウス・ホイール状態/
    /// スナップショットキャッシュをすべてリセットし、UIA TextChangedEvent(および有効時は
    /// TextSelectionChangedEvent)を発火する。
    /// </summary>
    /// <remarks>
    /// <c>SetSource</c> の 2 回目以降相当=バッファ参照の丸ごと差替え(本文の一部置換ではない=
    /// 部分置換は <see cref="ReplaceCharRange"/> を使う)。
    /// </remarks>
    public void ReplaceSource(TextBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        // §4-6: 他の状態変異 API と同じく IME 未確定はまず確定キャンセルする
        if (IsComposing) CancelCompositionAndDefault();
        _buffer = buffer;
        _caret = 0;
        _anchor = 0;
        _topLine = 0;
        _scrollX = 0;
        _lastCaretLine = 0;
        _desiredXpx = -1;
        _cellHighlight = null;      // 旧バッファのオフセット由来のセル強調は無効化
        _mouseDragging = false;     // ドラッグ選択の途中状態を破棄
        _wheelAccum = 0;            // ホイール蓄積(1 tick = 120)をリセット
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
        if (_hasFocus)
        {
            PositionCaret();
        }
        Invalidate();
        CacheSnapshot();  // P5 RPC スレッド用スナップショット更新
        // P6 Task 4: 差し替え後のバッファ状態で SavePointLeft 検出用の直前状態を同期
        // (SetSource と同旨=バッファが Modified=true でも初回 AfterEdit で spurious 発火しないよう)
        _wasModified = buffer.Modified;
        // P5 Task 8 / P6 Task 1: バッファ全差替えは AfterEdit と同じ通知契約
        // (SR/UIA クライアントが旧本文をキャッシュしたままにならないよう発火)
        RaiseUia(System.Windows.Automation.TextPatternIdentifiers.TextChangedEvent);
        if (RaiseUiaSelectionEvents)
            RaiseUia(System.Windows.Automation.TextPatternIdentifiers.TextSelectionChangedEvent);
        // P6 Task 4: バッファ全差替えは App 層のステータスバー更新契機なので UpdateUI 発火
        UpdateUI?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// P6 Task 2: 現在の TextBuffer スナップショットから全文を返す(App 層互換)。
    /// 大容量ファイルでは 64MB 閾値二層化(<see cref="SearchController"/>)で回避されるが、
    /// 呼び出し側でメモリ配慮が必要な場面もある(App 層側で判定=§設計 §2-8)。
    /// </summary>
    public string SnapshotText => _buffer?.Current.GetText(0, _buffer.Current.CharLength) ?? string.Empty;

    /// <summary>
    /// P6 Task 10: <see cref="TextBuffer"/> を差し込む(初回は <see cref="SetSource"/>・
    /// 2 度目以降は <see cref="ReplaceSource"/> に自動振り分け)。<see cref="Text"/> セッターの
    /// TextBuffer 直入れ版=App 層 Stream I/O 経路が string 全文化を経ずにバッファを流し込むための API。
    /// FileController の LoadInto / RestoreFromBackup(=fresh Document への初回差し込み or
    /// 開き直しでの差し替え)で使う。
    /// </summary>
    /// <remarks>
    /// SetSource は 1 度限りの契約(2 度目は <see cref="InvalidOperationException"/>)。
    /// ReplaceSource は _buffer 存在前提のイベント/UI 更新契約(CreateCaret/NotifyCompositionFont を
    /// 打たない)=fresh EditorControl に直接呼ぶとシステムキャレットが未生成のまま残る。
    /// 本メソッドは <see cref="Text"/> セッターの分岐と等価(の string 経由を省いた版)。
    /// </remarks>
    public void SetOrReplaceSource(TextBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_buffer is null) SetSource(buffer);
        else ReplaceSource(buffer);
    }

    /// <summary>
    /// P6 Task 10: 現在の <see cref="TextBuffer"/> 参照を返す(App 層 Stream I/O 経路の Save 対称化用)。
    /// SetSource/ReplaceSource で差し込まれたものをそのまま返す=<see cref="TextFileService.Save(string, TextBuffer, System.Text.Encoding, bool)"/>
    /// と組み合わせて 1GB 級 UTF-8 の string 全文化を回避する契約。null 経路(SetSource 前)では
    /// プロセス寿命の静的空 TextBuffer を返す(常に non-null 保証=呼び出し側で null チェック不要・
    /// 同経路の連続呼び出しで参照同一性も保つ=Task 10 レビュー M-2)。
    /// </summary>
    /// <remarks>
    /// 返す参照は「編集用」ではなく「保存/照会用のスナップショット提供元」の位置付け。
    /// バッファは可変(TextBuffer.Insert/Delete/Replace)なので、返した参照へ外部から書き込むと
    /// EditorControl の内部状態(キャレット/選択/描画)と齟齬が出る=読み取り用途に限る。
    /// null 経路で返す静的空バッファも同様=編集してはならない(プロセス全体で共有)。
    /// </remarks>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TextBuffer CurrentBuffer => _buffer ?? s_emptyBuffer;

    /// <summary>P6 Task 2: 長さベースの選択設定エイリアス(App 層互換)。</summary>
    public void SelectCharRange(int start, int length)
        => SetSelectionCharRange(start, start + Math.Max(0, length));

    /// <summary>
    /// P6 Task 2: <see cref="SetCaretCharOffset"/> のエイリアス(App 層互換)。
    /// <paramref name="offset"/> はキャレット位置の絶対値(現在位置からの差分ではない)。
    /// </summary>
    public void MoveCaretCharOffset(int offset) => SetCaretCharOffset(offset);

    /// <summary>P6 Task 3: 現在のバッファ論理行数(App 層互換=`Lines.Count` 相当)。</summary>
    public int LineCount => _buffer?.Current.LineCount ?? 0;

    /// <summary>P6 Task 3: 指定 0-based 行の先頭にキャレット移動(App 層互換=`Lines[i].Goto()` 相当)。</summary>
    public void GoToLine(int line)
    {
        if (_buffer is null) return;
        var snap = _buffer.Current;
        int clamped = Math.Clamp(line, 0, snap.LineCount - 1);
        SetCaretCharOffset(snap.GetLineStart(clamped));
    }

    /// <summary>P6 Task 3: `CaretCharOffset` の別名(App 層互換=Scintilla の `CurrentPosition`)。</summary>
    public int CurrentPosition => _caret;

    /// <summary>
    /// P6 Task 5: 本文中の改行を <paramref name="eol"/> に一括変換する(App 層互換=保存時の EOL 統一)。
    /// 既存本文の <c>\r\n</c> / <c>\r</c> / <c>\n</c> を検出→指定 EOL に置換した文字列で
    /// <see cref="ReplaceSource(TextBuffer)"/> し、<see cref="EolMode"/> も同時に更新する。
    /// SetSource 前は no-op。
    /// </summary>
    /// <remarks>
    /// <see cref="EolMode"/> は「以後の Enter 押下で挿入する改行」の設定であり、既存本文には
    /// 効かない。App 層(FileController の保存経路)は保存前に本 API で本文の改行を統一する。
    /// 実装は「一旦 LF に正規化 → 目的 EOL に置換」の 2 段階=CRLF/CR/LF 混在を安全に扱える。
    /// no-op fast-path(=すでに目的 EOL で統一されている場合)では ReplaceSource による
    /// キャレット/選択/スクロールリセット・UIA TextChanged 発火を回避する(EolMode だけ更新)。
    /// non fast-path でも「行 index + 改行文字以外の相対 offset」の対で caret/anchor/topLine/
    /// scrollX を保存→復元する(P6 レビュー I-2: Save 毎に caret が先頭へ飛ぶ退行を回避)。
    /// </remarks>
    public void ConvertEols(LineEnding eol)
    {
        if (_buffer is null) return;
        byte[] targetBytes = eol switch
        {
            LineEnding.Crlf => new byte[] { 0x0D, 0x0A },
            LineEnding.Lf => new byte[] { 0x0A },
            LineEnding.Cr => new byte[] { 0x0D },
            _ => new byte[] { 0x0A },
        };
        int targetCharLen = targetBytes.Length;  // ASCII のみ=byte 数 = char 数
        var snap = _buffer.Current;

        // P7 I-3 Task 3: SnapshotText 全文化を撤廃=byte スキャンで fast-path 判定。
        // すでに全 EOL が target で統一されていれば ReplaceSource(キャレット/選択/スクロールリセット
        // + UIA TextChanged 発火)を回避し、EolMode だけ更新して抜ける。
        if (IsEolAlreadyUniform(snap, targetBytes))
        {
            EolMode = eol;
            return;
        }

        // 変換前の caret/anchor を「改行以外の文字数+改行数」で分解=変換後も同じ論理位置を再構成できる。
        // SnapshotReader で chunked 走査(旧実装は SnapshotText 全文化=1GB 級 peak を招いていた)。
        var (caretM, caretK) = CountNonBreakAndBreaksInSnapshot(snap, _caret);
        var (anchorM, anchorK) = CountNonBreakAndBreaksInSnapshot(snap, _anchor);
        int savedTopLine = _topLine;
        int savedScrollX = _scrollX;

        // ピース単位に UTF-8 byte を走査し、EOL(0x0D/0x0A)を target に置換しつつ
        // TextBufferBuilder にストリーム流し込みで新スナップショットを構築。
        // CRLF がピース境界に跨るケースは 1 バイト carry(pendingCr)で吸収。
        var builder = new TextBufferBuilder();
        byte[] outBuf = new byte[64 * 1024];
        int outLen = 0;
        bool pendingCr = false;

        foreach (var piece in PieceTree.Enumerate(snap.Root))
        {
            var span = piece.Chunk.Span.Slice(piece.ByteStart, piece.ByteLen);
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                if (pendingCr)
                {
                    // 前ピース末尾の CR を持ち越し中。今の byte が LF なら CRLF として 1 改行、
                    // それ以外なら CR 単独として 1 改行を吐いてから今の byte を通常処理へ進める。
                    pendingCr = false;
                    if (b == 0x0A)
                    {
                        EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                        continue;
                    }
                    EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                }
                if (b == 0x0D)
                {
                    if (i + 1 < span.Length && span[i + 1] == 0x0A)
                    {
                        // ピース内 CRLF
                        EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                        i++;
                    }
                    else if (i + 1 < span.Length)
                    {
                        // ピース内 CR 単独(次 byte が LF 以外)
                        EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                    }
                    else
                    {
                        // ピース末尾 CR=次ピース先頭を確認しないと CRLF/CR 単独が判別不能。持ち越す。
                        pendingCr = true;
                    }
                }
                else if (b == 0x0A)
                {
                    EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                }
                else
                {
                    // Check-before-write: 直前 EmitEol が outLen==outBuf.Length のまま抜けたケース
                    // (EmitEol の flush 判定は `outLen + eol.Length > outBuf.Length` で = のときは flush しない)
                    // に備えて先に flush する=安全側に統一。
                    if (outLen == outBuf.Length) FlushBuf(ref outBuf, ref outLen, builder);
                    outBuf[outLen++] = b;
                }
            }
        }
        if (pendingCr) EmitEol(targetBytes, ref outBuf, ref outLen, builder);
        if (outLen > 0) FlushBuf(ref outBuf, ref outLen, builder);

        ReplaceSource(builder.Build());
        int total = _buffer!.Current.CharLength;
        _caret = Math.Min(caretM + caretK * targetCharLen, total);
        _anchor = Math.Min(anchorM + anchorK * targetCharLen, total);
        TopLine = savedTopLine;    // setter でクランプ+VScrollBar 同期
        ScrollX = savedScrollX;    // 同上
        EolMode = eol;
    }

    /// <summary>
    /// P7 I-3 Task 3: <paramref name="eol"/> バイト列を出力バッファへ書き足す。バッファ溢れ時は
    /// TextBufferBuilder へフラッシュしてから追記する(bare append より 1 分岐多いだけの hot path)。
    /// </summary>
    private static void EmitEol(byte[] eol, ref byte[] outBuf, ref int outLen, TextBufferBuilder builder)
    {
        if (outLen + eol.Length > outBuf.Length) FlushBuf(ref outBuf, ref outLen, builder);
        for (int i = 0; i < eol.Length; i++) outBuf[outLen++] = eol[i];
    }

    /// <summary>
    /// P7 I-3 Task 3: 出力バッファを TextBufferBuilder に流し込み、outLen をリセット。空なら no-op。
    /// </summary>
    private static void FlushBuf(ref byte[] outBuf, ref int outLen, TextBufferBuilder builder)
    {
        if (outLen == 0) return;
        builder.Add(new ReadOnlySpan<byte>(outBuf, 0, outLen));
        outLen = 0;
    }

    /// <summary>
    /// P7 I-3 Task 3: Snapshot 全 EOL がターゲット EOL と一致するかを byte スキャンで判定
    /// (fast-path 判定用=SnapshotText 全文化を回避)。CRLF/CR/LF 混在(target と異なる EOL が
    /// 1 つでも存在する)なら false=非 fast-path 経路で統一が必要。CR がピース境界に跨るケースは
    /// pendingCr で持ち越す(次ピース先頭が LF なら CRLF、そうでなければ CR 単独)。
    /// </summary>
    private static bool IsEolAlreadyUniform(TextSnapshot snap, byte[] targetBytes)
    {
        bool pendingCr = false;
        foreach (var piece in PieceTree.Enumerate(snap.Root))
        {
            var span = piece.Chunk.Span.Slice(piece.ByteStart, piece.ByteLen);
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                if (pendingCr)
                {
                    pendingCr = false;
                    if (b == 0x0A)
                    {
                        // 前ピース末尾 CR + 当ピース先頭 LF=CRLF。target が CRLF でなければ NG。
                        if (!(targetBytes.Length == 2 && targetBytes[0] == 0x0D && targetBytes[1] == 0x0A)) return false;
                        continue;
                    }
                    // CR 単独。target が CR でなければ NG。今の byte は落とさず後段で処理継続。
                    if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0D)) return false;
                }
                if (b == 0x0D)
                {
                    if (i + 1 < span.Length && span[i + 1] == 0x0A)
                    {
                        if (!(targetBytes.Length == 2 && targetBytes[0] == 0x0D && targetBytes[1] == 0x0A)) return false;
                        i++;
                    }
                    else if (i + 1 < span.Length)
                    {
                        if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0D)) return false;
                    }
                    else pendingCr = true;
                }
                else if (b == 0x0A)
                {
                    if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0A)) return false;
                }
            }
        }
        if (pendingCr)
        {
            // 全 span 走査後に残った CR は文書末尾の単独 CR。target が CR でなければ NG。
            if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0D)) return false;
        }
        return true;
    }

    /// <summary>
    /// P7 I-3 Task 3: [0, <paramref name="pos"/>) に含まれる「改行以外の文字数」と「改行数」を
    /// SnapshotReader(chunked TextReader)で走査して返す。CRLF は 1 改行として数える(旧
    /// <c>CountNonBreakAndBreaks(string, int)</c> と等価)。ConvertEols で char 位置を EOL
    /// 変換前後にマップし直すために使う。8192 char バッファ境界で '\r' が末尾に来るケースは carry で持ち越す。
    /// pos が CRLF の LF を指す(=接頭辞末尾が CR)ケースは \r 単独として 1 改行を計上(境界を跨がない安全側)。
    /// </summary>
    private static (int NonBreakChars, int Breaks) CountNonBreakAndBreaksInSnapshot(TextSnapshot snap, int pos)
    {
        int m = 0, k = 0;
        int p = Math.Min(pos, snap.CharLength);
        if (p == 0) return (0, 0);
        using var reader = snap.CreateReader();
        char[] buf = new char[8192];
        int consumed = 0;
        int carry = -1; // 前ブロック末尾の '\r' を持ち越し
        while (consumed < p)
        {
            int want = Math.Min(buf.Length, p - consumed);
            int n = reader.Read(buf, 0, want);
            if (n == 0) break;
            for (int j = 0; j < n; j++)
            {
                char c = buf[j];
                if (carry >= 0)
                {
                    // 前ブロック末尾の CR。今の char が LF なら CRLF=1 改行、そうでなければ CR 単独=1 改行。
                    if (c == '\n') { k++; consumed++; carry = -1; continue; }
                    k++; carry = -1;
                }
                if (c == '\r')
                {
                    if (j + 1 < n && buf[j + 1] == '\n') { k++; j++; consumed += 2; }
                    else if (j + 1 == n) { carry = '\r'; consumed++; }
                    else { k++; consumed++; }
                }
                else if (c == '\n') { k++; consumed++; }
                else { m++; consumed++; }
            }
        }
        if (carry >= 0) k++;
        return (m, k);
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
    /// <remarks>
    /// P4 Task 8(§4-2): false→true の切り替え時、IME 未確定期間中なら
    /// <see cref="CancelCompositionAndDefault"/> 経路で ImmNotifyIME(CPS_CANCEL) を通知し、
    /// overlay(<c>_ime</c>)を強制的にクリアする(=読み取り専用に切り替えた瞬間に浮きっぱなしの
    /// 未確定文字が残らない)。未確定期間外の呼び出しは早期 return で no-op=既存の全 setter
    /// 呼び出し(P3 の閲覧テスト群)には副作用が無い。
    /// </remarks>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            if (_readOnly == value) return;
            _readOnly = value;
            if (value) CancelCompositionAndDefault();
        }
    }
    private bool _readOnly;

    /// <summary>
    /// Enter 押下時に挿入する改行シーケンス。既定は <see cref="LineEnding.Crlf"/>(Windows 標準)。
    /// App 層 (P6) は開いた文書の実測改行(LineEndingDetector)を反映して設定する想定。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public LineEnding EolMode { get; set; } = LineEnding.Crlf;

    /// <summary>
    /// 純キャレット移動(本文変更なし)で行が変わり、着地行が空行(len=0)のとき発火する。
    /// App 層(P6)がこのイベントを購読して SR 能動発声「空行」を行う予定。
    /// 継承 SR 対策§2-5-3(HANDOFF §4.1)。
    /// </summary>
    /// <remarks>
    /// 発火条件は「純キャレット移動」経路のみ=OnKeyDown の移動系(Left/Right/Up/Down/Home/End/
    /// PageUp/PageDown・shift 併用の選択拡張も含む)と OnMouseDown。
    /// 編集経路(OnKeyPress の文字挿入・BackSpace/Delete/Enter/Tab/Undo/Redo/Cut/Paste)からは
    /// 発火しない=SR は編集経路とは別の通知経路(TextChanged / UIA TextChangedEvent 等)で
    /// 内容変化を読み上げるため、空行能動発声は移動時に限定する設計。
    /// マウスドラッグ末端(OnMouseMove)からも発火しない(Task 12 の申し送り=P7 実機検証で最終判断)。
    /// </remarks>
    public event EventHandler? CaretEnteredEmptyLine;

    // P6 Task 4: App 層互換イベント。TextBuffer の Modified 状態遷移と UI 更新契機を通知する。

    /// <summary>P6 Task 4: <see cref="SetSavePoint"/>(=<see cref="TextBuffer.MarkSaved"/>)呼び出し時に発火(App 層は「保存済み表示」に切替)。</summary>
    public event EventHandler? SavePointReached;

    /// <summary>P6 Task 4: 保存後最初の編集で Modified=false→true 遷移時に発火(App 層は「変更あり」表示に切替)。</summary>
    public event EventHandler? SavePointLeft;

    /// <summary>P6 Task 4: キャレット/選択/表示範囲変化時に発火(App 層のステータスバー更新用)。</summary>
    public event EventHandler? UpdateUI;

    /// <summary>
    /// キャレット/選択移動時の UIA TextSelectionChangedEvent を発火するか。
    /// <b>P3 では受け口のみ</b>(値は読み書きできるが挙動は無し)=P5 の UIA 接続で本挙動化する。
    /// P6 の CSV モードでは false にしてシンクへ移る遷移の一瞬に PC-Talker が行を読むのを防ぐ。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RaiseUiaSelectionEvents { get; set; } = true;

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
    /// 現在キャレットの論理行番号(0 始まり)。SetSource 前は 0(P6 の
    /// <c>ScintillaHost.CurrentLine</c> と同名=App 層互換 API・設計書 §2-8)。
    /// </summary>
    /// <remarks>
    /// 内部フィールド <see cref="_lastCaretLine"/> は「純キャレット移動時の行遷移検知」用で
    /// 編集経路(BackSpace/Delete/Enter/Undo/Redo/Cut/Paste)からは更新されないため公開値には使えない。
    /// 常にスナップショットから引き直す(<see cref="TextSnapshot.GetLineIndexOfChar"/> は O(log N))。
    /// </remarks>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CurrentLine => _buffer is null ? 0 : _buffer.Current.GetLineIndexOfChar(_caret);

    /// <summary>
    /// 指定オフセットの論理行内オフセット(0 始まり)。SetSource 前は 0・範囲外は
    /// [0, CharLength] にクランプ・サロゲート中間位置は前方(high)へスナップ。
    /// P6 の <c>ScintillaHost.GetColumn</c> と同名(App 層互換 API・設計書 §2-8)。
    /// </summary>
    /// <remarks>
    /// 「行内オフセット」= オフセット - その行の GetLineStart。UTF-16 コード単位で数えるため、
    /// タブ幅展開・全角=2 換算などは行わない(Scintilla の SCI_GETCOLUMN は幅展開するが、
    /// 本 API は簡潔さを優先=P6 移植で問題になれば拡張する)。
    /// </remarks>
    public int GetColumn(int offset)
    {
        if (_buffer is null) return 0;
        int snapped = SnapAndClamp(offset);
        var snap = _buffer.Current;
        int line = snap.GetLineIndexOfChar(snapped);
        return snapped - snap.GetLineStart(line);
    }

    /// <summary>
    /// 指定範囲 <c>[start, start+length)</c> を <paramref name="replacement"/> で置換する
    /// (P6 App 層互換 API・設計書 §2-8)。両端はサロゲート中間なら前方スナップ・範囲外は
    /// [0, CharLength] にクランプ。<paramref name="length"/> が負値のときは 0 として扱い挿入となる。
    /// キャレットは置換末尾(<c>start + replacement.Length</c>=snapped 後の値)に移動し
    /// 選択は解除される。<see cref="ReadOnly"/> / SetSource 前は no-op。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.ReplaceCharRange</c> と同名(App 層検索置換・整形機能等からの機械的
    /// 置換用)。Undo/Redo は <see cref="TextBuffer.Replace"/> 経由で 1 単位として積まれる。
    /// クランプは <see cref="HighlightCharRange"/>/<see cref="EnsureVisibleCharRange"/> と同じ流儀=
    /// <c>start + length</c> の int 加算オーバーフローを long 経由で防ぐ。
    /// _desiredXpx リセット・<see cref="AfterEdit"/> 経由の副作用(スクロールバー再計算・
    /// キャレット再配置・追従スクロール・再描画)は編集経路(Task 8〜11)と同じ扱い。
    /// </remarks>
    public void ReplaceCharRange(int start, int length, string replacement)
    {
        if (IsComposing) CancelCompositionAndDefault();
        if (_buffer is null || ReadOnly) return;
        ArgumentNullException.ThrowIfNull(replacement);
        int s = SnapAndClamp(start);
        // start + length は int 加算だとオーバーフローで負値になり得るため long 経由(EnsureVisibleCharRange
        // と同じ流儀)。負の length は 0 として扱う=start 位置への純挿入になる。
        long endLong = (long)start + Math.Max(0, length);
        int endInt = endLong > int.MaxValue ? int.MaxValue : (int)endLong;
        int e = SnapAndClamp(endInt);
        _buffer.Replace(s, e - s, replacement);
        _caret = _anchor = s + replacement.Length;
        _desiredXpx = -1;
        AfterEdit();
    }

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
        if (IsComposing) CancelCompositionAndDefault();
        if (_buffer is null) return;
        int snapped = SnapAndClamp(offset);
        if (_caret == snapped && _anchor == snapped) return;
        _caret = snapped;
        _anchor = snapped;   // 単純キャレット移動は選択解除
        // Task 13 レビュー I-1: _lastCaretLine を同期(App 層 programmatic ジャンプで
        // 空行に飛んだ場合に、後続のユーザー入力で行が変わらないケース=
        // OnKeyDown 移動系末尾の RaiseCaretEnteredEmptyLineIfNeeded が spurious fire
        // するのを防ぐ)。
        _lastCaretLine = _buffer.Current.GetLineIndexOfChar(_caret);
        PositionCaret();
        Invalidate();
        // P5 Task 8: 純粋な選択/キャレット移動での UIA イベント発火
        if (RaiseUiaSelectionEvents)
            RaiseUia(System.Windows.Automation.TextPatternIdentifiers.TextSelectionChangedEvent);
        // P6 Task 4: キャレット位置変化は App 層のステータスバー更新契機
        UpdateUI?.Invoke(this, EventArgs.Empty);
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
        if (IsComposing) CancelCompositionAndDefault();
        if (_buffer is null) return;
        int s = SnapAndClamp(Math.Min(start, end));
        int e = SnapAndClamp(Math.Max(start, end));
        if (_anchor == s && _caret == e) return;
        _anchor = s;
        _caret = e;
        // Task 13 レビュー I-1: _lastCaretLine 同期(SetCaretCharOffset と同旨)。
        _lastCaretLine = _buffer.Current.GetLineIndexOfChar(_caret);
        PositionCaret();
        Invalidate();
        // P5 Task 8: 純粋な選択/キャレット移動での UIA イベント発火
        if (RaiseUiaSelectionEvents)
            RaiseUia(System.Windows.Automation.TextPatternIdentifiers.TextSelectionChangedEvent);
        // P6 Task 4: 選択範囲変化は App 層のステータスバー更新契機
        UpdateUI?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// アンカー保持でキャレットのみを <paramref name="newCaret"/> に移動する(shift+移動系の共通経路)。
    /// サロゲートペア中間位置は前方スナップ・範囲外はクランプ。
    /// <c>newCaret == _anchor</c> のとき選択が消える(=アンカーと同位置)。
    /// SetSource 前の呼び出しは no-op。
    /// </summary>
    public void MoveCaretWithSelection(int newCaret)
    {
        if (IsComposing) CancelCompositionAndDefault();
        if (_buffer is null) return;
        int snapped = SnapAndClamp(newCaret);
        if (_caret == snapped) return;
        _caret = snapped;
        // _anchor は保持
        // Task 13 レビュー I-1: _lastCaretLine 同期(SetCaretCharOffset と同旨)。
        _lastCaretLine = _buffer.Current.GetLineIndexOfChar(_caret);
        PositionCaret();
        Invalidate();
        // P5 Task 8: 純粋な選択/キャレット移動での UIA イベント発火
        if (RaiseUiaSelectionEvents)
            RaiseUia(System.Windows.Automation.TextPatternIdentifiers.TextSelectionChangedEvent);
        // P6 Task 4: shift+移動系の共通経路。App 層のステータスバー更新契機として UpdateUI 発火
        UpdateUI?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// アンカーとキャレットを個別指定して選択範囲を設定する(非対称版)。
    /// <paramref name="anchor"/> &gt; <paramref name="caret"/> のときはキャレットが Min=選択先頭
    /// (=shift+左方向の選択)。両端はサロゲートペア中間位置なら前方スナップ・範囲外はクランプ。
    /// SetSource 前の呼び出しは no-op。
    /// </summary>
    public void SetSelectionAnchored(int anchor, int caret)
    {
        if (IsComposing) CancelCompositionAndDefault();
        if (_buffer is null) return;
        int a = SnapAndClamp(anchor);
        int c = SnapAndClamp(caret);
        if (_anchor == a && _caret == c) return;
        _anchor = a;
        _caret = c;
        // Task 13 レビュー I-1: _lastCaretLine 同期(SetCaretCharOffset と同旨)。
        _lastCaretLine = _buffer.Current.GetLineIndexOfChar(_caret);
        PositionCaret();
        Invalidate();
        // P5 Task 8: 純粋な選択/キャレット移動での UIA イベント発火
        if (RaiseUiaSelectionEvents)
            RaiseUia(System.Windows.Automation.TextPatternIdentifiers.TextSelectionChangedEvent);
        // P6 Task 4: 非対称版の選択範囲変化も App 層のステータスバー更新契機
        UpdateUI?.Invoke(this, EventArgs.Empty);
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
        // 編集経路(Undo/Delete)で buffer が縮んだ結果 _topLine が新 maxLine を超えて
        // 残っているケースを防御的にクランプする。TopLine セッター経由の変更はここへ入る
        // 前にクランプ済みだが、AfterEdit→UpdateVerticalScrollbar の順で来ると
        // 直前の buffer 縮小分が _topLine に反映されていないためここで補正する必要がある
        // (Task 13 EmptyLineNavigationTests の Enter→Undo 経路で顕在化した既存潜在バグ)。
        if (_topLine > maxLine) _topLine = maxLine;
        int visibleLines = Math.Max(1, ClientSize.Height / Math.Max(1, _metrics.LineHeightPx));
        _vscroll.Maximum = maxLine + Math.Max(0, visibleLines - 1);
        _vscroll.LargeChange = visibleLines;
        _vscroll.SmallChange = 1;
        _vscroll.Value = _topLine;
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
        // P5 Task 5: 編集後に RPC スレッド用スナップショットを更新
        CacheSnapshot();
        // P5 Task 8: UIA イベント発火(TextChanged は編集経路の唯一の発火点)。
        // 編集は同時に選択位置も動くため TextSelectionChanged も併せて発火。
        RaiseUia(System.Windows.Automation.TextPatternIdentifiers.TextChangedEvent);
        if (RaiseUiaSelectionEvents)
            RaiseUia(System.Windows.Automation.TextPatternIdentifiers.TextSelectionChangedEvent);
        // P6 Task 4: Modified 遷移(false→true=SavePointLeft / true→false=SavePointReached)を
        // 両方向で発火・常時 UpdateUI を発火。state-first-then-fire で SetSavePoint / ReplaceSource と
        // 揃え、handler 内での再入(SavePointLeft ハンドラが Undo 等で AfterEdit を再呼び出しするケース)
        // でも二重発火させない(§0 設計)。Undo で保存点へ戻る経路も本メソッドが呼ばれるため、
        // SavePointReached を対称に発火しないとタブラベル「*」が消えない実挙動退行(P6 レビュー I-1)。
        bool nowModified = Modified;
        bool shouldFireLeft    = !_wasModified && nowModified;
        bool shouldFireReached =  _wasModified && !nowModified;
        _wasModified = nowModified;
        if (shouldFireLeft)    SavePointLeft?.Invoke(this, EventArgs.Empty);
        if (shouldFireReached) SavePointReached?.Invoke(this, EventArgs.Empty);
        UpdateUI?.Invoke(this, EventArgs.Empty);
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
        if (IsComposing) CancelCompositionAndDefault();   // §4-6(Task 13 レビュー I-1)
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
        if (IsComposing) CancelCompositionAndDefault();   // §4-6(Task 13 レビュー I-1)
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
    public void SetSavePoint()
    {
        _buffer?.MarkSaved();
        // P6 Task 4: 保存直後は「未変更」状態=次の編集が Modified=false→true 遷移として
        // SavePointLeft を発火できるよう _wasModified も同期リセット。
        _wasModified = false;
        SavePointReached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// EmptyUndoBuffer 相当(<see cref="TextBuffer.ClearUndo"/> の別名)。Undo/Redo 履歴を破棄する。
    /// 保存点は維持されるため <see cref="Modified"/> の値は変わらない。SetSource 前は no-op。
    /// P6 の <c>ScintillaHost.EmptyUndoBuffer</c> と同名。
    /// </summary>
    public void EmptyUndoBuffer() => _buffer?.ClearUndo();

    /// <summary>
    /// 選択範囲のテキストをクリップボード(<see cref="TextDataFormat.UnicodeText"/> 固定・設計書 §0-10)へ書き込む。
    /// 選択なしのときは no-op(=クリップボード内容は保持)。本文不変=<see cref="ReadOnly"/> でも動く
    /// (Notepad と同挙動)。SetSource 前は no-op。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.Copy</c> と同名(App 層メニュー配線=<c>_docs.Active?.Editor.Copy()</c>
    /// と機械的置換用)。<see cref="Clipboard.SetText(string, TextDataFormat)"/> は STA 必須=
    /// 本コントロールが WinForms UI スレッド専用契約のため常に満たされる。
    /// 「行末改行がない選択のときは 1 行選択と見なして EOL を付ける」等の Scintilla 独自仕様は
    /// v1 では真似ず、素直に選択文字列だけを扱う(設計書 Task 11)。
    /// </remarks>
    public void Copy()
    {
        if (_buffer is null) return;
        var (s, en) = GetSelectionCharRange();
        if (s == en) return;
        string text = _buffer.Current.GetText(s, en - s);
        Clipboard.SetText(text, TextDataFormat.UnicodeText);
    }

    /// <summary>
    /// 選択範囲のテキストをクリップボードへ書き込み、その範囲を削除する。
    /// <see cref="ReadOnly"/> / 選択なし / SetSource 前は no-op(現行 Scintilla と一致)。
    /// キャレットは削除位置(=元の選択開始オフセット)へ移動し、選択は解除される。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.Cut</c> と同名。<see cref="Copy"/> → <see cref="TextBuffer.Replace"/>
    /// で「クリップボード書き込み → 本文削除」の順に実行する(Copy 失敗時に本文だけ消える事故を
    /// 防ぐ=<see cref="Clipboard.SetText(string, TextDataFormat)"/> が例外を投げると本メソッドも
    /// 上に throw して <see cref="AfterEdit"/> へ到達しない)。
    /// </remarks>
    public void Cut()
    {
        if (IsComposing) CancelCompositionAndDefault();   // §4-6(Task 13 レビュー I-1)
        if (_buffer is null || ReadOnly) return;
        var (s, en) = GetSelectionCharRange();
        if (s == en) return;
        Copy();
        _buffer.Replace(s, en - s, "");
        _caret = _anchor = s;
        _desiredXpx = -1;
        AfterEdit();
    }

    /// <summary>
    /// クリップボードの <see cref="TextDataFormat.UnicodeText"/> をキャレット位置に挿入する。
    /// 選択があるときは置換。挿入後のキャレットは挿入末尾に位置し、選択は解除される。
    /// <see cref="ReadOnly"/> / UnicodeText が無い or 空 / SetSource 前は no-op。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.Paste</c> と同名。<see cref="Clipboard.ContainsText(TextDataFormat)"/>
    /// で先にチェックしても実装差で空文字列を返すケースが理論上残り得るため、防御的に
    /// <c>string.IsNullOrEmpty</c> でも早期 return する(空文字列 Replace は本文不変だが履歴に
    /// 積む副作用があるため避けたい)。
    /// </remarks>
    public void Paste()
    {
        if (IsComposing) CancelCompositionAndDefault();   // §4-6(Task 13 レビュー I-1)
        if (_buffer is null || ReadOnly) return;
        if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) return;
        string text = Clipboard.GetText(TextDataFormat.UnicodeText);
        if (string.IsNullOrEmpty(text)) return;
        var (s, en) = GetSelectionCharRange();
        _buffer.Replace(s, en - s, text);
        _caret = _anchor = s + text.Length;
        _desiredXpx = -1;
        AfterEdit();
    }

    /// <summary>
    /// 文書全体を選択する(<see cref="SelectionAnchor"/>=0・<see cref="CaretCharOffset"/>=CharLength)。
    /// SetSource 前は no-op(CharLength=0 で空選択=<see cref="SetSelectionAnchored"/> が
    /// _buffer null で早期 return)。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.SelectAll</c> と同名。<see cref="Control.SelectAll"/> は
    /// <see cref="TextBoxBase"/> 以下でのみ導入されるため、Control 直接派生の本クラスでは
    /// 隠すべき同名メソッドが無く <c>new</c> キーワード不要。OnKeyDown の Ctrl+A case
    /// (Task 6)は <see cref="SetSelectionAnchored(int, int)"/> を直接呼んでいるため
    /// 本メソッド経由ではないが、App 層メニュー "すべて選択" などから直接呼ばれることを想定。
    /// </remarks>
    public void SelectAll()
        => SetSelectionAnchored(0, _buffer?.Current.CharLength ?? 0);

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

        // P5 Task 9: フォーカス獲得時の UIA イベント明示発火。
        // PC-Talker は 2 秒ポーリングで選択を追う既知挙動(HANDOFF §13.6)があるため、
        // フォーカス獲得時に AutomationFocusChangedEvent + TextSelectionChangedEvent を
        // 明示発火して SR に「今ここにフォーカスがある」と伝える(v1 ScintillaHost 踏襲)。
        RaiseUia(System.Windows.Automation.AutomationElementIdentifiers.AutomationFocusChangedEvent);
        if (RaiseUiaSelectionEvents)
            RaiseUia(System.Windows.Automation.TextPatternIdentifiers.TextSelectionChangedEvent);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        // P4 Task 8(§4-3): 未確定期間中にフォーカスを失う場合、まず IME 側へ
        // CPS_COMPLETE を通知して「確定」を試みる(Scintilla 互換=ユーザーの入力途中を
        // 失わせない)。ImmNotifyIME が届かない環境(IME 無効/取得失敗)でも overlay
        // (_ime)は必ず落として、base の後続処理(_hasFocus=false / DestroyCaret)より前に
        // フィールド状態を整えておく=直後の Invalidate/paint で古い overlay が浮かない。
        if (IsComposing)
        {
            nint hIMC = NativeMethods.ImmGetContext(Handle);
            if (hIMC != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.ImmNotifyIME(hIMC, NativeMethods.NI_COMPOSITIONSTR,
                                               NativeMethods.CPS_COMPLETE, 0);
                }
                finally { NativeMethods.ImmReleaseContext(Handle, hIMC); }
            }
            // ImmNotifyIME が届かない環境の保険=overlay を落とす。
            // base.OnLostFocus / DestroyCaret は再描画を保証しないため Invalidate 明示
            // (Task 8 レビュー I-1・CancelCompositionAndDefault と対称に揃える)。
            _ime = ImeCompositionState.Empty;
            Invalidate();
        }
        base.OnLostFocus(e);
        _hasFocus = false;
        NativeMethods.DestroyCaret();
    }

    /// <summary>
    /// P4 IME 経路。<see cref="OnKeyDown"/>/<see cref="OnKeyPress"/> は書き換えず、WndProc で
    /// WM_IME_* を横取りする(§0-4)。P4 Task 4/5/6/7/8 で WM_IME_SETCONTEXT /
    /// WM_IME_STARTCOMPOSITION / WM_IME_COMPOSITION(GCS_COMPSTR + GCS_RESULTSTR)/
    /// WM_IME_ENDCOMPOSITION を処理済。各 case は必ず <c>return;</c> で終える
    /// (末尾の <c>base.WndProc(ref m)</c> は unhandled 用=<c>return;</c> を忘れると
    /// 二重処理となり、base の既定 IME 挙動が KeyPress を re-post 等して 1 Splice=1 Undo
    /// が崩れる)。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        // P5 Task 6: UIA プロバイダ配線 ---- 先頭で処理
        if (m.Msg == NativeMethods.WM_GETOBJECT)
        {
            long objid = m.LParam.ToInt64();
            if (objid == NativeMethods.UiaRootObjectId)
            {
                _provider ??= new yEdit.Accessibility.TextControlProviderV2(this);
                m.Result = System.Windows.Automation.Provider.AutomationInteropProvider
                    .ReturnRawElementProvider(Handle, m.WParam, m.LParam, _provider);
                _testHook_LastGetObjectServed = true;
                return;
            }
            _testHook_LastGetObjectServed = false;
            // OBJID_CLIENT (=-4) / OBJID_WINDOW (=0) 等は base=DefWindowProc に流す
            // (=自前で MSAA プロキシを作らない=ネイティブ表面原則 §2-7)
        }

        // P5 Task 7: ネイティブ表面原則 = 本文非公開(WM_GETTEXT / WM_GETTEXTLENGTH に応答しない)
        if (m.Msg == NativeMethods.WM_GETTEXT || m.Msg == NativeMethods.WM_GETTEXTLENGTH)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        switch (m.Msg)
        {
            case NativeMethods.WM_IME_SETCONTEXT:
                OnImeSetContext(ref m);
                return;
            case NativeMethods.WM_IME_STARTCOMPOSITION:
                OnImeStartComposition();
                m.Result = IntPtr.Zero;
                return;
            case NativeMethods.WM_IME_COMPOSITION:
                OnImeComposition(m.LParam.ToInt64());
                m.Result = IntPtr.Zero;
                return;
            case NativeMethods.WM_IME_ENDCOMPOSITION:
                OnImeEndComposition();
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    // P5 Task 6: UIA プロバイダ(v2)は WM_GETOBJECT(UiaRootObjectId)で lazy 生成する。
    // インスタンスの寿命は EditorControl と同じ(Dispose で解放不要=マネージ参照のみ)。
    private yEdit.Accessibility.TextControlProviderV2? _provider;

    // Task 6 テスト用フック: WndProc 経路と self-served 判定を Editor.Tests から観察する。
    private bool _testHook_LastGetObjectServed;
    internal static void TestHook_WndProc(EditorControl c, ref Message m) => c.WndProc(ref m);
    internal static bool TestHook_LastGetObjectServed(EditorControl c) => c._testHook_LastGetObjectServed;

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

    // テスト用ヘルパ(internal・EditorControlImeTests から呼ぶ)。
    // WndProc は protected のためテストから直接呼べない=WM_IME_SETCONTEXT の lParam
    // マスク挙動を検証するための最小の受け口。
    internal void __TestProcessMessage(ref Message m) => WndProc(ref m);

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
    ///
    /// P4 Task 11: <see cref="IsComposing"/> 中は「未確定文字列内の IME カーソル位置」
    /// (<c>_ime.Start + _ime.CursorPos</c>)へキャレットを置く=IME 内で左右矢印を押した
    /// ときにシステムキャレットが追従するようにする。CursorPos の prefix 幅は
    /// <see cref="_underlineFontCache"/>(=Task 9 で描画に使う overlay フォント)で
    /// <see cref="TextRenderer.MeasureText"/> して加算する(<see cref="DrawImeOverlay"/>
    /// と同じ font/flags で測ることで、描画上の位置とピクセル整合を取る)。
    ///
    /// <para>Perf 注記: 未確定中のみ <see cref="Control.CreateGraphics"/> を都度作る=
    /// 未確定期間は入力の合間で相対的に短いため v1 では許容。将来 <c>_metrics</c> に
    /// 未確定文字列用の Measure API を持たせる余地がある(計画書 §Task 11 Follow-ups)。</para>
    /// </summary>
    private void PositionCaret()
    {
        if (!_hasFocus || _buffer is null) return;

        // P4 Task 11: 未確定中は IME 内カーソル位置 (_ime.Start + _ime.CursorPos) に SetCaretPos。
        // 非 IME 経路の前に分岐させる(視覚的にキャレット位置を反映する、というセマンティクスは同じ)。
        if (IsComposing)
        {
            var (x, y, visible) = ComputeCaretPoint(_ime.Start);
            if (!visible)
            {
                // 非 IME 経路と対称: 不可視時は画面外に退避してゴースト残留を防ぐ(Task 11 レビュー M-2)。
                NativeMethods.SetCaretPos(-1000, -1000);
                return;
            }
            // _ime.CursorPos は SnapCursorPos で 0..Text.Length にクランプ済(Task 2/6)だが、
            // 悪意/誤動作 IME 対策として範囲外を防御的にクランプ(0 なら prefix="" で幅 0=OK)。
            int cur = Math.Clamp(_ime.CursorPos, 0, _ime.Text.Length);
            string prefix = _ime.Text[..cur];
            using var g = CreateGraphics();
            Size sz = TextRenderer.MeasureText(g, prefix, _underlineFontCache,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            NativeMethods.SetCaretPos(x - _scrollX + sz.Width, y);
            // Task 12: スクロール変更等で IME 行の client 座標が動いた=候補窓も追従させる。
            // NotifyCandidateWindow は自前で ComputeCaretPoint を呼び直すため、visible 分岐は
            // ここで先に済んでいるが二重呼びは低コスト(未確定中のみ・GetContext 1 回)。
            NotifyCandidateWindow();
            return;
        }

        var (cx, cy, cvisible) = ComputeCaretPoint(_caret);
        if (cvisible) NativeMethods.SetCaretPos(cx - _scrollX, cy);
        else NativeMethods.SetCaretPos(-1000, -1000);
    }

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
    /// desiredXpx は水平方向の移動系と同じ扱いで -1 リセット(次の Up/Down で再計算)。
    /// BreakUndoCoalescing は「純キャレット移動をまたいだ連続タイピングの分割」で
    /// OnKeyDown の移動系と同じ流儀(Scintilla 互換)。
    /// </remarks>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (IsComposing) CancelCompositionAndDefault();
        base.OnMouseDown(e);
        if (_buffer is null || e.Button != MouseButtons.Left) return;
        Focus();
        // Task 13 レビュー I-1: setter で _lastCaretLine が同期されるため、「前の行」を setter
        // 呼び出し前に捕獲して Raise に渡す(OnKeyDown 移動系末尾と同旨)。
        int fromLine = _buffer.Current.GetLineIndexOfChar(_caret);
        int target = OffsetFromClientPoint(e.X, e.Y);
        bool shift = (ModifierKeys & Keys.Shift) != 0;
        if (shift)
            MoveCaretWithSelection(target);
        else
            SetCaretCharOffset(target);
        _mouseDragging = true;
        _desiredXpx = -1;
        _buffer.BreakUndoCoalescing();
        BringCaretIntoView();
        RaiseCaretEnteredEmptyLineIfNeeded(fromLine);
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
        _desiredXpx = -1;
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
    /// - <c>BringCaretIntoView()</c> / <c>RaiseCaretEnteredEmptyLineIfNeeded()</c> は Task 7/13 で
    ///   本実装。Task 6 では**呼び出し箇所を確定させる**ためスタブ(no-op)で置く。
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
        // P5 Task 12: Ctrl+←→ 単語ナビの App 層補完受け口(WordNavigatedEvent)発火判定。
        // 選択拡張中(shift)は発火しない=PC-Talker が「単語スパンで読み上げ」を期待するのは
        // 純単語移動のみ。判定は case Keys.Left/Right の分岐で行う。
        bool wordNavCandidate = false;

        switch (e.KeyCode)
        {
            case Keys.Left:
                target = ctrl ? WordBoundary.PrevWordStart(snap, _caret)
                              : NavigationCommands.MoveLeftChar(snap, _caret);
                resetDesired = true;
                if (ctrl && !shift) wordNavCandidate = true;
                break;
            case Keys.Right:
                target = ctrl ? WordBoundary.NextWordStart(snap, _caret)
                              : NavigationCommands.MoveRightChar(snap, _caret);
                resetDesired = true;
                if (ctrl && !shift) wordNavCandidate = true;
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
            // Task 13 レビュー I-1: setter は _lastCaretLine を同期する(App 層 programmatic
            // ジャンプ由来の spurious fire 抑止のため)ため、Raise が比較する「前の行」を
            // setter 呼び出し前に捕獲しておく必要がある(setter 呼び出し後だと常に一致してしまう)。
            int fromLine = snap.GetLineIndexOfChar(_caret);
            int beforeCaret = _caret;
            if (resetDesired) _desiredXpx = -1;
            if (shift) MoveCaretWithSelection(t2);
            else SetCaretCharOffset(t2);
            _buffer.BreakUndoCoalescing();          // 純キャレット移動は coalescing 破断
            BringCaretIntoView();
            RaiseCaretEnteredEmptyLineIfNeeded(fromLine);
            // P5 Task 12: 純単語ナビ(Ctrl+←→ かつ非 shift)で移動が起きたら WordNavigated 発火
            if (wordNavCandidate && beforeCaret != _caret)
                RaiseWordNavigated(Math.Min(beforeCaret, _caret), Math.Max(beforeCaret, _caret));
            e.Handled = true;
        }
    }

    /// <summary>
    /// Ctrl+←→ で単語ナビが発生したとき発火(選択拡張中でない場合のみ)。
    /// P0 で確定した「PC-Talker Ctrl+←→ 単語ナビの App 層補完受け口」(P5 Task 12)。
    /// UIA の TextSelectionChanged と同じく <see cref="RaiseUiaSelectionEvents"/>=false で抑止される。
    /// </summary>
    public event System.EventHandler<WordNavigatedEventArgs>? WordNavigated;

    private void RaiseWordNavigated(int wordStart, int wordEnd)
    {
        if (!RaiseUiaSelectionEvents) return;
        WordNavigated?.Invoke(this, new WordNavigatedEventArgs(wordStart, wordEnd));
    }

    // P5 Task 12: OnKeyDown をテストから直接叩くための internal 静的フック
    // (App 層の入力経路を通さず、Ctrl+←→ 系の分岐だけ検証できる)。
    internal static void TestHook_SendKey(EditorControl c, Keys keyData)
        => c.OnKeyDown(new KeyEventArgs(keyData));

    /// <summary>
    /// 文字挿入(WM_CHAR)入り口(P3 Task 8 / P4 Task 3 で <see cref="InsertConfirmedText"/> へ委譲)。
    /// 制御文字を弾いてから確定 1 文字を <see cref="InsertConfirmedText"/> に流し
    /// P4 の IME 確定経路(WM_IME_COMPOSITION の GCS_RESULTSTR)と挿入本体を共有する。
    /// </summary>
    /// <remarks>
    /// <b>制御文字は無視</b>: WM_CHAR は Ctrl 修飾の 0x01〜0x1F(Ctrl+A=0x01 等)や
    /// Ctrl+Backspace の 0x7F(ASCII DEL・Task 8 レビュー I-1)も届くため、これらは除外する。
    /// 編集操作(BackSpace/Enter/Tab/Ctrl+A 等)は <c>OnKeyDown</c> 経路(Task 6/9)で処理済み。
    /// 選択/上書き/サロゲートの分岐と <c>_desiredXpx</c>/AfterEdit の後処理は
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
    /// - <c>_desiredXpx</c> は -1 リセット(P3 §0-6)、AfterEdit で追従スクロール
    /// </remarks>
    private void InsertConfirmedText(string text)
    {
        if (_buffer is null || ReadOnly) return;
        if (string.IsNullOrEmpty(text)) return;

        var (s, en) = GetSelectionCharRange();
        if (s != en)
        {
            _buffer.Replace(s, en - s, text);
            _caret = _anchor = s + text.Length;
        }
        else if (Overtype)
        {
            var snap = _buffer.Current;
            int overwriteLen = 0;
            if (_caret < snap.CharLength)
            {
                char nc = snap.GetChar(_caret);
                if (nc != '\r' && nc != '\n')
                {
                    overwriteLen = (char.IsHighSurrogate(nc) && _caret + 1 < snap.CharLength
                                    && char.IsLowSurrogate(snap.GetChar(_caret + 1))) ? 2 : 1;
                }
            }
            _buffer.Replace(_caret, overwriteLen, text);
            _caret = _anchor = _caret + text.Length;
        }
        else
        {
            _buffer.Insert(_caret, text);
            _caret = _anchor = _caret + text.Length;
        }

        _desiredXpx = -1;
        AfterEdit();
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
    /// 純キャレット移動後に呼ばれる(P3 Task 13)。<paramref name="fromLine"/> と現在の
    /// キャレット論理行が異なり、着地行が空行(len=0)のとき <see cref="CaretEnteredEmptyLine"/>
    /// を発火する。編集経路(OnKeyPress / BackSpace/Delete/Enter/Tab/Undo/Redo/Cut/Paste)からは
    /// <b>呼ばない</b>(=SR は編集経路とは別の通知経路で内容変化を読み上げる=§0-7 の純キャレット
    /// 移動時のみ発火)。
    /// </summary>
    /// <param name="fromLine">移動 <b>前</b> のキャレット論理行(呼び出し側が setter 呼び出し前に
    /// 捕獲する)。setter が <see cref="_lastCaretLine"/> を同期する(Task 13 レビュー I-1)ため、
    /// このフィールドを比較対象に使うと OnKeyDown → SetCaretCharOffset の直後で常に一致してしまい
    /// 発火しない。呼び出し側で「前の行」を明示的に渡すことでこの結合を切る。</param>
    /// <remarks>
    /// 呼び出し箇所は 2 つ:
    /// <list type="bullet">
    /// <item><description><see cref="OnKeyDown"/> の移動系末尾(target==int の後)。shift 併用の
    /// 選択拡張経路からも呼ぶ=「選択拡張中に空行に到達した」ケースでも SR に通知する
    /// (設計原則: 呼び出し側で発火可否を決めない=無/有選択に関わらず「行遷移して空行に着地」
    /// なら発火。実運用で選択拡張中の発声が煩わしければ P7 実機検証で条件追加を検討)。</description></item>
    /// <item><description><see cref="OnMouseDown"/> の末尾。左クリックで別行の空行に着地したときも
    /// 発火する。ドラッグ末端(<see cref="OnMouseMove"/>)からは呼ばない(申し送り=P7 実機評価)。</description></item>
    /// </list>
    /// SetSource 前は <c>_buffer is null</c> で早期 return=フォーカスや UI 初期化順序に依存しない。
    /// </remarks>
    private void RaiseCaretEnteredEmptyLineIfNeeded(int fromLine)
    {
        if (_buffer is null) return;
        var snap = _buffer.Current;
        int toLine = snap.GetLineIndexOfChar(_caret);
        if (toLine == fromLine) return;
        _lastCaretLine = toLine;
        int lineLen = snap.GetLineEnd(toLine, includeBreak: false) - snap.GetLineStart(toLine);
        if (lineLen == 0)
            CaretEnteredEmptyLine?.Invoke(this, EventArgs.Empty);
    }

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
        _underlineFontCache.Dispose();
        _targetFontCache.Dispose();       // Task 10
        _font = newFont;
        _underlineFontCache = new Font(_font, _font.Style | FontStyle.Underline);
        _targetFontCache = new Font(_font, _font.Style | FontStyle.Underline | FontStyle.Bold);   // Task 10
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
        // Task 12: フォント変更後に IME へ未確定文字列用フォントを再通知(本文と候補窓のメトリクス整合)。
        NotifyCompositionFont();
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
        return snap.GetLineStart(line);
    }

    int yEdit.Accessibility.IUiaTextHost.LineEnd(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        int line = snap.GetLineIndexOfChar(o);
        if (line + 1 < snap.LineCount) return snap.GetLineStart(line + 1);
        return snap.CharLength;
    }

    int yEdit.Accessibility.IUiaTextHost.LineEndNoBreakOf(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null) return 0;
        int e = ((yEdit.Accessibility.IUiaTextHost)this).LineEnd(offset);
        // CRLF 混在対応: LF → CR の順で剥がす
        if (e > 0 && snap.GetChar(e - 1) == '\n') e--;
        if (e > 0 && snap.GetChar(e - 1) == '\r') e--;
        return e;
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

    private int _uiaTextChangedCount, _uiaSelectionChangedCount, _uiaFocusChangedCount;

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

    /// <summary>
    /// GDI ハンドル(Font)を解放する。P6 でタブ毎にインスタンス生成/破棄する運用のため、
    /// 生存中に確保した Font が Control 破棄時に必ず解放されるようにする。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            // Task 10: ApplyAppearance と対称に IME overlay 用フォントも解放する
            // (Task 9 で追加した _underlineFontCache は Dispose 追加漏れの補正込み・§0-6)。
            _underlineFontCache.Dispose();
            _targetFontCache.Dispose();
        }
        base.Dispose(disposing);
    }
}
