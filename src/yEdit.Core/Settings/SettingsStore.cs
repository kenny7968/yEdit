using System.Text.Json;

namespace yEdit.Core.Settings;

/// <summary>settings.json の読み書き。壊れていれば既定値で続行（握り潰さず既定へ）。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>既定の設定ファイルパス（%APPDATA%\yEdit\settings.json）。</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yEdit", "settings.json");

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public static void Save(string path, AppSettings settings)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(path, json);
    }
}
