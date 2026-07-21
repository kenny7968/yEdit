using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using yEdit.Core.Backup;
using yEdit.Core.Text;

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

    /// <summary>BK-M-3 (v0.11): バックアップに載せる本文の上限 (chars=UTF-16 code units)。
    /// 32M chars = 64 MB UTF-16 相当。日常編集の CSV / ログを大きく超える。上限超過時は
    /// <see cref="BackupRecord.Content"/>=null (path-only) にフォールバックし、
    /// <see cref="_trace"/>.Warn("backup-content-skipped", ...) で診断可能にする。
    /// テストから <see cref="Reconcile"/> の分岐を機械的に叩けるよう、ctor 経由で override 可能な
    /// seam (<see cref="_maxBackupChars"/>) を追加している(既定=この定数)。</summary>
    internal const int MaxBackupChars = 32 * 1024 * 1024;

    private readonly DocumentManager _docs;
    private readonly string _dir;

    /// <summary>BK-M-3: 実行時の size cap。既定は <see cref="MaxBackupChars"/>。ctor の
    /// optional 引数 <c>maxBackupCharsOverride</c> でテスト時に小さな値へ差し替え、実際に
    /// 32M chars 相当のバッファを alloc せずに fallback 分岐を検証する。</summary>
    private readonly int _maxBackupChars;

    /// <summary>BK-M-2: 自セッション用 subdirectory (<c>%APPDATA%\yEdit\backups\session-{Guid.N}\</c>)。
    /// ctor で生成しプロセス寿命で不変。SerialBackupWriter へ渡す書込先はこの subdir に閉じ、
    /// 復元列挙 (LoadAll) と 30 日 sweep (SweepOldSessions) は <see cref="_dir"/> (base dir) に対して行う。
    /// </summary>
    private readonly string _sessionDir;
    private readonly TimeProvider _clock;
    private readonly Func<string, IBackupWriter> _writerFactory;
    private readonly IRestorePrompt _restorePrompt;
    private readonly IBackupTraceSink _trace; // Task 1b: silent catch を診断可能に(既定=DebugBackupTraceSink)
    private bool _enabled; // UpdateSettings で実行時に切替可能
    private readonly System.Windows.Forms.Timer _timer = new();
    private IBackupWriter? _writer; // 無効時は生成しない(有効化時に factory 経由で遅延生成)
    private readonly Dictionary<Document, DocBackup> _map = new();
    private readonly ConcurrentQueue<string> _failed = new(); // 背景書込が失敗した Id(UI スレッドで回収)
    private bool _shutDown;

    /// <summary>BK-M-2: 起動時 sweep の age 閾値。30 日以上更新のない session-* subdir を削除する。</summary>
    private static readonly TimeSpan SessionSweepMaxAge = TimeSpan.FromDays(30);

    /// <summary>テスト観測用: 現在の Timer.Interval(ms)。UpdateSettings/ctor の Clamp 結果を assert 化する seam。</summary>
    internal int TimerIntervalMs => _timer.Interval;

    public BackupCoordinator(
        DocumentManager docs,
        bool enabled,
        int intervalSeconds,
        TimeProvider clock,
        Func<string, IBackupWriter> writerFactory,
        IRestorePrompt restorePrompt,
        string? directory = null,
        IBackupTraceSink? traceSink = null,
        int? maxBackupCharsOverride = null
    )
    {
        _docs = docs;
        _enabled = enabled;
        _clock = clock;
        _writerFactory = writerFactory;
        _restorePrompt = restorePrompt;
        _dir = directory ?? BackupStore.DefaultDirectory;
        // BK-M-3: 既定は MaxBackupChars。テストが小さな値を渡すと Reconcile の fallback 分岐を
        // 実際に 32M chars alloc せずに叩ける。負値は意図しない無効化を招くので 0 未満は defensive に
        // 既定へ戻す(既定 32M chars = 実運用上ほぼ超えない=本番挙動不変)。
        _maxBackupChars = maxBackupCharsOverride is int mo && mo >= 0 ? mo : MaxBackupChars;
        // BK-M-2: セッション別 subdir を ctor で生成 (プロセス寿命で不変)。
        // 別インスタンスと衝突しない一意名として Guid.N (32 桁 hex)。session- prefix で LoadAll /
        // SweepOldSessions が識別する契約(prefix を変えると sweep 対象から外れて孤児が溜まる)。
        _sessionDir = Path.Combine(_dir, "session-" + Guid.NewGuid().ToString("N"));
        // Task 1b: 既定は Trace.TraceWarning に流す DebugBackupTraceSink。MainForm は既定引数のまま呼ぶ
        // ため本番挙動は不変(silent catch → Trace 出力あり、例外は依然握り潰す)。
        _trace = traceSink ?? new DebugBackupTraceSink();

        // 無効時でもハンドラは購読しておく(後から UpdateSettings で有効化できるように)。
        // Tick/ActiveDocumentChanged は Reconcile 冒頭の !_enabled ガードで素通りするため無効中は無害。
        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000; // 上限クランプで int オーバーフロー防止
        _timer.Tick += (_, _) => Reconcile();
        _docs.ActiveDocumentChanged += (_, _) => Reconcile();
        if (!_enabled)
            return;

        _writer = CreateWriter();
        _timer.Start();
    }

    /// <summary>writer を factory で生成し、失敗通知フックを配線する(遅延生成の意味論を保存)。
    /// BK-M-2: factory シグニチャは <c>Func&lt;string, IBackupWriter&gt;</c>=書込先の session dir を
    /// 明示的に渡す(base dir と混同するミスを compile-time で防ぐ seam)。</summary>
    private IBackupWriter CreateWriter()
    {
        var w = _writerFactory(_sessionDir);
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
        if (_shutDown)
            return;
        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000;
        if (enabled == _enabled)
            return;

        _enabled = enabled;
        if (enabled)
        {
            _writer ??= CreateWriter();
            _timer.Start();
            Reconcile(); // 有効化した瞬間の未保存文書を即保護(保護窓を作らない)
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
    public int OfferRestoreOnStartup(
        IWin32Window owner,
        Func<BackupRecord, Document> restore,
        bool confirm
    )
    {
        if (!_enabled)
            return 0;
        try
        {
            // BK-M-2: 30 日以上更新のない孤児 session-* subdir を掃除する(前回異常終了/古いインスタンス由来)。
            // 時計は TimeProvider seam 経由でテスト可能。失敗は無害(次回起動で再挑戦)。
            BackupStore.SweepOldSessions(_dir, _clock.GetUtcNow().UtcDateTime, SessionSweepMaxAge);
        }
        catch (Exception ex)
        {
            _trace.Warn("sweep-old-sessions", SanitizeForDisplay.OneLine(_dir, 200), ex);
        }
        try
        {
            // BK-M-2: 自セッション dir と base dir 直下(flat 後方互換)の両方で *.tmp 残骸を掃除。
            // session dir は初回書込前だと存在しないが、SweepTempFiles は Directory.Exists=false で
            // 無害 return する。base dir は v0.3.0-sec 由来の残置対策。
            BackupStore.SweepTempFiles(_sessionDir);
            BackupStore.SweepTempFiles(_dir);
        }
        catch (Exception ex)
        {
            // BK-L-5: 将来的に _dir がユーザ設定で可変化された場合の CRLF injection / BiDi 混入
            // 防御として SanitizeForDisplay.OneLine(200) を通す(現状 %APPDATA%\yEdit\backups は
            // 非攻撃者制御だが、防御の invariant を BackupCoordinator 全 trace で統一する)。
            _trace.Warn("sweep-temp", SanitizeForDisplay.OneLine(_dir, 200), ex);
        } // 残骸掃除失敗は無害・診断のため trace

        IReadOnlyList<BackupRecord> records;
        try
        {
            // BK-L-6: per-file の破損 catch / invalid-id / null-record を trace で可視化する。
            // file パスは JSON の内容(攻撃者制御可能)ではなくディレクトリ列挙で得た値だが、
            // %APPDATA%\yEdit\backups 配下に置かれるファイル名は「.json」拡張子と Directory 名以外は
            // 攻撃者制御下にあり得る(RLO 混入等)ため、SanitizeForDisplay.OneLine で 1 行化してから
            // trace に載せる。kind (例外型名 / "invalid-id" / "null-record") はコード側の enum 相当なので
            // detail 末尾へコロン結合する(Option A: 既存 3 引数 sink API を無変更で維持)。
            // maxLength=200 は BK-L-5 の統一値(設計 §PR-F (4))=BackupCoordinator 全 trace で揃える。
            records = BackupStore.LoadAll(
                _dir,
                (file, kind) =>
                    _trace.Warn(
                        "backup-load-failed",
                        SanitizeForDisplay.OneLine(file, 200) + ":" + kind,
                        ex: null
                    )
            );
        }
        catch (Exception ex)
        {
            _trace.Warn("load-all", SanitizeForDisplay.OneLine(_dir, 200), ex);
            return 0;
        }
        if (records.Count == 0)
            return 0;

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
                    _map[doc] = new DocBackup
                    {
                        Id = rec.Id,
                        LastSig = ContentSignature.Of(doc.Editor.SnapshotText),
                        HasBackup = true,
                    };
                    restored++;
                }
                catch (Exception ex)
                {
                    // 1 件の不正レコードで全復元を巻き添えにしない。失敗分はバックアップを残し再挑戦可能に。
                    // BK-L-5: rec.Id は攻撃者 JSON 由来の可能性(LoadAll 経路は validator で reject 済み
                    // だが防御は薄く重ねる)+ prompt outcome 経路は validator を通らないため、
                    // 全 trace で SanitizeForDisplay.OneLine(200) 統一で無害化する。
                    _trace.Warn("restore-item-later", SanitizeForDisplay.OneLine(rec.Id, 200), ex);
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
                        _map[doc] = new DocBackup
                        {
                            Id = rec.Id,
                            LastSig = ContentSignature.Of(doc.Editor.SnapshotText),
                            HasBackup = true,
                        };
                    }
                    catch (Exception ex)
                    {
                        // 1 件の不正レコードで全復元を巻き添えにしない。失敗分はバックアップを残し再挑戦可能に。
                        // BK-L-5: outcome.Checked 経路は BackupIdValidator を通らないため、
                        // 攻撃者が Prompt から悪意 Id を注入し得る。SanitizeForDisplay.OneLine(200) で
                        // 制御文字/BiDi/過剰長を無害化する(BackupCoordinator 全 trace で統一)。
                        _trace.Warn("restore-item", SanitizeForDisplay.OneLine(rec.Id, 200), ex);
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
        if (!_enabled || _shutDown)
            return;

        // 背景書込が失敗した文書を強制再書込対象にする(楽観更新で欠落・陳腐化しないように)。
        while (_failed.TryDequeue(out var failedId))
            foreach (var v in _map.Values)
                if (v.Id == failedId)
                    v.ForceWrite = true;

        // 閉じた文書(map にあるが現存しない)→ バックアップ削除。
        var current = new HashSet<Document>(_docs.Documents);
        foreach (var doc in _map.Keys.ToList())
        {
            if (current.Contains(doc))
                continue;
            var gone = _map[doc];
            if (gone.HasBackup)
                _writer?.Delete(gone.Id);
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

            switch (
                BackupPlanner.Decide(modified, sig, info.LastSig, info.HasBackup, info.ForceWrite)
            )
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
        var info = new DocBackup
        {
            Id = Guid.NewGuid().ToString("N"),
            LastSig = ContentSignature.Of(content),
            HasBackup = false,
        };
        _map[doc] = info;
        if (doc.Editor.Modified)
        {
            EnqueueWrite(info, doc, content);
            info.HasBackup = true;
        }
    }

    /// <summary>書込ジョブを投入する。失敗時は Adapter が OnWriteFailed 経由で Id を _failed へ積み、
    /// 次 Reconcile で強制再書込する。
    /// BK-M-3: content.Length が上限 (<see cref="_maxBackupChars"/>) を超える場合は Content=null の
    /// path-only record にフォールバックし、_trace に "backup-content-skipped" を出す。ContentSignature の
    /// 判定は Reconcile 側で実 content から計算済みなので、ここで null に落としても sig(false-negative)
    /// は起きない(次 tick で内容が上限以下に戻れば通常経路で Write が再走る)。</summary>
    private void EnqueueWrite(DocBackup info, Document doc, string content)
    {
        string? persistContent = content;
        if (content.Length > _maxBackupChars)
        {
            // pathKey: doc.State.Path が null (untitled) の場合はプレースホルダ。
            // SanitizeForDisplay.OneLine(200) で BiDi/改行/過剰長を無害化 (BackupCoordinator 全 trace で統一)。
            var pathKey = SanitizeForDisplay.OneLine(
                doc.State.Path ?? $"<untitled-{doc.State.UntitledNumber}>",
                200
            );
            // BK-M-3 I-1: sizeChars を detail に折り込む(閾値ぎりぎり vs 遥かに超えの区別が付き
            // 閾値チューニング診断が可能になる)。追加する " (Nchars)" は自コード生成 = sanitize 不要。
            _trace.Warn("backup-content-skipped", pathKey + $" ({content.Length}chars)", ex: null);
            persistContent = null;
        }
        var rec = BuildRecord(info.Id, doc, persistContent);
        _writer?.Write(rec);
    }

    private BackupRecord BuildRecord(string id, Document doc, string? content) =>
        new(
            Id: id,
            OriginalPath: doc.State.Path,
            UntitledNumber: doc.State.UntitledNumber,
            CodePage: doc.State.Encoding.CodePage,
            HasBom: doc.State.HasBom,
            LineEndingId: (int)doc.State.LineEnding,
            Content: content,
            TimestampUtc: _clock.GetUtcNow().UtcDateTime
        );

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
        if (_shutDown)
            return;
        _shutDown = true;
        _timer.Stop();
        foreach (var info in _map.Values)
            if (info.HasBackup)
                _writer?.Delete(info.Id);
        _writer?.Dispose(); // 保留ジョブ(削除含む)をドレイン
        _timer.Dispose();
    }

    public void Dispose()
    {
        // Shutdown 済みなら timer/writer は解放済み。未経由(異常系)なら timer/writer を片付ける
        // (孤児バックアップは残し、次回起動で復元提案できるようにする)。冪等。
        if (_shutDown)
            return;
        _shutDown = true;
        _timer.Stop();
        _timer.Dispose();
        _writer?.Dispose();
    }
}
