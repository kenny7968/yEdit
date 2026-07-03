using yEdit.App.Speech;
using yEdit.Core.Text;

namespace yEdit.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Shift_JIS/EUC-JP を使うため CodePagesEncodingProvider を登録（Core も内部登録するが明示）。
        EncodingCatalog.EnsureRegistered();
        // SR 環境（NVDA/PC-Talker）を起動時に1回だけ判定して確定する（起動時確定方針）。
        SrContext.Detect();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
