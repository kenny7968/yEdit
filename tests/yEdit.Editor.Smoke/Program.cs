using yEdit.Core.Buffers;
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

// P4 Task 14: --ime サブコマンド。ATOK 実機検証(docs/plans/2026-07-06-p4-ime-checklist.md)
// 用に、未確定色/下線を目視しやすい長文サンプルをメモリ上で生成して開いた状態から起動する。
if (args.Length > 0 && args[0] == "--ime")
{
    ApplicationConfiguration.Initialize();
    var buf = TextBuffer.FromString(
        "IME 動作確認用サンプル(P4 Task 14)\n"
        + "文字入力→確定/変換/取消 の動作をこの EditorControl 上で試してください。\n\n"
        + string.Concat(Enumerable.Repeat("あいうえおかきくけこさしすせそたちつてと\n", 20)));
    Application.Run(new MainForm(buf, "(IME サンプル)"));
    return 0;
}

ApplicationConfiguration.Initialize();
Application.Run(new MainForm(args.FirstOrDefault()));
return 0;
