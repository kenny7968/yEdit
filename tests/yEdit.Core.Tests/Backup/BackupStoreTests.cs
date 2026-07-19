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

    /// <summary>
    /// テスト用ラベルから決定的な GUID N (32 桁 hex) を生成する。HIGH-1 白リスト検証導入後、
    /// BackupStore.LoadAll は GUID N でない Id を捨てるため、旧来の「id-1」等の可読ラベルは
    /// テストからそのまま流し込めない。ここで SHA-256 の先頭 16 バイトを 32 桁 hex に写し、
    /// テスト間で安定した(かつ相互に衝突しない)Id を得る(暗号強度は不要=識別子生成のみ)。
    /// </summary>
    private static string HashId(string label)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(label)
        );
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static BackupRecord Rec(string label, string? path, string content) =>
        new(
            Id: HashId(label),
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

        BackupStore.Delete(t.Root, HashId("id-a"));
        var after = BackupStore.LoadAll(t.Root);
        Assert.Single(after);
        Assert.Equal(HashId("id-b"), after[0].Id);

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
        Assert.Equal(HashId("good"), loaded.Id);
    }

    [Fact]
    public void LoadAll_SkipsRecord_WithMaliciousId()
    {
        // HIGH-1: JSON の Id が GUID N でなければ復元候補から捨てる契約(BackupStore.LoadAll)。
        // ファイル名は正当な GUID にしておく=Directory.EnumerateFiles で拾わせて中身の Id を検査させる。
        using var t = new TempDir();
        var poison = new BackupRecord(
            Id: @"..\..\..\..\Windows\Temp\evil", // 白リスト違反
            OriginalPath: null,
            UntitledNumber: 1,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: "x",
            TimestampUtc: DateTime.UtcNow
        );
        var file = Path.Combine(t.Root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(poison));

        var loaded = BackupStore.LoadAll(t.Root);

        Assert.Empty(loaded); // 攻撃 Id は復元ダイアログに現れない
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
        // 契約=存在しない(が形式は妥当な) ID / 空ディレクトリでも例外を投げないこと。
        // BK-L-7 導入により「不正 Id → ArgumentException」「不在 Id → harmless」へ契約が分割された。
        // ここでは後半(不在は harmless)を lock in する。前者は下の Theory テスト参照。
        Assert.Null(
            Record.Exception(() => BackupStore.Delete(t.Root, Guid.NewGuid().ToString("N")))
        );
        Assert.Null(Record.Exception(() => BackupStore.DeleteAll(t.Root)));
    }

    // -------------------------------------------------------------------------
    // BK-L-7: Write / Delete に GUID N 白リスト(HIGH-1 対称性)
    // -------------------------------------------------------------------------
    // HIGH-1 で LoadAll は既に BackupIdValidator.IsValid で「GUID N 形式(32 桁 hex・区切りなし)」
    // の白リスト検証を行い、%APPDATA%\yEdit\backups 配下に攻撃者が植えた不正 Id JSON を復元候補から
    // 捨てている。しかし Write / Delete は Id を Path.Combine(dir, record.Id + ".json") に無検証で
    // 流していたため、将来「Import」機能や JSON パッチ流入経路が増えた場合、record.Id に
    // "..\..\evil" 等が入るとローカル ACL 内の任意場所へ書き込みできる導線になり得る。
    // BK-L-7 は Write / Delete 冒頭で BackupIdValidator.IsValid を呼び、失敗時は ArgumentException
    // を投げる(silent 無視ではなくプログラムバグとして目に見えるように)。

    [Theory]
    [InlineData("")] // 空
    [InlineData("abcdef0123456789abcdef012345678")] // 31 文字
    [InlineData("abcdef0123456789abcdef01234567890")] // 33 文字
    [InlineData("abcdef01-2345-6789-abcd-ef0123456789")] // ハイフン形式(GUID N ではない)
    [InlineData(@"..\..\..\Windows\Temp\evil")] // パストラバーサル
    [InlineData("xyz injection")] // 制御文字類 / space
    public void Write_ThrowsArgumentException_ForInvalidId(string invalidId)
    {
        using var t = new TempDir();
        var rec = new BackupRecord(
            Id: invalidId,
            OriginalPath: null,
            UntitledNumber: 1,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: "x",
            TimestampUtc: DateTime.UtcNow
        );

        var ex = Assert.Throws<ArgumentException>(() => BackupStore.Write(t.Root, rec));
        Assert.Equal("record", ex.ParamName);
        // ファイルが実際には書かれていないことを確認(validation は Directory.CreateDirectory と
        // AtomicFile.Write の前段で発火するため .json / .tmp とも残らない=パストラバーサル入口を確実に塞ぐ)。
        Assert.Empty(Directory.GetFiles(t.Root, "*.json"));
        Assert.Empty(Directory.GetFiles(t.Root, "*.tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"..\..\..\Windows\System32\config\SAM")]
    [InlineData("gggggggggggggggggggggggggggggggg")] // 32 文字だが非 hex(g は hex ではない)
    public void Delete_ThrowsArgumentException_ForInvalidId(string invalidId)
    {
        using var t = new TempDir();
        var ex = Assert.Throws<ArgumentException>(() => BackupStore.Delete(t.Root, invalidId));
        Assert.Equal("id", ex.ParamName);
    }

    [Fact]
    public void Write_AllowsCanonicalGuidN()
    {
        // 正規の GUID N(32 桁 hex・区切りなし)は Write が例外を投げず、実ファイルが書かれる。
        // 既存の Rec(...) 経由テスト群も HashId で GUID N を通しているため invariant はカバー済みだが、
        // 「validation を通過した後の正常経路が壊れていない」ことを明示ロックする 1 本。
        using var t = new TempDir();
        string canonical = Guid.NewGuid().ToString("N");
        var rec = new BackupRecord(
            Id: canonical,
            OriginalPath: null,
            UntitledNumber: 1,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: "ok",
            TimestampUtc: DateTime.UtcNow
        );

        var recordException = Record.Exception(() => BackupStore.Write(t.Root, rec));
        Assert.Null(recordException);
        Assert.Single(Directory.GetFiles(t.Root, "*.json"));
    }
}
