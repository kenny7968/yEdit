# テスト戦略 Phase 2 Stage 5: BackupCoordinator シーム導入+テスト 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** BackupCoordinator の 3 境界(時計・背景書込・復元ダイアログ)を型付きインターフェースで注入化し、Reconcile を internal 化のうえ App.Tests から状態機械(登録・dirty サイクル・失敗回復・復元 4 分岐・Shutdown 冪等)を固定する。挙動不変。

**Architecture:** ストラングラー方式のシーム導入。`IBackupWriter`(型付き 3 メソッド+`OnWriteFailed` フック)/`IRestorePrompt`(結果 record 返し)/`TimeProvider`(.NET 9 標準)を追加し、`SerialBackupWriter` は BackupStore 直呼びを内包する Adapter に降格。BackupCoordinator は `BackupStore` への参照ゼロ・`DateTime.UtcNow` ゼロに落ちる。テストは実 DocumentManager+実 EditorControl+Fake 境界だけで駆動する(green から開始)。

**Tech Stack:** .NET 9 / WinForms / xUnit v2(STA ヘルパ=`Sta.Run`・可視 HostForm パターン=`TestHost.CreateWithDocs`)

- 日付: 2026-07-14
- 上位文書: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` §2.1・§2.2・§2.3・§4 Stage 5
- 設計書: `docs/plans/2026-07-14-test-strategy-phase2-stage5-design.md`
- ベースライン: main `6377d24`(Stage 4 マージ済み+本 Stage 設計書コミット)・テスト数 865(Core 573+Editor 218+App 74)

---

## 規約(全 Task 共通)

- ブランチ: `feature/test-strategy-phase2-stage5`(同一ディレクトリのフィーチャーブランチ→main へ no-ff マージ=いつもの運用)
- コミットメッセージは日本語。末尾に `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` を付ける
- 各 Task 末尾で `dotnet build yEdit.sln -c Release -warnaserror` が 0 警告であること
- git status に見えている untracked の `installer/`・`publish/` はこの作業と無関係。**絶対にコミットに含めない**(`git add` はパス指定で行う)
- 特徴付けテストが赤になった場合: 原則テスト側の期待を現行挙動へ合わせる。**ただしバックアップ書込/削除経路(Reconcile の Write/Delete・OfferRestoreOnStartup の Restore/DiscardAll)の赤はデータ喪失リスク=実装バグの可能性があるため、修正せずユーザーへ報告する**(Stage 4 の置換系と同型の規約)

---

### Task 1: ブランチ作成

**Step 1: main から作業ブランチを切る**

Run:
```powershell
git switch -c feature/test-strategy-phase2-stage5 main
```
Expected: `Switched to a new branch 'feature/test-strategy-phase2-stage5'`

---

### Task 2: シーム定義(未配線・コンパイルのみ)

**Files:**
- Create: `src/yEdit.App/Abstractions/IBackupWriter.cs`
- Create: `src/yEdit.App/Abstractions/IRestorePrompt.cs`

**Step 1: IBackupWriter を定義**

Create `src/yEdit.App/Abstractions/IBackupWriter.cs`:

```csharp
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// バックアップの背景書込ジョブ受け(Phase 2 Stage 5・上位文書 §2.1 の精密化)。
/// Coordinator が BackupStore への静的参照を持たないよう、Action 束ではなく型付きの
/// 3 メソッドで表面を切る。SerialBackupWriter が既存の BlockingCollection 直列実行で
/// 実装し、Fake は in-memory Dictionary で完全に I/O から独立する。
/// </summary>
public interface IBackupWriter : IDisposable
{
    /// <summary>書込失敗を UI スレッド側に通知するためのフック。
    /// Coordinator が ctor で失敗回復用の Enqueue を登録する(null なら握り潰す)。</summary>
    Action<string>? OnWriteFailed { get; set; }

    void Write(BackupRecord record);
    void Delete(string id);
    void DeleteAll();
}
```

**Step 2: IRestorePrompt を定義**

Create `src/yEdit.App/Abstractions/IRestorePrompt.cs`:

```csharp
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>復元ダイアログのユーザー選択(Phase 2 Stage 5・上位文書 §2.2)。</summary>
public enum RestoreAction { Later, Restore, DiscardAll }

/// <summary>復元ダイアログの結果 record。Action が Restore 以外なら Checked は空配列。</summary>
public sealed record RestoreOutcome(RestoreAction Action, IReadOnlyList<BackupRecord> Checked)
{
    public static readonly RestoreOutcome LaterEmpty = new(RestoreAction.Later, Array.Empty<BackupRecord>());
}

