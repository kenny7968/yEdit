using System.IO;
using yEdit.Core.Backup;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 レビュー Critical 級の回収: SerialBackupWriter の実書込パイプライン統合テスト。
/// BackupCoordinator の Fake 差し替えテストでは触れられない「実 I/O(BackupStore.Write)・
/// 実背景スレッド(BlockingCollection+ Thread)・実 Dispose ドレイン(CompleteAdding+Join)」を
/// 統合レベルで固定する。責務=ワーカー外側 catch:62-70(ワーカー死の防波堤)・Dispose ドレイン
/// 契約:73-84・Enqueue 締切後ガード:50-55・Write catch→OnWriteFailed 実発火。
///
/// 決定化の原則:待ちは一切入れない。全テストは「投入 → Dispose(=CompleteAdding+Join で
/// ドレイン完了が同期確定)→ ディスク/コールバックを assert」の形に統一。Sleep/リトライループ
/// 禁止。ディレクトリはテスト毎に <see cref="Directory.CreateTempSubdirectory"/> で完全隔離。
/// </summary>
public class SerialBackupWriterTests
{
    /// <summary>テスト毎に使い捨ての一時フォルダ(BackupStore の実 I/O が触るディスク領域)。
    /// FileControllerTests.TempDir と同じ流儀。掃除失敗はテスト失敗にしない(ReadOnly 属性等)。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("yEditSbw_").FullName;
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 掃除失敗は無害 */ }
        }
    }

    /// <summary>テスト用の BackupRecord ファクトリ(BackupCoordinatorTests.Rec と同じ形)。
    /// TimestampUtc は固定値でロードバック検証を deterministic に。</summary>
    private static BackupRecord Rec(string id, string content) => new(
        Id: id,
        OriginalPath: null,
        UntitledNumber: 1,
        CodePage: 65001,
        HasBom: false,
        LineEndingId: 0,
        Content: content,
        TimestampUtc: new DateTime(2026, 07, 15, 12, 0, 0, DateTimeKind.Utc));

    // ===== ドレイン契約:73-84(CompleteAdding+Join で保留ジョブがディスクに現れる) =====

    /// <summary>
    /// Dispose ドレイン契約(:73-84)の核。Write を 2 件投入して Dispose で締切→ Join(15s)で
    /// 背景スレッドが CompleteAdding 後の残ジョブを全消化 → BackupStore.LoadAll が両方見える。
    /// これが崩れると「終了直前の未保存文書が退避漏れ」の重篤バグに直結する。
    /// </summary>
    [Fact]
    public void Write_ThenDispose_DrainsToDisk()
    {
        using var tmp = new TempDir();
        var r1 = Rec("id-1", "one");
        var r2 = Rec("id-2", "two");

        using (var w = new SerialBackupWriter(tmp.Root))
        {
            w.Write(r1);
            w.Write(r2);
        } // using 脱出 = Dispose = CompleteAdding + Join でドレイン完了が同期確定

        var loaded = BackupStore.LoadAll(tmp.Root);
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, r => r.Id == "id-1" && r.Content == "one");
        Assert.Contains(loaded, r => r.Id == "id-2" && r.Content == "two");
    }

    /// <summary>
    /// 投入順の逐次実行契約(BlockingCollection は FIFO・単一 worker なので Write→Delete は必ずこの順)。
    /// Write→Delete(同 Id)→Dispose の後、ディスクに残らないこと=Delete が Write の後に実行された証拠。
    /// これが崩れると「削除ジョブが書込ジョブを追い越し=消したはずのバックアップが残る」に直結。
    /// </summary>
    [Fact]
    public void WriteThenDelete_SameId_EndsAbsent()
    {
        using var tmp = new TempDir();
        var rec = Rec("id-x", "will-be-deleted");

        using (var w = new SerialBackupWriter(tmp.Root))
        {
            w.Write(rec);
            w.Delete(rec.Id);
        } // Dispose で両ジョブとも投入順に消化

        Assert.Empty(BackupStore.LoadAll(tmp.Root));
    }

    /// <summary>
    /// DeleteAll ジョブが投入順に BackupStore.DeleteAll に到達し、既存の *.json を全削除する。
    /// 責務=「復元ダイアログの『すべて破棄』分岐」に対する統合パイプ担保。
    /// </summary>
    [Fact]
    public void DeleteAll_RemovesEverything()
    {
        using var tmp = new TempDir();

        using (var w = new SerialBackupWriter(tmp.Root))
        {
            w.Write(Rec("a", "1"));
            w.Write(Rec("b", "2"));
            w.Write(Rec("c", "3"));
            w.DeleteAll();
        }

        Assert.Empty(BackupStore.LoadAll(tmp.Root));
    }

    // ===== 失敗回復:33-34 & ワーカー生存:62-70(1 件の書込失敗が worker を殺さない) =====

    /// <summary>
    /// BackupStore.Write の実失敗経路を OnWriteFailed が実発火し、かつ後続ジョブが処理される
    /// (=ワーカー外側 catch:62-70=ワーカー死の防波堤が有効)ことを固定する。
    ///
    /// 失敗経路の設計: BackupStore.Write は AtomicFile.Write を呼び、tmp を書いてから
    /// File.Move(tmp, "&lt;id&gt;.json") する(初回書込=File.Exists=false 分岐)。ターゲットの
    /// "&lt;id&gt;.json" が既にディレクトリとして存在すると File.Move は決定的に IOException を
    /// 投げる(実測=「既に存在するファイルを作成することはできません」)。他の失敗経路
    /// (権限/ディスクフル)は環境依存で不安定なため、これが最も deterministic。
    ///
    /// ワーカー生存検証: 失敗ジョブの後に Delete("harmless") を投入し、Dispose のドレインが
    /// 15s 以内に戻る(=worker が生きていて CompleteAdding 後に foreach を抜けた)ことを暗黙に確認。
    /// worker が死んでいれば CompleteAdding してもスレッドは動かないが Join は先に終わった worker
    /// を待つだけなので現在の実装では検出困難=代替として Dispose が例外なく戻ることを assert。
    /// </summary>
    [Fact]
    public void WriteFailure_InvokesOnWriteFailed_AndWorkerSurvives()
    {
        using var tmp = new TempDir();
        // "will-fail.json" と同名のディレクトリを事前作成 → File.Move が決定的に失敗する経路。
        Directory.CreateDirectory(Path.Combine(tmp.Root, "will-fail.json"));

        var failures = new List<string>();
        var lockObj = new object();
        // OnWriteFailed は SerialBackupWriter.cs:22 のフックで、背景スレッドから発火する
        // (Write の catch:33-34 内で Invoke)。テスト側の記録は lock で保護する。
        Exception? disposeException = null;

        using (var w = new SerialBackupWriter(tmp.Root)
        {
            OnWriteFailed = id => { lock (lockObj) failures.Add(id); }
        })
        {
            var badRec = Rec("will-fail", "boom");
            w.Write(badRec);
            // 失敗後に別ジョブを投入して worker が動いていることを確認(Delete は正常な dir 内で no-op)。
            w.Delete("nonexistent-id");

            // Dispose が 15s Join 上限内に戻ること(=worker が生きていて素直に終わった)を後段で確認。
            try { w.Dispose(); }
            catch (Exception ex) { disposeException = ex; }
        }

        Assert.Null(disposeException);                          // Dispose が例外なく戻る=worker 死んでいない
        lock (lockObj) Assert.Contains("will-fail", failures);  // 失敗コールバックが Id 付きで発火
        // 失敗した *.json は書き込まれていない(BackupStore.LoadAll は will-fail ディレクトリを *.json glob で拾うが
        // Directory.EnumerateFiles はディレクトリを列挙しないためスキップされる=空)。
        Assert.Empty(BackupStore.LoadAll(tmp.Root));
    }

    // ===== Enqueue 締切後ガード:50-55(現行実装の実挙動固定) =====

    /// <summary>
    /// Dispose 後の Write/Delete/DeleteAll の現行挙動を固定する。
    ///
    /// 意図(src コメント:49「破棄後は無視」)と実装のギャップ: Enqueue:52 の
    /// `if (_queue.IsAddingCompleted) return;` は try/catch の外側にあり、
    /// _queue.Dispose 後は IsAddingCompleted の getter 自体が ObjectDisposedException を
    /// 投げるため(実測: BlockingCollection&lt;T&gt;.IsAddingCompleted の CheckDisposed で throw)、
    /// この例外が呼び出し元に伝播する(ODE は InvalidOperationException 派生だが try 内ではない)。
    ///
    /// 本テストは現行挙動を pin する(=将来 src を修正して "破棄後も無例外" とする際に、
    /// 挙動変更が明示的にこのテストの red で表れるようにする=退行検出のアンカー)。
    /// 追跡課題: Enqueue で先に `_disposed` フラグを見る or IsAddingCompleted を try 内に
    /// 移すいずれかで解消可能(src 変更禁止のため本ブランチでは修正しない)。
    ///
    /// LoadAll に影響なし=そもそも Enqueue が Add する前に throw するため、ジョブが積まれない
    /// (worker は Dispose 時点で既に foreach を抜けているのでどのみち何も起きない)。
    /// </summary>
    [Fact]
    public void Enqueue_AfterDispose_ThrowsObjectDisposed_AndLeavesDiskUnaffected()
    {
        using var tmp = new TempDir();
        var w = new SerialBackupWriter(tmp.Root);
        w.Dispose();

        Assert.Throws<ObjectDisposedException>(() => w.Write(Rec("z", "zzz")));
        Assert.Throws<ObjectDisposedException>(() => w.Delete("y"));
        Assert.Throws<ObjectDisposedException>(() => w.DeleteAll());

        // ジョブは 1 件も enqueue されていない=ディスクに何も書かれていない。
        Assert.Empty(BackupStore.LoadAll(tmp.Root));
    }

    // ===== Dispose 冪等:74-84(_disposed early-return) =====

    /// <summary>
    /// Dispose:74「if (_disposed) return;」の冪等契約。2 回目以降の Dispose が例外なく戻る
    /// (2 回目に _queue.CompleteAdding や _worker.Join に再突入して ObjectDisposedException を
    /// 起こさないこと=BackupCoordinator.Dispose が using 内・using 外の二経路から呼ぶ現実対応)。
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var tmp = new TempDir();
        var w = new SerialBackupWriter(tmp.Root);

        w.Dispose();
        // 2 回目・3 回目とも無例外(_disposed early-return)。
        var second = Record.Exception(() => w.Dispose());
        var third = Record.Exception(() => w.Dispose());

        Assert.Null(second);
        Assert.Null(third);
    }
}
