using Microsoft.Extensions.Time.Testing;
using yEdit.App.Speech;

namespace yEdit.App.Tests;

/// <summary>
/// UiaAnnouncer の通知契約「視覚表示(label.Text)は無条件・発声(Raise)は非空のときだけ」と、
/// UIA 通知非対応環境でも握りつぶして視覚のみに縮退する安全性を特徴付ける。
/// Core が検証済みの文言生成は対象外(責務=Speech 層の契約)。
/// UIA-M-4 (v0.11): 50 ms throttle + trailing timer + dedupe-off の契約もここで pin する。
/// </summary>
public class AnnouncerTests
{
    /// <summary>Raise 呼び出しを記録する UiaAnnouncer 派生(実 UIA 呼び出しを抑止して契約検証用)。
    /// UIA-M-4: trailing callback の UI-thread marshaling を同期に置換し、メッセージポンプなしで観測可能にする。</summary>
    private sealed class RecordingAnnouncer : UiaAnnouncer
    {
        public List<string> Spoken { get; } = new();

        public RecordingAnnouncer(Label label, TimeProvider? clock = null)
            : base(label, clock) { }

        protected override void Raise(string message) => Spoken.Add(message);

        // trailing callback の UI-thread marshal(_label.BeginInvoke)を同期呼び出しに置き換える。
        // BeginInvoke は Label にハンドルが無い/メッセージポンプが無い STA テストでは走らないため。
        protected override void InvokeOnUiThread(Action action) => action();
    }

    // ===== UiaAnnouncer の通知契約 =====