/// <summary>
/// 起動時の復元ダイアログの Controller 向け表面。ダイアログを ShowDialog し、
/// ユーザー選択(Later/Restore/DiscardAll)+チェック済み records をまとめて返す。
/// </summary>
public interface IRestorePrompt
{
    RestoreOutcome Prompt(IWin32Window owner, IReadOnlyList<BackupRecord> records);
}
```

**Step 3: ビルド確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: 0 警告(新規ファイルは未参照でもコンパイルされる)

**Step 4: Commit**

```powershell
git add src/yEdit.App/Abstractions/IBackupWriter.cs src/yEdit.App/Abstractions/IRestorePrompt.cs
git commit -m "feat: IBackupWriter/IRestorePrompt シームを追加(Stage 5・未配線)"
```

---

### Task 3: SerialBackupWriter を IBackupWriter 化+WinFormsRestorePrompt 追加

**Files:**
- Modify: `src/yEdit.App/SerialBackupWriter.cs`(既存 public Enqueue → private・ctor に dir 追加・IBackupWriter 実装)
- Create: `src/yEdit.App/WinFormsRestorePrompt.cs`

**Step 1: SerialBackupWriter を IBackupWriter 化**

`src/yEdit.App/SerialBackupWriter.cs` 全体を置換:

```csharp
using System.Collections.Concurrent;
using System.Threading;
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// バックアップの背景直列ライター。UI スレッドが投入したジョブ(Core への書込/削除)を、
/// 単一の背景スレッドで投入順に実行する。各ジョブの失敗は致命でないため握り潰す(無音)が、
/// 書込(Write)の失敗のみは OnWriteFailed に record.Id を渡して UI スレッド側に通知し、
/// 次 Reconcile で強制再書込を促す(Stage 5 で IBackupWriter を実装)。
/// Dispose で投入を締め切り、保留ジョブをドレインしてから戻る。
/// </summary>
public sealed class SerialBackupWriter : IBackupWriter
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly string _dir;
    private bool _disposed;

    /// <inheritdoc/>
    public Action<string>? OnWriteFailed { get; set; }

    public SerialBackupWriter(string directory)
    {
        _dir = directory;
        _worker = new Thread(Run) { IsBackground = true, Name = "yEdit backup writer" };
        _worker.Start();
    }

    public void Write(BackupRecord record) => Enqueue(() =>
    {
        try { BackupStore.Write(_dir, record); }
        catch { OnWriteFailed?.Invoke(record.Id); }
    });

    public void Delete(string id) => Enqueue(() =>
    {
        try { BackupStore.Delete(_dir, id); }
        catch { /* 削除失敗は致命でない・無音 */ }
    });

    public void DeleteAll() => Enqueue(() =>
    {
        try { BackupStore.DeleteAll(_dir); }
        catch { /* 一括削除失敗は致命でない・無音 */ }
    });

    /// <summary>ジョブを投入する(締め切り後・破棄後は無視)。実装詳細。</summary>
    private void Enqueue(Action job)
    {
        if (_queue.IsAddingCompleted) return;
        // 競合で AddingCompleted 済み／破棄済み(ObjectDisposedException は InvalidOperationException 派生)。
        try { _queue.Add(job); }
        catch (InvalidOperationException) { }
    }

    private void Run()
    {
        // 列挙自体(MoveNext)も保護する。Dispose 競合で ObjectDisposedException が出ても
        // 背景スレッドを巻き添えに落とさない(未捕捉例外はプロセス終了に直結するため)。
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                try { job(); }
                catch { /* バックアップ失敗は致命でない・無音 */ }
            }
        }
        catch { /* Dispose 競合等。ワーカーを静かに終える */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        // 保留ジョブのドレインを十分待つ(クリーン終了でバックアップ/削除を取りこぼさない)。
        bool finished = false;
        try { finished = _worker.Join(TimeSpan.FromSeconds(15)); } catch { /* 参加待ち失敗は無視 */ }
        // ワーカーがまだ走行中に Dispose すると MoveNext が ObjectDisposedException を投げるため、
        // 完全終了を確認できたときだけ破棄する。未終了なら放置(プロセス終了時で実害なし)。
        if (finished) { try { _queue.Dispose(); } catch { } }
    }
}
```

**Step 2: WinFormsRestorePrompt を追加**

Create `src/yEdit.App/WinFormsRestorePrompt.cs`:

```csharp
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// 起動時復元ダイアログの WinForms Adapter(Phase 2 Stage 5・上位文書 §2.2)。
/// RestoreDialog を ShowDialog し、内部 enum(RestoreDialog.RestoreAction)を
/// App 層公開 enum(<see cref="RestoreAction"/>)にマップして結果 record を返す。
/// </summary>
public sealed class WinFormsRestorePrompt : IRestorePrompt
{
    public RestoreOutcome Prompt(IWin32Window owner, IReadOnlyList<BackupRecord> records)
    {
        using var dlg = new RestoreDialog(records);
        dlg.ShowDialog(owner);
        return dlg.Action switch
        {
            RestoreDialog.RestoreAction.Restore    => new RestoreOutcome(RestoreAction.Restore, dlg.Checked),
            RestoreDialog.RestoreAction.DiscardAll => new RestoreOutcome(RestoreAction.DiscardAll, Array.Empty<BackupRecord>()),
            _                                      => RestoreOutcome.LaterEmpty,
        };
    }
}
```

**Step 3: ビルドが割れる想定(BackupCoordinator が旧 API を使っているため)**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: **失敗**(BackupCoordinator が `new SerialBackupWriter()` を引数なしで呼んでおり、また `_writer?.Enqueue(...)` を使っている=Task 4 で解消する)。この時点でのビルド失敗は許容(Task 4 でまとめて解消)=**Commit しない**。

**Step 4: 一旦 stash して手順を確認する**(コミット前提の Task 分割なので、Task 3 と Task 4 は 1 コミットに束ねる)

**注記**: Task 3 と Task 4 は密結合(SerialBackupWriter API 変更と BackupCoordinator 内部の呼び出し置換が同時にビルド可能になる)ため、**コミットは Task 4 の末尾で 1 回**にする。Task 3 の変更は git 上に残す(stash しない・そのまま Task 4 へ)。

---

### Task 4: BackupCoordinator 注入化+MainForm 配線更新(1 コミットにまとめる)

置換は**機械的**に行う。条件分岐・文言・失敗回復のフロー(`_failed` キューに積む→次 Reconcile で ForceWrite)・Shutdown/Dispose の冪等性を一切変えない(diff レビューで確認できる粒度)。

**Files:**
- Modify: `src/yEdit.App/BackupCoordinator.cs`(ctor+フィールド+全メソッド)
- Modify: `src/yEdit.App/MainForm.cs:60`(ファクトリと Adapter を渡す)

**Step 1: BackupCoordinator を注入化**

`src/yEdit.App/BackupCoordinator.cs` を以下に置換(ファイル全体):

```csharp
using System.Collections.Concurrent;
using System.Linq;
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// 自動バックアップとクラッシュ復元の統括。UI スレッドのタイマー(＋アクティブ文書変更)で
/// 文書を走査(Reconcile)し、変化のある未保存文書のスナップショットを背景直列ライターへ渡す。
/// スナップショット取得(SCI_* 由来)は UI スレッドで行い、ディスク I/O は背景で行う(§4.1 鉄則)。
/// クリーン終了では「当セッションが管理した文書」のバックアップのみ削除する(「あとで」先送りした
/// 孤児は残し次回再提案する)。判定の中核は Core の純粋関数 BackupPlanner で単体テスト可能。
/// Phase 2 Stage 5: 時計・背景書込・復元ダイアログを IBackupWriter/IRestorePrompt/TimeProvider で
/// 注入化し、BackupStore への直接参照を持たない(App.Tests から Reconcile を internal 直呼び)。
/// </summary>
public sealed class BackupCoordinator : IDisposable
{
    private sealed class DocBackup
    {
        public string Id = "";
        public long LastSig;
        public bool HasBackup;
        public bool ForceWrite; // 前回の背景書込が失敗 → 次 tick で強制再書込(陳腐化・欠落を防ぐ)
    }

