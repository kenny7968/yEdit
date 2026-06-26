using System.Collections.Concurrent;
using System.Linq;
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// 自動バックアップとクラッシュ復元の統括。UI スレッドのタイマー（＋アクティブ文書変更）で
/// 文書を走査（Reconcile）し、変化のある未保存文書のスナップショットを背景直列ライターへ渡す。
/// スナップショット取得（SCI_* 由来）は UI スレッドで行い、ディスク I/O は背景で行う（§4.1 鉄則）。
/// クリーン終了では「当セッションが管理した文書」のバックアップのみ削除する（「あとで」先送りした
/// 孤児は残し次回再提案する）。判定の中核は Core の純粋関数 BackupPlanner で単体テスト可能。
/// </summary>
public sealed class BackupCoordinator : IDisposable
{
    private sealed class DocBackup
    {
        public string Id = "";
        public long LastSig;
        public bool HasBackup;
        public bool ForceWrite; // 前回の背景書込が失敗 → 次 tick で強制再書込（陳腐化・欠落を防ぐ）
    }

    private readonly DocumentManager _docs;
    private readonly string _dir;
    private readonly bool _enabled;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly SerialBackupWriter? _writer;            // 無効時はスレッドを生成しない
    private readonly Dictionary<Document, DocBackup> _map = new();
    private readonly ConcurrentQueue<string> _failed = new(); // 背景書込が失敗した Id（UI スレッドで回収）
    private bool _shutDown;

    public BackupCoordinator(DocumentManager docs, bool enabled, int intervalSeconds, string? directory = null)
    {
        _docs = docs;
        _enabled = enabled;
        _dir = directory ?? BackupStore.DefaultDirectory;
        if (!_enabled) return;

        _writer = new SerialBackupWriter();
        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000; // 上限クランプで int オーバーフロー防止
        _timer.Tick += (_, _) => Reconcile();
        _docs.ActiveDocumentChanged += (_, _) => Reconcile();
        _timer.Start();
    }

    /// <summary>
    /// 起動時に孤児バックアップがあれば復元提案する。restore は復元先の新タブを作って Document を返す
    /// デリゲート（本文を載せ dirty のまま）。復元した文書には元 Id を引き継がせ、既存のバックアップ
    /// ファイルを継続使用する（孤児・無保護窓を作らない）。チェックしなかった項目は安全側で残し、
    /// 次回再提案する（明示的に消すのは「すべて破棄」のみ）。
    /// </summary>
    public void OfferRestoreOnStartup(IWin32Window owner, Func<BackupRecord, Document> restore)
    {
        if (!_enabled) return;
        try { BackupStore.SweepTempFiles(_dir); } catch { /* 残骸掃除失敗は無害 */ }

        IReadOnlyList<BackupRecord> records;
        try { records = BackupStore.LoadAll(_dir); }
        catch { return; }
        if (records.Count == 0) return;

        var ordered = records.OrderByDescending(r => r.TimestampUtc).ToList();
        using var dlg = new RestoreDialog(ordered);
        dlg.ShowDialog(owner);

        switch (dlg.Action)
        {
            case RestoreDialog.RestoreAction.Restore:
                foreach (var rec in dlg.Checked)
                {
                    try
                    {
                        var doc = restore(rec);
                        // Reconcile が先に新 Id で登録していても、ここで元 Id へ上書きして引き継ぐ。
                        _map[doc] = new DocBackup { Id = rec.Id, LastSig = ContentSignature.Of(doc.Editor.SnapshotText), HasBackup = true };
                    }
                    catch
                    {
                        // 1 件の不正レコードで全復元を巻き添えにしない。失敗分はバックアップを残し再挑戦可能に。
                    }
                }
                // チェックしなかった項目は削除しない（SR 誤操作での消失を避け、次回再提案）。
                break;

            case RestoreDialog.RestoreAction.DiscardAll:
                _writer?.Enqueue(() => BackupStore.DeleteAll(_dir));
                break;

            case RestoreDialog.RestoreAction.Later:
                break; // 何もしない（次回再提案）
        }
    }

