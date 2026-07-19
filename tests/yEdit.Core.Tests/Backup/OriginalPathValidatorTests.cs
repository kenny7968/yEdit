using Xunit;
using yEdit.Core.Backup;

namespace yEdit.Core.Tests.Backup;

/// <summary>
/// HIGH-2: BackupRecord.OriginalPath が復元先として安全か検証する契約を固定する。
/// 攻撃者 JSON が Windows / System32 / ProgramFiles 系のシステムパスへ復元させ、
/// ユーザ操作(Ctrl+S)で任意ファイル上書きに繋がる導線を遮断する(Untitled フォールバック)。
/// </summary>
public class OriginalPathValidatorTests
{
    [Fact]
    public void Check_ReturnsOk_ForNormalUserPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "notes.txt");
        var status = OriginalPathValidator.Check(path, out var normalized);
        Assert.Equal(PathValidation.Ok, status);
        Assert.Equal(Path.GetFullPath(path), normalized);
    }

    [Fact]
    public void Check_Rejects_RelativePath()
    {
        var status = OriginalPathValidator.Check(@"..\evil.txt", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_System32Path()
    {
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = Path.Combine(sys32, "drivers", "etc", "hosts");
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_WindowsRootPath()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(win, "win.ini");
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_ProgramFilesPath()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(pf))
            return; // 環境依存 skip
        var path = Path.Combine(pf, "some", "app.exe");
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_ProgramDataPath()
    {
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var path = Path.Combine(pd, "yEdit", "backups", "poison.json");
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_ReturnsOk_ForUncPath()
    {
        var status = OriginalPathValidator.Check(@"\\server\share\legit.txt", out _);
        Assert.Equal(PathValidation.Ok, status);
    }

    [Fact]
    public void Check_Rejects_InvalidPathChars()
    {
        var status = OriginalPathValidator.Check("C:\\x\0y.txt", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    // ---- I-2: DOS device path プレフィックス経由のバイパス回帰ガード ----

    [Fact]
    public void Check_Rejects_ExtendedPathToSystem32()
    {
        // \\?\C:\Windows\System32\... は .NET 9 の Path.GetFullPath でも \\?\ が残り、
        // 素の StartsWith("C:\\Windows\\") 判定を素通りする。stripping で塞ぐ。
        var status = OriginalPathValidator.Check(
            @"\\?\C:\Windows\System32\drivers\etc\hosts",
            out _
        );
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_DosDevicePathToWindowsRoot()
    {
        // \\.\ プレフィックス版も同様に BlockedRoots を素通りするため塞ぐ。
        var status = OriginalPathValidator.Check(@"\\.\C:\Windows\win.ini", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_ReturnsOk_ForExtendedUncPath()
    {
        // \\?\UNC\server\share\file.txt は「本物の UNC を長パスで表現」した安全な形式。
        // 判定は先頭 \\?\UNC\ を剥がし \\server\share\ に戻して評価する=Ok に落ちる。
        var status = OriginalPathValidator.Check(@"\\?\UNC\server\share\legit.txt", out _);
        Assert.Equal(PathValidation.Ok, status);
    }
}
