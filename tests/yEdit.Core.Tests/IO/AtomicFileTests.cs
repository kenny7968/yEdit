using Xunit;
using yEdit.Core.IO;

namespace yEdit.Core.Tests.IO;

public class AtomicFileTests
{
    [Fact]
    public void Write_creates_new_file_with_payload()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            AtomicFile.Write(path, new byte[] { 1, 2, 3 });
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Write_overwrites_existing_and_leaves_no_tmp()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "old");
            AtomicFile.Write(path, new byte[] { 0x6E, 0x65, 0x77 }); // "new"
            Assert.Equal("new", File.ReadAllText(path));
            Assert.Empty(
                Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*tmp*")
            );
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Write_to_fully_locked_target_throws_share_violation_and_keeps_original()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "original");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Assert.Throws<IOException>(() => AtomicFile.Write(path, new byte[] { 9 }));
                Assert.True(AtomicFile.IsShareOrLockViolation(ex));
            }
            // 原本は不変・tmp 残骸なし。
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Empty(
                Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*tmp*")
            );
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void IsShareOrLockViolation_is_false_for_generic_io_error() =>
        Assert.False(AtomicFile.IsShareOrLockViolation(new IOException("generic")));

    // CSV-L-7 (v0.11): reparse point guard の IOException は「共有違反」ではないため、
    // TextFileService.Save 側の in-place fallback 経路
    // (catch when IsShareOrLockViolation → File.WriteAllBytes) に流れ込まない invariant を pin する。
    // ここが崩れると reparse point 検出後もフォールバック書込が follow して結果的に上書きされる。
    [Fact]
    public void IsShareOrLockViolation_is_false_for_reparse_point_guard_exception() =>
        Assert.False(
            AtomicFile.IsShareOrLockViolation(
                new IOException("reparse point の上書きは許可されていません: x")
            )
        );

    // CSV-L-7 (v0.11): File.Replace は reparse point (symlink / directory junction) を
    // follow して権限外の実体を上書きし得るため、byte[] / Stream 両オーバーロードの
    // 冒頭で dest を検査し IOException で拒否する契約を pin する。junction は非管理者権限で
    // 作成できるので CI (windows-latest) で確実に成立する
    // (symlink 版は Developer Mode 前提 = 環境依存で不採用)。

    [Fact]
    public void Write_bytes_throws_when_destination_is_reparse_point_junction()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string target = Path.Combine(tempRoot, "target");
        string junction = Path.Combine(tempRoot, "link");
        Directory.CreateDirectory(target);
        try
        {
            ReparsePointCheckTests.CreateJunction(junction, target);
            var ex = Assert.Throws<IOException>(() =>
                AtomicFile.Write(junction, new byte[] { 1, 2, 3 })
            );
            // 生の File.Replace ではなく本 guard が発火したことを message で pin
            // (guard 追加前は Move/Replace が別 IOException を投げるため区別可能)。
            Assert.Contains("reparse point", ex.Message, StringComparison.OrdinalIgnoreCase);
            // guard は tmp を作らずに即 throw する契約=junction 親に *.tmp が残っていない。
            Assert.Empty(Directory.GetFiles(tempRoot, "*.tmp"));
            // junction target 側の実体も一切書かれていない。
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            ReparsePointCheckTests.SafeCleanupJunctionTree(junction, target, tempRoot);
        }
    }

    [Fact]
    public void Write_stream_throws_when_destination_is_reparse_point_junction()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string target = Path.Combine(tempRoot, "target");
        string junction = Path.Combine(tempRoot, "link");
        Directory.CreateDirectory(target);
        try
        {
            ReparsePointCheckTests.CreateJunction(junction, target);
            // writer は呼ばれずに guard で throw されることを pin する (writerInvoked=false)。
            bool writerInvoked = false;
            var ex = Assert.Throws<IOException>(() =>
                AtomicFile.Write(
                    junction,
                    s =>
                    {
                        writerInvoked = true;
                        s.WriteByte(0x39);
                    }
                )
            );
            Assert.Contains("reparse point", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(writerInvoked);
            Assert.Empty(Directory.GetFiles(tempRoot, "*.tmp"));
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            ReparsePointCheckTests.SafeCleanupJunctionTree(junction, target, tempRoot);
        }
    }
}
