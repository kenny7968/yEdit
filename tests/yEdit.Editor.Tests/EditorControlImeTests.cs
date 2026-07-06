namespace yEdit.Editor.Tests;

/// <summary>
/// P4 Task 4: EditorControl の IME メッセージ処理(WndProc override)の契約テスト。
/// - WM_IME_SETCONTEXT: lParam の ISC_SHOWUICOMPOSITIONWINDOW ビットを落として base に流す
///   (=既定 composition ウィンドウ描画を抑止し、Task 6 以降の自前描画に切り替える下地)。
/// Task 5〜8 で WM_IME_STARTCOMPOSITION / WM_IME_COMPOSITION / WM_IME_ENDCOMPOSITION の
/// case をここに追加していく。
/// </summary>
public class EditorControlImeTests
{
    // WndProc override が呼ばれ、WM_IME_SETCONTEXT の lParam から
    // ISC_SHOWUICOMPOSITIONWINDOW ビットが落ちて base に流されることを確認する。
    // WndProc は protected なため internal ヘルパー __TestProcessMessage(ref Message) を用意し
    // InternalsVisibleTo でテストから呼べるようにする(EditorControl.csproj 側で既に設定済み)。
    [Fact]
    public void WndProc_ImeSetContext_ClearsShowUICompositionWindowBit() => Sta.Run(() =>
    {
        using var c = new EditorControl();
        using var f = new Form { Visible = false };
        f.Controls.Add(c);
        var _ = f.Handle;

        var m = new Message
        {
            HWnd = c.Handle,
            Msg = NativeMethods.WM_IME_SETCONTEXT,
            WParam = 1,
            LParam = new nint(NativeMethods.ISC_SHOWUICOMPOSITIONWINDOW | 0x0F),
        };
        c.__TestProcessMessage(ref m);
        // 期待: base に渡す前に ISC_SHOWUICOMPOSITIONWINDOW ビットが落ちている(=lParam に反映済)
        Assert.Equal(0x0F, m.LParam.ToInt64() & 0xFFFFFFFFL);
    });
}
