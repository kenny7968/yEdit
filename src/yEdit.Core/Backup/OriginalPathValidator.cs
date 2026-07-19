namespace yEdit.Core.Backup;

public enum PathValidation
{
    Ok,
    Rejected,
}

/// <summary>
/// BackupRecord.OriginalPath が復元先として安全か検証する。
/// Windows のシステム/プログラム系ルート配下は Rejected(攻撃者 JSON からの
/// 任意ファイル上書きを塞ぐ)。ユーザ配下は Ok。UNC は Ok(実運用サポート)。
/// </summary>
public static class OriginalPathValidator
{
    private static readonly string[] BlockedRoots = BuildBlockedRoots();

    private static string[] BuildBlockedRoots() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        }
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar))
            .ToArray();

    public static PathValidation Check(string path, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return PathValidation.Rejected;
            normalized = Path.GetFullPath(path);
            string full = normalized;
            bool blocked = BlockedRoots.Any(root =>
                full.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            return blocked ? PathValidation.Rejected : PathValidation.Ok;
        }
        catch
        {
            return PathValidation.Rejected;
        }
    }
}