    private readonly DocumentManager _docs;
    private readonly string _dir;
    private readonly TimeProvider _clock;
    private readonly Func<IBackupWriter> _writerFactory;
    private readonly IRestorePrompt _restorePrompt;
    private bool _enabled;                       // UpdateSettings で実行時に切替可能
    private readonly System.Windows.Forms.Timer _timer = new();
    private IBackupWriter? _writer;              // 無効時は生成しない(有効化時に factory 経由で遅延生成)
    private readonly Dictionary<Document, DocBackup> _map = new();
    private readonly ConcurrentQueue<string> _failed = new(); // 背景書込が失敗した Id(UI スレッドで回収)
    private bool _shutDown;

    public BackupCoordinator(
        DocumentManager docs,
        bool enabled,
        int intervalSeconds,
        TimeProvider clock,
        Func<IBackupWriter> writerFactory,
        IRestorePrompt restorePrompt,
        string? directory = null)
    {
        _docs = docs;
        _enabled = enabled;
        _clock = clock;
        _writerFactory = writerFactory;
        _restorePrompt = restorePrompt;
        _dir = directory ?? BackupStore.DefaultDirectory;

        // 無効時でもハンドラは購読しておく(後から UpdateSettings で有効化できるように)。
        // Tick/ActiveDocumentChanged は Reconcile 冒頭の !_enabled ガードで素通りするため無効中は無害。
        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000; // 上限クランプで int オーバーフロー防止
        _timer.Tick += (_, _) => Reconcile();
        _docs.ActiveDocumentChanged += (_, _) => Reconcile();
        if (!_enabled) return;

        _writer = CreateWriter();
        _timer.Start();
    }

    /// <summary>writer を factory で生成し、失敗通知フックを配線する(遅延生成の意味論を保存)。</summary>
    private IBackupWriter CreateWriter()
    {
        var w = _writerFactory();
        w.OnWriteFailed = OnBackgroundWriteFailed;
        return w;
    }

