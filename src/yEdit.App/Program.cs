using yEdit.App.Speech;
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
        // 設定を先に読み、「優先するスクリーンリーダー」を SR 判定へ渡す（起動時確定方針・読込は起動で1回だけ）。
        var settings = SettingsStore.Load(SettingsStore.DefaultPath);
        SrContext.Detect(preferNvda: settings.PreferredScreenReader != "pctalker");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(settings));
    }
}
