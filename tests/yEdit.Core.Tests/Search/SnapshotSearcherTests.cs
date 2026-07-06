using yEdit.Core.Buffers;
using yEdit.Core.Search;
using Xunit;

namespace yEdit.Core.Tests.Search;

/// <summary>
/// P6 Task 11: <see cref="SnapshotSearcher"/> の閾値二層化テスト。
/// 閾値以下 (=<see cref="TextSearcher"/> 委譲)と閾値超(=窓照合/行単位 regex)を
/// 同一データで叩き、結果一致=既存挙動 100% 一致 と 大容量経路の正しさを検証する。
/// static <see cref="SnapshotSearcher.ThresholdChars"/> は各テストの finally で復元する。
/// </summary>
[Collection(nameof(SnapshotSearcherTests))] // static 状態を触るためテストクラス内で直列化
public class SnapshotSearcherTests
{
    private static SnapshotSearcher Make(
        string pattern, bool matchCase = false, bool wholeWord = false, bool useRegex = false)
        => new(new SearchOptions(pattern, matchCase, wholeWord, useRegex));

    private static TextSnapshot Snap(string text) => TextBuffer.FromString(text).Current;

    /// <summary>閾値と窓サイズを一時的に置き換え、Dispose で元に戻すヘルパ。</summary>
    private static IDisposable Large(int threshold = 4, int window = 8)
        => new ThresholdOverride(threshold, window);

    private sealed class ThresholdOverride : IDisposable
    {
        private readonly int _savedThreshold;
        private readonly int _savedWindow;
        public ThresholdOverride(int threshold, int window)
        {
            _savedThreshold = SnapshotSearcher.ThresholdChars;
            _savedWindow = SnapshotSearcher.WindowSize;
            SnapshotSearcher.ThresholdChars = threshold;
            SnapshotSearcher.WindowSize = window;
        }
        public void Dispose()
        {
            SnapshotSearcher.ThresholdChars = _savedThreshold;
            SnapshotSearcher.WindowSize = _savedWindow;
        }
    }

    // ==============================
    // 閾値以下 = TextSearcher と完全一致
    // ==============================

    [Fact]
    public void BelowThreshold_delegates_to_TextSearcher_for_all_apis()
    {
        var snap = Snap("ab ab ab");
        var s = Make("ab");
        Assert.Equal(3, s.Count(snap));
        Assert.Equal(new MatchSpan(0, 2), s.FindNext(snap, 0));
        Assert.Equal(new MatchSpan(3, 2), s.FindPrev(snap, 6));
        Assert.Equal((2, 3), s.Locate(snap, new MatchSpan(3, 2)));
        Assert.Equal("X", s.ReplacementAt(snap, new MatchSpan(0, 2), "X"));
        var (fragment, count) = s.ReplaceInRange(snap, 0, snap.CharLength, "X");
        Assert.Equal("X X X", fragment);
        Assert.Equal(3, count);
    }

    // ==============================
    // Count: 閾値超で挙動一致(リテラル/IgnoreCase/WholeWord/regex 行内)
    // ==============================

