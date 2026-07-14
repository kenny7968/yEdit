using yEdit.Core.Backup;

namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IBackupWriter"/> のテスト用フェイク。in-memory Dictionary に格納するため
/// 実 I/O(BackupStore.Write 等)は起きない=テストが Coordinator の呼び出し配線・状態機械を
/// 純粋に観測できる。書込失敗の再現は <see cref="FailNextWriteWith"/> を使う。
/// </summary>
public sealed class FakeBackupWriter : IBackupWriter
{
    /// <summary>現在ディスクにあるとみなす記録(Id → 最新 record)。</summary>
    public Dictionary<string, BackupRecord> Store { get; } = new();

    /// <summary>Write 呼び出し履歴(順序保持・sig 追跡に使う)。</summary>
    public List<BackupRecord> Writes { get; } = new();

    /// <summary>Delete された Id の履歴。</summary>
    public List<string> Deletes { get; } = new();

    /// <summary>DeleteAll 呼び出し回数。</summary>
    public int DeleteAllCount;

    /// <summary>Dispose 呼び出し回数(冪等性検証に使う)。</summary>
    public int DisposeCount;

    public Action<string>? OnWriteFailed { get; set; }

    /// <summary>次の Write を失敗させる(Id を通知)。挙動: Store には格納せず OnWriteFailed を発火。</summary>
    private string? _failNextWriteId;
    public void FailNextWriteWith(string id) => _failNextWriteId = id;

    public void Write(BackupRecord record)
    {
        Writes.Add(record);
        if (_failNextWriteId is not null && _failNextWriteId == record.Id)
        {
            _failNextWriteId = null;
            OnWriteFailed?.Invoke(record.Id);
            return;
        }
        Store[record.Id] = record;
    }

    public void Delete(string id)
    {
        Deletes.Add(id);
        Store.Remove(id);
    }

    public void DeleteAll()
    {
        DeleteAllCount++;
        Store.Clear();
    }

    public void Dispose() => DisposeCount++;
}
