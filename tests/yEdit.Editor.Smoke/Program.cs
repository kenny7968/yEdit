using yEdit.Core.Text;
using yEdit.Editor.Smoke;

// P2 Task 14 の smoke 起動器。--bench を渡すと GDI 経由の実描画ベンチ(offscreen Form)
// を走らせて EXIT 0/1 で判定する。それ以外の起動では EditorControl を Fill させた
// 単体 Form を出し、ファイルを読み込ませて eye check する用途。
//
// エンコーディングプロバイダ登録: MainForm の Shift_JIS / EUC-JP メニューが使えるように
// 起動最初に一度だけ呼ぶ(Core が内部で二重チェックしているが明示・yEdit.App と同じ扱い)。
EncodingCatalog.EnsureRegistered();

if (args.Length > 0 && args[0] == "--bench")
{
    return GdiBench.Run(args);
}

ApplicationConfiguration.Initialize();
Application.Run(new MainForm(args.FirstOrDefault()));
return 0;
