using System;
using System.Windows.Forms;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlWordNavEventTests
{
    [Fact]
    public void CtrlRight_FiresWordNavigatedEvent()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                ctrl.SetCaretCharOffset(0);
                WordNavigatedEventArgs? received = null;
                ctrl.WordNavigated += (_, e) => received = e;

                EditorControl.TestHook_SendKey(ctrl, Keys.Right | Keys.Control);
                Application.DoEvents();

                Assert.NotNull(received);
                // WordBoundary.NextWordStart("hello world", 0) は "world" の先頭 = 6 を返す想定
                Assert.Equal(0, received!.WordStart);
                Assert.Equal(6, received.WordEnd);
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void ShiftCtrlRight_DoesNotFire()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                ctrl.SetCaretCharOffset(0);
                int callCount = 0;
                ctrl.WordNavigated += (_, _) => callCount++;

                EditorControl.TestHook_SendKey(ctrl, Keys.Right | Keys.Control | Keys.Shift);
                Application.DoEvents();

                Assert.Equal(0, callCount);
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void RaiseUiaSelectionEvents_False_SuppressesWordNav()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.RaiseUiaSelectionEvents = false;
            using var form = new Form(); form.Controls.Add(ctrl); form.Show();
            try
            {
                ctrl.SetCaretCharOffset(0);
                int callCount = 0;
                ctrl.WordNavigated += (_, _) => callCount++;

                EditorControl.TestHook_SendKey(ctrl, Keys.Right | Keys.Control);
                Application.DoEvents();

                Assert.Equal(0, callCount);
            }
            finally { form.Close(); }
        });
    }
}
