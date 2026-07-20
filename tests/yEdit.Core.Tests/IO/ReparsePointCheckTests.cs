using System.Diagnostics;
using yEdit.Core.IO;

namespace yEdit.Core.Tests.IO;

/// <summary>
/// CSV-L-7 (v0.11): <see cref="AtomicFile.Write(string, byte[])"/> /
/// <see cref="AtomicFile.Write(string, System.Action{System.IO.Stream})"/> の File.Replace が
/// reparse point (symbolic link / directory junction) を follow して権限外の実体を
/// 上書きすることを防ぐための述語のテスト。
///
/// 検証観点:
///  - 通常ファイルは false
///  - 存在しないパスは false (missing は 「新規作成」正常経路のため必ず false=silent fallback)
///  - 空文字は false (null-safety 契約)
///  - directory junction (mklink /J で作成=非管理者権限で作成可能) は true
///
/// symbol link (mklink /D, mklink 単体) 版はランナーが Developer Mode / 管理者権限
/// 前提のため scope out (junction カバーで属性 bit の判定ロジックは同一のため十分)。
/// </summary>
public class ReparsePointCheckTests
{
    [Fact]
    public void IsReparsePoint_returns_false_for_regular_file()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "hello");
            Assert.False(ReparsePointCheck.IsReparsePoint(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void IsReparsePoint_returns_false_for_missing_file()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Assert.False(File.Exists(path));
        // 新規保存は AtomicFile.Write の正常経路=missing を false に確定させる契約を pin する。
        Assert.False(ReparsePointCheck.IsReparsePoint(path));
    }

    [Fact]
    public void IsReparsePoint_returns_false_for_empty_path()
    {
        Assert.False(ReparsePointCheck.IsReparsePoint(""));
    }

    [Fact]
    public void IsReparsePoint_returns_true_for_directory_junction()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string target = Path.Combine(tempRoot, "target");
        string junction = Path.Combine(tempRoot, "link");
        Directory.CreateDirectory(target);
        try
        {
            CreateJunction(junction, target);
            Assert.True(ReparsePointCheck.IsReparsePoint(junction));
        }
        finally
        {
            SafeCleanupJunctionTree(junction, target, tempRoot);
        }
    }

    /// <summary>
    /// <c>cmd /c mklink /J</c> で directory junction を作成する。junction は非管理者権限で
    /// 作成可能なため CI (windows-latest) で確実に成立する
    /// (symbolic link は Developer Mode / 管理者権限が必要=環境依存で不採用)。
    /// </summary>
    internal static void CreateJunction(string junction, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p =
            Process.Start(psi)
            ?? throw new InvalidOperationException("mklink /J: Process.Start returned null");
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mklink /J failed (ExitCode={p.ExitCode}): {p.StandardError.ReadToEnd()}"
            );
        }
    }

    /// <summary>
    /// junction を非再帰で削除してから target/tempRoot を消す。
    /// junction を <c>Directory.Delete(recursive: true)</c> で消すと Windows/.NET のバージョン
    /// によっては target 側の実体まで巻き込む恐れがあるため、順序と recursive フラグを明示する。
    /// </summary>
    internal static void SafeCleanupJunctionTree(string junction, string target, string tempRoot)
    {
        try
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction, recursive: false);
        }
        catch
        { /* 残骸は実害小 */
        }
        try
        {
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
        catch
        { /* 残骸は実害小 */
        }
        try
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
        catch
        { /* 残骸は実害小 */
        }
    }
}
