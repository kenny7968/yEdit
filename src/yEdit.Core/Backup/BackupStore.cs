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
    /// temp は「ファイル名.乱数.tmp」（AtomicFile 準拠）で、SweepTempFiles の "*.tmp" 掃除対象に収まる。</summary>
    public static void Write(string dir, BackupRecord record)
    {
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
                if (rec is not null && !string.IsNullOrEmpty(rec.Id))
                    list.Add(rec);
            }
            catch
            { /* 破損・途中書き込みは無視（次回のクリーン書き込みで上書きされる） */
            }
        }
        return list;
    }

    /// <summary>指定 Id のバックアップを削除する（存在しなくても無害）。</summary>
    public static void Delete(string dir, string id) => TryDelete(Path.Combine(dir, id + ".json"));

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
