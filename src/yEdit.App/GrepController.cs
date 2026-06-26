using System.IO;
using yEdit.Core.Search;

namespace yEdit.App;

/// <summary>
/// grep の統括。ダイアログ入力を <see cref="GrepService"/>（別スレッド）へ渡し、結果を結果窓へ
/// 反映し件数を SR 通知する。結果のジャンプは jumpTo デリゲートへ委譲（MainForm がファイルを
/// 開いて該当を選択）。Core はスレッド非依存のため、スレッド制御は本クラスに閉じる（§4.1）。
/// </summary>
public sealed class GrepController
{
    private readonly DocumentManager _docs;
    private readonly Form _owner;
    private readonly Action<GrepHit> _jumpTo;
    private GrepDialog? _dialog;
    private GrepResultsWindow? _results;
    private CancellationTokenSource? _cts;

    public GrepController(DocumentManager docs, Form owner, Action<GrepHit> jumpTo)
    {
        _docs = docs;
        _owner = owner;
        _jumpTo = jumpTo;
    }

    /// <summary>ダイアログを開く（既定フォルダ＝アクティブ文書のフォルダ）。</summary>
    public void Open()
    {
        if (_dialog is null || _dialog.IsDisposed) _dialog = new GrepDialog(this);
        if (string.IsNullOrEmpty(_dialog.Folder)) _dialog.SetFolder(DefaultFolder());
        if (!_dialog.Visible) _dialog.Show(_owner);
        _dialog.Activate();
        _dialog.FocusPattern();
    }

    private string DefaultFolder()
    {
        string? path = _docs.Active?.State.Path;
        if (path is not null)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir is not null && dir.Length > 0) return dir;
            }
            catch { /* 不正パスはマイドキュメントへフォールバック */ }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>入力を検証して別スレッドで grep を実行し、結果を反映する。</summary>
    public async void Run()
    {
        var d = _dialog;
        if (d is null) return;
        if (string.IsNullOrEmpty(d.Pattern)) { d.RaiseNotification("検索文字列を入力してください"); return; }
        if (!Directory.Exists(d.Folder)) { d.RaiseNotification("フォルダが見つかりません"); return; }

        var opts = new SearchOptions(d.Pattern, d.MatchCase, d.WholeWord, d.UseRegex);
        if (!new TextSearcher(opts).IsValid) { d.RaiseNotification("正規表現が正しくありません"); return; }

        var req = new GrepRequest(d.Folder, d.Filter, d.Recursive, opts);
        string pattern = d.Pattern, folder = d.Folder;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<GrepProgress>(p => d.SetStatus(
            p.CurrentFile is null
                ? $"{p.FilesScanned} ファイル走査・{p.HitCount} 件"
                : $"{p.FilesScanned} ファイル走査中… {p.HitCount} 件"));

        d.SetRunning(true);
        d.SetStatus("検索中…");
        try
        {
            // GrepService は協調キャンセルで部分結果＋Cancelled を返す（例外で打ち切らない）。
            var outcome = await Task.Run(() => GrepService.Search(req, progress, ct));
            ShowResults(pattern, folder, outcome);
            d.RaiseNotification(Summary(outcome));
        }
        catch (Exception ex)
        {
            d.RaiseNotification("検索エラー: " + ex.Message);
        }
        finally
        {
            if (!d.IsDisposed) d.SetRunning(false);
        }
    }

    public void Cancel() => _cts?.Cancel();

    private static string Summary(GrepOutcome o)
    {
        if (o.Hits.Count == 0) return o.Cancelled ? "中断しました（0 件）" : "見つかりません";
        string head = o.Cancelled ? "中断: " : "";
        return $"{head}{o.Hits.Count} 行 / {o.FilesMatched} ファイル";
    }

    private void ShowResults(string pattern, string folder, GrepOutcome outcome)
    {
        if (_results is null || _results.IsDisposed)
        {
            _results = new GrepResultsWindow();
            _results.HitActivated += hit => _jumpTo(hit);
        }
        _results.Populate(pattern, folder, outcome);
        if (outcome.Hits.Count > 0) _results.ShowResults(_owner);
    }
}
