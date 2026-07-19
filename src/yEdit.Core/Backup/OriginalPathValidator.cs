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

            // DOS device path プレフィックス(\\?\C:\..., \\.\C:\...)は .NET 9 の Path.GetFullPath 後も
            // 剥がされずに残るため、そのまま BlockedRoots (C:\Windows\... 等)との StartsWith 判定に流すと
            // 素通りしてしまう(実証: 攻撃者 JSON に `\\?\C:\Windows\System32\drivers\etc\hosts` を植えると
            // Ok が返る)。判定用にコピーを 1 本作り、そこから 4 文字プレフィックスを剥がして評価する。
            // \\?\UNC\server\share\... は「本物の UNC を長パス表現した安全な形式」なので、
            // \\server\share\... に戻したうえで既存の UNC 経路と同じ扱い(BlockedRoots に非該当=Ok)にする。
            string forCheck = normalized;
            if (forCheck.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                forCheck = @"\\" + forCheck[8..];
            else if (
                forCheck.StartsWith(@"\\?\", StringComparison.Ordinal)
                || forCheck.StartsWith(@"\\.\", StringComparison.Ordinal)
            )
                forCheck = forCheck[4..];

#pragma warning disable S3267 // foreach を LINQ Where に置換しない: plan Step 1.8 に従い可読性を優先する。
            foreach (var root in BlockedRoots)
            {
                if (
                    forCheck.StartsWith(
                        root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    return PathValidation.Rejected;
            }
#pragma warning restore S3267
            return PathValidation.Ok;
        }
        catch
        {
            return PathValidation.Rejected;
        }
    }
}
