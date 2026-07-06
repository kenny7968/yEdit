using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;

namespace yEdit.Accessibility;

/// <summary>
/// 自作テキストコントロールのルート UIA プロバイダ。
/// WM_GETOBJECT から返し、TextPattern を公開する（fragment root として hwnd に同居）。
/// </summary>
public sealed class TextControlProvider :
    IRawElementProviderSimple,
    IRawElementProviderFragment,
    IRawElementProviderFragmentRoot
{
    private readonly IUiaTextHostLegacy _host;
    private readonly TextProviderImpl _textProvider;

    public TextControlProvider(IUiaTextHostLegacy host)
    {
        _host = host;
        _textProvider = new TextProviderImpl(host, this);
    }

    // ---------- IRawElementProviderSimple ----------

    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;

    public object GetPatternProvider(int patternId)
    {
        if (patternId == TextPatternIdentifiers.Pattern.Id) return _textProvider;
        return null;
    }

    public object GetPropertyValue(int propertyId)
    {
        if (propertyId == AutomationElementIdentifiers.ControlTypeProperty.Id)
            return _host.ControlTypeId;
        if (propertyId == AutomationElementIdentifiers.NameProperty.Id)
            return _host.Name;
        if (propertyId == AutomationElementIdentifiers.AutomationIdProperty.Id)
            return _host.AutomationId;
        if (propertyId == AutomationElementIdentifiers.IsContentElementProperty.Id)
            return true;
        if (propertyId == AutomationElementIdentifiers.IsControlElementProperty.Id)
            return true;
        if (propertyId == AutomationElementIdentifiers.IsEnabledProperty.Id)
            return true;
        if (propertyId == AutomationElementIdentifiers.IsKeyboardFocusableProperty.Id)
            return true;
        if (propertyId == AutomationElementIdentifiers.HasKeyboardFocusProperty.Id)
            return _host.HasFocus;
        return null;
    }

    public IRawElementProviderSimple HostRawElementProvider
        => AutomationInteropProvider.HostProviderFromHandle(_host.Handle);

    // ---------- IRawElementProviderFragment ----------

    public Rect BoundingRectangle => _host.BoundingRectangle;

    public IRawElementProviderFragmentRoot FragmentRoot => this;

    public IRawElementProviderSimple[] GetEmbeddedFragmentRoots() => null;

    public int[] GetRuntimeId() => new int[] { AutomationInteropProvider.AppendRuntimeId, 1 };

    public IRawElementProviderFragment Navigate(NavigateDirection direction) => null; // 子なし／親は host 経由

    public void SetFocus() => _host.SetFocus();

    // ---------- IRawElementProviderFragmentRoot ----------

    public IRawElementProviderFragment ElementProviderFromPoint(double x, double y) => this;

    public IRawElementProviderFragment GetFocus() => _host.HasFocus ? this : null;
}