    [Fact]
    public void Say_NonEmpty_UpdatesLabel_AndSpeaksOnce() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var announcer = new RecordingAnnouncer(label);
            announcer.Say("3 件中 1 件目");
            Assert.Equal("3 件中 1 件目", label.Text); // 視覚は無条件(晴眼/弱視も第一級)
            Assert.Equal(new[] { "3 件中 1 件目" }, announcer.Spoken);
        });

    [Fact]
    public void Say_Empty_ClearsLabel_WithoutSpeaking() =>
        Sta.Run(() =>
        {
            using var label = new Label { Text = "前回の通知" };
            var announcer = new RecordingAnnouncer(label);
            announcer.Say("");
            Assert.Equal("", label.Text); // 空=視覚クリアのみ
            Assert.Empty(announcer.Spoken); // 発声なし
        });

    [Fact]
    public void Say_Null_ClearsLabel_WithoutSpeaking() =>
        Sta.Run(() =>
        {
            using var label = new Label { Text = "前回の通知" };
            var announcer = new RecordingAnnouncer(label);
            announcer.Say(null!); // 防御(message ?? "")の特徴付け
            Assert.Equal("", label.Text);
            Assert.Empty(announcer.Spoken);
        });

    [Fact]
    public void Say_WhitespaceOnly_UpdatesLabel_AndSpeaks() =>
        Sta.Run(() =>
        {
            using var label = new Label { Text = "前回の通知" };
            var announcer = new RecordingAnnouncer(label);
            announcer.Say(" ");
            // ガードは IsNullOrEmpty であって IsNullOrWhiteSpace ではない: 空白のみは表示・発声される。
            // 空白 1 文字を「クリア扱い」に変えると SR の読み上げ挙動が変わるため、変えるなら意図的に。
            // (AnnouncerBase 畳み込み後は UiaAnnouncer.Say の IsNullOrEmpty ガードを pin する契約)。
            Assert.Equal(" ", label.Text);
            Assert.Equal(new[] { " " }, announcer.Spoken);
        });

    // ===== UiaAnnouncer の安全性 =====

    [Fact]
    public void UiaAnnouncer_Say_SetsLabelText_AndDoesNotThrow_WithoutUiaSupport() =>
        Sta.Run(() =>
        {
            using var label = new Label(); // ハンドル未生成=UIA 通知は失敗し得る環境
            var announcer = new UiaAnnouncer(label);
            announcer.Say("検索結果 3 件"); // 握りつぶし契約=例外を漏らさない
            Assert.Equal("検索結果 3 件", label.Text);
        });

    [Fact]
    public void UiaAnnouncer_Say_Empty_ClearsLabel() =>
        Sta.Run(() =>
        {
            using var label = new Label { Text = "前回の通知" };
            var announcer = new UiaAnnouncer(label);
            announcer.Say("");
            Assert.Equal("", label.Text);
        });

    // ===== UIA-M-4: 50 ms throttle + trailing timer + dedupe-off の契約 =====

    [Fact]
    public void Say_ThrottlesRaise_When50msWithinPrevious() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("first"); // 初回は throttle 対象外(常に Raise される)
            clock.Advance(TimeSpan.FromMilliseconds(25)); // 50 ms 未満で 2 度目
            announcer.Say("second");
            // 直後の assertion 時点では trailing timer は未発火(進めていない)。Raise は 1 件のみ。
            Assert.Equal(new[] { "first" }, announcer.Spoken);
            // 視覚 (Label.Text) は throttle 対象外=常に最新を反映する(晴眼/弱視も第一級)。
            Assert.Equal("second", label.Text);
        });

    [Fact]
    public void Say_RaisesTrailingMessage_AfterThrottleWindow() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("first"); // 即 Raise
            clock.Advance(TimeSpan.FromMilliseconds(25));
            announcer.Say("throttled"); // 25 ms=window 内 → skip + pending buffer
            // trailing timer は Say 時点 (T+25 ms) から更に 50 ms 後 (T+75 ms) に発火する。
            // T+25 ms から更に 50 ms 進めると発火し、pending の "throttled" が Raise される。
            clock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new[] { "first", "throttled" }, announcer.Spoken);
        });

    [Fact]
    public void Say_RepeatsIdenticalMessage_WhenOutsideThrottleWindow() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("cell A1");
            clock.Advance(TimeSpan.FromMilliseconds(60)); // 窓外(50 ms 超)
            announcer.Say("cell A1"); // 同一メッセージだが dedupe しない契約 → Raise
            // SR で同一セルを意図的に読み直すのは基本操作。沈黙させてはならない。
            Assert.Equal(new[] { "cell A1", "cell A1" }, announcer.Spoken);
        });

    [Fact]
    public void Say_TrailingMessage_IsLastReceived_NotFirstThrottled() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("a"); // 即 Raise
            clock.Advance(TimeSpan.FromMilliseconds(10));
            announcer.Say("b"); // window 内 → skip + pending=b
            clock.Advance(TimeSpan.FromMilliseconds(10));
            announcer.Say("c"); // window 内 → skip + pending=c (b を上書き=last-write-wins)
            // trailing timer は "b" 時点 (T+10 ms) にスケジュールされ、T+60 ms に発火する。
            // T+20 ms から 50 ms 進めると T+70 ms=trailing 発火済み。pending の "c" が Raise される。
            clock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new[] { "a", "c" }, announcer.Spoken);
        });

    [Fact]
    public void Say_ThirdCall_AfterTrailingFires_RaisesImmediately() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            // T=0: Say → Raise. T+25 ms: Say → throttled/buffered. T+75 ms: trailing 発火 → Raise。
            announcer.Say("a");
            clock.Advance(TimeSpan.FromMilliseconds(25));
            announcer.Say("b");
            clock.Advance(TimeSpan.FromMilliseconds(50)); // T+75 ms=trailing 発火 (Raise "b")
            // trailing 発火直後 (T+75 ms) では _lastSaidUtc も T+75 ms 相当まで進んでいる。
            // 更に 50 ms 以上進めれば次の Say は throttle 対象外で即 Raise される契約を pin する。
            clock.Advance(TimeSpan.FromMilliseconds(60));
            announcer.Say("c");
            Assert.Equal(new[] { "a", "b", "c" }, announcer.Spoken);
        });
}
