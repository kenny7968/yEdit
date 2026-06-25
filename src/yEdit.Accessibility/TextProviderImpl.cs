using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace yEdit.Accessibility;

/// <summary>UIA TextPattern 本体（ITextProvider）。範囲の生成と現在選択の提供を担う。計装あり。</summary>
internal sealed class TextProviderImpl : ITextProvider
{
    public IUiaTextHost Host { get; }
    public IRawElementProviderSimple RootProvider { get; }

    public TextProviderImpl(IUiaTextHost host, IRawElementProviderSimple root)
    {
        Host = host;
        RootProvider = root;
    }

    private static int Tid => Environment.CurrentManagedThreadId;

    public ITextRangeProvider[] GetSelection()
    {
        var (s, e) = Host.GetSelection();
        UiaDiag.Log($"ITextProvider.GetSelection -> [{s},{e}] tid={Tid}");
        return new ITextRangeProvider[] { new TextRangeProvider(this, s, e) };
    }

    public ITextRangeProvider[] GetVisibleRanges()
    {
        UiaDiag.Log($"ITextProvider.GetVisibleRanges -> [0,{Host.TextLength}] tid={Tid}");
        return new ITextRangeProvider[] { new TextRangeProvider(this, 0, Host.TextLength) };
    }

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
    {
        UiaDiag.Log($"ITextProvider.RangeFromChild tid={Tid}");
        return new TextRangeProvider(this, 0, 0);
    }

    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        // 実験では近似（先頭）。PC-Talker がこれを使うなら座標→オフセット変換が必要。
        UiaDiag.Log($"ITextProvider.RangeFromPoint(({screenLocation.X:0},{screenLocation.Y:0})) -> [0,0] (STUB) tid={Tid}");
        return new TextRangeProvider(this, 0, 0);
    }

    public ITextRangeProvider DocumentRange
    {
        get
        {
            UiaDiag.Log($"ITextProvider.DocumentRange -> [0,{Host.TextLength}] tid={Tid}");
            return new TextRangeProvider(this, 0, Host.TextLength);
        }
    }

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
}