    /// <summary>UI スレッドで文書を走査し、必要なバックアップ書込/削除ジョブを投入する。</summary>
    private void Reconcile()
    {
        if (!_enabled || _shutDown) return;

        // 背景書込が失敗した文書を強制再書込対象にする（楽観更新で欠落・陳腐化しないように）。
        while (_failed.TryDequeue(out var failedId))
            foreach (var v in _map.Values)
                if (v.Id == failedId) v.ForceWrite = true;

        // 閉じた文書（map にあるが現存しない）→ バックアップ削除。
        var current = new HashSet<Document>(_docs.Documents);
        foreach (var doc in _map.Keys.ToList())
        {
            if (current.Contains(doc)) continue;
            var gone = _map[doc];
            if (gone.HasBackup) { string id = gone.Id; _writer?.Enqueue(() => BackupStore.Delete(_dir, id)); }
            _map.Remove(doc);
        }

        foreach (var doc in _docs.Documents)
        {
            if (!_map.TryGetValue(doc, out var info))
            {
                RegisterNew(doc);
                continue;
            }

            bool modified = doc.Editor.Modified;
            string content = modified ? doc.Editor.SnapshotText : ""; // クリーン時はスナップショット不要
            long sig = modified ? ContentSignature.Of(content) : info.LastSig;

            switch (BackupPlanner.Decide(modified, sig, info.LastSig, info.HasBackup, info.ForceWrite))
            {
                case BackupAction.Write:
                    EnqueueWrite(info, doc, content);
                    info.LastSig = sig;
                    info.HasBackup = true;
                    info.ForceWrite = false;
                    break;
                case BackupAction.Delete:
                    string did = info.Id;
                    _writer?.Enqueue(() => BackupStore.Delete(_dir, did));
                    info.HasBackup = false;
                    info.LastSig = sig;
                    info.ForceWrite = false;
                    break;
                case BackupAction.None:
                    break;
            }
        }
    }

    /// <summary>未登録文書を登録する。登録時点で既に dirty なら即退避し保護窓を作らない（起動時無題タブ対策）。</summary>
    private void RegisterNew(Document doc)
    {
        string content = doc.Editor.SnapshotText;
        var info = new DocBackup { Id = Guid.NewGuid().ToString("N"), LastSig = ContentSignature.Of(content), HasBackup = false };
        _map[doc] = info;
        if (doc.Editor.Modified)
        {
            EnqueueWrite(info, doc, content);
            info.HasBackup = true;
        }
    }

    /// <summary>書込ジョブを投入する。失敗時は Id を _failed へ積み、次 Reconcile で強制再書込する。</summary>
    private void EnqueueWrite(DocBackup info, Document doc, string content)
    {
        var rec = BuildRecord(info.Id, doc, content);
        string id = info.Id;
        _writer?.Enqueue(() =>
        {
            try { BackupStore.Write(_dir, rec); }
            catch { _failed.Enqueue(id); }
        });
    }

    private static BackupRecord BuildRecord(string id, Document doc, string content) => new(
        Id: id,
        OriginalPath: doc.State.Path,
        UntitledNumber: doc.State.UntitledNumber,
        CodePage: doc.State.Encoding.CodePage,
        HasBom: doc.State.HasBom,
        LineEndingId: (int)doc.State.LineEnding,
        Content: content,
        TimestampUtc: DateTime.UtcNow);

    /// <summary>
    /// クリーン終了: タイマー停止 → 当セッション管理分のバックアップ削除を投入 → 背景書込をドレイン。
    /// 「あとで」先送りした孤児は _map に無いので残り、次回起動で再提案される。
    /// 未保存確認をすべて通過した後に呼ぶこと。
    /// </summary>
    public void Shutdown()
    {
        if (!_enabled || _shutDown) return;
        _shutDown = true;
        _timer.Stop();
        foreach (var info in _map.Values)
            if (info.HasBackup) { string id = info.Id; _writer?.Enqueue(() => BackupStore.Delete(_dir, id)); }
        _writer?.Dispose(); // 保留ジョブ（削除含む）をドレイン
        _timer.Dispose();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        if (!_shutDown) _writer?.Dispose(); // Shutdown 未経由（異常系）なら writer だけ片付け、孤児は残す
    }
}
