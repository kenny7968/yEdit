using yEdit.Core.Csv;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 新CSVモード（グリッド型ナビゲーション）の配線。CSVモード中は ScintillaHost.ReadOnly=true で
/// 本文を編集不可にし、素キーのコマンドでセル移動・読み上げを行う。現在セルは
/// DocumentState.CsvRow/CsvCol を真実源にする。
/// 目的: NVDA はネイティブ Scintilla 統合（クラス名 "Scintilla"・UIA 非依存）で、OS の
/// フォーカス獲得・キャレット移動・選択変更に反応して生バッファを読み上げる。これは
/// アプリ側では抑止できないため、CSVモード中はフォーカス自体を Document.CsvSink
/// （1×1px のフォーカスシンク）へ退避して全経路を遮断し、読み上げを Announcer に一本化する。
/// システムキャレットも動かさない（可視域スクロールはキャレット無移動の
/// EnsureVisibleCharRange）。RaiseUiaSelectionEvents=false は PC-Talker（UIA 経路）向けの
/// 防御（シンクへ移る遷移の一瞬に OnGotFocus の明示イベントで読まれるのを防ぐ）。
/// F2 は CsvCellEditor に委譲し、終了時の復帰先もシンクにする。
/// </summary>
public sealed class CsvController
{
    private readonly DocumentManager _docs;
    private readonly IAnnouncer _announcer;
    private readonly CsvCellEditor _editor = new();
    private string? _cachedText;
    private CsvDocument? _cachedDoc;

    public CsvController(DocumentManager docs, IAnnouncer announcer)
    {
        _docs = docs;
        _announcer = announcer;
    }

    /// <summary>F2 編集オーバーレイ表示中か（MainForm がキー横取りを抑止するのに使う）。</summary>
    public bool IsEditing => _editor.IsEditing;

    /// <summary>進行中の F2 編集を強制破棄する（タブ閉じ/切替時に呼ぶ・冪等）。</summary>
    public void AbortEdit() => _editor.Abort();

