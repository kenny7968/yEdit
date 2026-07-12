using System.Text.RegularExpressions;
using yEdit.App.Speech;
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
    private readonly IAnnouncer _announcer;
    private FindReplaceDialog? _dialog;
    private MatchSpan? _lastHit; // 直前に選択したヒット（ゼロ幅でも前進できるよう歩進に使う）
    private (int Start, int End)? _selectionScope; // 「選択範囲のみ」ON 時に捕捉した置換対象範囲

    public SearchController(DocumentManager docs, Form owner, IAnnouncer announcer)
    {
        _docs = docs;
        _owner = owner;
        _announcer = announcer;
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

    /// <summary>次を検索。ヒットして選択を移動できたら true、それ以外(未ヒット/無効式/タイムアウト)は false。</summary>
    public bool FindNext() => Find(forward: true);
    /// <summary>前を検索。ヒットして選択を移動できたら true、それ以外(未ヒット/無効式/タイムアウト)は false。</summary>
    public bool FindPrev() => Find(forward: false);

    private bool Find(bool forward)
    {
        var ed = ActiveEditor;
        var opts = CurrentOptions();
        if (ed is null || opts is null) return false;
        var searcher = new SnapshotSearcher(opts);
        if (!searcher.IsValid) { Announce("正規表現が正しくありません"); return false; }

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
                return false;
            }

            ed.SelectCharRange(hit.Value.Start, hit.Value.Length);
            _lastHit = hit;
            var loc = searcher.Locate(snap, hit.Value);
            // 位置不明（Locate 失敗）時は空メッセージ＝ステータスのクリアのみ（発声なし）。
            Announce(loc is { } l ? $"{l.Total} 件中 {l.Ordinal} 件目" : "");
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            Announce("検索式が複雑すぎます");
            return false;
        }
    }

    /// <summary>現ヒット未選択なら次を検索して即置換、選択済なら置換して次へ(VSCode 準拠)。</summary>
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

            // G-3 修正: 現ヒット未選択なら次を検索してそのまま即置換する(VSCode 準拠)。
            // 未ヒットの前進先が見つからない場合は Find と同じ「これ以上見つかりません」で終了。
            if (repl is null)
            {
                var next0 = searcher.FindNext(snap, selEnd);
                if (next0 is null) { Announce("これ以上見つかりません"); return; }
                var replCand = searcher.ReplacementAt(snap, next0.Value, d.Replacement);
                // ここは通常到達しない(直前の FindNext ヒットに対して同一 snap/searcher で
                // ReplacementAt が null を返すのは異常系)。防御としてユーザーへ明示する。
                if (replCand is null) { Announce("置換できません"); return; }
                span = next0.Value;
                repl = replCand;
            }

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
    /// 視覚だけ更新したい増分カウント等は Announce ではなくダイアログの SetStatus を使う。
    /// P7/P8 申し送り: G-2 で「次を検索」後にダイアログを Hide するため、Hidden な _dialog を
    /// 経由せず MainForm 共有 Announcer 直接発声で経路を整理。実挙動は不変（元々同じ Announcer）。</summary>
    internal void Announce(string message) => _announcer.Say(message);
}
