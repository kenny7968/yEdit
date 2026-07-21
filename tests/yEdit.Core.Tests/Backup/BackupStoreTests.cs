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
    [InlineData(null!)] // M-3: null(実行時到達は理論のみだが JSON デシリアライズ/将来の new 経路で入り得る=Id 契約として lock in・HIGH-1 BackupIdValidatorTests 対称)
    [InlineData("abcdef0123456789abcdef012345678")] // 31 文字
    [InlineData("abcdef0123456789abcdef01234567890")] // 33 文字
    [InlineData("abcdef01-2345-6789-abcd-ef0123456789")] // ハイフン形式(GUID N ではない)
    [InlineData(@"..\..\..\Windows\Temp\evil")] // パストラバーサル
    [InlineData("xyz injection")] // 制御文字類 / space
    public void Write_ThrowsArgumentException_ForInvalidId(string? invalidId)
    {
        using var t = new TempDir();
        var rec = new BackupRecord(
            // null は BackupRecord.Id が非 nullable string である契約を跨いだ「実行時到達は理論のみ」ケース。
            // BackupIdValidator.IsValid(null) は false=Write 冒頭の validation で ArgumentException として即座に弾かれる。
            Id: invalidId!,
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
        // M-2: 事前に妥当な GUID N の record を 1 個置く=Delete が invalid Id を早期リジェクトし
        // 実ファイルを削除しないことを invariant として lock in する(Write 側の Assert.Empty と対称)。
        // validation が Path.Combine + TryDelete の前段で発火するため、妥当ファイルは無傷のはず。
        var canonical = Guid.NewGuid().ToString("N");
        var seed = new BackupRecord(
            Id: canonical,
            OriginalPath: null,
            UntitledNumber: 1,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: "seed",
            TimestampUtc: DateTime.UtcNow
        );
        BackupStore.Write(t.Root, seed);
        var before = Directory.GetFiles(t.Root, "*.json");

        var ex = Assert.Throws<ArgumentException>(() => BackupStore.Delete(t.Root, invalidId));
        Assert.Equal("id", ex.ParamName);
        Assert.Equal(before, Directory.GetFiles(t.Root, "*.json")); // 副作用ゼロ
    }

    // -------------------------------------------------------------------------
    // BK-L-6: LoadAll per-file catch を optional trace sink 経由で可視化する
    // -------------------------------------------------------------------------
    // 現状 catch { /* 無視 */ } は破損 JSON/JSON パース失敗を診断できず、攻撃者が植えた
    // 壊れ JSON を silent に握り潰していた。BK-L-6 では LoadAll に
    // `Action<string, string>? traceSink` を追加し、(file, kind) を通知する。
    // - kind = ex.GetType().Name (JsonException / IOException / …) — 破損 catch
    // - kind = "invalid-id" — BackupIdValidator.IsValid=false のレコード
    // - kind = "null-record" — JSON トップレベルが `null` の場合(rec is null)
    // 破損ファイル自体は従来通り skip(LoadAll 全体は落とさない=既存挙動維持)。
    // sanitize は上位 App 層(BackupCoordinator)が SanitizeForDisplay.OneLine で行う契約。

    [Fact]
    public void LoadAll_InvokesTraceSink_OnCorruptFile()
    {
        using var t = new TempDir();
        // 既存 valid record を 1 件 + 破損 JSON を 1 件並べる=trace 併存(skip でなく)を検証。
        BackupStore.Write(t.Root, Rec("good", null, "ok"));
        string brokenPath = Path.Combine(t.Root, "broken.json");
        File.WriteAllText(brokenPath, "{ this is not valid json ");

        var traced = new List<(string File, string Kind)>();
        var all = BackupStore.LoadAll(t.Root, (file, kind) => traced.Add((file, kind)));

        // 破損側は trace に上がり、valid 側は従来通り load される。
        var loaded = Assert.Single(all);
        Assert.Equal(HashId("good"), loaded.Id);
        var entry = Assert.Single(traced);
        Assert.Equal(brokenPath, entry.File);
        // kind は JSON パーサ例外の型名(JsonException 派生)。実装差分に強くするため
        // 型名を厳格固定せず「破損 JSON なら null-record/invalid-id ではない=例外型名」で検証。
        Assert.NotEqual("invalid-id", entry.Kind);
        Assert.NotEqual("null-record", entry.Kind);
        Assert.Contains("Exception", entry.Kind); // JsonException 等の Exception 派生の型名
    }

    [Fact]
    public void LoadAll_InvokesTraceSink_OnRecordWithMaliciousId()
    {
        // HIGH-1 と対称: BackupIdValidator.IsValid=false のレコードは復元候補から捨てる契約は保ちつつ、
        // 破棄理由(kind="invalid-id")と file 名を trace で観測可能にする。
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

        var traced = new List<(string File, string Kind)>();
        var loaded = BackupStore.LoadAll(t.Root, (f, k) => traced.Add((f, k)));

        Assert.Empty(loaded); // 攻撃 Id は復元ダイアログに現れない(既存 invariant を保つ)
        var entry = Assert.Single(traced);
        Assert.Equal(file, entry.File);
        Assert.Equal("invalid-id", entry.Kind);
    }

    [Fact]
    public void LoadAll_WithoutTraceSink_KeepsSilentSkip()
    {
        // 旧 API 互換: traceSink=null(引数省略)では破損 JSON でも例外は投げず、trace も呼ばれない。
        // 既存呼び出し箇所(テスト・将来の別呼び出し側)の silent 継続を lock in する。
        using var t = new TempDir();
        BackupStore.Write(t.Root, Rec("good", null, "ok"));
        File.WriteAllText(Path.Combine(t.Root, "broken.json"), "{ this is not valid json ");

        // 引数 1 個で呼ぶ=optional traceSink=null が明示されない経路。
        var ex = Record.Exception(() => BackupStore.LoadAll(t.Root));
        Assert.Null(ex);

        // 明示 null でも同じ(引数 2 個の呼び方でも例外は投げない)。
        var all = BackupStore.LoadAll(t.Root, traceSink: null);
        var loaded = Assert.Single(all);
        Assert.Equal(HashId("good"), loaded.Id);
    }

    // -------------------------------------------------------------------------
    // BK-M-2: セッション別 subdir + 30 日 sweep(BackupStore)
    // -------------------------------------------------------------------------
    // 現状のフラット配置(%APPDATA%\yEdit\backups\ 直下)は、複数 yEdit インスタンス同時起動時に
    // 別インスタンスが「すべて破棄」を選ぶと自インスタンスのライブ backup が消える問題があった。
    // BK-M-2 は書込先を %APPDATA%\yEdit\backups\session-{Guid.N}\ へ移し、
    // - LoadAll: 全 session-* subdir + flat 後方互換を列挙(復元候補は全部見せる)
    // - DeleteSessionDir: 自セッション subdir 限定削除(他インスタンスのライブを守る)
    // - SweepOldSessions: 30 日以上古い session-* subdir を削除(孤児掃除)
    // という 3 表面で invariant を保つ。

    [Fact]
    public void LoadAll_EnumeratesAllSessionSubdirs()
    {
        using var t = new TempDir();
        var sessionA = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        var sessionB = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionA);
        Directory.CreateDirectory(sessionB);
        BackupStore.Write(sessionA, Rec("r-a", null, "content-a"));
        BackupStore.Write(sessionB, Rec("r-b", null, "content-b"));

        var all = BackupStore.LoadAll(t.Root);

        // session-A + session-B の 2 件が集約列挙される(順序不問)。
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Content == "content-a");
        Assert.Contains(all, r => r.Content == "content-b");
    }

    [Fact]
    public void LoadAll_MixesSessionSubdir_AndFlatCompat()
    {
        using var t = new TempDir();
        // v0.3.0-sec 由来の flat 配置 + BK-M-2 の session subdir が混在するケース。
        // 両方から復元候補を集める(後方互換の中核 invariant)。
        BackupStore.Write(t.Root, Rec("flat", null, "flat-content"));
        var session = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        BackupStore.Write(session, Rec("session", null, "session-content"));

        var all = BackupStore.LoadAll(t.Root);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Content == "flat-content");
        Assert.Contains(all, r => r.Content == "session-content");
    }

    [Fact]
    public void LoadAll_IgnoresNonSessionSubdirs()
    {
        using var t = new TempDir();
        // "session-" prefix でない subdir はユーザが手で置いた/他アプリの残置物の可能性がある。
        // 列挙対象外にする invariant を pin(過剰列挙で他アプリの状態を見せない)。
        var other = Path.Combine(t.Root, "other-dir");
        Directory.CreateDirectory(other);
        BackupStore.Write(other, Rec("misplaced", null, "misplaced-content"));

        var all = BackupStore.LoadAll(t.Root);

        Assert.Empty(all);
    }

    [Fact]
    public void LoadAll_FlatCompat_LoadsFromBaseDir_WhenNoSessionSubdirs()
    {
        using var t = new TempDir();
        // v0.3.0-sec ユーザの既存 flat 配置(session-* 無し)が引き続き読める。
        BackupStore.Write(t.Root, Rec("legacy", null, "legacy-content"));

        var all = BackupStore.LoadAll(t.Root);

        var loaded = Assert.Single(all);
        Assert.Equal("legacy-content", loaded.Content);
    }

    [Fact]
    public void DeleteSessionDir_RemovesOnlyGivenSession()
    {
        using var t = new TempDir();
        var sessionA = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        var sessionB = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionA);
        Directory.CreateDirectory(sessionB);
        BackupStore.Write(sessionA, Rec("r-a", null, "content-a"));
        BackupStore.Write(sessionB, Rec("r-b", null, "content-b"));

        BackupStore.DeleteSessionDir(sessionA);

        // session-A の中身と(空になった) session-A dir 自体が消える。session-B は無傷。
        Assert.False(Directory.Exists(sessionA));
        Assert.True(Directory.Exists(sessionB));
        var remaining = BackupStore.LoadAll(t.Root);
        Assert.Single(remaining);
        Assert.Equal("content-b", remaining[0].Content);
    }

    [Fact]
    public void DeleteSessionDir_RemovesTmpResiduals_AsWell()
    {
        using var t = new TempDir();
        var session = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        BackupStore.Write(session, Rec("r", null, "ok"));
        // クラッシュ由来の *.tmp を模擬(AtomicFile が書込中に落ちた残骸)。
        File.WriteAllText(Path.Combine(session, "stale.tmp"), "incomplete");

        BackupStore.DeleteSessionDir(session);

        // 空になった dir 自体も消える(*.tmp も掃除対象)。
        Assert.False(Directory.Exists(session));
    }

    [Fact]
    public void DeleteSessionDir_OnMissing_IsHarmless()
    {
        using var t = new TempDir();
        var missing = Path.Combine(t.Root, "session-does-not-exist");

        // 存在しない session dir に対して呼んでも例外を投げない(shutdown 経路の冪等性)。
        Assert.Null(Record.Exception(() => BackupStore.DeleteSessionDir(missing)));
    }

    [Fact]
    public void SweepOldSessions_RemovesOnlyDirsOlderThanMaxAge()
    {
        using var t = new TempDir();
        var now = new DateTime(2026, 07, 21, 12, 0, 0, DateTimeKind.Utc);
        var fresh = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        var recent = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        var stale = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(recent);
        Directory.CreateDirectory(stale);
        // それぞれ 1 日/10 日/60 日前の最終書込時刻に設定(seam=Directory.SetLastWriteTimeUtc)。
        Directory.SetLastWriteTimeUtc(fresh, now - TimeSpan.FromDays(1));
        Directory.SetLastWriteTimeUtc(recent, now - TimeSpan.FromDays(10));
        Directory.SetLastWriteTimeUtc(stale, now - TimeSpan.FromDays(60));

        BackupStore.SweepOldSessions(t.Root, now, TimeSpan.FromDays(30));

        Assert.True(Directory.Exists(fresh)); // 1 日=残す
        Assert.True(Directory.Exists(recent)); // 10 日=残す
        Assert.False(Directory.Exists(stale)); // 60 日=削除(30 日超)
    }

    [Fact]
    public void SweepOldSessions_IgnoresFlatFilesAndNonSessionDirs()
    {
        using var t = new TempDir();
        var now = new DateTime(2026, 07, 21, 12, 0, 0, DateTimeKind.Utc);
        // flat 配置の record(v0.3.0-sec 由来)は保護される=SweepOldSessions は触らない。
        BackupStore.Write(t.Root, Rec("flat", null, "flat-content"));
        // "session-" prefix でない古い subdir も保護される(他アプリ/ユーザ手動作成の残置物)。
        var other = Path.Combine(t.Root, "other-old-dir");
        Directory.CreateDirectory(other);
        Directory.SetLastWriteTimeUtc(other, now - TimeSpan.FromDays(365));
        // 比較対象=非常に古い session-*=これは消える。
        var stale = Path.Combine(t.Root, "session-stale-guid");
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(stale, now - TimeSpan.FromDays(365));

        BackupStore.SweepOldSessions(t.Root, now, TimeSpan.FromDays(30));

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(other)); // 副作用ゼロ(他アプリ dir を巻き添えにしない)
        Assert.Single(Directory.GetFiles(t.Root, "*.json")); // flat 配置の json は無傷
    }

    [Fact]
    public void SweepOldSessions_OnMissingBaseDir_IsHarmless()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "yedit_bak_missing_" + Guid.NewGuid().ToString("N")
        );
        Assert.Null(
            Record.Exception(() =>
                BackupStore.SweepOldSessions(missing, DateTime.UtcNow, TimeSpan.FromDays(30))
            )
        );
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
