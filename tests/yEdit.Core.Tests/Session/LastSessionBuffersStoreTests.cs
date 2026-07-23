using System.IO;
using Xunit;
using yEdit.Core.Session;

namespace yEdit.Core.Tests.Session;

public class LastSessionBuffersStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var map = LastSessionBuffersStore.Load(TempPath());
        Assert.Empty(map);
    }

    [Fact]
    public void Save_Then_Load_Roundtrips()
    {
        string path = TempPath();
        try
        {
            var src = new Dictionary<string, string>
            {
                ["k1"] = "hello",
                ["k2"] = "こんにちは\n2行目",
            };
            LastSessionBuffersStore.Save(path, src);
            var loaded = LastSessionBuffersStore.Load(path);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("hello", loaded["k1"]);
            Assert.Equal("こんにちは\n2行目", loaded["k2"]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json");
            var map = LastSessionBuffersStore.Load(path);
            Assert.Empty(map);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, string.Empty);
            var map = LastSessionBuffersStore.Load(path);
            Assert.Empty(map);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesParentDirectoryIfMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string path = Path.Combine(dir, "last-session-buffers.json");
        try
        {
            var map = new Dictionary<string, string> { ["k"] = "v" };
            LastSessionBuffersStore.Save(path, map);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Delete_MissingFile_IsNoOp()
    {
        string path = TempPath();
        // 例外にならず、存在しないファイルが依然として存在しないこと(no-op)を確認する。
        LastSessionBuffersStore.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_ExistingFile_Removes()
    {
        string path = TempPath();
        File.WriteAllText(path, "{}");
        LastSessionBuffersStore.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_JsonWithNullValue_SkipsNullEntries()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"k1\":null,\"k2\":\"ok\"}");
            var map = LastSessionBuffersStore.Load(path);
            Assert.False(map.ContainsKey("k1")); // 明示 null は skip
            Assert.Equal("ok", map["k2"]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_EmptyDict_And_Load_Roundtrips()
    {
        string path = TempPath();
        try
        {
            LastSessionBuffersStore.Save(path, new Dictionary<string, string>());
            var loaded = LastSessionBuffersStore.Load(path);
            Assert.Empty(loaded);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_Overwrites_Existing_File()
    {
        string path = TempPath();
        try
        {
            LastSessionBuffersStore.Save(path, new Dictionary<string, string> { ["k1"] = "first" });
            LastSessionBuffersStore.Save(
                path,
                new Dictionary<string, string> { ["k2"] = "second" }
            );
            var loaded = LastSessionBuffersStore.Load(path);
            Assert.Single(loaded);
            Assert.Equal("second", loaded["k2"]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
