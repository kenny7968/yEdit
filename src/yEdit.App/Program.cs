using yEdit.App.Speech;
using yEdit.Core.Text;

namespace yEdit.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Shift_JIS/EUC-JP を使うため CodePagesEncodingProvider を登録（Core も内部登録するが明示）。
        EncodingCatalog.EnsureRegistered();
        // SR 環境（NVDA/PC-Talker）を起動時に1回だけ判定して確定する（起動時確定方針）。
        SrContext.Detect();
        ApplicationConfiguration.Initialize();
        // 「送る」・関連付けからの起動: 第1引数のファイルを起動時に開く。
        // 複数引数（「送る」の複数選択）の全タブ展開はやらない（第1引数のみ・申し送り済み）。
        Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
    }
}
