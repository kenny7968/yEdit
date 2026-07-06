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

    [Fact]
    public void SavePointReached_Fires_WhenMarkSaved()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            ctrl.ReplaceCharRange(0, 5, "xxx");  // Modified=true
            int fired = 0;
            ctrl.SavePointReached += (_, _) => fired++;
            ctrl.SetSavePoint();
            Assert.Equal(1, fired);
            Assert.False(ctrl.Modified);
        });
    }

    [Fact]
    public void SavePointLeft_Fires_WhenModifiedAfterSave()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            int fired = 0;
            ctrl.SavePointLeft += (_, _) => fired++;
            ctrl.ReplaceCharRange(0, 5, "xxx");   // Modified=false → true
            Assert.Equal(1, fired);
        });
    }

    [Fact]
    public void UpdateUI_Fires_OnCaretMove()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            int fired = 0;
            ctrl.UpdateUI += (_, _) => fired++;
            ctrl.SetCaretCharOffset(3);
            Assert.True(fired >= 1);
        });
    }
}
