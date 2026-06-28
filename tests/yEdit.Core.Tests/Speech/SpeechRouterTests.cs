using yEdit.Core.Speech;

namespace yEdit.Core.Tests.Speech;

public class SpeechRouterTests
{
    private sealed class FakeChannel : ISpeechChannel
    {
        public bool Result;
        public int Calls;
        public string? Last;
        public string Name => "fake";
        public bool TrySpeak(string message) { Calls++; Last = message; return Result; }
    }

    [Fact]
    public void PcTalker_running_and_speaks_does_not_use_fallback()
    {
        var pct = new FakeChannel { Result = true };
        var uia = new FakeChannel { Result = true };
        var router = new SpeechRouter(pct, () => true);

        router.Speak("こんにちは", uia);

        Assert.Equal(1, pct.Calls);
        Assert.Equal("こんにちは", pct.Last);
        Assert.Equal(0, uia.Calls); // 先頭で成功したらフォールバックしない
    }

    [Fact]
    public void PcTalker_running_but_fails_falls_back_to_uia()
    {
        var pct = new FakeChannel { Result = false };
        var uia = new FakeChannel { Result = true };
        var router = new SpeechRouter(pct, () => true);

        router.Speak("x", uia);

        Assert.Equal(1, pct.Calls);
        Assert.Equal(1, uia.Calls); // PcTalkerが未発声ならUIAへ
    }

    [Fact]
    public void PcTalker_not_running_uses_uia_only()
    {
        var pct = new FakeChannel { Result = true };
        var uia = new FakeChannel { Result = true };
        var router = new SpeechRouter(pct, () => false);

        router.Speak("x", uia);

        Assert.Equal(0, pct.Calls); // 非稼働ならPcTalkerは呼ばない
        Assert.Equal(1, uia.Calls);
    }
}
