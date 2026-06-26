using System.Linq;
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// 自動バックアップとクラッシュ復元の統括。UI スレッドのタイマー（＋アクティブ文書変更）で
/// 文書を走査（Reconcile）し、変化のある未保存文書のスナップショットを背景直列ライターへ渡す。
/// スナップショット取得（SCI_* 由来）は UI スレッドで行い、ディスク I/O は背景で行う（§4.1 鉄則）。
/// クリーン終了で全バックアップを削除する＝起動時に残る孤児が前回の異常終了の印になる。
/// </summary>
public sealed class BackupCoordinator : IDisposable
{
    private sealed class DocBackup
    {
        public string Id = "";
        public int LastSig;
        public bool HasBackup;
    }

    private readonly DocumentManager _docs;
    private readonly string _dir;
    private readonly bool _enabled;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly SerialBackupWriter _writer = new();
    private readonly Dictionary<Document, DocBackup> _map = new();
    private bool _shutDown;

    public BackupCoordinator(DocumentManager docs, bool enabled, int intervalSeconds, string? directory = null)
    {
        _docs = docs;
        _enabled = enabled;
        _dir = directory ?? BackupStore.DefaultDirectory;
        if (!_enabled) return;

        _timer.Interval = Math.Max(5, intervalSeconds) * 1000;
        _timer.Tick += (_, _) => Reconcile();
        _docs.ActiveDocumentChanged += (_, _) => Reconcile();
        _timer.Start();
    }

    /// <summary>
    /// 起動時に孤児バックアップがあれば復元提案する。restore は復元先の新タブを作って Document を返す
    /// デリゲート（本文を載せ dirty のままにする）。復元した文書には元 Id を引き継がせ、既存の
    /// バックアップファイルを継続使用する（孤児・無保護窓を作らない）。
    /// </summary>
    public void OfferRestoreOnStartup(IWin32Window owner, Func<BackupRecord, Document> restore)
    {
        if (!_enabled) return;
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
                var kept = new HashSet<string>();
                foreach (var rec in dlg.Checked)
                {
                    var doc = restore(rec);
                    // Reconcile が先に新 Id で登録していても、ここで元 Id へ上書きして引き継ぐ。
                    _map[doc] = new DocBackup { Id = rec.Id, LastSig = Sig(doc.Editor.SnapshotText), HasBackup = true };
                    kept.Add(rec.Id);
                }
                foreach (var rec in ordered)
                    if (!kept.Contains(rec.Id)) { string id = rec.Id; _writer.Enqueue(() => BackupStore.Delete(_dir, id)); }
                break;

            case RestoreDialog.RestoreAction.DiscardAll:
                _writer.Enqueue(() => BackupStore.DeleteAll(_dir));
                break;

            case RestoreDialog.RestoreAction.Later:
                break; // 何もしない（次回再提案）
        }
    }

    /// <summary>UI スレッドで文書を走査し、必要なバックアップ書込/削除ジョブを投入する。</summary>
    private void Reconcile()
    {
        if (!_enabled || _shutDown) return;

        // 閉じた文書（map にあるが現存しない）→ バックアップ削除。
        var current = new HashSet<Document>(_docs.Documents);
        foreach (var doc in _map.Keys.ToList())
        {
            if (current.Contains(doc)) continue;
            var gone = _map[doc];
            if (gone.HasBackup) { string id = gone.Id; _writer.Enqueue(() => BackupStore.Delete(_dir, id)); }
            _map.Remove(doc);
        }

        foreach (var doc in _docs.Documents)
        {
            if (!_map.TryGetValue(doc, out var info))
            {
                // 新規登録: 現状を基準にし、変化があるまで書かない（tick 抑止）。
                _map[doc] = new DocBackup { Id = Guid.NewGuid().ToString("N"), LastSig = Sig(doc.Editor.SnapshotText), HasBackup = false };
                continue;
            }

            if (doc.Editor.Modified)
            {
                string content = doc.Editor.SnapshotText; // UI スレッドで取得
                int sig = Sig(content);
                if (sig != info.LastSig)
                {
                    var rec = BuildRecord(info.Id, doc, content);
                    _writer.Enqueue(() => BackupStore.Write(_dir, rec));
                    info.LastSig = sig;
                    info.HasBackup = true;
                }
            }
            else if (info.HasBackup)
            {
                // 保存等でクリーン化 → バックアップ不要（内容はディスクと一致）。
                string id = info.Id;
                _writer.Enqueue(() => BackupStore.Delete(_dir, id));
                info.HasBackup = false;
                info.LastSig = Sig(doc.Editor.SnapshotText);
            }
        }
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

    // プロセス内のみで比較する簡易署名（tick 抑止用）。長さとハッシュを混ぜて衝突を抑える。
    private static int Sig(string s) => unchecked(s.Length * 397) ^ s.GetHashCode();

    /// <summary>
    /// クリーン終了: タイマー停止 → 背景書込をドレイン → 全バックアップ削除（孤児を残さない）。
    /// 未保存確認をすべて通過した後に呼ぶこと。
    /// </summary>
    public void Shutdown()
    {
        if (!_enabled || _shutDown) return;
        _shutDown = true;
        _timer.Stop();
        _writer.Dispose(); // 保留ジョブをドレイン
        try { BackupStore.DeleteAll(_dir); } catch { /* 残骸は次回提案されるだけで実害小 */ }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        if (!_shutDown) _writer.Dispose(); // Shutdown 未経由（異常系）なら writer だけ片付け、孤児は残す
    }
}
