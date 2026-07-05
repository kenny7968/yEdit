namespace yEdit.Core.Buffers;

/// <summary>
/// 区分(ピース)ローカルの統計。改行セマンティクス:
/// Breaks = 区分内のLF数 + 「区分内でLFが直後に続かないCR」の数(末尾CRは単独扱いで数える)。
/// </summary>
internal readonly record struct PieceStats(long ByteLen, int CharLen, int Breaks, bool FirstIsLf, bool LastIsCr)
{
    public static readonly PieceStats Empty = default;
}
