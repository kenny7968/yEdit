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
}