    /// <summary>CSVモードを手動でトグルする。ON 時は読取専用化＋現在セルを確定して読み上げ。</summary>
    public void ToggleMode()
    {
        var doc = _docs.Active;
        if (doc is null || _editor.IsEditing) return;

        if (!doc.State.CsvMode)
        {
            var csv = ParseCached(doc.Editor);
            if (!csv.Ok) { _announcer.Say(CsvAnnounceFormatter.ParseError); return; } // 解析不可ならモードに入らない
            doc.State.CsvMode = true;
            doc.Editor.ReadOnly = true;
            // PC-Talker（UIA経路）向け防御: シンクへ移る遷移の一瞬にエディタがフォーカスを
            // 得た際、OnGotFocus の明示 TextSelectionChangedEvent で行を読まれるのを防ぐ。
            doc.Editor.RaiseUiaSelectionEvents = false;
            if (csv.Rows.Count == 0)
            {
                doc.Editor.ClearHighlight();
                doc.CsvSink.Focus();               // データ無しでもフォーカスはシンクへ退避する
                _announcer.Say(CsvAnnounceFormatter.ModeOn);
                return;
            }
            // ON 時のみ、その時点のキャレット位置から初期セルを導出する（以降はキャレットではなく状態を真実源にする）。
            var (row, col) = csv.FindCell(doc.Editor.CaretCharOffset);
            doc.State.CsvRow = row;
            doc.State.CsvCol = col;
            ApplyCell(doc.Editor, csv, row, col, announce: false);   // ハイライト＋スクロール＋シンクへフォーカス
            var f = csv.GetField(row, col);
            _announcer.Say(f is null
                ? CsvAnnounceFormatter.ModeOn
                : CsvAnnounceFormatter.ModeOn + " " + CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
        }
        else
        {
            var csv = ParseCached(doc.Editor);
            doc.State.CsvMode = false;                 // 先に解除（エディタ GotFocus のシンク退避ガードを外す）
            doc.Editor.ReadOnly = false;
            doc.Editor.ClearHighlight();
            // モード中に動かなかったキャレットを最終セル位置へ復帰させ、編集領域へフォーカスを返す。
            // 以降は通常編集なので、SR がフォーカス獲得で現在行を読むのは標準挙動として許容。
            if (csv.Ok && csv.Rows.Count > 0)
            {
                var f = csv.GetField(doc.State.CsvRow, doc.State.CsvCol);
                if (f is not null) doc.Editor.MoveCaretCharOffset(f.Start);
            }
            // キャレット復帰の後に再有効化し、SR への通知をフォーカス獲得時の1イベントに絞る
            // （通常編集の SR 挙動へ復帰。先に有効化するとシンクフォーカス中のキャレット移動が
            // 余分な TextSelectionChangedEvent を発火し、PC-Talker の二重読みを招く）。
            doc.Editor.RaiseUiaSelectionEvents = true;
            doc.Editor.Focus();
            _announcer.Say(CsvAnnounceFormatter.ModeOff);
        }
    }

    // ---- 移動（読み上げ付き） ----
    public void Move(Direction dir)
    {
        if (!TryContext(out var ed, out var csv, out var row, out var col)) return;
        var t = csv.MoveCell(row, col, dir);
        if (t is null) { _announcer.Say(EdgeMessage(dir)); return; }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    public void MoveRowStart()    { if (TryContext(out var ed, out var csv, out var r, out _)) ApplyTarget(ed, csv, csv.RowStart(r)); }
    public void MoveRowEnd()      { if (TryContext(out var ed, out var csv, out var r, out _)) ApplyTarget(ed, csv, csv.RowEnd(r)); }
    public void MoveColumnTop()   { if (TryContext(out var ed, out var csv, out _, out var c)) ApplyTarget(ed, csv, csv.ColumnTop(c)); }
    public void MoveColumnBottom(){ if (TryContext(out var ed, out var csv, out _, out var c)) ApplyTarget(ed, csv, csv.ColumnBottom(c)); }
    public void MoveTopLeft()     { if (TryContext(out var ed, out var csv, out _, out _))     ApplyTarget(ed, csv, csv.TopLeft()); }
    public void MoveBottomRight() { if (TryContext(out var ed, out var csv, out _, out _))     ApplyTarget(ed, csv, csv.BottomRight()); }

    /// <summary>セル指定移動（G）。「行,列」入力ボックス→範囲検証→移動。</summary>
    public void GoToCell()
    {
        if (!TryContext(out var ed, out var csv, out var row, out var col)) return;
        using var dlg = new CsvGoToCellDialog(row + 1, col + 1);
        if (dlg.ShowDialog(ed.FindForm()) != DialogResult.OK) return;
        if (!dlg.TryGetCell(out int r1, out int c1)) { _announcer.Say(CsvAnnounceFormatter.BadCellFormat); return; }
        var t = csv.GoTo(r1 - 1, c1 - 1);
        if (t is null) { _announcer.Say(CsvAnnounceFormatter.OutOfRange); return; }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    // ---- 読み上げのみ（移動なし） ----
    /// <summary>現在セルを読み上げる（Tab）。</summary>
    public void ReadCurrent()
    {
        if (!TryContext(out _, out var csv, out var row, out var col)) return;
        var f = csv.GetField(row, col);
        if (f is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
    }

    /// <summary>現在列の最上段セルを読み上げる（C）。</summary>
    public void ReadColumnTop()
    {
        if (!TryContext(out _, out var csv, out _, out var col)) return;
        var t = csv.ColumnTop(col);
        var f = t is null ? null : csv.GetField(t.Value.row, t.Value.col);
        _announcer.Say(CsvAnnounceFormatter.Header(f?.Value ?? ""));
    }

    /// <summary>現在行の左端セルを読み上げる（R）。</summary>
    public void ReadRowHead()
    {
        if (!TryContext(out _, out var csv, out var row, out _)) return;
        var f = csv.GetField(row, 0);
        _announcer.Say(CsvAnnounceFormatter.Header(f?.Value ?? ""));
    }

    /// <summary>現在セルを F2 編集する。確定で CSV 直列化→本文反映→再ハイライト＋読み上げ。</summary>
    public void BeginEdit()
    {
        if (_editor.IsEditing) return;
        if (!TryContext(out var ed, out var csv, out var row, out var col)) return;
        // 開始時点のセル span（直列化対象）を確定。row/col はセル内容変更では不変。
        // csv は TryContext がメモ化済みの現在パース（=開始時点のスナップショット）。
        var f = csv.GetField(row, col);
        if (f is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        int start = f.Start, length = f.Length;
        // オーバーレイの配置座標（PointFromCharOffset）は可視領域基準なので、
        // ナビ後にリサイズ等で当該セルが視野外へずれていた場合に備えて明示的に可視化する。
        ed.EnsureVisibleCharRange(start, length);

        _editor.Begin(ed, f, _docs.Active!.CsvSink,   // TryContext 成功時は Active 非 null
            onCommit: text =>
            {
                string serialized = CsvWriter.EscapeField(text);
                bool wasRo = ed.ReadOnly;
                ed.ReadOnly = false;
                ed.ReplaceCharRange(start, length, serialized);
                ed.ReadOnly = wasRo;
                var csv2 = ParseCached(ed);
                if (csv2.Ok) ApplyCell(ed, csv2, row, col, announce: true);
                else _announcer.Say(CsvAnnounceFormatter.ParseError);
            },
            onCancel: () =>
            {
                var csv2 = ParseCached(ed);
                if (csv2.Ok && csv2.Rows.Count > 0) ApplyCell(ed, csv2, row, col, announce: false);
            });
    }

    // ==================== 内部 ====================

    /// <summary>アクティブが CSV モードならそのエディタ、でなければ null。</summary>
    private ScintillaHost? ActiveCsvEditor()
    {
        var doc = _docs.Active;
        return (doc is not null && doc.State.CsvMode) ? doc.Editor : null;
    }

    /// <summary>SnapshotText の参照同一性でメモ化したパース。編集で _snapshot が差し替わると自動失効。</summary>
    private CsvDocument ParseCached(ScintillaHost ed)
    {
        var text = ed.SnapshotText;
        if (!ReferenceEquals(text, _cachedText)) { _cachedDoc = CsvParser.Parse(text); _cachedText = text; }
        return _cachedDoc!;
    }

    /// <summary>パースして現在 (row,col) を得る。CSVでない/解析不可/データ無しは読み上げて false。
    /// (row,col) は DocumentState を真実源とし、パース結果の行列数へクランプする（本文編集で
    /// 行/列が減っても範囲外を指さないように補正）。</summary>
    private bool TryContext(out ScintillaHost ed, out CsvDocument csv, out int row, out int col)
    {
        ed = null!; csv = null!; row = 0; col = 0;
        if (_editor.IsEditing) return false;   // F2 編集中はメニュー経由のナビ/読み上げを抑止（マウス経路の保護）
        var doc = _docs.Active;
        if (doc is null || !doc.State.CsvMode) return false;
        ed = doc.Editor;
        csv = ParseCached(ed);
        if (!csv.Ok) { ed.ClearHighlight(); _announcer.Say(CsvAnnounceFormatter.ParseError); return false; }
        if (csv.Rows.Count == 0) { ed.ClearHighlight(); _announcer.Say(CsvAnnounceFormatter.NoData); return false; }
        row = ClampRow(csv, doc.State.CsvRow);
        col = ClampCol(csv, row, doc.State.CsvCol);
        doc.State.CsvRow = row;                          // クランプ結果を書き戻し（次回以降の整合）
        doc.State.CsvCol = col;
        return true;
    }

    private static int ClampRow(CsvDocument csv, int r) =>
        r < 0 ? 0 : (r >= csv.Rows.Count ? csv.Rows.Count - 1 : r);
    private static int ClampCol(CsvDocument csv, int row, int c)
    {
        int w = csv.Rows[row].Count;
        if (w <= 0) return 0;
        return c < 0 ? 0 : (c >= w ? w - 1 : c);
    }

    private void ApplyTarget(ScintillaHost ed, CsvDocument csv, (int row, int col)? t)
    {
        if (t is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    /// <summary>(row,col) のセルへ ハイライト＋可視域スクロール＋DocumentState 更新＋必要なら読み上げ。
    /// システムキャレットは動かさない（SR の自動読み上げ発火を避け、Announcer 一本に集約する）。
    /// フォーカスはフォーカスシンクに退避したまま維持する（FocusTarget 経由）。</summary>
    private void ApplyCell(ScintillaHost ed, CsvDocument csv, int row, int col, bool announce)
    {
        var f = csv.GetField(row, col);
        if (f is null) { _announcer.Say(CsvAnnounceFormatter.CannotMove); return; }
        ed.HighlightCharRange(f.Start, f.Length);
        ed.EnsureVisibleCharRange(f.Start, f.Length);
        var doc = _docs.Active;
        if (doc is not null)
        {
            doc.State.CsvRow = row;
            doc.State.CsvCol = col;
            doc.FocusTarget.Focus();   // CSVモード中はシンク（Scintilla に SR のフォーカス読みを向けない）
        }
        if (announce) _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
    }

    private static string EdgeMessage(Direction dir) => dir switch
    {
        Direction.Left => CsvAnnounceFormatter.LeftEdge,
        Direction.Right => CsvAnnounceFormatter.RightEdge,
        Direction.Up => CsvAnnounceFormatter.TopEdge,
        _ => CsvAnnounceFormatter.BottomEdge,
    };
}
