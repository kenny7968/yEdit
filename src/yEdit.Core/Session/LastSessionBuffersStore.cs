using System.Text.Json;

namespace yEdit.Core.Session;

/// <summary>
/// 無題タブの本文を BufferKey→本文 のマップとして単一 JSON ファイルへ保存する。
/// 破損時は空 Dict を返し呼び出し側が grace degradation で continue する(空タブは復元せず skip する
/// 契約は FileController.RestoreLastSession 側にある)。設計書 §2.4。
/// </summary>
public static class LastSessionBuffersStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Load 時のファイルサイズ上限(bytes)。書込側は 1M chars/tab(=UTF-16 で 2 MB/tab)を上限とし
    /// 最大 10 タブ級の想定でも 20 MB 前後だが、余裕を持って 32 MB。この上限を超える入力は
    /// stale/攻撃 JSON と見なし、Load は empty を返して Trace.TraceWarning する。書込側キャップ
    /// (§設計 §4.3 の 1M chars/tab)と併せた二重防御=BackupCoordinator BK-M-3 と対称。
    /// </summary>
    internal const long MaxLoadFileSizeBytes = 32L * 1024 * 1024;

    /// <summary>%APPDATA%\yEdit\last-session-buffers.json(SettingsStore.DefaultPath と同ディレクトリ)。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "yEdit",
            "last-session-buffers.json"
        );

    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, string>();
            // 事前 size cap(§Load-size-cap): 32 MB を超えるファイルは stale/攻撃入力と見なし
            // 読まずに empty で返す(BackupCoordinator の BK-M-3 と対称の防御=書込側キャップと二重に張る)。
            var info = new FileInfo(path);
            if (info.Length > MaxLoadFileSizeBytes)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "yEdit: last-session-buffers.json too large ({0} bytes); ignoring.",
                    info.Length
                );
                return new Dictionary<string, string>();
            }
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>();
            var map = JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Options);
            if (map is null)
                return new Dictionary<string, string>();
            // 値が明示 null のエントリは skip(復元契約=キー欠落と同じ扱い=空タブを追加しない)
            var result = new Dictionary<string, string>(map.Count);
            foreach (var kvp in map.Where(kvp => kvp.Value is not null))
                result[kvp.Key] = kvp.Value!;
            return result;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static void Save(string path, IReadOnlyDictionary<string, string> map)
    {
        // path 由来の親ディレクトリ。pathological input(ルート "C:\\" や 純ファイル名)では
        // GetDirectoryName が null/空文字を返して例外になり得るが、本 static は失敗を握らない=
        // 呼び出し側(MainForm.SaveLastSessionBuffersSafe)が try/catch でラップする契約
        // (SettingsStore.Save と対称)。
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(map, Options);
        File.WriteAllText(path, json);
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 削除失敗は致命でない ― 次回 Save で BufferKey は Guid.N で再発行されるため、
            // 残骸に含まれる旧本文はどの SessionTabRecord からも参照されない。
        }
    }
}
