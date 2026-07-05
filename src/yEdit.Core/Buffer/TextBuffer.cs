using System.Text;

namespace yEdit.Core.Buffers;

/// <summary>
/// UTF-8永続ピーステーブルのテキストバッファ。公開オフセットはすべてUTF-16コード単位。
/// スナップショット(Current)はルート参照コピーで O(1)・以後の編集の影響を受けない。
/// </summary>
/// <summary>Undo/Redo後の推奨キャレット位置。</summary>
public readonly record struct UndoResult(int CaretPos);

public sealed class TextBuffer
{
    private readonly AppendBuffer _append = new();
    private readonly UndoHistory _history = new();
    private TextSnapshot _current;
    private PieceTree.Node? _savedRoot;

    internal TextBuffer(PieceTree.Node? root)
    {
        _current = new TextSnapshot(root);
        _savedRoot = root;
    }

    /// <summary>小文書・テスト用。ストリーム読込は TextBufferBuilder を使う。</summary>
    public static TextBuffer FromString(string text)
    {
        var builder = new TextBufferBuilder();
        builder.Add(Encoding.UTF8.GetBytes(text));
        return builder.Build();
    }

    public TextSnapshot Current => _current;

    /// <summary>現在ルート != 保存時ルート(参照比較。Undoで保存点に戻ると false に戻る)。</summary>
    public bool Modified => !ReferenceEquals(_current.Root, _savedRoot);

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    /// <summary>Undo。不可なら null。キャレット= Pos + RemovedLen(削除が復元された末尾)。</summary>
    public UndoResult? Undo()
    {
        var e = _history.PopUndo();
        if (e is null) return null;
        _current = new TextSnapshot(e.Value.RootBefore);
        return new UndoResult(e.Value.Pos + e.Value.RemovedLen);
    }

    /// <summary>Redo。不可なら null。キャレット= Pos + InsertedLen。</summary>
    public UndoResult? Redo()
    {
        var e = _history.PopRedo();
        if (e is null) return null;
        _current = new TextSnapshot(e.Value.RootAfter);
        return new UndoResult(e.Value.Pos + e.Value.InsertedLen);
    }

    /// <summary>キャレット移動・保存時にApp側が呼ぶ(coalescing強制分割)。</summary>
    public void BreakUndoCoalescing() => _history.BreakCoalescing();

    /// <summary>SavePoint。以後 Modified は現在ルートとの参照比較。
    /// 保存点はcoalescing境界(Undoが保存点を飛び越えて融合エントリを戻さないように)。</summary>
    public void MarkSaved()
    {
        _savedRoot = _current.Root;
        _history.BreakCoalescing();
    }

    /// <summary>EmptyUndoBuffer相当。両スタック破棄(保存点は維持)。</summary>
    public void ClearUndo() => _history.Clear();

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

        var rootBefore = _current.Root;

        // 2) 木を [0,start) / [start,end) / [end,…) に分割し中央を捨てる
        var (l, rest) = PieceTree.Split(rootBefore, start);
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

        // 5) 再結合+Undoログ記録
        var mid = left;
        foreach (var p in newPieces) mid = PieceTree.Join(mid, p, null);
        var newRoot = PieceTree.Join2(mid, right);
        _current = new TextSnapshot(newRoot);
        // 挿入のUTF-16長: 孤立サロゲート→U+FFFD 置換は1単位→1単位なので insert.Length と一致
        _history.Record(rootBefore, newRoot, start, end - start, insert.Length,
                        insert.Contains('\n') || insert.Contains('\r'));
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
