using System.Text.Json;

namespace yEdit.Core.Backup;

/// <summary>
/// バックアップのサイドカー保存（1 文書＝1 JSON ファイル）。原子的に書き込み（同ディレクトリの
/// temp に書いてから File.Replace／新規は Move）、破損ファイルは読み飛ばす。SR/スレッド非依存の純 I/O。
/// 孤児ファイルの有無＝前回の異常終了の痕跡（クリーン終了時に DeleteAll される）。
/// </summary>
public static class BackupStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>既定のバックアップディレクトリ（%APPDATA%\yEdit\backups）。</summary>
    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "yEdit",
            "backups"
        );

    /// <summary>1 件のバックアップを原子的に書き込む（&lt;Id&gt;.json を temp 経由で差し替え）。
    /// temp は「ファイル名.乱数.tmp」（AtomicFile 準拠）で、SweepTempFiles の "*.tmp" 掃除対象に収まる。
    /// HIGH-1 対称: <see cref="BackupIdValidator"/> の白リスト(GUID N・32 桁 hex・区切りなし)を満たさない
    /// Id は <see cref="ArgumentException"/> で拒否する(パストラバーサル入口を書込側でも塞ぐ)。</summary>
    public static void Write(string dir, BackupRecord record)
    {
        // BK-L-7: Id 白リスト(GUID N・32 桁 hex・区切りなし)を書込前に検証する。
        // HIGH-1 で LoadAll 側は既に検証済み(復元候補から捨てる)だが、Write/Delete 側も
        // Id を Path.Combine に流す前段で塞ぐ(HIGH-1 の対称性)。将来 Import 等の新規流入経路が
        // 増えた場合のパストラバーサル入口(record.Id に "..\..\evil" 等)を構造的に閉じる。
        // silent 無視ではなく ArgumentException として顕在化する意図(プログラムバグを目に見えるように)。
        if (!BackupIdValidator.IsValid(record.Id))
            throw new ArgumentException(
                $"BackupRecord.Id must be a canonical GUID N (32 hex chars). Got: '{record.Id}'",
                nameof(record)
            );

        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, record.Id + ".json");
        IO.AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(record, Options));
    }

    /// <summary>ディレクトリ内の全バックアップを読み込む（破損・読めないファイルはスキップ）。
    /// BK-M-2: <paramref name="dir"/> 直下の flat 配置 (v0.3.0-sec 由来の後方互換) と、
    /// <paramref name="dir"/> 配下の <c>session-*</c> サブディレクトリ (BK-M-2 以降の正規配置) の
    /// 両方を列挙する。他インスタンス/前回クラッシュ由来の session-* も全部復元候補に上げるため、
    /// 「別インスタンスが『すべて破棄』を選ぶと自インスタンスのライブ backup が消える」問題を回避する
    /// (削除は <see cref="DeleteSessionDir(string)"/> 経由で自セッション限定に切り替える)。
    /// BK-L-6: optional <paramref name="traceSink"/> で破棄理由を可視化する。破棄自体は従来通り
    /// (LoadAll は落ちない・skip 挙動は変えない)が、silent catch では JSON パース失敗/攻撃者
    /// 植え込みの JSON が診断不能だったため kind 別に通知する:
    /// - kind = 例外の型名(JsonException / IOException / UnauthorizedAccessException …) — 破損 catch
    /// - kind = "invalid-id" — <see cref="BackupIdValidator"/>.IsValid=false のレコード
    /// - kind = "null-record" — JSON のトップレベルが null の場合
    /// file パスは攻撃者制御下にある可能性があるため、Core 層では sanitize せず素の値を渡す。
    /// UI/ログ表示側(BackupCoordinator)で SanitizeForDisplay.OneLine による無害化を行う契約。
    /// traceSink=null(既定)では旧来の silent 挙動を完全維持する。</summary>
    public static IReadOnlyList<BackupRecord> LoadAll(
        string dir,
        Action<string, string>? traceSink = null
    )
    {
        var list = new List<BackupRecord>();
        if (!Directory.Exists(dir))
            return list;

        // BK-M-2: 列挙順 = flat 配置(base dir 直下) → session-* サブディレクトリ。
        // v0.3.0-sec 由来の残置バックアップと現行の session subdir 配置の双方をユーザに見せる。
        LoadFromDir(dir, list, traceSink);
        foreach (string sub in Directory.EnumerateDirectories(dir, "session-*"))
            LoadFromDir(sub, list, traceSink);

        return list;
    }

    private static void LoadFromDir(
        string dir,
        List<BackupRecord> list,
        Action<string, string>? traceSink
    )
    {
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(file), Options);
                if (rec is null)
                {
                    // JSON トップレベルが `null` (System.Text.Json の Deserialize<T> は null を返し得る)。
                    // 破棄自体は従来通りだが、攻撃者が意図的に空 JSON を植えた痕跡として trace は出す。
                    traceSink?.Invoke(file, "null-record");
                    continue;
                }
                if (!BackupIdValidator.IsValid(rec.Id))
                {
                    // HIGH-1: 攻撃者が植えた不正 Id は復元候補から捨てる(パストラバーサル入口の遮断)。
                    // BK-L-6: 破棄理由 "invalid-id" を trace で可視化(silent 破棄からの差分)。
                    traceSink?.Invoke(file, "invalid-id");
                    continue;
                }
                list.Add(rec);
            }
            catch (Exception ex)
            {
                // 破損・途中書き込み(JsonException / IOException 等)は無視(次回のクリーン書き込みで上書きされる)。
                // BK-L-6: kind として例外型名を渡す(sanitize は上位 App 層で SanitizeForDisplay.OneLine)。
                traceSink?.Invoke(file, ex.GetType().Name);
            }
        }
    }

    /// <summary>指定 Id のバックアップを削除する（存在しなくても無害）。
    /// HIGH-1 対称: <see cref="BackupIdValidator"/> の白リスト(GUID N・32 桁 hex・区切りなし)を満たさない
    /// Id は <see cref="ArgumentException"/> で拒否する(Write 側と同じパストラバーサル入口を Delete 側でも塞ぐ)。</summary>
    public static void Delete(string dir, string id)
    {
        // BK-L-7: HIGH-1 対称性。書込側と同じ白リストを Delete にも適用する(Path.Combine への流入前段で遮断)。
        if (!BackupIdValidator.IsValid(id))
            throw new ArgumentException(
                $"Backup id must be a canonical GUID N (32 hex chars). Got: '{id}'",
                nameof(id)
            );

        TryDelete(Path.Combine(dir, id + ".json"));
    }

    /// <summary>全バックアップ（*.json）と書込中残骸（*.tmp）を削除する（復元の「すべて破棄」用）。
    /// BK-M-2 以降、SerialBackupWriter.DeleteAll は代わりに <see cref="DeleteSessionDir(string)"/> を
    /// 呼ぶが、本メソッドは flat 後方互換の呼び出し元(将来の import 経路等)のために残す。</summary>
    public static void DeleteAll(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            TryDelete(file);
        SweepTempFiles(dir);
    }

    /// <summary>BK-M-2: 指定した自セッション用 subdirectory の中身(*.json + *.tmp)を消し、
    /// 空になった dir 自体も削除する(冗長掃除)。他インスタンス由来の session-* には触れない
    /// (SerialBackupWriter.DeleteAll の実体)。失敗は握り潰す(残骸は次回起動の 30 日 sweep で回収)。</summary>
    public static void DeleteSessionDir(string sessionDir)
    {
        if (!Directory.Exists(sessionDir))
            return;
        foreach (string file in Directory.EnumerateFiles(sessionDir, "*.json"))
            TryDelete(file);
        SweepTempFiles(sessionDir);
        // 空 dir 自体を削除(次書込で Directory.CreateDirectory が再作成する)。ロック競合や
        // 別プロセス書込のタイミングで空でない場合は Directory.Delete が例外→握り潰す。
        try
        {
            Directory.Delete(sessionDir);
        }
        catch
        { /* 空でない/ロック中/既に消失 = 無害。30 日 sweep で最終回収される */
        }
    }

    /// <summary>BK-M-2: <paramref name="baseDir"/> 配下の <c>session-*</c> subdirectory のうち、
    /// <c>Directory.GetLastWriteTimeUtc</c> が <paramref name="nowUtc"/> - <paramref name="maxAge"/>
    /// より古いものを再帰削除する(孤児掃除)。<c>session-*</c> 以外(flat 配置の *.json や
    /// ユーザが手で置いた other-dir 等)は無視する=副作用を最小化。時計は呼び出し側 (TimeProvider
    /// seam 経由) で注入する。失敗は握り潰す(次回起動で再挑戦)。</summary>
    public static void SweepOldSessions(string baseDir, DateTime nowUtc, TimeSpan maxAge)
    {
        if (!Directory.Exists(baseDir))
            return;
        var threshold = nowUtc - maxAge;
        foreach (string sub in Directory.EnumerateDirectories(baseDir, "session-*"))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(sub) < threshold)
                    Directory.Delete(sub, recursive: true);
            }
            catch
            { /* 属性取得失敗/削除失敗は無害(次回起動で再挑戦) */
            }
        }
    }

    /// <summary>書込中のクラッシュで残った *.tmp（不完全な中間ファイル）を掃除する。起動時に呼ぶ。</summary>
    public static void SweepTempFiles(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (string file in Directory.EnumerateFiles(dir, "*.tmp"))
            TryDelete(file);
    }

    private static void TryDelete(string p)
    {
        try
        {
            if (File.Exists(p))
                File.Delete(p);
        }
        catch
        { /* 残骸は実害小 */
        }
    }
}
