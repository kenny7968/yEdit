using yEdit.Core.Csv;

namespace yEdit.App;

/// <summary>
/// CSVモードのコマンド定義（素キー・メニュー表示・実行）の単一の真実源。
/// MainForm は ProcessCmdKey の横取り表と CSV メニューの両方をここから生成する
/// （コマンド追加・キー変更時の片側更新漏れを防ぐ）。
/// </summary>
internal static class CsvCommands
{
    /// <summary>1 コマンド。MenuText が null の項目はキー専用（メニューに出さない別名・ガード）。
    /// Group はメニューの区分（値が変わる境界にセパレータを挿入する）。</summary>
    internal sealed record Command(Keys Key, string? MenuText, string KeyHint, int Group, Action<CsvController> Execute);

    /// <summary>定義順がそのままメニューの表示順。</summary>
    internal static readonly IReadOnlyList<Command> All = new Command[]
    {
        // 隣接セルへの移動
        new(Keys.Up,    "上のセル(&U)", "↑", 0, c => c.Move(Direction.Up)),
        new(Keys.Down,  "下のセル(&D)", "↓", 0, c => c.Move(Direction.Down)),
        new(Keys.Left,  "左のセル(&L)", "←", 0, c => c.Move(Direction.Left)),
        new(Keys.Right, "右のセル(&R)", "→", 0, c => c.Move(Direction.Right)),
        // 読み上げのみ（移動なし）。メニューには出さないが Tab/C/R のキー動作は維持する。
        new(Keys.Tab, null, "", 1, c => c.ReadCurrent()),
        new(Keys.C,   null, "", 1, c => c.ReadColumnTop()),
        new(Keys.R,   null, "", 1, c => c.ReadRowHead()),
        // 行/列の端へのジャンプ
        new(Keys.Home,     "行頭へ(&S)", "Home",     2, c => c.MoveRowStart()),
        new(Keys.End,      "行末へ(&N)", "End",      2, c => c.MoveRowEnd()),
        new(Keys.PageUp,   "列頭へ(&T)", "PageUp",   2, c => c.MoveColumnTop()),
        new(Keys.PageDown, "列末へ(&B)", "PageDown", 2, c => c.MoveColumnBottom()),
        new(Keys.Control | Keys.Home, "左上へ(&1)", "Ctrl+Home", 2, c => c.MoveTopLeft()),
        new(Keys.Control | Keys.End,  "右下へ(&9)", "Ctrl+End",  2, c => c.MoveBottomRight()),
        // セル指定・編集
        new(Keys.G,  "セルへ移動(&G)...", "G",  3, c => c.GoToCell()),
        new(Keys.F2, "セルを編集(&F)",    "F2", 3, c => c.BeginEdit()),
        // キー専用（メニュー非表示の別名・ガード）
        new(Keys.Shift | Keys.Tab, null, "", 4, c => c.ReadCurrent()), // Shift+Tab でフォーカスがシンクから逃げるのを防ぐ
        new(Keys.Control | Keys.G, null, "", 4, c => c.GoToCell()),    // 行ジャンプはCSVモード中セル指定に読み替え（素通りするとキャレット移動＋生読みを誘発）
    };

    /// <summary>ProcessCmdKey 用のキー→コマンド表。</summary>
    internal static readonly IReadOnlyDictionary<Keys, Command> ByKey = All.ToDictionary(c => c.Key);
}
