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
