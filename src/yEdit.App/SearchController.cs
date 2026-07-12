using System.Text.RegularExpressions;
using yEdit.Core.Csv;
using yEdit.Core.Search;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 検索・置換の統括。Core 照合と EditorControl の選択/置換を仲介し、結果を
/// ダイアログのステータス＋UIA 通知で SR に伝える。対象はアクティブ文書を毎回解決する。
/// </summary>
public sealed class SearchController
{
    private readonly DocumentManager _docs;
    private readonly Form _owner;
    private FindReplaceDialog? _dialog;
    private MatchSpan? _lastHit; // 直前に選択したヒット（ゼロ幅でも前進できるよう歩進に使う）
    private (int Start, int End)? _selectionScope; // 「選択範囲のみ」ON 時に捕捉した置換対象範囲

    public SearchController(DocumentManager docs, Form owner)
    {
        _docs = docs;
        _owner = owner;
        _docs.ActiveDocumentChanged += (_, _) =>
        {
            _lastHit = null;                              // 別文書の歩進状態を持ち越さない
            _selectionScope = null;                       // 別文書へ切替時は捕捉済みスコープも無効化
            if (_dialog?.Visible == true) UpdateCount();  // 表示中なら新アクティブで件数を更新
        };
    }

    private EditorControl? ActiveEditor => _docs.Active?.Editor;

    // CSVモード中は本文が読取専用で置換が無反映になるため、置換系を抑止して誤成功通知を防ぐ。
    private bool IsCsvModeActive => _docs.Active?.State.CsvMode == true;

    public void OpenFind() => Open(replaceMode: false);
    public void OpenReplace() => Open(replaceMode: true);

    private void Open(bool replaceMode)
    {
        if (_dialog is null || _dialog.IsDisposed) _dialog = new FindReplaceDialog(this);
        _dialog.SetMode(replaceMode);
        if (!_dialog.Visible) _dialog.Show(_owner);
        _dialog.Activate();
        _dialog.FocusPattern();
        UpdateCount();
    }

    private SearchOptions? CurrentOptions()
    {
        var d = _dialog;
        if (d is null || string.IsNullOrEmpty(d.Pattern)) return null;
        return new SearchOptions(d.Pattern, d.MatchCase, d.WholeWord, d.UseRegex);
    }

    /// <summary>増分カウント（移動しない）。エラー/タイムアウトはステータスのみ更新（通知しない）。</summary>
    public void UpdateCount()
    {
        var d = _dialog;
        if (d is null) return;
        var opts = CurrentOptions();
        if (opts is null) { d.SetStatus(""); return; }
        var searcher = new SnapshotSearcher(opts);
        if (!searcher.IsValid) { d.SetStatus("正規表現が正しくありません"); return; }
        // P6 Task 11: SnapshotText 経由の全文 string 化を回避し、64MB 閾値二層化(閾値超は窓/行照合)。
        // CurrentBuffer は non-null 保証(SetSource 前も静的空 TextBuffer=Task 10 M-2)。
        // ActiveEditor が null(文書なし)なら "見つかりません" 相当。
        var snap = ActiveEditor?.CurrentBuffer.Current;
        try
        {
            int n = snap is null ? 0 : searcher.Count(snap);
            d.SetStatus(n == 0 ? "見つかりません" : $"{n} 件");
        }
        catch (RegexMatchTimeoutException) { d.SetStatus("検索式が複雑すぎます"); }
    }

    public void FindNext() => Find(forward: true);
    public void FindPrev() => Find(forward: false);

