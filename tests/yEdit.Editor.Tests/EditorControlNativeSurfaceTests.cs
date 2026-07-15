using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlNativeSurfaceTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern System.IntPtr SendMessageGetText(System.IntPtr hWnd, uint msg, System.IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern System.IntPtr SendMessageInt(System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);

    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;

    [Fact]
    public void WM_GETTEXT_ReturnsEmpty()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("secret content"));
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                var sb = new StringBuilder(1024);
                SendMessageGetText(ctrl.Handle, WM_GETTEXT, new System.IntPtr(1024), sb);
                Assert.Equal("", sb.ToString());
            }
            finally { form.Close(); }
        });
    }

    [Fact]
    public void WM_GETTEXTLENGTH_ReturnsZero()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("some text"));
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                var r = SendMessageInt(ctrl.Handle, WM_GETTEXTLENGTH, System.IntPtr.Zero, System.IntPtr.Zero);
                Assert.Equal(System.IntPtr.Zero, r);
            }
            finally { form.Close(); }
        });
    }
}
