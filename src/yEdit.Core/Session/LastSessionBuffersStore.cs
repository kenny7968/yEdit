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
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>();
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options);
            return map ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static void Save(string path, IReadOnlyDictionary<string, string> map)
    {
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
            // 削除失敗は致命でない(次回 Load が Deserialize しても既存本文は使われないため無害)
        }
    }
}