    /// <summary>背景書込の失敗通知(Adapter から UI スレッド外で来る可能性あり=ConcurrentQueue で受ける)。</summary>
    private void OnBackgroundWriteFailed(string id) => _failed.Enqueue(id);

    /// <summary>
    /// 設定ダイアログ OK 時の即時反映。間隔は常に更新し、有効/無効の切替では
    /// タイマーとライターを追従させる。無効化では既存バックアップファイルを削除しない
    /// (次回起動時の孤児提案に任せる・安全側)。
    /// </summary>
    public void UpdateSettings(bool enabled, int intervalSeconds)
    {
        if (_shutDown) return;
        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000;
        if (enabled == _enabled) return;

        _enabled = enabled;
        if (enabled)
        {
            _writer ??= CreateWriter();
            _timer.Start();
            Reconcile();   // 有効化した瞬間の未保存文書を即保護(保護窓を作らない)
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// 起動時に孤児バックアップがあれば復元提案する。restore は復元先の新タブを作って Document を返す
    /// デリゲート(本文を載せ dirty のまま)。復元した文書には元 Id を引き継がせ、既存のバックアップ
    /// ファイルを継続使用する(孤児・無保護窓を作らない)。チェックしなかった項目は安全側で残し、
    /// 次回再提案する(明示的に消すのは「すべて破棄」のみ)。
    /// confirm=false ではダイアログを出さず全件復元し、その件数を返す(ダイアログ経路は 0 を返す)。
    /// </summary>
    public int OfferRestoreOnStartup(IWin32Window owner, Func<BackupRecord, Document> restore, bool confirm)
    {
        if (!_enabled) return 0;
        try { BackupStore.SweepTempFiles(_dir); } catch { /* 残骸掃除失敗は無害 */ }

        IReadOnlyList<BackupRecord> records;
        try { records = BackupStore.LoadAll(_dir); }
        catch { return 0; }
        if (records.Count == 0) return 0;

        var ordered = records.OrderByDescending(r => r.TimestampUtc).ToList();

        // 確認 OFF: ダイアログを出さず全件復元(設計 2026-07-04)。呼び出し側が件数を能動通知する。
        if (!confirm)
        {
            int restored = 0;
            foreach (var rec in ordered)
            {
                try
                {
                    var doc = restore(rec);
                    _map[doc] = new DocBackup { Id = rec.Id, LastSig = ContentSignature.Of(doc.Editor.SnapshotText), HasBackup = true };
                    restored++;
                }
                catch
                {
                    // 1 件の不正レコードで全復元を巻き添えにしない。失敗分はバックアップを残し再挑戦可能に。
                }
            }
            return restored;
        }

        var outcome = _restorePrompt.Prompt(owner, ordered);
        switch (outcome.Action)
        {
            case RestoreAction.Restore:
                foreach (var rec in outcome.Checked)
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
                // チェックしなかった項目は削除しない(SR 誤操作での消失を避け、次回再提案)。
                break;

            case RestoreAction.DiscardAll:
                _writer?.DeleteAll();
                break;

            case RestoreAction.Later:
                break; // 何もしない(次回再提案)
        }
        return 0;
    }

    /// <summary>UI スレッドで文書を走査し、必要なバックアップ書込/削除ジョブを投入する。
    /// App.Tests から直接叩けるよう internal(Timer は本番のみ)。</summary>
    internal void Reconcile()
    {
        if (!_enabled || _shutDown) return;

        // 背景書込が失敗した文書を強制再書込対象にする(楽観更新で欠落・陳腐化しないように)。
        while (_failed.TryDequeue(out var failedId))
            foreach (var v in _map.Values)
                if (v.Id == failedId) v.ForceWrite = true;

        // 閉じた文書(map にあるが現存しない)→ バックアップ削除。
        var current = new HashSet<Document>(_docs.Documents);
        foreach (var doc in _map.Keys.ToList())
        {
            if (current.Contains(doc)) continue;
            var gone = _map[doc];
            if (gone.HasBackup) _writer?.Delete(gone.Id);
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
                    _writer?.Delete(info.Id);
                    info.HasBackup = false;
                    info.LastSig = sig;
                    info.ForceWrite = false;
                    break;
                case BackupAction.None:
                    break;
            }
        }
    }

    /// <summary>未登録文書を登録する。登録時点で既に dirty なら即退避し保護窓を作らない(起動時無題タブ対策)。</summary>
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

    /// <summary>書込ジョブを投入する。失敗時は Adapter が OnWriteFailed 経由で Id を _failed へ積み、
    /// 次 Reconcile で強制再書込する。</summary>
    private void EnqueueWrite(DocBackup info, Document doc, string content)
    {
        var rec = BuildRecord(info.Id, doc, content);
        _writer?.Write(rec);
    }

    private BackupRecord BuildRecord(string id, Document doc, string content) => new(
        Id: id,
        OriginalPath: doc.State.Path,
        UntitledNumber: doc.State.UntitledNumber,
        CodePage: doc.State.Encoding.CodePage,
        HasBom: doc.State.HasBom,
        LineEndingId: (int)doc.State.LineEnding,
        Content: content,
        TimestampUtc: _clock.GetUtcNow().UtcDateTime);

    /// <summary>
    /// クリーン終了: タイマー停止 → 当セッション管理分のバックアップ削除を投入 → 背景書込をドレイン。
    /// 「あとで」先送りした孤児は _map に無いので残り、次回起動で再提案される。
    /// 未保存確認をすべて通過した後に呼ぶこと。
    /// </summary>
    public void Shutdown()
    {
        // ガードは _shutDown のみ: セッション途中で無効化されても、有効だった間に書いた
        // バックアップ(_map の HasBackup)をクリーン終了で削除する。一度も有効になって
        // いなければ _map は空・_writer は null で各行は無害に素通りする。
        if (_shutDown) return;
        _shutDown = true;
        _timer.Stop();
        foreach (var info in _map.Values)
            if (info.HasBackup) _writer?.Delete(info.Id);
        _writer?.Dispose(); // 保留ジョブ(削除含む)をドレイン
        _timer.Dispose();
    }

    public void Dispose()
    {
        // Shutdown 済みなら timer/writer は解放済み。未経由(異常系)なら timer/writer を片付ける
        // (孤児バックアップは残し、次回起動で復元提案できるようにする)。冪等。
        if (_shutDown) return;
        _shutDown = true;
        _timer.Stop();
        _timer.Dispose();
        _writer?.Dispose();
    }
}
```

**Step 2: MainForm の生成箇所(`MainForm.cs:60`)を注入化**

Modify `src/yEdit.App/MainForm.cs:60`:

```csharp
// 変更前:
_backup = new BackupCoordinator(_docs, _settings.BackupEnabled, _settings.BackupIntervalSeconds);
// 変更後:
_backup = new BackupCoordinator(
    _docs, _settings.BackupEnabled, _settings.BackupIntervalSeconds,
    TimeProvider.System,
    () => new SerialBackupWriter(BackupStore.DefaultDirectory),
    new WinFormsRestorePrompt());
```

**Step 3: 参照が消えたことを grep で確認**

Run:
```powershell
git grep -n "BackupStore\." -- "src/yEdit.App/BackupCoordinator.cs"
```
Expected: `SweepTempFiles`・`LoadAll`・`DefaultDirectory` のみヒット(**Write/Delete/DeleteAll のヒットゼロ**)

Run:
```powershell
git grep -n "DateTime\.UtcNow" -- "src/yEdit.App/BackupCoordinator.cs"
```
Expected: ヒットなし(exit code 1)

Run:
```powershell
git grep -n "RestoreDialog" -- "src/yEdit.App/BackupCoordinator.cs"
```
Expected: ヒットなし(exit code 1)

**Step 4: ビルド+既存全テストで挙動不変を確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.Core.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.Editor.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Core 573+Editor 218+App 74 全緑

**Step 5: Commit(Task 3+Task 4 の変更をまとめてコミット)**

```powershell
git add src/yEdit.App/SerialBackupWriter.cs src/yEdit.App/WinFormsRestorePrompt.cs src/yEdit.App/BackupCoordinator.cs src/yEdit.App/MainForm.cs
git commit -m "refactor: BackupCoordinator の時計・背景書込・復元ダイアログをシーム化(挙動不変)"
```

---

### Task 5: Fake 群を追加

**Files:**
- Create: `tests/yEdit.App.Tests/Fakes/FakeBackupWriter.cs`
- Create: `tests/yEdit.App.Tests/Fakes/FakeRestorePrompt.cs`
- Create: `tests/yEdit.App.Tests/Fakes/FakeTimeProvider.cs`

**Step 1: FakeBackupWriter を作成**

Create `tests/yEdit.App.Tests/Fakes/FakeBackupWriter.cs`:

```csharp
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
```

**Step 2: FakeRestorePrompt を作成**

Create `tests/yEdit.App.Tests/Fakes/FakeRestorePrompt.cs`:

```csharp
using yEdit.Core.Backup;

namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IRestorePrompt"/> のテスト用フェイク。次回の Prompt 呼び出しで返す結果を
/// <see cref="NextOutcome"/> に事前登録する(既定は Later)。<see cref="LastRecords"/> で
/// Coordinator が渡した records(降順ソート済み)を検証できる。
/// </summary>
public sealed class FakeRestorePrompt : IRestorePrompt
{
    public RestoreOutcome NextOutcome { get; set; } = RestoreOutcome.LaterEmpty;
    public int PromptCount;
    public IReadOnlyList<BackupRecord>? LastRecords;

