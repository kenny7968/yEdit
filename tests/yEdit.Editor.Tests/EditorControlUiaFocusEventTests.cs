using System;
using System.Windows.Forms;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlUiaFocusEventTests
{
    [Fact]
    public void OnGotFocus_RaisesFocusChangedAndTextSelectionChanged()
    {
        Sta.Run(() =>
        {
            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                using var ctrl = new EditorControl();
                ctrl.SetSource(TextBuffer.FromString("hi"));
                using var form = new Form();
                form.Controls.Add(ctrl);
                using var other = new TextBox();
                form.Controls.Add(other);
                form.Show();

                // WM_GETOBJECT 経由でプロバイダを生成させる(=RaiseUia の early return を回避)
                var msg = Message.Create(ctrl.Handle, 0x003D, System.IntPtr.Zero, new System.IntPtr(-25));
                EditorControl.TestHook_WndProc(ctrl, ref msg);

                other.Focus();
                Application.DoEvents();

                EditorControl.TestHook_ResetUiaEventCounts(ctrl);
                ctrl.Focus();
                Application.DoEvents();

                var (_, selChanged, focusChanged) = EditorControl.TestHook_UiaEventCounts(ctrl);
                Assert.True(focusChanged >= 1, "AutomationFocusChangedEvent が発火していない");
                Assert.True(selChanged >= 1, "OnGotFocus 明示発火 TextSelectionChangedEvent が発火していない");
                form.Close();
            }
            finally { EditorControl.TestHook_ForceUiaListen = false; }
        });
    }
}
