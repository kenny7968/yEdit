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
        // 設定は起動で1回だけ読む（起動時確定方針）。
        var settings = SettingsStore.Load(SettingsStore.DefaultPath);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(settings));
    }
}
