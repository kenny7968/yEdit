using Xunit;
using yEdit.Core.Settings;

namespace yEdit.Core.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_returns_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var s = SettingsStore.Load(path);
        Assert.Equal(new AppSettings().FontName, s.FontName);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings
            {
                FontName = "BIZ UDゴシック",
                FontSize = 14,
                WindowWidth = 1000,
            };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.Equal("BIZ UDゴシック", loaded.FontName);
            Assert.Equal(14, loaded.FontSize);
            Assert.Equal(1000, loaded.WindowWidth);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_file_returns_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            var s = SettingsStore.Load(path);
            Assert.Equal(new AppSettings().FontSize, s.FontSize);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_normalizes_corrupt_numeric_and_null_fields()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            // 有効な JSON だが値が壊れている（未対応コードページ・範囲外改行・0サイズ・null参照型）。
            File.WriteAllText(
                path,
                "{\"DefaultCodePage\":99999,\"DefaultLineEnding\":7,\"FontSize\":0,\"WindowWidth\":1,\"RecentFiles\":null,\"Theme\":null}"
            );
            var s = SettingsStore.Load(path);
            var def = new AppSettings();
            Assert.Equal(def.DefaultCodePage, s.DefaultCodePage); // 未対応CP→既定
            Assert.Equal(def.DefaultLineEnding, s.DefaultLineEnding); // 範囲外→既定
            Assert.True(s.FontSize > 0); // 0→既定
            Assert.True(s.WindowWidth >= 200); // 極小→既定
            Assert.NotNull(s.RecentFiles); // null→空リスト
            Assert.False(string.IsNullOrEmpty(s.Theme)); // null→default
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Defaults_wrap_is_disabled_with_80_columns()
    {
        var def = new AppSettings();
        Assert.False(def.WrapColumnEnabled);
        Assert.Equal(80, def.WrapColumn);
    }

    [Fact]
    public void Save_then_load_roundtrips_wrap_settings()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings { WrapColumnEnabled = true, WrapColumn = 60 };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.WrapColumnEnabled);
            Assert.Equal(60, loaded.WrapColumn);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_clamps_out_of_range_wrap_column()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"WrapColumnEnabled\":true,\"WrapColumn\":99999}");
            var s = SettingsStore.Load(path);
            Assert.Equal(1000, s.WrapColumn); // 上限へクランプ
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Defaults_kinsoku_sets_are_conservative_symbols()
    {
        var def = new AppSettings();
        Assert.Contains("、", def.KinsokuLineStartChars);
        Assert.Contains("）", def.KinsokuLineStartChars);
        Assert.DoesNotContain("ー", def.KinsokuLineStartChars); // 長音は既定で入れない
        Assert.Contains("（", def.KinsokuLineEndChars);
        Assert.Equal("、。，．", def.KinsokuHangChars);
    }

    [Fact]
    public void Save_then_load_roundtrips_kinsoku_settings()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings
            {
                KinsokuLineStartChars = ")】",
                KinsokuLineEndChars = "(【",
                KinsokuHangChars = "。",
            };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.Equal(")】", loaded.KinsokuLineStartChars);
            Assert.Equal("(【", loaded.KinsokuLineEndChars);
            Assert.Equal("。", loaded.KinsokuHangChars);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_restores_default_kinsoku_when_null()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(
                path,
                "{\"KinsokuLineStartChars\":null,\"KinsokuLineEndChars\":null,\"KinsokuHangChars\":null}"
            );
            var s = SettingsStore.Load(path);
            var def = new AppSettings();
            Assert.Equal(def.KinsokuLineStartChars, s.KinsokuLineStartChars); // null→既定
            Assert.Equal(def.KinsokuLineEndChars, s.KinsokuLineEndChars);
            Assert.Equal(def.KinsokuHangChars, s.KinsokuHangChars);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_preserves_empty_kinsoku_as_disabled()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(
                path,
                "{\"KinsokuLineStartChars\":\"\",\"KinsokuLineEndChars\":\"\",\"KinsokuHangChars\":\"\"}"
            );
            var s = SettingsStore.Load(path);
            Assert.Equal("", s.KinsokuLineStartChars); // 空文字＝そのルール無効。保持する
            Assert.Equal("", s.KinsokuLineEndChars);
            Assert.Equal("", s.KinsokuHangChars);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_normalizes_new_keys_out_of_range()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"TabWidth\":0,\"CaretWidth\":99}");
            var s = SettingsStore.Load(path);
            Assert.Equal(4, s.TabWidth); // 範囲外→既定
            Assert.Equal(1, s.CaretWidth); // 範囲外→既定
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_ignores_unknown_removed_keys()
    {
        // P7 撤去: PreferredScreenReader は削除済み。settings.json に残っていても
        // System.Text.Json の既定挙動で未知プロパティは無視され起動失敗しない（前方互換）。
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"PreferredScreenReader\":\"pctalker\",\"TabWidth\":8}");
            var s = SettingsStore.Load(path);
            Assert.Equal(8, s.TabWidth); // 既知キーは通常通り反映
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_preserves_valid_new_keys()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(
                path,
                "{\"TabWidth\":8,\"CaretWidth\":5,"
                    + "\"CsvAutoModeOnOpen\":true,\"TabsToSpaces\":true,\"ShowLineNumbers\":true,"
                    + "\"HighlightCurrentLine\":true,\"ShowWhitespace\":true,\"ConfirmRestoreOnStartup\":false}"
            );
            var s = SettingsStore.Load(path);
            Assert.Equal(8, s.TabWidth);
            Assert.Equal(5, s.CaretWidth);
            Assert.True(s.CsvAutoModeOnOpen);
            Assert.True(s.TabsToSpaces);
            Assert.True(s.ShowLineNumbers);
            Assert.True(s.HighlightCurrentLine);
            Assert.True(s.ShowWhitespace);
            Assert.False(s.ConfirmRestoreOnStartup);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
