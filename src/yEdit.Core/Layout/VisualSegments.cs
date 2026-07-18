namespace yEdit.Core.Layout;

/// <summary>視覚セグメント列(<see cref="LineLayout.Wrap"/> 出力)への共通照会を集約する。
/// EditorControl の TryFindVisualSegmentCore / VerticalNavigation.FindSegIndex /
/// NavigationCommands.MoveHomeSmart(wrap overload) から共有する。</summary>
public static class VisualSegments
{
    /// <summary>offsetInLine を含む視覚セグメントの (index, segment) を返す。</summary>
    /// <remarks>行末位置(=最終 segEnd)は最終セグメント扱い。
    /// 空 segs は非対応=<see cref="LineLayout.Wrap"/> は空入力でも [(0,0)] を返す契約なので
    /// 呼び出し側で空 segs を渡さないことを保証する。</remarks>
    public static (int Index, WrapSegment Segment) FindContaining(
        IReadOnlyList<WrapSegment> segs,
        int offsetInLine
    )
    {
        for (int i = 0; i < segs.Count; i++)
        {
            int segEnd = segs[i].OffsetInLine + segs[i].Length;
            if (offsetInLine < segEnd || i == segs.Count - 1)
                return (i, segs[i]);
        }
        throw new System.ArgumentException("segs must not be empty", nameof(segs));
    }
}
