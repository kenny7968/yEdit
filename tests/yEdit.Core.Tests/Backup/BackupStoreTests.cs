using Xunit;
using yEdit.Core.Backup;

namespace yEdit.Core.Tests.Backup;

public class BackupStoreTests
{
    private sealed class TempDir : IDisposable
    {
        public string Root { get; }

        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "yedit_bak_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            { /* テスト後の best-effort クリーンアップ・ロック競合等は OS 側の temp GC に委ねて無視 */
            }
        }
    }

    private static BackupRecord Rec(string id, string? path, string content) =>
        new(
            Id: id,
            OriginalPath: path,
            UntitledNumber: path is null ? 3 : 0,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: content,
            TimestampUtc: new DateTime(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc)
        );

    [Fact]
    public void Write_then_LoadAll_roundtrips_content_and_metadata()
    {
        using var t = new TempDir();
        var rec = Rec("id-1", @"C:\docs\a.txt", "一行目\r\n二行目\ttab \"quote\" 😀\r\n");
        BackupStore.Write(t.Root, rec);

        var all = BackupStore.LoadAll(t.Root);
        var loaded = Assert.Single(all);
        Assert.Equal(rec, loaded); // record の構造的等価で全フィールド一致
    }

    [Fact]
    public void Untitled_record_roundtrips_with_null_path()
    {
        using var t = new TempDir();
        var rec = Rec("id-untitled", null, "no path content");
        BackupStore.Write(t.Root, rec);

        var loaded = Assert.Single(BackupStore.LoadAll(t.Root));
        Assert.Null(loaded.OriginalPath);
        Assert.Equal(3, loaded.UntitledNumber);
        Assert.Equal("no path content", loaded.Content);
    }

    [Fact]
    public void Write_same_id_overwrites_atomically()
    {
        using var t = new TempDir();
        BackupStore.Write(t.Root, Rec("id-x", null, "古い内容"));
        BackupStore.Write(t.Root, Rec("id-x", null, "新しい内容"));

        var loaded = Assert.Single(BackupStore.LoadAll(t.Root));
        Assert.Equal("新しい内容", loaded.Content);
        // .tmp 残骸が残っていないこと（原子的差し替え）。
        Assert.Empty(Directory.GetFiles(t.Root, "*.tmp"));
    }

    [Fact]
    public void Delete_removes_one_DeleteAll_clears()
    {
        using var t = new TempDir();
        BackupStore.Write(t.Root, Rec("id-a", null, "a"));
        BackupStore.Write(t.Root, Rec("id-b", null, "b"));

        BackupStore.Delete(t.Root, "id-a");
        var after = BackupStore.LoadAll(t.Root);
        Assert.Single(after);
        Assert.Equal("id-b", after[0].Id);

        BackupStore.DeleteAll(t.Root);
        Assert.Empty(BackupStore.LoadAll(t.Root));
    }

    [Fact]
    public void Corrupt_file_is_skipped_valid_still_loads()
    {
        using var t = new TempDir();
        BackupStore.Write(t.Root, Rec("good", null, "ok"));
        File.WriteAllText(Path.Combine(t.Root, "broken.json"), "{ this is not valid json ");

        var all = BackupStore.LoadAll(t.Root);
        var loaded = Assert.Single(all);
        Assert.Equal("good", loaded.Id);
    }

    [Fact]
    public void LoadAll_on_missing_directory_returns_empty()
    {
        string missing = Path.Combine(
            Path.GetTempPath(),
            "yedit_bak_missing_" + Guid.NewGuid().ToString("N")
        );
        Assert.Empty(BackupStore.LoadAll(missing));
    }

    [Fact]
    public void Delete_on_missing_is_harmless()
    {
        using var t = new TempDir();
        // 契約=存在しない ID / 空ディレクトリでも例外を投げないこと
        Assert.Null(Record.Exception(() => BackupStore.Delete(t.Root, "does-not-exist")));
        Assert.Null(Record.Exception(() => BackupStore.DeleteAll(t.Root)));
    }
}