    private void Find(bool forward)
    {
        var ed = ActiveEditor;
        var opts = CurrentOptions();
        if (ed is null || opts is null) return;
        var searcher = new SnapshotSearcher(opts);
        if (!searcher.IsValid) { Announce("正規表現が正しくありません"); return; }

        // P6 Task 11: 全文 string 化を避け、TextSnapshot を直接渡す(閾値超は窓/行照合に自動切替)。
        var snap = ed.CurrentBuffer.Current;
        var (selStart, selEnd) = ed.GetSelectionCharRange();
        try
        {
            MatchSpan? hit;
            if (forward)
            {
                int from = (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
                    ? h.Start + Math.Max(1, h.Length)   // 直前ヒットの次へ（ゼロ幅でも前進）
                    : selEnd;
                hit = searcher.FindNext(snap, from);
            }
            else
            {
                int before = (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
                    ? h.Start
                    : selStart;
                hit = searcher.FindPrev(snap, before);
            }

            if (hit is null)
            {
                _lastHit = null;
                Announce("これ以上見つかりません");
                return;
            }

            ed.SelectCharRange(hit.Value.Start, hit.Value.Length);
            _lastHit = hit;
            var loc = searcher.Locate(snap, hit.Value);
            // 位置不明（Locate 失敗）時は空メッセージ＝ステータスのクリアのみ（発声なし）。
            Announce(loc is { } l ? $"{l.Total} 件中 {l.Ordinal} 件目" : "");
        }
        catch (RegexMatchTimeoutException)
        {
            Announce("検索式が複雑すぎます");
        }
    }

    /// <summary>現在の選択が今のヒットなら置換し次へ。違えばまず次を検索（標準の置換動作）。</summary>
    public void ReplaceOne()
    {
        var ed = ActiveEditor;
        var opts = CurrentOptions();
        var d = _dialog;
        if (ed is null || opts is null || d is null) return;
        if (IsCsvModeActive) { Announce(CsvAnnounceFormatter.BlockedInCsvMode); return; }
        var searcher = new SnapshotSearcher(opts);
        if (!searcher.IsValid) { Announce("正規表現が正しくありません"); return; }

        try
        {
            // P6 Task 11: 現在バッファの Snapshot を直接渡す(閾値超は窓/行照合に自動切替)。
            var snap = ed.CurrentBuffer.Current;
            var (selStart, selEnd) = ed.GetSelectionCharRange();
            var span = new MatchSpan(selStart, selEnd - selStart);
            string? repl = selEnd > selStart ? searcher.ReplacementAt(snap, span, d.Replacement) : null;

            if (repl is null) { Find(forward: true); return; } // まだヒット未選択 → 次を検索

            ed.ReplaceCharRange(span.Start, span.Length, repl);
            var snap2 = ed.CurrentBuffer.Current;
            // repl が non-null＝マッチ長>0 が保証されるため Max(1,…) は不要。空置換（削除）
            // のとき +1 すると置換直後の隣接ヒットを取りこぼすので素の repl.Length（0含む）で前進する。
            var next = searcher.FindNext(snap2, span.Start + repl.Length);
            if (next is null)
            {
                _lastHit = null;
                Announce("置換しました。これ以上見つかりません");
                return;
            }
            ed.SelectCharRange(next.Value.Start, next.Value.Length);
            _lastHit = next;
            var loc = searcher.Locate(snap2, next.Value);
            Announce(loc is { } l ? $"置換しました。{l.Total} 件中 {l.Ordinal} 件目" : "置換しました");
        }
        catch (RegexMatchTimeoutException)
        {
            Announce("検索式が複雑すぎます");
        }
    }

    /// <summary>「選択範囲のみ」トグル時に対象範囲を捕捉/破棄する（find 移動でクロバーされないよう保持）。</summary>
    public void OnInSelectionToggled(bool on)
    {
        if (on && ActiveEditor is { } ed)
        {
            var (s, e) = ed.GetSelectionCharRange();
            _selectionScope = e > s ? (s, e) : null;
        }
        else
        {
            _selectionScope = null;
        }
        UpdateCount();
    }

    /// <summary>全文（または選択範囲のみ）を一括置換し件数を通知する。</summary>
    public void ReplaceAll()
    {
        var ed = ActiveEditor;
        var opts = CurrentOptions();
        var d = _dialog;
        if (ed is null || opts is null || d is null) return;
        if (IsCsvModeActive) { Announce(CsvAnnounceFormatter.BlockedInCsvMode); return; }
        var searcher = new SnapshotSearcher(opts);
        if (!searcher.IsValid) { Announce("正規表現が正しくありません"); return; }

        try
        {
            // P6 Task 11: 全文 string 化を避け Snapshot を直接渡す(閾値超は窓/行照合に自動切替)。
            var snap = ed.CurrentBuffer.Current;
            int rangeStart, rangeLen;
            if (d.InSelection)
            {
                if (_selectionScope is not { } scope)
                {
                    Announce("選択範囲がありません");
                    return;
                }
                rangeStart = scope.Start; rangeLen = scope.End - scope.Start;
            }
            else { rangeStart = 0; rangeLen = snap.CharLength; }

            var (fragment, count) = searcher.ReplaceInRange(snap, rangeStart, rangeLen, d.Replacement);
            if (count == 0) { Announce("見つかりません"); return; }
            ed.ReplaceCharRange(rangeStart, rangeLen, fragment);
            _lastHit = null;
            Announce($"{count} 件置換しました");
        }
        catch (RegexMatchTimeoutException)
        {
            Announce("検索式が複雑すぎます");
        }
    }

    /// <summary>ステータス Label を更新しつつ SR にライブ通知（Say 契約: 空は視覚クリアのみ・発声なし）。
    /// 視覚だけ更新したい増分カウント等は Announce ではなくダイアログの SetStatus を使う。</summary>
    internal void Announce(string message) => _dialog?.RaiseNotification(message);
}
