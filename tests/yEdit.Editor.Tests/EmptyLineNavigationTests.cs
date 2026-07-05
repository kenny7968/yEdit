using System.Reflection;

namespace yEdit.Editor.Tests;

/// <summary>
/// P3 Task 13: 空行遷移検知(CaretEnteredEmptyLine)と RaiseUiaSelectionEvents プロパティ受け口の契約テスト。
/// - CaretEnteredEmptyLine: 純キャレット移動で行が変わり、着地行が空行(len=0)のとき発火。
///   編集経路(挿入/削除/Enter/Tab/Undo/Redo/Cut/Paste)からは発火しない(SR は別経路で通知)。
///   継承 SR 対策§2-5-3(HANDOFF §4.1)受け口=App 層 P6 が購読して能動発声「空行」する。
/// - RaiseUiaSelectionEvents: P3 では読み書きできるだけの受け口(既定 true・挙動は P5 で本実装)。
/// </summary>
public class EmptyLineNavigationTests
{
    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new Form();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    private static void SendKey(EditorControl c, Keys keyData)
    {
        var mi = typeof(EditorControl).GetMethod("OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic);
        mi!.Invoke(c, new object[] { new KeyEventArgs(keyData) });
    }

    private static void SendKeyPress(EditorControl c, char ch)
    {
        var mi = typeof(EditorControl).GetMethod("OnKeyPress",
            BindingFlags.Instance | BindingFlags.NonPublic);
        mi!.Invoke(c, new object[] { new KeyPressEventArgs(ch) });
    }

    // ===== 基本発火 =====

    [Fact]
    public void CaretEnteredEmptyLine_FiresOnEmptyRow_ByDown() => Sta.Run(() =>
    {
        // "abc\n\nxyz" の行1(空行)にキー Down で移動 → 発火
        var (f, c) = MakeControl("abc\n\nxyz");
        using (f) using (c)
        {
            c.SetCaretCharOffset(0);   // 行0
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;
            SendKey(c, Keys.Down);   // 行0→行1(空行)
            Assert.Equal(1, fired);
        }
    });

    [Fact]
    public void CaretEnteredEmptyLine_DoesNotFire_WhenLeavingEmptyLineToNonEmptyLine() => Sta.Run(() =>
    {
        // 空行 line1 から Left で行を跨いで非空行(line0)に着地=着地先が非空なので発火しない。
        // 追加ガード: I-1 対応後は SetCaretCharOffset(4) で _lastCaretLine が同期される
        // (line1)ため、Left で line0 に移った瞬間の RaiseCaretEnteredEmptyLineIfNeeded は
        // 「_lastCaretLine=1 != 現在 line=0 かつ line0 は非空」で発火しない二重防御になる。
        var (f, c) = MakeControl("abc\n\nxyz");
        using (f) using (c)
        {
            c.SetCaretCharOffset(4);   // 行1(空行)
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;
            SendKey(c, Keys.Left);   // 行0 の末尾(3)へ = 非空行に着地=発火なし
            Assert.Equal(0, fired);
        }
    });

    [Fact]
    public void CaretEnteredEmptyLine_FiresOnEmptyRow_ByLeftArrow() => Sta.Run(() =>
    {
        // 行0 の 'y' から Left で行を跨いで空行に着地
        var (f, c) = MakeControl("abc\n\nxyz");
        using (f) using (c)
        {
            c.SetCaretCharOffset(5);   // 行2 'x' の頭
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;
            SendKey(c, Keys.Left);   // 行1(空行)へ
            Assert.Equal(1, fired);
        }
    });

    // ===== 編集経路では発火しない =====

    [Fact]
    public void CaretEnteredEmptyLine_DoesNotFire_OnCharInsertion() => Sta.Run(() =>
    {
        // 挿入で空行の右に移動しても発火しない(=編集経路は SR 別経路で通知される)
        var (f, c) = MakeControl("abc\n\nxyz");
        using (f) using (c)
        {
            c.SetCaretCharOffset(0);
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;
            SendKeyPress(c, 'X');   // "Xabc\n\nxyz"(挿入経路)
            Assert.Equal(0, fired);
        }
    });

    [Fact]
    public void CaretEnteredEmptyLine_DoesNotFire_OnEnter() => Sta.Run(() =>
    {
        // Enter で新しい空行を作って着地しても発火しない(編集経路)
        var (f, c) = MakeControl("abc");
        using (f) using (c)
        {
            c.SetCaretCharOffset(3);
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;
            SendKey(c, Keys.Enter);   // "abc\r\n" caret=5
            Assert.Equal(0, fired);
        }
    });

    [Fact]
    public void CaretEnteredEmptyLine_DoesNotFire_OnUndo() => Sta.Run(() =>
    {
        // Undo で行が空行に戻っても発火しない(編集経路)
        var (f, c) = MakeControl("abc");
        using (f) using (c)
        {
            c.SetCaretCharOffset(3);
            SendKey(c, Keys.Enter);   // "abc\r\n" caret=5(空の最終行)
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;
            c.Undo();   // 戻る=行0 に戻る
            Assert.Equal(0, fired);
        }
    });

    // ===== 空でない行への移動では発火しない =====

    [Fact]
    public void CaretEnteredEmptyLine_DoesNotFire_OnNonEmptyRow() => Sta.Run(() =>
    {
        var (f, c) = MakeControl("abc\ndef\nxyz");
        using (f) using (c)
        {
            c.SetCaretCharOffset(0);
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;
            SendKey(c, Keys.Down);   // 行0→行1("def")=非空行
            Assert.Equal(0, fired);
        }
    });

    // ===== 連続空行 =====

    [Fact]
    public void CaretEnteredEmptyLine_FiresMultipleTimes_AcrossMultipleEmptyRows() => Sta.Run(() =>
    {
        // 連続空行を Down で通ると行ごとに発火する
        var (f, c) = MakeControl("abc\n\n\nxyz");
        using (f) using (c)
        {
            c.SetCaretCharOffset(0);
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;
            SendKey(c, Keys.Down);   // 行0→行1(空)
            Assert.Equal(1, fired);
            SendKey(c, Keys.Down);   // 行1→行2(空)
            Assert.Equal(2, fired);
            SendKey(c, Keys.Down);   // 行2→行3("xyz")=非空
            Assert.Equal(2, fired);
        }
    });

    // ===== I-1 回帰保護: 公開キャレット setter での _lastCaretLine 同期 =====

    [Fact]
    public void CaretEnteredEmptyLine_DoesNotFire_AfterProgrammaticJumpToEmptyLine_ThenSameLineKeyNoOp() => Sta.Run(() =>
    {
        // I-1 回帰保護: App 層が programmatic に空行へジャンプ → ユーザーが同位置に留まるキー押下
        // → _lastCaretLine 同期のおかげで spurious fire しない。
        var (f, c) = MakeControl("abc\n\n");   // line0="abc" (0-3), line1=空 (4-4), line2=空 (5-5)
        using (f) using (c)
        {
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;

            // programmatic ジャンプ(検索ジャンプ・GoTo・CsvFocusSink 相当):line2 空行へ。
            // §0-7 により programmatic 移動自体は CaretEnteredEmptyLine を発火しない。
            c.SetCaretCharOffset(5);
            Assert.Equal(0, fired);

            // 同位置に留まる Home 押下 → MoveHomeSmart(snap, 5)=5(空行なので同位置)
            // → OnKeyDown 末尾の RaiseCaretEnteredEmptyLineIfNeeded は
            //   「line=2 == _lastCaretLine=2(setter で同期済み)」で早期 return=発火なし。
            SendKey(c, Keys.Home);
            Assert.Equal(0, fired);
        }
    });

    [Fact]
    public void CaretEnteredEmptyLine_FiresOnLeavingProgrammaticJumpAndReturningToEmpty() => Sta.Run(() =>
    {
        // I-1 対応後: programmatic ジャンプで line1(空行)へ → Left で line0 に離脱 → 再度 Right
        // で line1 に戻る。戻ったときは正しく発火する(=stale で発火抑制されない)。
        var (f, c) = MakeControl("abc\n\nxyz");
        using (f) using (c)
        {
            int fired = 0;
            c.CaretEnteredEmptyLine += (_, _) => fired++;

            c.SetCaretCharOffset(4);   // line1 空行(programmatic・§0-7 で発火しない)
            Assert.Equal(0, fired);

            SendKey(c, Keys.Left);   // line0 末尾へ(非空行)
            Assert.Equal(0, fired);

            SendKey(c, Keys.Right);   // line1 空行へ復帰(発火)
            Assert.Equal(1, fired);
        }
    });

    // ===== RaiseUiaSelectionEvents プロパティ受け口 =====

    [Fact]
    public void RaiseUiaSelectionEvents_DefaultIsTrue() => Sta.Run(() =>
    {
        var (f, c) = MakeControl("abc");
        using (f) using (c) { Assert.True(c.RaiseUiaSelectionEvents); }
    });

    [Fact]
    public void RaiseUiaSelectionEvents_CanBeSet() => Sta.Run(() =>
    {
        var (f, c) = MakeControl("abc");
        using (f) using (c)
        {
            c.RaiseUiaSelectionEvents = false;
            Assert.False(c.RaiseUiaSelectionEvents);
            c.RaiseUiaSelectionEvents = true;
            Assert.True(c.RaiseUiaSelectionEvents);
        }
    });
}
