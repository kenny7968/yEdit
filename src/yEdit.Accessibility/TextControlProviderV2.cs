using System.Windows.Automation.Provider;

namespace yEdit.Accessibility;

/// <summary>v2 用 UIA ルートプロバイダの仮スケルトン(Task 4 で本実装)。</summary>
internal sealed class TextControlProviderV2 : IRawElementProviderSimple
{
    private readonly IUiaTextHost _host;
    public TextControlProviderV2(IUiaTextHost host) { _host = host; }
    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
    public object GetPatternProvider(int patternId) => null!;
    public object GetPropertyValue(int propertyId) => null!;
    public IRawElementProviderSimple HostRawElementProvider => null!;
}
