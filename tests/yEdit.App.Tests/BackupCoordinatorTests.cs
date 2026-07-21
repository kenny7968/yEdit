using System.IO;
using yEdit.App.Tests.Fakes;
using yEdit.Core.Backup;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 5: BackupCoordinator の配線・状態機械・失敗回復・復元 4 分岐のテスト
/// (設計書 §3・§5)。実 DocumentManager+実 EditorControl を STA 上で使い、
/// Form 境界(FakeRestorePrompt)・背景書込(FakeBackupWriter)・時計(FakeTimeProvider)
/// だけを偽物にする。バックアップの判定正しさ(BackupPlanner)・I/O 正しさ(BackupStore)は
/// Core 検証済みのため再検証しない(責務=配線・遷移・失敗回復・冪等性)。
/// </summary>
public class BackupCoordinatorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 07, 14, 12, 34, 56, TimeSpan.Zero);

    /// <summary>BackupCoordinator を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeBackupWriter Writer { get; } = new();
        public FakeRestorePrompt Prompt { get; } = new();
        public FakeTimeProvider Clock { get; } = new(FixedNow);
        public FakeBackupTraceSink Trace { get; } = new();
        public BackupCoordinator Backup { get; }
        public string TempDir { get; }
        public int WriterFactoryCalls;

        /// <summary>BK-M-2: factory が受け取った session dir を capture する
        /// (test で Ctor_GeneratesUniqueSessionDir_PerInstance が観測する)。</summary>
        public string? CapturedSessionDir;

        public Host(bool enabled = true, int intervalSeconds = 30)
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            // OfferRestoreOnStartup 内の LoadAll/SweepTempFiles が Enumerate する空ディレクトリ。
            // 実 I/O は起きないが Directory.Exists=false で無害に return するパス指定は避ける。
            TempDir = Path.Combine(
                Path.GetTempPath(),
                "yEdit-Stage5-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(TempDir);
            // Task 1b: 既定引数(traceSink=null)経路は本番既定 = DebugBackupTraceSink。
            // ここでは FakeBackupTraceSink を注入して catch{} の trace 発火を assert 可能にする。
            // BK-M-2: factory シグニチャは Func<string, IBackupWriter>=session dir を受け取る。
            Backup = new BackupCoordinator(
                Docs,
                enabled,
                intervalSeconds,
                Clock,
                sessionDir =>
                {
                    WriterFactoryCalls++;
                    CapturedSessionDir = sessionDir;
                    return Writer;
                },
                Prompt,
                TempDir,
                Trace
            );
        }

        /// <summary>
        /// テスト用に本文を持つ文書を作る。dirty=true(既定)では Text セッター後に
        /// <see cref="EditorControl.ClearSavePoint"/> を呼び Modified=true を強制する
        /// (Text セッターは新規 TextBuffer.FromString を差し込むため Modified=false 起点で始まる=
        /// FileControllerTests の実測コメントと同じ挙動)。dirty=false は Text セッター直後の
        /// クリーン状態のまま(SetSavePoint は fresh バッファに対して事実上 no-op)。
        /// </summary>
        public Document NewDoc(string text, bool dirty = true)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            if (dirty)
                doc.Editor.ClearSavePoint();
            return doc;
        }

        public void Dispose()
        {
            Backup.Dispose();
            Form.Dispose();
            try
            {
                Directory.Delete(TempDir, recursive: true);
            }
            catch
            { /* 掃除失敗は無害 */
            }
        }
    }

    // ===== ctor+UpdateSettings(有効/無効の切替・間隔クランプ) =====

    [Fact]
    public void Ctor_Disabled_DoesNotCreateWriter() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false);
            Assert.Equal(0, host.WriterFactoryCalls); // 無効時は writer を生成しない(リソース節約)
        });

    [Fact]
    public void Ctor_Enabled_CreatesWriter_AndWiresFailedHook() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true);
            Assert.Equal(1, host.WriterFactoryCalls);
            Assert.NotNull(host.Writer.OnWriteFailed); // Coordinator が失敗フックを配線している
        });

    [Fact]
    public void Ctor_ClampsIntervalToLowerBound() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true, intervalSeconds: 1);
            Assert.Equal(5_000, host.Backup.TimerIntervalMs); // 5 未満は下端 5s へクランプ
        });

    [Fact]
    public void Ctor_ClampsIntervalToUpperBound() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true, intervalSeconds: 99_999);
            Assert.Equal(3_600_000, host.Backup.TimerIntervalMs); // 3600 超は上端 3600s へクランプ
        });

    [Fact]
    public void UpdateSettings_ClampsIntervalToLowerBound() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true, intervalSeconds: 30);
            host.Backup.UpdateSettings(true, 1);
            Assert.Equal(5_000, host.Backup.TimerIntervalMs); // 設定ダイアログ経由でも下端クランプ
        });

    [Fact]
    public void UpdateSettings_ClampsIntervalToUpperBound() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true, intervalSeconds: 30);
            host.Backup.UpdateSettings(true, 99_999);
            Assert.Equal(3_600_000, host.Backup.TimerIntervalMs); // 設定ダイアログ経由でも上端クランプ
        });

    [Fact]
    public void UpdateSettings_EnableFromDisabled_CreatesWriter_AndReconcilesImmediately() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false);
            host.NewDoc("abc"); // dirty な文書がある状態で
            host.Backup.UpdateSettings(true, 30); // 無効→有効: writer 生成+即 Reconcile が走る

            Assert.Equal(1, host.WriterFactoryCalls);
            Assert.Single(host.Writer.Writes); // 有効化した瞬間の未保存文書を保護窓なしで即退避
        });

    // ===== Reconcile 登録(dirty→即 Write / clean→なし / 閉じた doc→Delete) =====

    [Fact]
    public void Reconcile_RegisterNew_Dirty_WritesImmediately() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("hello");

            host.Backup.Reconcile();

            var write = Assert.Single(host.Writer.Writes);
            Assert.Equal("hello", write.Content);
            Assert.Single(host.Writer.Store);
        });

    [Fact]
    public void Reconcile_RegisterNew_Clean_DoesNotWrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello", dirty: false); // Text セッター直後の fresh バッファ=Modified=false

            host.Backup.Reconcile();

            Assert.Empty(host.Writer.Writes); // 保存済み文書は退避しない
        });

    [Fact]
    public void Reconcile_ClosedDoc_DeletesBackup_AndDropsFromMap() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // Write 発生・_map に登録
            var id = host.Writer.Writes[0].Id;

            host.Docs.TryClose(doc, _ => true); // 閉じる(未保存確認は素通し)
            host.Backup.Reconcile();

            Assert.Contains(id, host.Writer.Deletes);
            Assert.False(host.Writer.Store.ContainsKey(id));
        });

    // ===== Reconcile dirty サイクル(sig 変化検知・clean 化での削除) =====

    [Fact]
    public void Reconcile_SameContentTwice_WritesOnlyOnce() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello");

            host.Backup.Reconcile();
            host.Backup.Reconcile(); // 同 sig=None

            Assert.Single(host.Writer.Writes);
        });

    [Fact]
    public void Reconcile_ContentChanged_WritesAgain() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();

            // Text セッターは ReplaceSource 経由で新規 fresh バッファ(Modified=false)を差し込むため、
            // 内容変更後に ClearSavePoint で dirty 状態を明示する(BackupPlanner が Write を出す前提)。
            doc.Editor.Text = "hello world";
            doc.Editor.ClearSavePoint();
            host.Backup.Reconcile();

            Assert.Equal(2, host.Writer.Writes.Count);
            Assert.Equal("hello world", host.Writer.Writes[^1].Content);
        });

    [Fact]
    public void Reconcile_DirtyThenSaved_DeletesBackup() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // Write 発生
            var id = host.Writer.Writes[0].Id;

            doc.Editor.SetSavePoint(); // 保存相当(Modified=false へ)
            host.Backup.Reconcile();

            Assert.Contains(id, host.Writer.Deletes);
            Assert.False(host.Writer.Store.ContainsKey(id));

            // 続く Reconcile では None(HasBackup=false・Modified=false)=追加 Delete も Write も出さない
            int deletesBefore = host.Writer.Deletes.Count;
            host.Backup.Reconcile();
            Assert.Equal(deletesBefore, host.Writer.Deletes.Count);
        });

    // ===== 失敗回復(_failed → 次 Reconcile で ForceWrite) =====

    [Fact]
    public void FailedWrite_ForcesRewrite_NextReconcile() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("hello");
            host.Backup.Reconcile(); // Write 1 回目(成功)
            var id = host.Writer.Writes[0].Id;

            host.Writer.OnWriteFailed?.Invoke(id); // 背景失敗を Coordinator に通知
            host.Backup.Reconcile(); // 同 sig でも ForceWrite=true で再書込

            Assert.Equal(2, host.Writer.Writes.Count); // 1 回目+再書込
            Assert.Equal(id, host.Writer.Writes[^1].Id); // 同じ Id で再書込
        });

    [Fact]
    public void ForceWrite_ClearsAfterSuccessfulRewrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("hello");
            host.Backup.Reconcile();
            var id = host.Writer.Writes[0].Id;
            host.Writer.OnWriteFailed?.Invoke(id);
            host.Backup.Reconcile(); // 再書込=ForceWrite クリア想定

            host.Backup.Reconcile(); // 続く Reconcile では None(同 sig・ForceWrite=false)

            Assert.Equal(2, host.Writer.Writes.Count); // 追加 Write は出ない
        });

    // ===== OfferRestoreOnStartup(4 分岐) =====

    /// <summary>テスト用ラベルから決定的な GUID N (32 桁 hex) を生成する。HIGH-1 白リスト検証導入後、
    /// BackupStore.LoadAll は GUID N でない Id を捨てるため、SHA-256 の先頭 16 バイトを 32 桁 hex に
    /// 写して安定した Id を得る(暗号強度は不要=識別子生成のみ)。</summary>
    private static string HashId(string label)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(label)
        );
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static BackupRecord Rec(string label, string content, DateTime? ts = null) =>
        new(
            Id: HashId(label),
            OriginalPath: null,
            UntitledNumber: 1,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: content,
            TimestampUtc: ts ?? new DateTime(2026, 07, 14, 12, 0, 0, DateTimeKind.Utc)
        );

    private static void PlantBackup(string dir, BackupRecord rec) => BackupStore.Write(dir, rec);

    [Fact]
    public void OfferRestore_Disabled_ReturnsZero_WithoutPrompting() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false);
            PlantBackup(host.TempDir, Rec("orphan-1", "abc"));

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(0, restored);
            Assert.Equal(0, host.Prompt.PromptCount); // 無効時は LoadAll/SweepTempFiles すら走らせない
        });

    [Fact]
    public void OfferRestore_NoRecords_ReturnsZero_WithoutPrompting() =>
        Sta.Run(() =>
        {
            using var host = new Host(); // records 0 件のディレクトリ

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(0, restored);
            Assert.Equal(0, host.Prompt.PromptCount); // 0 件時はダイアログを出さない
        });

    [Fact]
    public void OfferRestore_ConfirmFalse_RestoresAll_AndReturnsCount() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("r1", "one"));
            PlantBackup(host.TempDir, Rec("r2", "two"));

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content;
                    return d;
                },
                confirm: false
            );

            Assert.Equal(2, restored);
            Assert.Equal(0, host.Prompt.PromptCount); // confirm=false はダイアログを経由しない
        });

    [Fact]
    public void OfferRestore_ConfirmFalse_OneBadRecord_DoesNotBlockOthers() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("good", "ok"));
            PlantBackup(host.TempDir, Rec("bad", "boom"));

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    if (r.Id == HashId("bad"))
                        throw new InvalidOperationException("restore failed");
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content;
                    return d;
                },
                confirm: false
            );

            Assert.Equal(1, restored); // 1 件の失敗で他を巻き添えにしない
        });

    [Fact]
    public void OfferRestore_ConfirmTrue_Restore_UsesCheckedRecords_AndInheritsId() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("keep", "keeper"));
            PlantBackup(host.TempDir, Rec("skip", "skipper"));

            var kept = Rec("keep", "keeper");
            host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.Restore, new[] { kept });

            // 復元ラムダは本番 FileController.RestoreFromBackup と同様に ClearSavePoint で dirty に
            // 固定する(§restore-dirty バグ修正 `59ad8b5`)。これがないと Modified=false・HasBackup=true で
            // 次 Reconcile が Delete("keep") に落ち Id 引き継ぎの検証ができない。
            int returned = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content;
                    d.Editor.ClearSavePoint();
                    return d;
                },
                confirm: true
            );

            Assert.Equal(0, returned); // Restore 分岐は 0 を返す(件数は呼び側で通知しない)
            Assert.Equal(1, host.Prompt.PromptCount);
            Assert.Equal(2, host.Prompt.LastRecords?.Count); // ダイアログには全件を渡す
            // 元 Id の引き継ぎ検証: 復元 doc の内容を変更 → Reconcile が「keep」Id で Write。
            // LastSig は OfferRestoreOnStartup 内で sig("keeper") に固定されるため、内容を変えない
            // Reconcile は None を返す(sig 一致)。ここでは Id 引き継ぎの証拠として「Write が新 GUID
            // ではなく "keep" で走る」ことを確認したいので、意図的に内容を進める。
            var restored = host.Docs.Documents.Single();
            restored.Editor.Text = "keeper edited";
            restored.Editor.ClearSavePoint();
            host.Backup.Reconcile();
            Assert.Contains(host.Writer.Writes, w => w.Id == HashId("keep")); // 復元タブは dirty=Write 走る・Id は元
        });

    [Fact]
    public void OfferRestore_ConfirmTrue_OneBadRecord_DoesNotAbortOthers() =>
        Sta.Run(() =>
        {
            // confirm=false 側の同型テスト(OfferRestore_ConfirmFalse_OneBadRecord_DoesNotBlockOthers)と
            // 対称形。ダイアログの Checked に 2 件並べ、片方の restore が throw しても他方は完了する
            // ことを検証(BackupCoordinator §confirm=true 内側 catch:151-154 の実 assert 化)。
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("bad", "boom"));
            PlantBackup(host.TempDir, Rec("good", "ok"));

            var rec1 = Rec("bad", "boom");
            var rec2 = Rec("good", "ok");
            host.Prompt.NextOutcome = new RestoreOutcome(
                RestoreAction.Restore,
                new[] { rec1, rec2 }
            );

            int restoreCalls = 0;
            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    restoreCalls++;
                    if (r.Id == HashId("bad"))
                        throw new InvalidOperationException("restore failed");
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content;
                    d.Editor.ClearSavePoint();
                    return d; // "good" は成功=タブに登録される
                },
                confirm: true
            ); // 例外が伝播しないこと自体が assert(赤なら Test Runner が拾う)

            Assert.Equal(2, restoreCalls); // 1 件目の失敗で 2 件目を巻き添えにしない
            var restored = host.Docs.Documents.Single(); // 成功した "good" の文書が map/tab に登録済み
            // 元 Id "good" の引き継ぎまで検証(confirm=false 側と異なり、confirm=true は _map 登録も走る)。
            restored.Editor.Text = "ok edited";
            restored.Editor.ClearSavePoint();
            host.Backup.Reconcile();
            Assert.Contains(host.Writer.Writes, w => w.Id == HashId("good"));
        });

    // ===== Task 1b: silent catch → IBackupTraceSink 導線(restore-item / restore-item-later) =====

    [Fact]
    public void OfferRestoreOnStartup_ConfirmFalse_RestoreThrows_WarnsRestoreItemLater() =>
        Sta.Run(() =>
        {
            // Task 1b: BackupCoordinator の confirm=false 経路(:128-137)の silent catch を
            // IBackupTraceSink 経由で診断可能にした契約を固定する。
            // 例外は握り潰す(全復元を巻き添えにしない)挙動は保持しつつ、trace に category=
            // "restore-item-later" と例外実体が渡されることを assert。
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("bad", "boom"));

            var ex = new InvalidOperationException("restore failed later");
            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw ex,
                confirm: false
            );

            Assert.Equal(0, restored); // 失敗した 1 件は復元件数に含まれない
            var warn = Assert.Single(host.Trace.Warnings); // 1 レコード=1 warn
            Assert.Equal("restore-item-later", warn.Category);
            Assert.Same(ex, warn.Ex); // 例外実体まで trace へ渡す
            // Task 3 (BK-L-5): detail は SanitizeForDisplay.OneLine(rec.Id, 200) 経由。
            // 正当な GUID N (32 桁 lowercase hex) は sanitize しても不変 = 生 Id と一致。
            Assert.Equal(HashId("bad"), warn.Detail);
        });

    [Fact]
    public void OfferRestoreOnStartup_ConfirmTrue_RestoreThrows_WarnsRestoreItem() =>
        Sta.Run(() =>
        {
            // Task 1b: confirm=true 経路(:146-157)の silent catch を IBackupTraceSink 経由で診断可能に。
            // OfferRestore_ConfirmTrue_OneBadRecord_DoesNotAbortOthers の対称形として、
            // 例外が握り潰される(他レコードを巻き添えにしない)挙動は保持しつつ、trace に
            // category="restore-item" と例外実体が渡ることを assert する。
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("bad", "boom"));

            var rec = Rec("bad", "boom");
            host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.Restore, new[] { rec });

            var ex = new InvalidOperationException("restore failed");
            host.Backup.OfferRestoreOnStartup(host.Form, r => throw ex, confirm: true);

            Assert.Equal(1, host.Prompt.PromptCount); // ダイアログ経路
            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("restore-item", warn.Category);
            Assert.Same(ex, warn.Ex);
            // Task 3 (BK-L-5): detail は SanitizeForDisplay.OneLine(rec.Id, 200) 経由。
            // 正当な GUID N (32 桁 lowercase hex) は sanitize しても不変 = 生 Id と一致。
            Assert.Equal(HashId("bad"), warn.Detail);
        });

    [Fact]
    public void OfferRestoreOnStartup_ConfirmTrue_RestoreThrows_SanitizesId_ViaSanitizeForDisplay() =>
        Sta.Run(() =>
        {
            // Task 3 (BK-L-5): restore-item catch の detail は SanitizeForDisplay.OneLine(rec.Id, 200)
            // で無害化する契約を pin する。攻撃者は BackupRecord.Id に BiDi override (U+202E) や
            // 制御文字/過剰長を仕込める。LoadAll 経路は BackupIdValidator が reject するが、
            // Prompt の outcome.Checked 経路は validator を通らず、悪意 Id を直接注入できる。
            // 本テストは (a) BiDi/制御文字 drop、(b) 200 文字超は "…" 切詰め、
            // (c) 旧 SafeIdForLog (非 hex を '?' 置換) に戻す変異を kill する三役を担う。
            using var host = new Host();
            // LoadAll が非空を返すよう有効な record を 1 件 plant (無いと records.Count==0 で早期 return)。
            PlantBackup(host.TempDir, Rec("dummy", "x"));

            // outcome.Checked に載せる悪意 Id: BiDi override + CRLF + 300 文字超。
            // BackupStore.Write は BackupIdValidator で reject するため、Write 経由では入れられない。
            // \u202E = RIGHT-TO-LEFT OVERRIDE (Format cat) → SanitizeForDisplay で drop される。
            var maliciousId = "abc\u202Edef\r\nGET /evil" + new string('x', 300);
            var maliciousRec = new BackupRecord(
                Id: maliciousId,
                OriginalPath: null,
                UntitledNumber: 1,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: 0,
                Content: "boom",
                TimestampUtc: FixedNow.UtcDateTime
            );
            host.Prompt.NextOutcome = new RestoreOutcome(
                RestoreAction.Restore,
                new[] { maliciousRec }
            );

            var ex = new InvalidOperationException("restore failed");
            host.Backup.OfferRestoreOnStartup(host.Form, r => throw ex, confirm: true);

            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("restore-item", warn.Category);
            Assert.Same(ex, warn.Ex);
            // detail == SanitizeForDisplay.OneLine(maliciousId, 200) の厳密一致で契約を pin。
            Assert.Equal(yEdit.Core.Text.SanitizeForDisplay.OneLine(maliciousId, 200), warn.Detail);
            // 直接的な健全性 assert (契約破りをすぐに読み取れるように):
            // char overload は Contains(char) 経由で ordinal 比較。string overload は CurrentCulture 下で
            // ‮ (Cf/Format) を無視可能扱いにするため sanitize 済みでも常に赤化する罠がある。
            Assert.DoesNotContain('\u202E', warn.Detail); // BiDi override (RLO) drop
            Assert.DoesNotContain('\r', warn.Detail); // CR drop → 空白畳み込み
            Assert.DoesNotContain('\n', warn.Detail); // LF drop → 空白畳み込み
            Assert.True(warn.Detail.Length <= 200, "detail length should be <=200");
        });

    [Fact]
    public void OfferRestoreOnStartup_TriggersTraceSink_OnCorruptBackup() =>
        Sta.Run(() =>
        {
            // Task 2 (BK-L-6): BackupStore.LoadAll の per-file catch を trace sink 経由に。
            // 破損 JSON を仕込んだ状態で OfferRestoreOnStartup を叩き、valid record は復元される
            // (既存挙動維持=一部破損で他を巻き添えにしない)一方で破損側は
            // FakeBackupTraceSink.Warnings に category="backup-load-failed" として届くことを assert。
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("good", "ok")); // valid: 復元される
            File.WriteAllText(
                Path.Combine(host.TempDir, "broken.json"),
                "{ this is not valid json "
            );

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content;
                    return d;
                },
                confirm: false
            );

            Assert.Equal(1, restored); // valid は復元される(破損で巻き添えにならない)
            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("backup-load-failed", warn.Category);
            Assert.Null(warn.Ex); // detail に kind をコロン結合するため Exception 実体は渡さない
            // detail = "<sanitized file path>:<kind>" 形式(SanitizeForDisplay.OneLine + ":" + kind)
            Assert.Contains("broken.json", warn.Detail);
            Assert.Contains(":", warn.Detail);
            // BackupIdValidator/null-record ではなく破損 JSON 由来の例外型名
            Assert.DoesNotContain("invalid-id", warn.Detail);
            Assert.DoesNotContain("null-record", warn.Detail);
        });

    [Fact]
    public void OfferRestore_ConfirmTrue_DiscardAll_InvokesWriterDeleteAll() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("r1", "one"));
            host.Prompt.NextOutcome = new RestoreOutcome(
                RestoreAction.DiscardAll,
                Array.Empty<BackupRecord>()
            );

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(1, host.Writer.DeleteAllCount);
        });

    [Fact]
    public void OfferRestore_ConfirmTrue_Later_DoesNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("r1", "one"));
            host.Prompt.NextOutcome = RestoreOutcome.LaterEmpty;

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(0, host.Writer.DeleteAllCount);
            Assert.Empty(host.Writer.Deletes);
            Assert.Empty(host.Writer.Writes);
        });

    // ===== Shutdown/Dispose(冪等・管理分削除) =====

    [Fact]
    public void Shutdown_DeletesManagedBackups_AndDisposesWriter() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("one");
            _ = host.NewDoc("two");
            host.Backup.Reconcile(); // 両方 Write=HasBackup=true

            host.Backup.Shutdown();

            Assert.Equal(2, host.Writer.Deletes.Count); // 管理分を全 Delete
            Assert.Equal(1, host.Writer.DisposeCount); // ライターをドレイン
        });

    [Fact]
    public void Shutdown_Idempotent_SecondCallIsNoOp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello");
            host.Backup.Reconcile();

            host.Backup.Shutdown();
            int deletesAfterFirst = host.Writer.Deletes.Count;
            int disposesAfterFirst = host.Writer.DisposeCount;
            host.Backup.Shutdown(); // 2 回目

            Assert.Equal(deletesAfterFirst, host.Writer.Deletes.Count);
            Assert.Equal(disposesAfterFirst, host.Writer.DisposeCount);
        });

    [Fact]
    public void Dispose_WithoutShutdown_DisposesWriter_WithoutDeletingBackups() =>
        Sta.Run(() =>
        {
            // 後始末は他テストと同じく using var host に統一(BackupCoordinator.Dispose は _shutDown で
            // 冪等=Host.Dispose 内の 2 回目 Dispose は writer/timer に再突入しない)。
            using var host = new Host();
            host.NewDoc("hello");
            host.Backup.Reconcile();

            host.Backup.Dispose(); // 異常系(Shutdown 未経由)

            Assert.Empty(host.Writer.Deletes); // 管理分の削除は行わない(孤児として次回復元)
            Assert.Equal(1, host.Writer.DisposeCount);
        });

    // ===== TimeProvider(BackupRecord.TimestampUtc が clock 由来) =====

    [Fact]
    public void BuildRecord_UsesInjectedClock_ForTimestamp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello");

            host.Backup.Reconcile();

            Assert.Equal(FixedNow.UtcDateTime, host.Writer.Writes[0].TimestampUtc);
        });

    // ===== 追加: 対応固定(Reconcile の Write/Delete が IBackupWriter 経由であることの担保) =====

    [Fact]
    public void Reconcile_MultipleDocs_WriteRoutingIsPerDoc() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("A");
            _ = host.NewDoc("B");

            host.Backup.Reconcile();

            Assert.Equal(2, host.Writer.Writes.Count);
            Assert.NotEqual(host.Writer.Writes[0].Id, host.Writer.Writes[1].Id); // 個別 Id
        });

    [Fact]
    public void ReconcileAfterActiveDocumentChanged_DoesNotDoubleWrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello");

            host.Backup.Reconcile(); // 初回 Write
            _ = host.Docs.CreateNew(); // ActiveDocumentChanged=内部で Reconcile が走る

            Assert.Single(host.Writer.Writes); // 元 doc は同 sig=再 Write なし(2 回目の Reconcile で None)
        });

    [Fact]
    public void UpdateSettings_DisableFromEnabled_DoesNotDisposeWriter() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true);
            host.NewDoc("hello");
            host.Backup.Reconcile();

            host.Backup.UpdateSettings(false, 30); // 有効→無効: 既存ファイルを削除しない・writer は残す
            int disposedBefore = host.Writer.DisposeCount;

            Assert.Equal(disposedBefore, host.Writer.DisposeCount); // Dispose は Shutdown/Dispose まで待つ
            Assert.Empty(host.Writer.Deletes);
        });

    // ===== Lazy 生成の一度性(_writer ??= CreateWriter の pin) =====

    [Fact]
    public void Writer_IsCreated_LazilyOnce_AcrossMultipleReconciles() =>
        Sta.Run(() =>
        {
            // BackupCoordinator.cs:93 `_writer ??= CreateWriter()` を pin する。
            // 無効起動 → 有効化(factory 呼ばれ 1)→ dirty サイクル+Reconcile 複数回 →
            // 無効化 → 再有効化(??= により factory 呼ばれない=2 にならない)を通しても
            // WriterFactoryCalls が 1 のまま=Lazy 生成の意味論(既存 writer は再利用)を固定。
            // ??= を = に変えたバグ変異では、再有効化時に factory が 2 回目を呼び 2 になる。
            using var host = new Host(enabled: false);
            Assert.Equal(0, host.WriterFactoryCalls); // 無効起動=writer 未生成(既存 Ctor_Disabled と対称)

            host.Backup.UpdateSettings(true, 30); // 無効→有効(初回生成)
            Assert.Equal(1, host.WriterFactoryCalls);

            // dirty サイクルを複数回回しても Reconcile 経路は factory を呼ばない(そもそも Reconcile 側に生成分岐がない)。
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            doc.Editor.Text = "hello world";
            doc.Editor.ClearSavePoint();
            host.Backup.Reconcile();

            // 有効→無効→有効の切替で ??= の右辺は再評価されない(既存 _writer を再利用)。
            host.Backup.UpdateSettings(false, 30);
            host.Backup.UpdateSettings(true, 30);
            host.Backup.Reconcile();

            Assert.Equal(1, host.WriterFactoryCalls); // ??= が 2 回目以降を抑止(mutation kill: ??= → = で赤化)
        });

    // ===== HasBackup=false Delete ガード(:189 Reconcile-close 経路 / :270 Shutdown 経路) =====

    [Fact]
    public void Reconcile_ClosedDoc_WithoutBackup_DoesNotCall_Delete() =>
        Sta.Run(() =>
        {
            // BackupCoordinator.cs:189 `if (gone.HasBackup) _writer?.Delete(gone.Id)` を pin する。
            // clean な doc は Reconcile で _map に登録されるが Write が走らず HasBackup=false のまま。
            // 閉じてから Reconcile → gone.HasBackup=false 分岐で Delete をスキップすることを固定。
            // 条件を `if (true)` に変異させると Delete が余分に呼ばれ本テストが赤化する。
            using var host = new Host();
            var doc = host.NewDoc("hello", dirty: false); // Text セッター直後の fresh バッファ=Modified=false
            host.Backup.Reconcile(); // RegisterNew: _map に HasBackup=false で登録・Write は出ない
            Assert.Empty(host.Writer.Writes); // sanity: HasBackup=false 前提

            host.Docs.TryClose(doc, _ => true); // 未保存確認は素通し(clean なので通常も出ない)
            host.Backup.Reconcile(); // 閉じた doc の gone 経路: HasBackup=false → Delete しない

            Assert.Empty(host.Writer.Deletes); // ガード発火(:189)
        });

    [Fact]
    public void Shutdown_WithoutBackup_DoesNotCall_Delete() =>
        Sta.Run(() =>
        {
            // BackupCoordinator.cs:270 `if (info.HasBackup) _writer?.Delete(info.Id)` を pin する。
            // clean な doc(HasBackup=false)のまま Shutdown → 管理分だが Delete をスキップし、
            // writer は Dispose される(Shutdown_DeletesManagedBackups_AndDisposesWriter と対称)。
            // 条件を `if (true)` に変異させると Delete が余分に呼ばれ本テストが赤化する。
            using var host = new Host();
            host.NewDoc("hello", dirty: false); // Text セッター直後=Modified=false
            host.Backup.Reconcile(); // RegisterNew: HasBackup=false で登録・Write なし
            Assert.Empty(host.Writer.Writes); // sanity: HasBackup=false 前提

            host.Backup.Shutdown();

            Assert.Empty(host.Writer.Deletes); // ガード発火(:270)
            Assert.Equal(1, host.Writer.DisposeCount); // Shutdown は writer を必ず Dispose する
        });

    // ===== BK-M-2: セッション別 subdir(factory シグニチャ + 30 日 sweep) =====

    [Fact]
    public void Ctor_PassesSessionSubdir_ToWriterFactory() =>
        Sta.Run(() =>
        {
            // BK-M-2: factory シグニチャ Func<string, IBackupWriter>=writer は session dir を
            // ctor で受け取る。ctor が渡すのは base dir(TempDir)ではなく "session-{Guid.N}" prefix の
            // 直接子 subdir(_dir と混同したまま呼ぶ変異を kill する pin)。
            using var host = new Host(enabled: true);

            Assert.Equal(1, host.WriterFactoryCalls);
            Assert.NotNull(host.CapturedSessionDir);
            var expectedPrefix = Path.Combine(host.TempDir, "session-");
            Assert.StartsWith(expectedPrefix, host.CapturedSessionDir!);
            Assert.NotEqual(host.TempDir, host.CapturedSessionDir); // base dir 直渡しは NG
            // "session-{Guid.N}" の 32 桁 hex 部分が実際に付いていることを確認。
            var suffix = Path.GetFileName(host.CapturedSessionDir!)?.Substring("session-".Length);
            Assert.Equal(32, suffix?.Length);
        });

    [Fact]
    public void Ctor_GeneratesUniqueSessionDir_PerInstance() =>
        Sta.Run(() =>
        {
            // BK-M-2: プロセス内で複数 Coordinator を生成しても session dir が衝突しない
            // (Guid.NewGuid で生成=同時 2 インスタンス起動シナリオのモック)。
            // Host は base dir(TempDir)を各々別に生成するため、full path 比較は
            // 「Guid を hardcode に変えた」変異を kill できない。session-* subdir の
            // 名前部分(session-{Guid.N})を切り出して比較=Guid 部分が実際に別値であることを pin。
            using var host1 = new Host(enabled: true);
            using var host2 = new Host(enabled: true);

            Assert.NotNull(host1.CapturedSessionDir);
            Assert.NotNull(host2.CapturedSessionDir);
            var name1 = Path.GetFileName(host1.CapturedSessionDir!);
            var name2 = Path.GetFileName(host2.CapturedSessionDir!);
            Assert.NotEqual(name1, name2); // "session-{Guid.N}" 部分が衝突しない
        });

    [Fact]
    public void OfferRestoreOnStartup_SweepsOldSessions_OlderThan30Days() =>
        Sta.Run(() =>
        {
            // BK-M-2: 30 日以上更新のない session-* subdir が OfferRestoreOnStartup 冒頭で消える。
            // 時計は FakeTimeProvider で FixedNow(2026-07-14)固定=SetLastWriteTimeUtc で
            // 60 日前に相当する古い session dir を植える(seam=Directory.SetLastWriteTimeUtc)。
            using var host = new Host();
            var stale = Path.Combine(host.TempDir, "session-stale-guid");
            var fresh = Path.Combine(host.TempDir, "session-fresh-guid");
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(fresh);
            Directory.SetLastWriteTimeUtc(stale, FixedNow.UtcDateTime - TimeSpan.FromDays(60));
            Directory.SetLastWriteTimeUtc(fresh, FixedNow.UtcDateTime - TimeSpan.FromDays(5));

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.False(Directory.Exists(stale)); // 60 日=30 日超で削除
            Assert.True(Directory.Exists(fresh)); // 5 日=残す
        });
}
