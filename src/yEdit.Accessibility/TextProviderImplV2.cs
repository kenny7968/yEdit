using System.Windows.Automation.Provider;

namespace yEdit.Accessibility;

/// <summary>v2 用 UIA TextProvider の仮スケルトン(Task 4 で本実装)。</summary>
internal sealed class TextProviderImplV2
{
    public IUiaTextHost Host { get; }
    public IRawElementProviderSimple RootProvider { get; }

    public TextProviderImplV2(IUiaTextHost host, IRawElementProviderSimple root)
    {
        Host = host;
        RootProvider = root;
    }
}
