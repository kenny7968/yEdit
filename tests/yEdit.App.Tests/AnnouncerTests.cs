using yEdit.App.Speech;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 2(縮小版): 残存 Speech サブシステムの契約テスト。
/// AnnouncerBase の通知契約「視覚表示(label.Text)は無条件・発声(Speak)は非空のときだけ」と、
/// UiaAnnouncer の安全性(UIA 通知非対応環境でも握りつぶして視覚のみ)を特徴付ける。
/// Core が検証済みの文言生成は対象外(責務=Speech 層の契約)。
/// </summary>
public class AnnouncerTests
{
    /// <summary>Speak 呼び出しを記録する AnnouncerBase 派生(基底の契約検証用)。</summary>
    private sealed class RecordingAnnouncer : AnnouncerBase
    {
        public List<string> Spoken { get; } = new();
        public RecordingAnnouncer(Label label) : base(label) { }
        protected override void Speak(string message) => Spoken.Add(message);
    }

    // ===== AnnouncerBase の通知契約 =====

    [Fact]
    public void Say_NonEmpty_UpdatesLabel_AndSpeaksOnce() => Sta.Run(() =>
    {
        using var label = new Label();
        var announcer = new RecordingAnnouncer(label);
        announcer.Say("3 件中 1 件目");
        Assert.Equal("3 件中 1 件目", label.Text);          // 視覚は無条件(晴眼/弱視も第一級)
        Assert.Equal(new[] { "3 件中 1 件目" }, announcer.Spoken);
    });

    [Fact]
    public void Say_Empty_ClearsLabel_WithoutSpeaking() => Sta.Run(() =>
    {
        using var label = new Label { Text = "前回の通知" };
        var announcer = new RecordingAnnouncer(label);
        announcer.Say("");
        Assert.Equal("", label.Text);                        // 空=視覚クリアのみ
        Assert.Empty(announcer.Spoken);                      // 発声なし
    });

    [Fact]
    public void Say_Null_ClearsLabel_WithoutSpeaking() => Sta.Run(() =>
    {
        using var label = new Label { Text = "前回の通知" };
        var announcer = new RecordingAnnouncer(label);
        announcer.Say(null!);                                // 防御(message ?? "")の特徴付け
        Assert.Equal("", label.Text);
        Assert.Empty(announcer.Spoken);
    });

    [Fact]
    public void Say_WhitespaceOnly_UpdatesLabel_AndSpeaks() => Sta.Run(() =>
    {
        using var label = new Label { Text = "前回の通知" };
        var announcer = new RecordingAnnouncer(label);
        announcer.Say(" ");
        // ガードは IsNullOrEmpty であって IsNullOrWhiteSpace ではない: 空白のみは表示・発声される。
        // この区別の固定が Stage 2 レビュー申し送り(空白 1 文字を「クリア扱い」に変えると
        // SR の読み上げ挙動が変わるため、変えるなら意図的に)。
        Assert.Equal(" ", label.Text);
        Assert.Equal(new[] { " " }, announcer.Spoken);
    });

    // ===== UiaAnnouncer の安全性 =====

    [Fact]
    public void UiaAnnouncer_Say_SetsLabelText_AndDoesNotThrow_WithoutUiaSupport() => Sta.Run(() =>
    {
        using var label = new Label();                       // ハンドル未生成=UIA 通知は失敗し得る環境
        var announcer = new UiaAnnouncer(label);
        announcer.Say("検索結果 3 件");                      // 握りつぶし契約=例外を漏らさない
        Assert.Equal("検索結果 3 件", label.Text);
    });

    [Fact]
    public void UiaAnnouncer_Say_Empty_ClearsLabel() => Sta.Run(() =>
    {
        using var label = new Label { Text = "前回の通知" };
        var announcer = new UiaAnnouncer(label);
        announcer.Say("");
        Assert.Equal("", label.Text);
    });
}
