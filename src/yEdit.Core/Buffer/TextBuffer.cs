using System.Text;

namespace yEdit.Core.Buffers;

/// <summary>
/// UTF-8永続ピーステーブルのテキストバッファ。公開オフセットはすべてUTF-16コード単位。
/// スナップショット(Current)はルート参照コピーで O(1)・以後の編集の影響を受けない。
/// </summary>
public sealed class TextBuffer
{
    private readonly AppendBuffer _append = new();
    private TextSnapshot _current;

    internal TextBuffer(PieceTree.Node? root) => _current = new TextSnapshot(root);

    /// <summary>小文書・テスト用。ストリーム読込は TextBufferBuilder を使う。</summary>
    public static TextBuffer FromString(string text)
    {
        var builder = new TextBufferBuilder();
        builder.Add(Encoding.UTF8.GetBytes(text));
        return builder.Build();
    }

    public TextSnapshot Current => _current;

    public void Insert(int pos, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateRange(pos, 0);
        Splice(pos, 0, text);
    }

    public void Delete(int pos, int length)
    {
        ValidateRange(pos, length);
        Splice(pos, length, "");
    }

    /// <summary>1 Undo単位のsplice。</summary>
    public void Replace(int pos, int length, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateRange(pos, length);
        Splice(pos, length, text);
    }

    /// <summary>全編集の共通経路。範囲 [pos, pos+delLen) を insert で置き換える。</summary>
    private void Splice(int pos, int delLen, string insert)
    {
        // 1) サロゲートペア中間は前方(低い方)へスナップ(§0-3)
        int start = SnapLow(pos);
        int end = SnapLow(pos + delLen);
        if (start == end && insert.Length == 0) return;   // 無変化

        // 2) 木を [0,start) / [start,end) / [end,…) に分割し中央を捨てる
        var (l, rest) = PieceTree.Split(_current.Root, start);
        var (_, r) = PieceTree.Split(rest, end - start);

        // 3) 挿入テキストを追記バッファへ
        var newPieces = _append.Append(insert);

        // 4) 隣接マージ: 同一チャンクかつバイト連続なら1ピース化(連続タイピングでピース数が伸びない)
        PieceTree.Node? left = l;
        if (newPieces.Count > 0 && left is not null)
        {
            var (remaining, last) = PieceTree.SplitLast(left);
            var first = newPieces[0];
            if (ReferenceEquals(last.Chunk, first.Chunk) && last.ByteStart + last.ByteLen == first.ByteStart)
            {
                newPieces[0] = new Piece(last.Chunk, last.ByteStart, last.ByteLen + first.ByteLen,
                                         PieceStats.Combine(last.Stats, first.Stats));
                left = remaining;
            }
            else left = PieceTree.Join(remaining, last, null);
        }
        PieceTree.Node? right = r;
        if (newPieces.Count > 0 && right is not null)
        {
            var (first, remaining) = PieceTree.SplitFirst(right);
            var lastNew = newPieces[^1];
            if (ReferenceEquals(lastNew.Chunk, first.Chunk) && lastNew.ByteStart + lastNew.ByteLen == first.ByteStart)
            {
                newPieces[^1] = new Piece(first.Chunk, lastNew.ByteStart, lastNew.ByteLen + first.ByteLen,
                                          PieceStats.Combine(lastNew.Stats, first.Stats));
                right = remaining;
            }
            else right = PieceTree.Join(null, first, remaining);
        }

        // 5) 再結合
        var mid = left;
        foreach (var p in newPieces) mid = PieceTree.Join(mid, p, null);
        _current = new TextSnapshot(PieceTree.Join2(mid, right));
    }

    /// <summary>サロゲートペア中間なら前方(低い方)の境界へ。</summary>
    private int SnapLow(int p)
    {
        if (p > 0 && p < _current.CharLength && char.IsLowSurrogate(_current.GetChar(p))) p--;
        return p;
    }

    private void ValidateRange(int pos, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pos);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if ((long)pos + length > _current.CharLength)
            throw new ArgumentOutOfRangeException(nameof(pos), pos, "範囲が文書末尾を超えています。");
    }
}
