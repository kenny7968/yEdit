using System.Runtime.InteropServices;
using System.Windows.Automation.Provider;
using yEdit.Editor;

namespace yEdit.Editor.Smoke;

/// <summary>
/// P5 Task 13: smoke 用の最小 Announcer(実 App 層 Announcer のサブセット)。
/// P0 で確定した PC-Talker Ctrl+←→ 単語ナビの 1 文字読み補完を smoke 上で再現。
/// P6 で本物 Announcer に差し替えられる=このクラスは Task 14 実機チェックリストが通ったら
/// 撤退候補。
/// </summary>
/// <remarks>
/// 発声経路: PC-Talker 直叩き(<c>PCTKPReadW</c>, mode=1=割り込み読み)。
/// PC-Talker 未インストール環境では <see cref="DllNotFoundException"/> を握り潰して no-op。
/// NVDA/ナレーター向けの UIA 通知は WPF Automation API に該当 API が無いため smoke では省略
/// (v2 プロバイダの <see cref="System.Windows.Automation.Provider.AutomationInteropProvider.RaiseAutomationEvent"/>
/// の TextChanged/TextSelectionChanged で NVDA は追従できる)。
/// </remarks>
internal sealed class UiaSmokeAnnouncer
{
    [DllImport("PCTKUSR.DLL", CharSet = CharSet.Unicode)]
    private static extern int PCTKPReadW(string text, int mode);

    private readonly EditorControl _ctrl;
    private readonly IRawElementProviderSimple? _provider;

    public UiaSmokeAnnouncer(EditorControl ctrl, IRawElementProviderSimple? provider)
    {
        _ctrl = ctrl;
        _provider = provider;
        _ctrl.CaretEnteredEmptyLine += (_, _) => Announce("空行");
        _ctrl.WordNavigated += (_, e) =>
        {
            // WordStart..WordEnd を GetTextRange で切り出して発声
            var host = (yEdit.Accessibility.IUiaTextHost)_ctrl;
            string span = host.GetTextRange(e.WordStart, e.WordEnd - e.WordStart);
            if (!string.IsNullOrWhiteSpace(span)) Announce(span.Trim());
        };
    }

    private void Announce(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // PC-Talker 直叩き(mode=1=割り込み読み)。未インストール環境は無害に no-op。
        try { PCTKPReadW(text, 1); }
        catch (DllNotFoundException) { /* PC-Talker 未インストール環境で握り潰し */ }
        catch (EntryPointNotFoundException) { /* 別バージョンで名前が違う場合 */ }
    }
}
