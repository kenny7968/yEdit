namespace yEdit.Core.IO;

/// <summary>
/// パスが UNC 形式(\\server\share\...)か否かの純粋述語。
/// マップドネットワークドライブ経由の判定は含まない(将来の MEDIUM リリースで扱う)。
/// </summary>
public static class UncPathDetector
{
    public static bool IsUnc(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }
}
