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
}
