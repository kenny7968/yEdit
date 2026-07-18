namespace yEdit.Core.Text;

/// <summary>
/// 「最近のファイル」リストの純ロジック（UI 非依存・テスト可能）。先頭が最新。
/// PathKey で正規化して重複を除き（同一ファイルの大小/区切り違いを 1 件に）、上限でクランプする。
/// </summary>
public static class RecentFilesList
{
    /// <summary>
    /// current の先頭に path を加えた新リストを返す。path と同一（PathKey 一致）の既存項目は除き、
    /// 全体を max 件にクランプする。max が 0 以下なら空リスト。
    /// </summary>
    public static List<string> Add(IEnumerable<string> current, string path, int max)
    {
        var result = new List<string>();
        if (max <= 0)
            return result;

        result.Add(path);
        string key = PathKey.For(path);
        foreach (string p in current)
        {
            if (result.Count >= max)
                break; // 追加前に上限判定（max==1 の超過を防ぐ）
            if (PathKey.For(p) == key)
                continue; // 同一ファイルは先頭の 1 件に集約
            result.Add(p);
        }
        return result;
    }
}
