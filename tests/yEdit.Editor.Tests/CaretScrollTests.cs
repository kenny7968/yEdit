using System.Linq;

namespace yEdit.Editor.Tests;

/// <summary>
/// P3 Task 7: キャレット追従スクロール(BringCaretIntoView + EnsureVisibleCharRange)の契約テスト。
/// - 垂直: caret 論理行が [TopLine, TopLine + visibleRows) 外なら TopLine 追従
/// - 水平: 折り返し OFF + HScroll 表示中で caret X が可視外なら ScrollX 追従
/// - EnsureVisibleCharRange: 範囲末尾を可視化しつつ caret/anchor は保存/復元
/// - SetSource 前は throw せず no-op
///
/// テスト値は MS ゴシック 12pt の実 LineHeight に依存するため相対比較で書く
/// (「TopLine が上端/下端に張り付く」「no-op」「呼び出し前と後の相対関係」)。
/// </summary>
public class CaretScrollTests
{
    // ハンドル生成のため親フォームに載せてサイズを付ける。
    // GdiCharMetrics は SystemFonts 依存だが、LineHeightPx は行数計算のみに使うため
    // font 依存性は小さい(MS ゴシック 12pt で ~16-20px)。
    // 可視行数は明示的に高さから計算するため、テスト値は「TopLine 変化」の相対比較にする。
    private static (Form f, EditorControl c) MakeControl(string text, int width, int height)
    {
        var f = new Form { Size = new System.Drawing.Size(width, height) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    [Fact]
    public void BringCaretIntoView_ScrollsDown_WhenCaretBelowVisible() => Sta.Run(() =>
    {
        // 10 行の文書、可視領域が 3 行程度になるように高さを絞る
        var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
        var (f, c) = MakeControl(text, width: 400, height: 60);   // 60px なら MS ゴシック 12pt で ~3行
        using (f) using (c)
        {
            c.TopLine = 0;
            int lineHeight = c.LineHeightPx;
            int visibleRows = Math.Max(1, c.ClientSize.Height / lineHeight);

            // 末尾行(index 9)にキャレットを置いて BringCaretIntoView 呼び出し
            int lineStart = text.LastIndexOf('\n') + 1;
            c.SetCaretCharOffset(lineStart);
            c.BringCaretIntoView();

            // TopLine は少なくとも「末尾行が可視領域末尾に入る」位置に調整される
            Assert.True(c.TopLine >= 9 - visibleRows + 1, $"expected TopLine >= {9 - visibleRows + 1}, got {c.TopLine}");
            Assert.True(c.TopLine <= 9, $"expected TopLine <= 9, got {c.TopLine}");
        }
    });

    [Fact]
    public void BringCaretIntoView_ScrollsUp_WhenCaretAboveVisible() => Sta.Run(() =>
    {
        var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
        var (f, c) = MakeControl(text, width: 400, height: 60);
        using (f) using (c)
        {
            c.TopLine = 5;    // 可視領域を下方向にずらす

            // 先頭行にキャレットを置いて呼び出し
            c.SetCaretCharOffset(0);
            c.BringCaretIntoView();

            Assert.Equal(0, c.TopLine);   // 上端に張り付く
        }
    });

    [Fact]
    public void BringCaretIntoView_NoOp_WhenCaretAlreadyVisible() => Sta.Run(() =>
    {
        var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
        var (f, c) = MakeControl(text, width: 400, height: 200);   // 全行入る想定
        using (f) using (c)
        {
            c.TopLine = 0;
            int initial = c.TopLine;
            c.SetCaretCharOffset(text.IndexOf("line2"));   // 3行目
            c.BringCaretIntoView();
            Assert.Equal(initial, c.TopLine);
        }
    });

    [Fact]
    public void BringCaretIntoView_NoOp_BeforeSetSource() => Sta.Run(() =>
    {
        // SetSource 前の呼び出しは throw せず何もしない
        using var f = new Form();
        using var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.BringCaretIntoView();   // 例外が投げられなければ OK
        Assert.Equal(0, c.TopLine);
    });

    [Fact]
    public void EnsureVisibleCharRange_PreservesCaretAndAnchor() => Sta.Run(() =>
    {
        var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
        var (f, c) = MakeControl(text, width: 400, height: 60);
        using (f) using (c)
        {
            c.TopLine = 0;
            c.SetSelectionCharRange(2, 5);
            var (start0, end0) = c.GetSelectionCharRange();

            int lineStart = text.LastIndexOf('\n') + 1;
            c.EnsureVisibleCharRange(lineStart, 4);   // 末尾行を可視化

            // 選択とキャレットは変わらない
            var (start1, end1) = c.GetSelectionCharRange();
            Assert.Equal(start0, start1);
            Assert.Equal(end0, end1);
            // でも TopLine は末尾方向に動いている
            Assert.True(c.TopLine > 0);
        }
    });

    [Fact]
    public void EnsureVisibleCharRange_NoOp_BeforeSetSource() => Sta.Run(() =>
    {
        using var f = new Form();
        using var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.EnsureVisibleCharRange(0, 10);   // 例外なし
    });

    [Fact]
    public void KeyDown_Down_ScrollsWhenReachingBottom() => Sta.Run(() =>
    {
        // Task 6 の OnKeyDown で BringCaretIntoView が呼ばれるので、
        // 連続 Down で TopLine が追従することを検証
        var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"l{i}"));
        var (f, c) = MakeControl(text, width: 200, height: 60);
        using (f) using (c)
        {
            c.TopLine = 0;
            c.SetCaretCharOffset(0);

            // Down を 9 回押してキャレットを末尾行へ
            var mi = typeof(EditorControl).GetMethod("OnKeyDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            for (int i = 0; i < 9; i++)
                mi!.Invoke(c, new object[] { new KeyEventArgs(Keys.Down) });

            // TopLine は 0 より進んでいる(末尾行が可視領域に入る位置まで)
            Assert.True(c.TopLine > 0, $"expected TopLine to advance, still 0");
        }
    });
}
