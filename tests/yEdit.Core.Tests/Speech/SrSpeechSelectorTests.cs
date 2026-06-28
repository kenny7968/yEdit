using yEdit.Core.Speech;

namespace yEdit.Core.Tests.Speech;

public class SrSpeechSelectorTests
{
    [Fact]
    public void PcTalker_running_and_no_nvda_selects_pctalker()
        => Assert.Equal(SpeechMode.PcTalker, SrSpeechSelector.Select(nvdaRunning: false, pcTalkerRunning: true));

    [Fact]
    public void Nvda_running_selects_uia_even_if_pctalker_running()
        => Assert.Equal(SpeechMode.Uia, SrSpeechSelector.Select(nvdaRunning: true, pcTalkerRunning: true));

    [Fact]
    public void Nvda_only_selects_uia()
        => Assert.Equal(SpeechMode.Uia, SrSpeechSelector.Select(nvdaRunning: true, pcTalkerRunning: false));

    [Fact]
    public void Neither_running_defaults_to_uia()
        => Assert.Equal(SpeechMode.Uia, SrSpeechSelector.Select(nvdaRunning: false, pcTalkerRunning: false));
}
