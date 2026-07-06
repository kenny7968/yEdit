namespace yEdit.Editor.Tests;

/// <summary>
/// P4 Task 4-8: EditorControl の IME メッセージ処理(WndProc override)の契約テスト。
/// - WM_IME_SETCONTEXT: lParam の ISC_SHOWUICOMPOSITIONWINDOW ビットを落として base に流す
///   (=既定 composition ウィンドウ描画を抑止し、Task 6 以降の自前描画に切り替える下地)。
/// - WM_IME_STARTCOMPOSITION: 選択削除 + _ime.Start をキャレット位置で初期化(Task 5)。
/// Task 6〜8 で WM_IME_COMPOSITION / WM_IME_ENDCOMPOSITION の case をここに追加していく。
/// </summary>
public class EditorControlImeTests
{
    // ClipboardTests / CaretScrollTests と同じ流儀で Form と EditorControl を
    // タプルで返し、テスト側で using (f) using (c) の二重 using で Dispose する。
    // Sta.Run 内で全てを書き下すことも可能だが、IME テストは Task 5〜8 で数が
    // 増えるためヘルパを切り出しておく(P4 Task 5)。
    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new Form { Visible = false };
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

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

    // WM_IME_STARTCOMPOSITION 受信時:
    // - 選択があれば削除して先頭位置にキャレットを寄せる(1 Splice=1 Undo)
    // - _ime.Start = 現在のキャレット位置 に初期化
    // - _ime.Text は空のまま(=IsComposing は false)
    [Fact]
    public void ImeStartComposition_ClearsSelectionAndInitializesImeStart() => Sta.Run(() =>
    {
        var (f, c) = MakeControl("abcdef");
        using (f) using (c)
        {
            c.SetSelectionCharRange(1, 4);   // 選択 "bcd"

            var m = new Message
            {
                HWnd = c.Handle,
                Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
            };
            c.__TestProcessMessage(ref m);

            Assert.Equal("aef", c.__TestSnapshotText());   // 選択削除
            Assert.Equal(1, c.CaretCharOffset);            // キャレット = 選択先頭
            Assert.Equal(1, c.__TestImeStart());
            Assert.False(c.__TestIsComposing());           // 未確定文字列はまだ空
        }
    });

    // 選択が無い状態で WM_IME_STARTCOMPOSITION を受けた場合、キャレット位置は
    // 変わらず、_ime.Start が現在のキャレット位置で初期化される。
    // (本文編集が発生しないため AfterEdit の副作用は走らない=Task 5 設計)
    [Fact]
    public void ImeStartComposition_NoSelection_KeepsCaretAndInitializesImeStart() => Sta.Run(() =>
    {
        var (f, c) = MakeControl("abcdef");
        using (f) using (c)
        {
            c.SetCaretCharOffset(3);

            var m = new Message { HWnd = c.Handle, Msg = NativeMethods.WM_IME_STARTCOMPOSITION };
            c.__TestProcessMessage(ref m);

            Assert.Equal("abcdef", c.__TestSnapshotText());   // 本文不変
            Assert.Equal(3, c.CaretCharOffset);
            Assert.Equal(3, c.__TestImeStart());
            Assert.False(c.__TestIsComposing());
        }
    });
}
