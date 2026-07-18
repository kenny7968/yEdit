using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;

namespace yEdit.Accessibility;

/// <summary>
/// 自作 EditorControl のルート UIA プロバイダ(v2)。
/// WM_GETOBJECT から返し、TextPattern を公開する(fragment root として hwnd に同居)。
/// </summary>
public sealed class TextControlProviderV2
    : IRawElementProviderSimple,
        IRawElementProviderFragment,
        IRawElementProviderFragmentRoot
{
    private readonly IUiaTextHost _host;
    private readonly TextProviderImplV2 _textProvider;

    public TextControlProviderV2(IUiaTextHost host)
    {
        _host = host;
        _textProvider = new TextProviderImplV2(host, this);
    }

    // ---------- IRawElementProviderSimple ----------

    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;

    public object GetPatternProvider(int patternId)
    {
        if (patternId == TextPatternIdentifiers.Pattern.Id)
            return _textProvider;
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

    public IRawElementProviderSimple HostRawElementProvider =>
        AutomationInteropProvider.HostProviderFromHandle(_host.Handle);

    // ---------- IRawElementProviderFragment ----------

    public Rect BoundingRectangle => _host.BoundingRectangle;

    public IRawElementProviderFragmentRoot FragmentRoot => this;

#pragma warning disable S1168 // reason: UIA provider API 慣用句(null = no embedded roots)。Array.Empty<> は UIA host の挙動差リスク
    public IRawElementProviderSimple[] GetEmbeddedFragmentRoots() => null;
#pragma warning restore S1168

    public int[] GetRuntimeId() => new int[] { AutomationInteropProvider.AppendRuntimeId, 1 };

    public IRawElementProviderFragment Navigate(NavigateDirection direction) => null; // 子なし・親は host 経由

    public void SetFocus() => _host.SetFocus();

    // ---------- IRawElementProviderFragmentRoot ----------

    public IRawElementProviderFragment ElementProviderFromPoint(double x, double y) => this;

    public IRawElementProviderFragment GetFocus() => _host.HasFocus ? this : null;
}
