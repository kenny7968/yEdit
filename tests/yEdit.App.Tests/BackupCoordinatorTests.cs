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
}
