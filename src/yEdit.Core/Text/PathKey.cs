namespace yEdit.Core.Text;

/// <summary>
/// 同一ファイル判定用の正規化キー。Windows 前提で大文字小文字を無視し、
/// GetFullPath で相対パス・区切り文字差を吸収する。正規化できない場合は
/// 元文字列を小文字化して返す（比較が破綻しないようにする）。
/// </summary>
public static class PathKey
{
    public static string For(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }
        return full.ToLowerInvariant();
    }
}
