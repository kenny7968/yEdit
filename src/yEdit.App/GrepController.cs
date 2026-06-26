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
    private bool _closing; // アプリ終了中は結果反映を抑止

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

        // 連打対策: 直前の実行を中止し、本実行専用の CTS を作る（破棄は本実行の finally で）。
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        var progress = new Progress<GrepProgress>(p =>
        {
            // 破棄済み・後発実行に追い越された・終了中なら、古い進捗で新しい状態を上書きしない。
            if (d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing) return;
            d.SetStatus(p.CurrentFile is null
                ? $"{p.FilesScanned} ファイル走査・{p.HitCount} 件"
                : $"{p.FilesScanned} ファイル走査中… {p.HitCount} 件");
        });

        d.SetRunning(true);
        d.SetStatus("検索中…");
        d.RaiseNotification("検索を開始しました");
        try
        {
            // GrepService は協調キャンセルで部分結果＋Cancelled を返す（例外で打ち切らない）。
            var outcome = await Task.Run(() => GrepService.Search(req, progress, ct));

            // ダイアログ破棄済み・後発の実行に追い越された・終了中なら UI を触らない（結果窓も出さない）。
            if (d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing) return;

            ShowResults(pattern, folder, outcome);
            // ヒットがあれば結果窓のフォーカスが SR を駆動するので二重読みを避ける。ただし
            // 読み取りエラーがある時は走査が不完全な旨を必ず音声化する（誤った「見つかりません」防止）。
            if (outcome.Hits.Count == 0 || outcome.Errors.Count > 0)
                d.RaiseNotification(Summary(outcome));
            else
                d.SetStatus(Summary(outcome));
        }
        catch (Exception ex)
        {
            if (!d.IsDisposed && ReferenceEquals(_cts, cts))
                d.RaiseNotification("検索エラー: " + ex.Message);
        }
        finally
        {
            // 本実行が最新なら状態をリセット。後発実行に追い越されていればそちらに任せる。
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                if (!d.IsDisposed) d.SetRunning(false);
            }
            cts.Dispose();
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>アプリ終了開始: 実行中の grep を中止し、以後の結果反映を抑止する。</summary>
    public void BeginClose() { _closing = true; _cts?.Cancel(); }

    /// <summary>終了がキャンセルされた場合に通常運用へ戻す。</summary>
    public void CancelClose() => _closing = false;

    private static string Summary(GrepOutcome o)
    {
        string errs = o.Errors.Count > 0 ? $"・読み取り不可 {o.Errors.Count} 件" : "";
        if (o.Hits.Count == 0)
            return (o.Cancelled ? "中断しました（0 件）" : "見つかりません") + errs;
        string head = o.Cancelled ? "中断: " : "";
        return $"{head}{o.Hits.Count} 行 / {o.FilesMatched} ファイル{errs}";
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
