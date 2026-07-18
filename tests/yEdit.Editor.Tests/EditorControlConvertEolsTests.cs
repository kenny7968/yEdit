using System.Windows.Forms;
using Xunit;
using yEdit.Core.Buffers;
using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.Editor.Tests;

public class EditorControlConvertEolsTests
{
    [Fact]
    public void ConvertEols_ToCrlf_ReplacesLoneLfs()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal("aaa\r\nbbb\r\nccc", ctrl.SnapshotText);
            Assert.Equal(LineEnding.Crlf, ctrl.EolMode);
        });
    }

    [Fact]
    public void ConvertEols_ToLf_ReplacesCrlfs()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\r\nbbb\r\nccc"));
            ctrl.ConvertEols(LineEnding.Lf);
            Assert.Equal("aaa\nbbb\nccc", ctrl.SnapshotText);
        });
    }

    [Fact]
    public void ConvertEols_ToCr_ReplacesAll()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\r\nbbb\nccc\r"));
            ctrl.ConvertEols(LineEnding.Cr);
            Assert.Equal("aaa\rbbb\rccc\r", ctrl.SnapshotText);
        });
    }

    [Fact]
    public void ConvertEols_BeforeSetSource_NoOp()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // Should not throw; buffer is null so early return
            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal(string.Empty, ctrl.SnapshotText);
        });
    }

    [Fact]
    public void ConvertEols_FastPath_PreservesCaretAndSetsEolMode()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // Buffer already in LF form — target=Lf means converted == src → fast-path
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.SetCaretCharOffset(5);
            Assert.Equal(5, ctrl.CaretCharOffset);

            ctrl.ConvertEols(LineEnding.Lf);

            // Fast-path: buffer NOT rebuilt via ReplaceSource, so caret preserved
            Assert.Equal(5, ctrl.CaretCharOffset);
            Assert.Equal(LineEnding.Lf, ctrl.EolMode);
            Assert.Equal("aaa\nbbb\nccc", ctrl.SnapshotText);
        });
    }

    // P6 レビュー I-2 回帰: 非 fast-path でも caret 論理位置が保持される
    [Fact]
    public void ConvertEols_NonFastPath_PreservesCaretLogicalPosition_LfToCrlf()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // 3 行 LF、行 1 の 2 文字目にキャレット="aaa\n"+"b"+"b" の直後
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.SetCaretCharOffset(6); // 'b' 'b' の間=行 1 の offset 2

            ctrl.ConvertEols(LineEnding.Crlf);

            // 変換後: "aaa\r\nbbb\r\nccc"、同じ論理位置は行 1 の offset 2=absolute offset 7
            // (非改行文字 5=a a a b b + 改行数 1=\n→\r\n の 2 chars)
            Assert.Equal("aaa\r\nbbb\r\nccc", ctrl.SnapshotText);
            Assert.Equal(7, ctrl.CaretCharOffset);
            Assert.Equal(LineEnding.Crlf, ctrl.EolMode);
        });
    }

    [Fact]
    public void ConvertEols_NonFastPath_PreservesCaretLogicalPosition_CrlfToLf()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // 3 行 CRLF、行 2 の先頭="aaa\r\nbbb\r\n"+"ccc" の先頭 c
            ctrl.SetSource(TextBuffer.FromString("aaa\r\nbbb\r\nccc"));
            ctrl.SetCaretCharOffset(10); // 行 2 の offset 0

            ctrl.ConvertEols(LineEnding.Lf);

            // 変換後: "aaa\nbbb\nccc"、同じ論理位置は絶対 8(非改行 6+改行 2)
            Assert.Equal("aaa\nbbb\nccc", ctrl.SnapshotText);
            Assert.Equal(8, ctrl.CaretCharOffset);
            Assert.Equal(LineEnding.Lf, ctrl.EolMode);
        });
    }

    [Fact]
    public void ConvertEols_NonFastPath_PreservesAnchorForSelection()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            // 選択: [4, 7)=行 1 全体("bbb")
            ctrl.SelectCharRange(4, 3);
            var (s0, e0) = ctrl.GetSelectionCharRange();
            Assert.Equal((4, 7), (s0, e0));

            ctrl.ConvertEols(LineEnding.Crlf);

            // 変換後 "aaa\r\nbbb\r\nccc" で行 1 全体="bbb"=[5, 8)
            var (s1, e1) = ctrl.GetSelectionCharRange();
            Assert.Equal((5, 8), (s1, e1));
        });
    }

    // P7 I-3 Task 3: chunk 境界(=TextBufferBuilder.TargetChunkBytes 近傍)で
    // CRLF が別チャンクへ跨っても LF に正しく統一される(byte 単位走査+pendingCr 吸収の回帰)。
    [Fact]
    public void ConvertEols_Utf8_LargeContent_ChunkBoundary_CrlfSpansChunks()
    {
        Sta.Run(() =>
        {
            // 4MB(TextBufferBuilder.TargetChunkBytes)近傍で CRLF が切れるように文字列を組む
            // ASCII のみで 4MB - 1 バイトのフィラー + "\r\n" を境界に置く
            int fill = 4 * 1024 * 1024 - 1;
            string body = new string('a', fill) + "\r\n" + "tail\n";
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(body));
            ctrl.ConvertEols(LineEnding.Lf);
            string result = ctrl.SnapshotText;
            Assert.Equal(new string('a', fill) + "\n" + "tail\n", result);
        });
    }

    // P7 I-3 Task 3: 混在 EOL(CRLF/CR/LF)が一括で target=CRLF に統一される(fast-path 非適用パス)。
    [Fact]
    public void ConvertEols_Utf8_MixedEols_AllConvertedToTarget()
    {
        Sta.Run(() =>
        {
            string body = "a\r\nb\rc\nd\r\ne";
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(body));
            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal("a\r\nb\r\nc\r\nd\r\ne", ctrl.SnapshotText);
        });
    }

    // P7 I-3 Task 3 Minor-2: 文書末尾が孤立 CR = foreach 後の `if (pendingCr) EmitEol` の drain 経路を検証。
    [Fact]
    public void ConvertEols_TrailingLoneCr_ToLf_DrainedByPendingCr()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r"));
            ctrl.ConvertEols(LineEnding.Lf);
            Assert.Equal("abc\n", ctrl.SnapshotText);
        });
    }

    // P7 I-3 Task 3 Minor-2: CRLF が 4MB ピース境界を跨ぐ +全体 CRLF 統一 +target=CRLF
    // → IsEolAlreadyUniform が pendingCr で境界跨ぎ CRLF を正しく accept、fast-path で return する。
    // fast-path 発火の証拠: EolMode だけ更新される(挙動観察=結果本文が完全に不変)。
    [Fact]
    public void ConvertEols_FastPath_CrlfSpansChunks_WithTargetCrlf_NoRebuild()
    {
        Sta.Run(() =>
        {
            int fill = 4 * 1024 * 1024 - 1;
            string body = new string('a', fill) + "\r\n" + new string('b', 100) + "\r\n";
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(body));
            ctrl.ConvertEols(LineEnding.Crlf);
            // 変換不要=完全不変
            Assert.Equal(body, ctrl.SnapshotText);
            Assert.Equal(LineEnding.Crlf, ctrl.EolMode);
        });
    }

    // P7 I-3 Task 3 Minor-2: 文書 "a\r\nb"(char length=4)にキャレット=2(CRLF の LF を指す)
    // → CountNonBreakAndBreaksInSnapshot は「\r 単独 + LF 単独」に分けて数える安全側=(M=1, K=1)
    // 変換後 "a\nb"(char length=3)にキャレット=1+1*1=2(=同じ論理位置)
    [Fact]
    public void ConvertEols_CaretOnLfOfCrlf_ConvertToLf_LogicalPositionPreserved()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            ctrl.SetCaretCharOffset(2); // CRLF の LF を指す
            ctrl.ConvertEols(LineEnding.Lf);
            Assert.Equal("a\nb", ctrl.SnapshotText);
            // caret は「a\n」の直後(=位置 2)にあるはず(CRLF→LF で 1 個縮んだ論理位置と一致)
            Assert.Equal(2, ctrl.CaretCharOffset);
        });
    }
}
