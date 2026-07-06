using System.Windows.Forms;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlCompatApiTests
{
    [Fact]
    public void SnapshotText_ReturnsFullText()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            Assert.Equal("hello world", ctrl.SnapshotText);
        });
    }

    [Fact]
    public void SelectCharRange_LengthVersion_SelectsRange()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.SelectCharRange(6, 5);   // "world"
            Assert.Equal((6, 11), ctrl.GetSelectionCharRange());
        });
    }

    [Fact]
    public void MoveCaretCharOffset_MovesCaret()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.MoveCaretCharOffset(5);
            Assert.Equal(5, ctrl.CaretCharOffset);
        });
    }

    [Fact]
    public void SelectCharRange_NegativeLength_CollapsesToEmpty()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.SelectCharRange(6, -3);
            Assert.Equal((6, 6), ctrl.GetSelectionCharRange());
        });
    }

    [Fact]
    public void LineCount_ReturnsBufferLineCount()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            Assert.Equal(3, ctrl.LineCount);
        });
    }

    [Fact]
    public void GoToLine_MovesCaretToLineStart()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.GoToLine(2);   // 0-based=2 → 3行目 "ccc"
            Assert.Equal(8, ctrl.CaretCharOffset);
        });
    }

    [Fact]
    public void CurrentPosition_MatchesCaret()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            ctrl.SetCaretCharOffset(3);
            Assert.Equal(3, ctrl.CurrentPosition);
        });
    }
}
