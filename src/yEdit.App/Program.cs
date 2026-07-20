using System.Diagnostics;
using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Shift_JIS/EUC-JP を使うため CodePagesEncodingProvider を登録（Core も内部登録するが明示）。
        EncodingCatalog.EnsureRegistered();
        // MD-L-2: 依存 (Markdig) のバージョンを Trace ログへ (post-mortem/依存更新時の追跡用)。
        // 既定リスナ未装着の環境では実質 no-op。ApplicationConfiguration.Initialize() より前で
        // 早い段階に出しておく (WinForms init 失敗時にも記録が残る)。
        var markdigVersion = typeof(Markdig.Markdown).Assembly.GetName().Version;
        Trace.TraceInformation($"yEdit deps: Markdig={markdigVersion}");
        // 設定は起動で1回だけ読む（起動時確定方針）。
        var settings = SettingsStore.Load(SettingsStore.DefaultPath);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(settings));
    }
}
