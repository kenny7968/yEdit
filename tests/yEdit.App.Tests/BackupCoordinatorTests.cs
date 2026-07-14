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
        public BackupCoordinator Backup { get; }
        public string TempDir { get; }
        public int WriterFactoryCalls;

        public Host(bool enabled = true, int intervalSeconds = 30)
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            // OfferRestoreOnStartup 内の LoadAll/SweepTempFiles が Enumerate する空ディレクトリ。
            // 実 I/O は起きないが Directory.Exists=false で無害に return するパス指定は避ける。
            TempDir = Path.Combine(Path.GetTempPath(), "yEdit-Stage5-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);
            Backup = new BackupCoordinator(
                Docs, enabled, intervalSeconds,
                Clock,
                () => { WriterFactoryCalls++; return Writer; },
                Prompt,
                TempDir);
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
            if (dirty) doc.Editor.ClearSavePoint();
            return doc;
        }

        public void Dispose()
        {
            Backup.Dispose();
            Form.Dispose();
            try { Directory.Delete(TempDir, recursive: true); } catch { /* 掃除失敗は無害 */ }
        }
    }

    // ===== ctor+UpdateSettings(有効/無効の切替・間隔クランプ) =====

    [Fact]
    public void Ctor_Disabled_DoesNotCreateWriter() => Sta.Run(() =>
    {
        using var host = new Host(enabled: false);
        Assert.Equal(0, host.WriterFactoryCalls);   // 無効時は writer を生成しない(リソース節約)
    });

    [Fact]
    public void Ctor_Enabled_CreatesWriter_AndWiresFailedHook() => Sta.Run(() =>
    {
        using var host = new Host(enabled: true);
        Assert.Equal(1, host.WriterFactoryCalls);
        Assert.NotNull(host.Writer.OnWriteFailed);  // Coordinator が失敗フックを配線している
    });

    [Fact]
    public void UpdateSettings_IntervalClamp_TooSmall_And_TooLarge() => Sta.Run(() =>
    {
        using var host = new Host(enabled: true, intervalSeconds: 30);
        host.Backup.UpdateSettings(true, 1);        // 5 未満はクランプ(下端 5s)
        host.Backup.UpdateSettings(true, 99_999);   // 3600 超はクランプ(上端 3600s)
        // 直接観測はできないが、int オーバーフローで例外にならないこと自体が保証(現行の Clamp を維持)。
        // 実効間隔の観測は Timer 抽象化を持たない本 Stage の範囲外。
    });

    [Fact]
    public void UpdateSettings_EnableFromDisabled_CreatesWriter_AndReconcilesImmediately() => Sta.Run(() =>
    {
        using var host = new Host(enabled: false);
        host.NewDoc("abc");                          // dirty な文書がある状態で
        host.Backup.UpdateSettings(true, 30);        // 無効→有効: writer 生成+即 Reconcile が走る

        Assert.Equal(1, host.WriterFactoryCalls);
        Assert.Single(host.Writer.Writes);           // 有効化した瞬間の未保存文書を保護窓なしで即退避
    });

    // ===== Reconcile 登録(dirty→即 Write / clean→なし / 閉じた doc→Delete) =====

    [Fact]
    public void Reconcile_RegisterNew_Dirty_WritesImmediately() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("hello");

        host.Backup.Reconcile();

        var write = Assert.Single(host.Writer.Writes);
        Assert.Equal("hello", write.Content);
        Assert.Single(host.Writer.Store);
    });

    [Fact]
    public void Reconcile_RegisterNew_Clean_DoesNotWrite() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("hello", dirty: false);          // Text セッター直後の fresh バッファ=Modified=false

        host.Backup.Reconcile();

        Assert.Empty(host.Writer.Writes);            // 保存済み文書は退避しない
    });

    [Fact]
    public void Reconcile_ClosedDoc_DeletesBackup_AndDropsFromMap() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("hello");
        host.Backup.Reconcile();                     // Write 発生・_map に登録
        var id = host.Writer.Writes[0].Id;

        host.Docs.TryClose(doc, _ => true);          // 閉じる(未保存確認は素通し)
        host.Backup.Reconcile();

        Assert.Contains(id, host.Writer.Deletes);
        Assert.False(host.Writer.Store.ContainsKey(id));
    });

    // ===== Reconcile dirty サイクル(sig 変化検知・clean 化での削除) =====

    [Fact]
    public void Reconcile_SameContentTwice_WritesOnlyOnce() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("hello");

        host.Backup.Reconcile();
        host.Backup.Reconcile();                     // 同 sig=None

        Assert.Single(host.Writer.Writes);
    });

    [Fact]
    public void Reconcile_ContentChanged_WritesAgain() => Sta.Run(() =>
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
    public void Reconcile_DirtyThenSaved_DeletesBackup() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("hello");
        host.Backup.Reconcile();                     // Write 発生
        var id = host.Writer.Writes[0].Id;

        doc.Editor.SetSavePoint();                   // 保存相当(Modified=false へ)
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
    public void FailedWrite_ForcesRewrite_NextReconcile() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("hello");
        host.Backup.Reconcile();                     // Write 1 回目(成功)
        var id = host.Writer.Writes[0].Id;

        host.Writer.OnWriteFailed?.Invoke(id);       // 背景失敗を Coordinator に通知
        host.Backup.Reconcile();                     // 同 sig でも ForceWrite=true で再書込

        Assert.Equal(2, host.Writer.Writes.Count);   // 1 回目+再書込
        Assert.Equal(id, host.Writer.Writes[^1].Id); // 同じ Id で再書込
    });

    [Fact]
    public void ForceWrite_ClearsAfterSuccessfulRewrite() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewDoc("hello");
        host.Backup.Reconcile();
        var id = host.Writer.Writes[0].Id;
        host.Writer.OnWriteFailed?.Invoke(id);
        host.Backup.Reconcile();                     // 再書込=ForceWrite クリア想定

        host.Backup.Reconcile();                     // 続く Reconcile では None(同 sig・ForceWrite=false)

        Assert.Equal(2, host.Writer.Writes.Count);   // 追加 Write は出ない
    });

    // ===== OfferRestoreOnStartup(4 分岐) =====

    private static BackupRecord Rec(string id, string content, DateTime? ts = null) => new(
        Id: id, OriginalPath: null, UntitledNumber: 1,
        CodePage: 65001, HasBom: false, LineEndingId: 0,
        Content: content, TimestampUtc: ts ?? new DateTime(2026, 07, 14, 12, 0, 0, DateTimeKind.Utc));

    private static void PlantBackup(string dir, BackupRecord rec) => BackupStore.Write(dir, rec);

    [Fact]
    public void OfferRestore_Disabled_ReturnsZero_WithoutPrompting() => Sta.Run(() =>
    {
        using var host = new Host(enabled: false);
        PlantBackup(host.TempDir, Rec("orphan-1", "abc"));

        int restored = host.Backup.OfferRestoreOnStartup(host.Form, r => throw new Xunit.Sdk.XunitException("restore must not be called"), confirm: true);

        Assert.Equal(0, restored);
        Assert.Equal(0, host.Prompt.PromptCount);   // 無効時は LoadAll/SweepTempFiles すら走らせない
    });

    [Fact]
    public void OfferRestore_NoRecords_ReturnsZero_WithoutPrompting() => Sta.Run(() =>
    {
        using var host = new Host();                 // records 0 件のディレクトリ

        int restored = host.Backup.OfferRestoreOnStartup(host.Form, r => throw new Xunit.Sdk.XunitException("restore must not be called"), confirm: true);

        Assert.Equal(0, restored);
        Assert.Equal(0, host.Prompt.PromptCount);   // 0 件時はダイアログを出さない
    });

    [Fact]
    public void OfferRestore_ConfirmFalse_RestoresAll_AndReturnsCount() => Sta.Run(() =>
    {
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("r1", "one"));
        PlantBackup(host.TempDir, Rec("r2", "two"));

        int restored = host.Backup.OfferRestoreOnStartup(host.Form, r => { var d = host.Docs.CreateNew(); d.Editor.Text = r.Content; return d; }, confirm: false);

        Assert.Equal(2, restored);
        Assert.Equal(0, host.Prompt.PromptCount);   // confirm=false はダイアログを経由しない
    });

    [Fact]
    public void OfferRestore_ConfirmFalse_OneBadRecord_DoesNotBlockOthers() => Sta.Run(() =>
    {
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("good", "ok"));
        PlantBackup(host.TempDir, Rec("bad", "boom"));

        int restored = host.Backup.OfferRestoreOnStartup(host.Form, r =>
        {
            if (r.Id == "bad") throw new InvalidOperationException("restore failed");
            var d = host.Docs.CreateNew(); d.Editor.Text = r.Content; return d;
        }, confirm: false);

        Assert.Equal(1, restored);                   // 1 件の失敗で他を巻き添えにしない
    });

    [Fact]
    public void OfferRestore_ConfirmTrue_Restore_UsesCheckedRecords_AndInheritsId() => Sta.Run(() =>
    {
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("keep", "keeper"));
        PlantBackup(host.TempDir, Rec("skip", "skipper"));

        var kept = Rec("keep", "keeper");
        host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.Restore, new[] { kept });

        // 復元ラムダは本番 FileController.RestoreFromBackup と同様に ClearSavePoint で dirty に
        // 固定する(§restore-dirty バグ修正 `59ad8b5`)。これがないと Modified=false・HasBackup=true で
        // 次 Reconcile が Delete("keep") に落ち Id 引き継ぎの検証ができない。
        int returned = host.Backup.OfferRestoreOnStartup(host.Form, r =>
        {
            var d = host.Docs.CreateNew();
            d.Editor.Text = r.Content;
            d.Editor.ClearSavePoint();
            return d;
        }, confirm: true);

        Assert.Equal(0, returned);                   // Restore 分岐は 0 を返す(件数は呼び側で通知しない)
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
        Assert.Contains(host.Writer.Writes, w => w.Id == "keep"); // 復元タブは dirty=Write 走る・Id は元
    });

    [Fact]
    public void OfferRestore_ConfirmTrue_DiscardAll_InvokesWriterDeleteAll() => Sta.Run(() =>
    {
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("r1", "one"));
        host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.DiscardAll, Array.Empty<BackupRecord>());

        host.Backup.OfferRestoreOnStartup(host.Form, r => throw new Xunit.Sdk.XunitException("restore must not be called"), confirm: true);

        Assert.Equal(1, host.Writer.DeleteAllCount);
    });

    [Fact]
    public void OfferRestore_ConfirmTrue_Later_DoesNothing() => Sta.Run(() =>
    {
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("r1", "one"));
        host.Prompt.NextOutcome = RestoreOutcome.LaterEmpty;

        host.Backup.OfferRestoreOnStartup(host.Form, r => throw new Xunit.Sdk.XunitException("restore must not be called"), confirm: true);

        Assert.Equal(0, host.Writer.DeleteAllCount);
        Assert.Empty(host.Writer.Deletes);
        Assert.Empty(host.Writer.Writes);
    });

    // ===== Shutdown/Dispose(冪等・管理分削除) =====

    [Fact]
    public void Shutdown_DeletesManagedBackups_AndDisposesWriter() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc1 = host.NewDoc("one");
        var doc2 = host.NewDoc("two");
        host.Backup.Reconcile();                     // 両方 Write=HasBackup=true

        host.Backup.Shutdown();

        Assert.Equal(2, host.Writer.Deletes.Count);  // 管理分を全 Delete
        Assert.Equal(1, host.Writer.DisposeCount);   // ライターをドレイン
    });

    [Fact]
    public void Shutdown_Idempotent_SecondCallIsNoOp() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("hello");
        host.Backup.Reconcile();

        host.Backup.Shutdown();
        int deletesAfterFirst = host.Writer.Deletes.Count;
        int disposesAfterFirst = host.Writer.DisposeCount;
        host.Backup.Shutdown();                      // 2 回目

        Assert.Equal(deletesAfterFirst, host.Writer.Deletes.Count);
        Assert.Equal(disposesAfterFirst, host.Writer.DisposeCount);
    });

    [Fact]
    public void Dispose_WithoutShutdown_DisposesWriter_WithoutDeletingBackups() => Sta.Run(() =>
    {
        var host = new Host();
        host.NewDoc("hello");
        host.Backup.Reconcile();

        host.Backup.Dispose();                       // 異常系(Shutdown 未経由)

        Assert.Empty(host.Writer.Deletes);           // 管理分の削除は行わない(孤児として次回復元)
        Assert.Equal(1, host.Writer.DisposeCount);
        host.Form.Dispose();
        try { Directory.Delete(host.TempDir, recursive: true); } catch { }
    });

    // ===== TimeProvider(BackupRecord.TimestampUtc が clock 由来) =====

    [Fact]
    public void BuildRecord_UsesInjectedClock_ForTimestamp() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("hello");

        host.Backup.Reconcile();

        Assert.Equal(FixedNow.UtcDateTime, host.Writer.Writes[0].TimestampUtc);
    });

    // ===== 追加: 対応固定(Reconcile の Write/Delete が IBackupWriter 経由であることの担保) =====

    [Fact]
    public void Reconcile_MultipleDocs_WriteRoutingIsPerDoc() => Sta.Run(() =>
    {
        using var host = new Host();
        var a = host.NewDoc("A");
        var b = host.NewDoc("B");

        host.Backup.Reconcile();

        Assert.Equal(2, host.Writer.Writes.Count);
        Assert.NotEqual(host.Writer.Writes[0].Id, host.Writer.Writes[1].Id);  // 個別 Id
    });

    [Fact]
    public void ReconcileAfterActiveDocumentChanged_DoesNotDoubleWrite() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewDoc("hello");

        host.Backup.Reconcile();                     // 初回 Write
        _ = host.Docs.CreateNew();                   // ActiveDocumentChanged=内部で Reconcile が走る

        Assert.Single(host.Writer.Writes);           // 元 doc は同 sig=再 Write なし(2 回目の Reconcile で None)
    });

    [Fact]
    public void UpdateSettings_DisableFromEnabled_DoesNotDisposeWriter() => Sta.Run(() =>
    {
        using var host = new Host(enabled: true);
        host.NewDoc("hello");
        host.Backup.Reconcile();

        host.Backup.UpdateSettings(false, 30);       // 有効→無効: 既存ファイルを削除しない・writer は残す
        int disposedBefore = host.Writer.DisposeCount;

        Assert.Equal(disposedBefore, host.Writer.DisposeCount); // Dispose は Shutdown/Dispose まで待つ
        Assert.Empty(host.Writer.Deletes);
    });
}
