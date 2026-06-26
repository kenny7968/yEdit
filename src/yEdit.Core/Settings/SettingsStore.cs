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
            var s = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            return Normalize(s);
        }
        catch { return new AppSettings(); }
    }

    /// <summary>
    /// JSON で明示的に null が入った参照型フィールドを既定へ補正する（"RecentFiles": null 等でも
    /// 後段の NRE を起こさないため）。System.Text.Json は欠落キーは初期化子を残すが、明示 null は上書きする。
    /// </summary>
    private static AppSettings Normalize(AppSettings s)
    {
        var def = new AppSettings();
        s.RecentFiles ??= def.RecentFiles;
        if (string.IsNullOrEmpty(s.Theme)) s.Theme = def.Theme;
        if (string.IsNullOrEmpty(s.FontName)) s.FontName = def.FontName;
        return s;
    }

    public static void Save(string path, AppSettings settings)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(path, json);
    }
}
