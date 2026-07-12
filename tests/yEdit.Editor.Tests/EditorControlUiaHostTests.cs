using System;
using System.Windows.Forms;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlUiaHostTests
{
    [Fact]
    public void Host_GetTextRange_ReturnsSubstring()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("Hello, world!");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal("Hello", host.GetTextRange(0, 5));
            Assert.Equal("world", host.GetTextRange(7, 5));
        });
    }

    [Fact]
    public void Host_TextLength_MatchesBuffer()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdef");
            ctrl.SetSource(buf);
            Assert.Equal(6, ((IUiaTextHost)ctrl).TextLength);
        });
    }

    [Fact]
    public void Host_GetSelection_ReturnsCurrent()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdefg");
            ctrl.SetSource(buf);
            ctrl.SetSelectionCharRange(2, 5);
            Assert.Equal((2, 5), ((IUiaTextHost)ctrl).GetSelection());
        });
    }

    [Fact]
    public void Host_LineStartOf_ReturnsLineStart()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("aaa\nbbb\nccc");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal(0, host.LineStartOf(2));
            Assert.Equal(4, host.LineStartOf(5));
            Assert.Equal(8, host.LineStartOf(10));
        });
    }

    [Fact]
    public void Host_LineEndNoBreakOf_ExcludesLineBreak()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("aaa\nbbb");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal(3, host.LineEndNoBreakOf(1));   // "aaa" の後・"\n" 前
            Assert.Equal(7, host.LineEndNoBreakOf(5));   // 末尾行
        });
    }

    // ===== P8-1c: 折り返し ON での視覚行境界(N-3 修正) =====

    [Fact]
    public void Host_LineStartOf_WithWrap_ReturnsVisualSegmentStart()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // "abcdefghij" 10 文字を wrapColumns=4 で分割
            // GDI ASCII 幅は環境依存だが、ASCII は 8px 前後で MonoCharMetrics 相当。ここは実 GDI で計測。
            var buf = TextBuffer.FromString("abcdefghij");
            ctrl.SetSource(buf);
            ctrl.WrapColumns = 4;
            IUiaTextHost host = ctrl;
            int startOfMid = host.LineStartOf(6);  // caret 6 の視覚 seg 先頭
            int startOfEnd = host.LineStartOf(9);  // caret 9 の視覚 seg 先頭
            // 継続 seg=論理行先頭(0)ではなく視覚 seg の start
            Assert.True(startOfMid > 0, $"expected visual seg start > 0 for mid caret, got {startOfMid}");
            Assert.True(startOfEnd > 0, $"expected visual seg start > 0 for end caret, got {startOfEnd}");
        });
    }

    [Fact]
    public void Host_LineStartOf_WithWrap_FirstSegOfLine_ReturnsLogicalStart()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdefghij");
            ctrl.SetSource(buf);
            ctrl.WrapColumns = 4;
            IUiaTextHost host = ctrl;
            // 第 1 視覚 seg 内(offset 0..3 想定)は論理行先頭 0
            Assert.Equal(0, host.LineStartOf(0));
            Assert.Equal(0, host.LineStartOf(1));
        });
    }

    [Fact]
    public void Host_LineEnd_WithWrap_ContinuationSeg_DoesNotCrossBreak()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdefghij\nsecond");
            ctrl.SetSource(buf);
            ctrl.WrapColumns = 4;
            IUiaTextHost host = ctrl;
            // 第 1 論理行内の継続 seg の LineEnd は次視覚 seg 先頭=改行を跨がない
            int end = host.LineEnd(2);   // 第 1 論理行の第 1 seg 内
            Assert.True(end <= 10, $"continuation LineEnd should not cross break (10), got {end}");
        });
    }

    [Fact]
    public void Host_LineEnd_WithWrap_LastSegOfLine_CrossesBreak()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("ab\ncd");
            ctrl.SetSource(buf);
            ctrl.WrapColumns = 4;  // 2 文字は 1 視覚 seg に収まる=通常の論理行と同じ
            IUiaTextHost host = ctrl;
            // 論理行末最終 seg は改行を含めて次論理行先頭を返す(既存挙動維持)
            Assert.Equal(3, host.LineEnd(1));  // "ab" の後 = 3(改行含む)
            Assert.Equal(5, host.LineEnd(4));  // "cd" 末尾 = TextLength
        });
    }

    [Fact]
    public void Host_LineStartOf_WrapOff_UsesLogicalLine()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("aaa\nbbbbbbbbbb");   // wrap OFF なら bbb 側は 1 論理行
            ctrl.SetSource(buf);
            // WrapColumns = 0 が既定=wrap OFF
            IUiaTextHost host = ctrl;
            // 論理行先頭(0)を返す(P8-1c 前と同じ)
            Assert.Equal(4, host.LineStartOf(10));
            Assert.Equal(4, host.LineStartOf(13));
        });
    }

    [Fact]
    public void Host_WordStart_UsesCoreWordBoundary()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("hello world");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            // "hello world" の 3 は "hello" 内 → WordStart=0
            Assert.Equal(0, host.WordStart(3));
            // "hello world" の 9 は "world" 内 → WordStart=6
            Assert.Equal(6, host.WordStart(9));
        });
    }

    [Fact]
    public void Host_ControlTypeId_IsDocument()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            IUiaTextHost host = ctrl;
            Assert.Equal(System.Windows.Automation.ControlType.Document.Id, host.ControlTypeId);
        });
    }

    [Fact]
    public void Host_AutomationId_Editor()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            IUiaTextHost host = ctrl;
            Assert.Equal("editor", host.AutomationId);
        });
    }

    [Fact]
    public void Host_Name_Honmon()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            IUiaTextHost host = ctrl;
            Assert.Equal("本文", host.Name);
        });
    }

    [Fact]
    public void Host_SetSelection_MarshalsToUIThread()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdefg");
            ctrl.SetSource(buf);
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                IUiaTextHost host = ctrl;
                host.SetSelection(1, 4);
                Application.DoEvents();   // BeginInvoke を回す
                Assert.Equal((1, 4), host.GetSelection());
            }
            finally { form.Close(); }
        });
    }
}
