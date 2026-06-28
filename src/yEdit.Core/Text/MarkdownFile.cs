namespace yEdit.Core.Text;

/// <summary>マークダウンファイルの判定。メニュー活性化とテストで共用する。</summary>
public static class MarkdownFile
{
    /// <summary>パスの拡張子が .md（大文字小文字無視）なら true。null・拡張子なし・他拡張子は false。</summary>
    public static bool IsMarkdownPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return string.Equals(
            System.IO.Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase);
    }
}