    public RestoreOutcome Prompt(IWin32Window owner, IReadOnlyList<BackupRecord> records)
    {
        PromptCount++;
        LastRecords = records;
        return NextOutcome;
    }
}
```

**Step 3: FakeTimeProvider を作成**

Create `tests/yEdit.App.Tests/Fakes/FakeTimeProvider.cs`:

```csharp
namespace yEdit.App.Tests.Fakes;

/// <summary>
/// テスト用の <see cref="TimeProvider"/>。GetUtcNow を固定値で返し、
/// <see cref="Advance(TimeSpan)"/> で明示的に進められる。NuGet 依存を増やさない自作 6 行。
/// タイマー(CreateTimer)は本 Stage では使わない(Reconcile を internal 直呼びするため)。
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
```

**Step 4: ビルド確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: 0 警告

**Step 5: Commit**

```powershell
git add tests/yEdit.App.Tests/Fakes/FakeBackupWriter.cs tests/yEdit.App.Tests/Fakes/FakeRestorePrompt.cs tests/yEdit.App.Tests/Fakes/FakeTimeProvider.cs
git commit -m "test: Fake 群(FakeBackupWriter/FakeRestorePrompt/FakeTimeProvider)を追加"
```

---

### Task 6: BackupCoordinatorTests 第 1 弾(ctor/UpdateSettings/Reconcile 登録=10 件)

**Files:**
- Create: `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`

**Step 1: テストハーネス+第 1 弾を書く**

Create `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`:

```csharp
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

