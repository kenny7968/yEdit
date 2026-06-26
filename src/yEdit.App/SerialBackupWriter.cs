using System.Collections.Concurrent;
using System.Threading;

namespace yEdit.App;

/// <summary>
/// バックアップの背景直列ライター。UI スレッドが投入したジョブ（Core への書込/削除）を、
/// 単一の背景スレッドで投入順に実行する。各ジョブの失敗は致命でないため握り潰す（無音）。
/// Dispose で投入を締め切り、保留ジョブをドレインしてから戻る。
/// </summary>
public sealed class SerialBackupWriter : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private bool _disposed;

    public SerialBackupWriter()
    {
        _worker = new Thread(Run) { IsBackground = true, Name = "yEdit backup writer" };
        _worker.Start();
    }

    /// <summary>ジョブを投入する（締め切り後・破棄後は無視）。</summary>
    public void Enqueue(Action job)
    {
        if (_queue.IsAddingCompleted) return;
        // 競合で AddingCompleted 済み／破棄済み（ObjectDisposedException は InvalidOperationException 派生）。
        try { _queue.Add(job); }
        catch (InvalidOperationException) { }
    }

    private void Run()
    {
        // 列挙自体（MoveNext）も保護する。Dispose 競合で ObjectDisposedException が出ても
        // 背景スレッドを巻き添えに落とさない（未捕捉例外はプロセス終了に直結するため）。
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                try { job(); }
                catch { /* バックアップ失敗は致命でない・無音 */ }
            }
        }
        catch { /* Dispose 競合等。ワーカーを静かに終える */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        // 保留ジョブのドレインを十分待つ（クリーン終了でバックアップ/削除を取りこぼさない）。
        bool finished = false;
        try { finished = _worker.Join(TimeSpan.FromSeconds(15)); } catch { /* 参加待ち失敗は無視 */ }
        // ワーカーがまだ走行中に Dispose すると MoveNext が ObjectDisposedException を投げるため、
        // 完全終了を確認できたときだけ破棄する。未終了なら放置（プロセス終了時で実害なし）。
        if (finished) { try { _queue.Dispose(); } catch { } }
    }
}