    [Fact]
    public void Count_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab ab ab ab ab");
        var s = Make("ab", matchCase: true);
        int expected = s.Count(snap);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(expected, s.Count(snap));
        }
    }

    [Fact]
    public void Count_LiteralIgnoreCaseAboveThreshold_matches_below()
    {
        var snap = Snap("ab AB Ab ab");
        var s = Make("ab"); // MatchCase=false (default)
        int expected = s.Count(snap);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(expected, s.Count(snap));
        }
    }

    [Fact]
    public void Count_WholeWordAboveThreshold_matches_below()
    {
        var snap = Snap("cat category cat-dog cat");
        var s = Make("cat", matchCase: true, wholeWord: true);
        int expected = s.Count(snap);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(expected, s.Count(snap));
        }
    }

    [Fact]
    public void Count_RegexInsideLineAboveThreshold_matches_below()
    {
        var snap = Snap("a1 a2 a3\nb1 b2\nc9 c8 c7");
        var s = Make(@"[abc]\d", useRegex: true, matchCase: true);
        int expected = s.Count(snap);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(expected, s.Count(snap));
        }
    }

    // ==============================
    // FindNext / FindPrev: 閾値超で挙動一致
    // ==============================

    [Fact]
    public void FindNext_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("hello world wonderful world");
        var s = Make("world", matchCase: true);
        var expected = s.FindNext(snap, 0);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(expected, s.FindNext(snap, 0));
            // from が既ヒット末尾を超えた場合も一致
            Assert.Equal(new MatchSpan(22, 5), s.FindNext(snap, 12));
        }
    }

    [Fact]
    public void FindPrev_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab XY ab XY ab");
        var s = Make("ab", matchCase: true);
        var expected = s.FindPrev(snap, 12);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(expected, s.FindPrev(snap, 12));
            Assert.Equal(new MatchSpan(0, 2), s.FindPrev(snap, 3));
            Assert.Null(s.FindPrev(snap, 0));
        }
    }

    [Fact]
    public void FindNext_RegexInsideLineAboveThreshold_matches_below()
    {
        var snap = Snap("line1: abc123\nline2: def456\nline3: xyz789");
        var s = Make(@"\d+", useRegex: true, matchCase: true);
        var expected0 = s.FindNext(snap, 0);
        var expected1 = s.FindNext(snap, 15);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(expected0, s.FindNext(snap, 0));
            Assert.Equal(expected1, s.FindNext(snap, 15));
        }
    }

    // ==============================
    // 改行跨ぎ regex = 閾値超で「見つからない」契約(壊れる契約§2-8)
    // ==============================

    [Fact]
    public void Regex_CrossLine_pattern_matches_below_but_never_matches_above()
    {
        var snap = Snap("foo\nbar\nfoo\nbar");
        var s = Make(@"foo\r?\nbar", useRegex: true, matchCase: true);

        // 閾値以下(TextSearcher)は改行またぎ regex を検出できる
        var below = s.FindNext(snap, 0);
        Assert.NotNull(below);

        // 閾値超(行単位 regex)は改行またぎヒットを絶対に返さない=壊れる契約
        using (Large(threshold: 4, window: 6))
        {
            Assert.Null(s.FindNext(snap, 0));
            Assert.Equal(0, s.Count(snap));
        }
    }

    // ==============================
    // Locate / ReplacementAt / ReplaceInRange
    // ==============================

    [Fact]
    public void Locate_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab XY ab XY ab");
        var s = Make("ab", matchCase: true);
        var span = new MatchSpan(6, 2);
        var expected = s.Locate(snap, span);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(expected, s.Locate(snap, span));
            // 該当なし= null
            Assert.Null(s.Locate(snap, new MatchSpan(1, 2)));
        }
    }

    [Fact]
    public void ReplacementAt_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab XY ab XY ab");
        var s = Make("ab", matchCase: true);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal("REPL", s.ReplacementAt(snap, new MatchSpan(6, 2), "REPL"));
            // ヒットでない場所は null
            Assert.Null(s.ReplacementAt(snap, new MatchSpan(3, 2), "X"));
            // リテラルは $ 展開なし
            Assert.Equal("X$1", s.ReplacementAt(snap, new MatchSpan(0, 2), "X$1"));
        }
    }

    [Fact]
    public void ReplacementAt_RegexAboveThreshold_expands_groups_per_line()
    {
        var snap = Snap("ab AB\nab cd");
        var s = Make("(a)(b)", useRegex: true, matchCase: true);
        var expected = s.ReplacementAt(snap, new MatchSpan(0, 2), "$2$1");
        Assert.Equal("ba", expected);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal("ba", s.ReplacementAt(snap, new MatchSpan(0, 2), "$2$1"));
        }
    }

    [Fact]
    public void ReplaceInRange_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab_ab_ab_ab");
        var s = Make("ab", matchCase: true);
        var (belowFrag, belowCount) = s.ReplaceInRange(snap, 0, snap.CharLength, "X");
        using (Large(threshold: 4, window: 6))
        {
            var (aboveFrag, aboveCount) = s.ReplaceInRange(snap, 0, snap.CharLength, "X");
            Assert.Equal(belowFrag, aboveFrag);
            Assert.Equal(belowCount, aboveCount);
        }
    }

    [Fact]
    public void ReplaceInRange_LiteralExcludesStraddlingBoundary_above_threshold()
    {
        // [0,5) には index 0 と index 3 の "ab" が完全包含 → 2 件置換
        var snap = Snap("ab_ab_ab");
        var s = Make("ab", matchCase: true);
        using (Large(threshold: 4, window: 6))
        {
            var (frag, count) = s.ReplaceInRange(snap, 0, 5, "X");
            Assert.Equal("X_X", frag);
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public void ReplaceInRange_RegexPerLine_above_threshold_preserves_line_breaks_and_non_target_lines()
    {
        var snap = Snap("aaa\nbbb\naaa");
        var s = Make("a", useRegex: true, matchCase: true);
        var (belowFrag, belowCount) = s.ReplaceInRange(snap, 0, snap.CharLength, "X");
        using (Large(threshold: 4, window: 6))
        {
            var (aboveFrag, aboveCount) = s.ReplaceInRange(snap, 0, snap.CharLength, "X");
            Assert.Equal(belowFrag, aboveFrag);
            Assert.Equal(belowCount, aboveCount);
        }
    }

    // ==============================
    // 端点 / 空 / IsValid
    // ==============================

    [Fact]
    public void EmptySnapshot_does_not_throw_and_yields_no_hits_above_threshold()
    {
        var snap = Snap("");
        var s = Make("ab");
        using (Large(threshold: 0, window: 4))
        {
            Assert.Equal(0, s.Count(snap));
            Assert.Null(s.FindNext(snap, 0));
            Assert.Null(s.FindPrev(snap, 0));
            var (frag, count) = s.ReplaceInRange(snap, 0, 0, "X");
            Assert.Equal("", frag);
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public void InvalidRegex_returns_null_and_zero_above_threshold()
    {
        var snap = Snap("hello world");
        var s = Make("(", useRegex: true);
        Assert.False(s.IsValid);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(0, s.Count(snap));
            Assert.Null(s.FindNext(snap, 0));
        }
    }

    [Fact]
    public void FindNext_ClampsNegativeFrom_above_threshold()
    {
        var snap = Snap("ab ab ab");
        var s = Make("ab", matchCase: true);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(new MatchSpan(0, 2), s.FindNext(snap, -5));
        }
    }

    [Fact]
    public void FindNext_PastEnd_returns_null_above_threshold()
    {
        var snap = Snap("ab");
        var s = Make("ab", matchCase: true);
        using (Large(threshold: 1, window: 4))
        {
            Assert.Null(s.FindNext(snap, snap.CharLength));
        }
    }

    // ==============================
    // 窓境界を跨ぐヒットの取りこぼしなし(overlap = plen - 1)
    // ==============================

    [Fact]
    public void FindNext_LiteralAcrossWindowBoundary_still_hits_above_threshold()
    {
        // window=6 でパターン長=4 のとき、overlap=3 で境界跨ぎを拾えるかの検証
        var snap = Snap("xxxx1234xxx");
        var s = Make("1234", matchCase: true);
        var expected = s.FindNext(snap, 0);
        using (Large(threshold: 4, window: 6))
        {
            Assert.Equal(expected, s.FindNext(snap, 0));
        }
    }
}