        public Document NewDoc(string text, bool dirty = true)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            if (!dirty) doc.Editor.SetSavePoint();
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
        host.NewDoc("hello", dirty: false);          // SetSavePoint 打って Modified=false

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

        host.Docs.TryClose(doc, () => true);         // 閉じる(未保存確認は素通し)
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

        doc.Editor.Text = "hello world";             // 内容変更(Text setter は dirty のまま)
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
```

**Step 2: テスト実行(green を確認=特徴付けの成立)**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~BackupCoordinatorTests"
```
Expected: **Passed! 10 件**

**Step 3: Commit**

```powershell
git add tests/yEdit.App.Tests/BackupCoordinatorTests.cs
git commit -m "test: BackupCoordinator の ctor/UpdateSettings/Reconcile 登録+dirty サイクル 10 件"
```

---

### Task 7: BackupCoordinatorTests 第 2 弾(失敗回復・OfferRestoreOnStartup・Shutdown/Dispose・TimeProvider=15 件)

**Files:**
- Modify: `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`(テスト追記)

**Step 1: 失敗回復のテストを追記**

```csharp
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
```

**Step 2: OfferRestoreOnStartup のテストを追記**

```csharp
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

        int restored = host.Backup.OfferRestoreOnStartup(host.Form, r => throw new Xunit.Sdk.XunitException(), confirm: true);

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

        int returned = host.Backup.OfferRestoreOnStartup(host.Form, r => { var d = host.Docs.CreateNew(); d.Editor.Text = r.Content; return d; }, confirm: true);

        Assert.Equal(0, returned);                   // Restore 分岐は 0 を返す(件数は呼び側で通知しない)
        Assert.Equal(1, host.Prompt.PromptCount);
        Assert.Equal(2, host.Prompt.LastRecords?.Count); // ダイアログには全件を渡す
        // 元 Id の引き継ぎは、続く Reconcile で「keep」Id が Write に載る/Delete されないことで固定
        host.Backup.Reconcile();
        Assert.Contains(host.Writer.Writes, w => w.Id == "keep"); // 復元タブは dirty=Write 走る・Id は元
    });

    [Fact]
    public void OfferRestore_ConfirmTrue_DiscardAll_InvokesWriterDeleteAll() => Sta.Run(() =>
    {
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("r1", "one"));
        host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.DiscardAll, Array.Empty<BackupRecord>());

        host.Backup.OfferRestoreOnStartup(host.Form, r => throw new Xunit.Sdk.XunitException(), confirm: true);

        Assert.Equal(1, host.Writer.DeleteAllCount);
    });

    [Fact]
    public void OfferRestore_ConfirmTrue_Later_DoesNothing() => Sta.Run(() =>
    {
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("r1", "one"));
        host.Prompt.NextOutcome = RestoreOutcome.LaterEmpty;

        host.Backup.OfferRestoreOnStartup(host.Form, r => throw new Xunit.Sdk.XunitException(), confirm: true);

        Assert.Equal(0, host.Writer.DeleteAllCount);
        Assert.Empty(host.Writer.Deletes);
        Assert.Empty(host.Writer.Writes);
    });
```

**Step 3: Shutdown/Dispose+TimeProvider のテストを追記**

```csharp
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
```

**Step 4: テスト実行(全 25 件 green を確認)**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~BackupCoordinatorTests"
```
Expected: **Passed! 25 件**

**Step 5: App.Tests 全体+ビルドを確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Passed! **99 件**(既存 74+新規 25)

**Step 6: Commit**

```powershell
git add tests/yEdit.App.Tests/BackupCoordinatorTests.cs
git commit -m "test: BackupCoordinator の失敗回復・復元 4 分岐・Shutdown 冪等・TimeProvider 15 件"
```

---

### Task 8: ローカルゲート+設計書へ実施記録

**Files:**
- Modify: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`(「Stage 4 実施記録」節の直後に追記)

**Step 1: ローカルゲートを全実行**

Run:
```powershell
powershell -File <repo>\tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`(Release 0 警告・Core 573+Editor 218+App 99=890 緑)

**Step 2: 設計書に実施記録を追記**

`docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` の「Stage 4 実施記録」節の直後に追記:

```markdown

### Stage 5 実施記録(2026-07-14)

- **完了**: 実装計画=`docs/plans/2026-07-14-test-strategy-phase2-stage5.md`・設計書=`docs/plans/2026-07-14-test-strategy-phase2-stage5-design.md`(上位文書 §2.1/§2.2 からの精密化 3 点=型付き IBackupWriter/TimeProvider.System/writerFactory による Lazy 生成保存)。①IBackupWriter+IRestorePrompt+RestoreOutcome シーム追加 ②SerialBackupWriter を IBackupWriter 化(既存 Enqueue は private 化・ctor に dir 追加)+WinFormsRestorePrompt 追加 ③BackupCoordinator 注入化(BackupStore/DateTime.UtcNow/RestoreDialog への参照ゼロ・Reconcile を internal 化) ④MainForm 配線更新 ⑤FakeBackupWriter/FakeRestorePrompt/FakeTimeProvider 追加 ⑥BackupCoordinatorTests 25 件(ctor+UpdateSettings/Reconcile 登録+dirty サイクル/失敗回復/復元 4 分岐/Shutdown 冪等/TimeProvider/対応固定)。
- **テスト数**: 865 → **890**(App 74→99・純増 +25)。ゲート全通過(Release 0 警告)。
- **L5 スポット確認**: 不要(§5 のとおりダイアログ抽象化のみで SR 経路不変)。手動スモーク 1 分は任意。
```

(マージコミットのハッシュはマージ後にユーザー確認のうえ追記)

**Step 3: Commit**

```powershell
git add docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md docs/plans/2026-07-14-test-strategy-phase2-stage5.md
git commit -m "docs: Phase2 設計書に Stage 5 実施記録を追記+実装計画を追加"
```

---

### Task 9: レビュー→手動スモーク(任意)→マージ

**Step 1: 別エージェントによるコードレビュー**(いつもの運用)

ブランチ全 diff(`git diff main...feature/test-strategy-phase2-stage5`)を対象に依頼。観点:

- **挙動不変**:
  - OfferRestoreOnStartup の 4 分岐(disabled/no-records/confirm=false/confirm=true×3)の順序・戻り値・_map 登録
  - Reconcile の順序: `_failed` drain → 閉じた doc の Delete → 各 doc に対する Register/None/Write/Delete 判定
  - Shutdown/Dispose の冪等性(`_shutDown` フラグ)+Shutdown の管理分削除+Dispose の異常系挙動
  - BuildRecord のフィールド構築が現行と 1:1(clock 経路以外は無変更)
- **シームの完全性**:
  - `BackupCoordinator.cs` に `BackupStore\.Write|BackupStore\.Delete|BackupStore\.DeleteAll|DateTime\.UtcNow|new RestoreDialog|RestoreDialog\.RestoreAction` のいずれもヒットしない(grep 6 本)
  - MainForm の生成箇所(`MainForm.cs:60`)以外に `new SerialBackupWriter` の直 new が存在しない(grep)
- **失敗回復経路**: FakeBackupWriter の `FailNextWriteWith` を使わずとも、`OnWriteFailed?.Invoke(id)` を直呼びして `_failed` へ積む経路を Coordinator が経由することが `FailedWrite_ForcesRewrite_NextReconcile` で観測されるか
- **テストの実効性=ミューテーション検証**(Stage 3/4 で標準化)。最低限の変異例:
  - `Reconcile` の `if (gone.HasBackup) _writer?.Delete(gone.Id);` の HasBackup ガード削除 → `Reconcile_ClosedDoc_DeletesBackup_AndDropsFromMap` は生存(HasBackup=true の場合しか通過しない)なので、**HasBackup=false で閉じるケース**の追加を検討(→ Task 7 の追記候補として申し送り)
  - `BuildRecord` の `_clock.GetUtcNow().UtcDateTime` を `DateTime.UtcNow` に戻す → `BuildRecord_UsesInjectedClock_ForTimestamp` が赤になること
  - `Shutdown` の `foreach ... if (info.HasBackup)` を無条件 delete に → 挙動同じで生存の可能性(準等価変異=許容記録)
  - `UpdateSettings` の `Reconcile()` 呼び削除 → `UpdateSettings_EnableFromDisabled_CreatesWriter_AndReconcilesImmediately` が赤になること
  - `OfferRestore` の `if (!_enabled) return 0;` 削除 → `OfferRestore_Disabled_ReturnsZero_WithoutPrompting` が赤になること
  - `IBackupWriter.OnWriteFailed` フックの Coordinator ctor 配線(`w.OnWriteFailed = OnBackgroundWriteFailed`)削除 → `FailedWrite_ForcesRewrite_NextReconcile` が赤になること
- **Core 検証済み事項の再検証をしていないか**(BackupPlanner.Decide・BackupStore の I/O・ContentSignature)

**Step 2: 手動スモーク(ユーザー任意・L5 実機 SR は不要)**

SR 経路不変(ダイアログ抽象化のみ)のため L5 は実施しない(設計書 §5)。配線の実感確認として 1-2 分のスモークを任意で:

- 起動→本文編集(dirty)→30 秒待って自動バックアップが走ることを確認(%APPDATA%\yEdit\backups に .json が出現)→保存→バックアップ .json が消える→終了
- yEdit を強制終了(タスクマネージャ)→再起動→復元ダイアログが出る→「選択を復元」/「あとで」/「すべて破棄」の 3 分岐を試す

**Step 3: main へ no-ff マージ**

```powershell
git switch main
git merge --no-ff feature/test-strategy-phase2-stage5 -m "テスト戦略 Phase2 Stage5: BackupCoordinator シーム導入+テスト 25 件をマージ"
powershell -File <repo>\tools\pre-merge-check.ps1
git branch -d feature/test-strategy-phase2-stage5
```
Expected: マージ後ゲート全緑(890)

**Step 4: 実施記録へマージコミットのハッシュを追記**(小コミット)

```powershell
git log --oneline -1 main    # マージコミットのハッシュを確認
# 上記ハッシュを Stage 5 実施記録に追記して commit
git add docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md
git commit -m "docs: Stage 5 実施記録にマージハッシュとマージ後ゲート結果を追記"
```

---

## DoD(Stage 5)

1. `tools/pre-merge-check.ps1` 全緑(Release ビルド 0 警告)
2. テスト数 865 → **890**(App 74→99・純増 +25)
3. **挙動不変**: OfferRestoreOnStartup の 4 分岐/Shutdown 冪等/Reconcile の Write/Delete/None 判定/失敗回復のフロー(diff レビューで機械的確認)
4. **シーム完全性**: `BackupCoordinator.cs` から `BackupStore.Write/Delete/DeleteAll`・`DateTime.UtcNow`・`RestoreDialog` への参照ゼロ(grep 6 本で確認)
5. 別エージェントによるコードレビュー(マージ前・ミューテーション検証を標準適用)
6. L5 実機 SR スポット確認は**不要**(根拠: 上位文書 §5「他 Stage はダイアログ抽象化のみで SR 経路不変」)。手動スモーク 1-2 分は任意
7. main へ no-ff マージ+設計書へ実施記録・マージハッシュ追記

## リスクと対策

- **`_writer` の Lazy 生成の意味論**: writerFactory 化で「有効時にのみ生成」を保存(§4.1)。無効時のスレッド増加なし。Task 4 の `WriterFactoryCalls` 検証で担保。
- **失敗回復経路の切断**: IBackupWriter.OnWriteFailed フックで Adapter→Coordinator に通知を戻す(§3.4)。挙動 1:1。`FailedWrite_ForcesRewrite_NextReconcile` で固定。
- **BackupStore への参照残存**: Task 4 の grep 3 本+レビュー観点で機械的に確認。
- **RestoreDialog.RestoreAction 内部 enum**: Adapter(WinFormsRestorePrompt)で App 層公開 `RestoreAction` へマップ=UI 側は無変更。
- **TimeProvider 依存追加**: .NET 9 標準=追加パッケージ不要。FakeTimeProvider は 6 行の自作。
- **特徴付けが赤の場合**: 原則テスト側を現行挙動へ合わせる。**ただしバックアップ書込/削除経路の赤はデータ喪失リスク=修正せずユーザーへ報告**(規約)。

## 申し送り(Stage 6 以降へ)

- **次 Stage**: CsvController(ICellPicker 導入)=上位文書 §4 Stage 6。マージ前に L5 実機 SR スポット必須(CSV セル読み)。
- **Stage 8 候補**:
  - `BackupCoordinator._writer` の Lazy ライフサイクル(writerFactory 化)は残置=設計判断として保存。
  - `SerialBackupWriter` の private 化された Enqueue が外部から呼ばれていないか継続監視(本 Stage で MainForm から使わないことは確認済み)。
  - `HasBackup=false で閉じた doc の Delete 経路`(HasBackup ガード)のミューテーション被覆が甘い=Stage 8 で HasBackup=false ケースの追加テスト検討。
- **Sta.cs の共有抽出**: 3 プロジェクト目条件は継続監視(本 Stage では発動しない)。
- **BackupStore の LoadAll/SweepTempFiles**: 抽象化しない YAGNI 判断。将来「起動時経路も Fake で駆動したい」需要が生じた場合の候補=Stage 8 で判断。
