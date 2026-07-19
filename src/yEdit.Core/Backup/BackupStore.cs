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

    /// <summary>ディレクトリ内の全バックアップを読み込む（破損・読めないファイルはスキップ）。</summary>
    public static IReadOnlyList<BackupRecord> LoadAll(string dir)
    {
        var list = new List<BackupRecord>();
        if (!Directory.Exists(dir))
            return list;

        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(file), Options);
                if (rec is null || !BackupIdValidator.IsValid(rec.Id))
                    continue; // HIGH-1: 攻撃者が植えた不正 Id は復元候補から捨てる(パストラバーサル入口の遮断)
                list.Add(rec);
            }
            catch
            { /* 破損・途中書き込みは無視（次回のクリーン書き込みで上書きされる） */
            }
        }
        return list;
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

    /// <summary>全バックアップ（*.json）と書込中残骸（*.tmp）を削除する（復元の「すべて破棄」用）。</summary>
    public static void DeleteAll(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            TryDelete(file);
        SweepTempFiles(dir);
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
