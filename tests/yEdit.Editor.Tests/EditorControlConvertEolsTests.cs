using System.Windows.Forms;
using yEdit.Core.Buffers;
using yEdit.Core.Text;
using yEdit.Editor;
using Xunit;

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
            ctrl.SetCaretCharOffset(6);   // 'b' 'b' の間=行 1 の offset 2

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
            ctrl.SetCaretCharOffset(10);   // 行 2 の offset 0

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
}
