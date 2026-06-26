using System.Text.RegularExpressions;
using yEdit.Core.Search;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 検索・置換の統括。Core 照合と ScintillaHost の選択/置換を仲介し、結果を
/// ダイアログのステータス＋UIA 通知で SR に伝える。対象はアクティブ文書を毎回解決する。
/// </summary>
public sealed class SearchController
{
    private readonly DocumentManager _docs;
    private readonly Form _owner;
    private FindReplaceDialog? _dialog;
    private MatchSpan? _lastHit; // 直前に選択したヒット（ゼロ幅でも前進できるよう歩進に使う）

    public SearchController(DocumentManager docs, Form owner)
    {
        _docs = docs;
        _owner = owner;
    }

    private ScintillaHost? ActiveEditor => _docs.Active?.Editor;

    public void OpenFind() => Open(replaceMode: false);
    public void OpenReplace() => Open(replaceMode: true);

    private void Open(bool replaceMode)
    {
        _dialog ??= new FindReplaceDialog(this);
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
        var searcher = new TextSearcher(opts);
        if (!searcher.IsValid) { d.SetStatus("正規表現が正しくありません"); return; }
        string text = ActiveEditor?.SnapshotText ?? "";
        try
        {
            int n = searcher.Count(text);
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
        var searcher = new TextSearcher(opts);
        if (!searcher.IsValid) { Announce("正規表現が正しくありません"); return; }

        string text = ed.SnapshotText;
        var (selStart, selEnd) = ed.GetSelectionCharRange();
        try
        {
            MatchSpan? hit;
            if (forward)
            {
                int from = (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
                    ? h.Start + Math.Max(1, h.Length)   // 直前ヒットの次へ（ゼロ幅でも前進）
                    : selEnd;
                hit = searcher.FindNext(text, from);
            }
            else
            {
                int before = (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
                    ? h.Start
                    : selStart;
                hit = searcher.FindPrev(text, before);
            }

            if (hit is null)
            {
                _lastHit = null;
                Announce("これ以上見つかりません");
                _dialog?.SetStatus("これ以上見つかりません");
                return;
            }

            ed.SelectCharRange(hit.Value.Start, hit.Value.Length);
            _lastHit = hit;
            var loc = searcher.Locate(text, hit.Value);
            string msg = loc is { } l ? $"{l.Total} 件中 {l.Ordinal} 件目" : "";
            Announce(msg);
            _dialog?.SetStatus(msg);
        }
        catch (RegexMatchTimeoutException)
        {
            Announce("検索式が複雑すぎます");
            _dialog?.SetStatus("検索式が複雑すぎます");
        }
    }

    // ----- 置換（コミット2 で実装） -----
    public void ReplaceOne() { /* Task 6 */ }
    public void ReplaceAll() { /* Task 6 */ }

    /// <summary>ステータス Label 経由で SR にライブ通知（NVDA 対応厚・PC-Talker は実機確認）。</summary>
    internal void Announce(string message) => _dialog?.RaiseNotification(message);
}
