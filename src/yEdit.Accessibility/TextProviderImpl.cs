using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace yEdit.Accessibility;

/// <summary>UIA TextPattern 本体（ITextProvider）。範囲の生成と現在選択の提供を担う。</summary>
internal sealed class TextProviderImpl : ITextProvider
{
    public IUiaTextHostLegacy Host { get; }
    public IRawElementProviderSimple RootProvider { get; }

    public TextProviderImpl(IUiaTextHostLegacy host, IRawElementProviderSimple root)
    {
        Host = host;
        RootProvider = root;
    }

    public ITextRangeProvider[] GetSelection()
    {
        var (s, e) = Host.GetSelection();
        return new ITextRangeProvider[] { new TextRangeProvider(this, s, e) };
    }

    public ITextRangeProvider[] GetVisibleRanges()
        => new ITextRangeProvider[] { new TextRangeProvider(this, 0, Host.TextLength) };

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
        => new TextRangeProvider(this, 0, 0);

    public ITextRangeProvider RangeFromPoint(Point screenLocation)
        // 近似（先頭）。PC-Talker がこれを使うなら座標→オフセット変換が必要（スタブ）。
        => new TextRangeProvider(this, 0, 0);

    public ITextRangeProvider DocumentRange => new TextRangeProvider(this, 0, Host.TextLength);

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
}
