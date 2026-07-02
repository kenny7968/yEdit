namespace yEdit.App;

/// <summary>
/// CSVモード中にキーボードフォーカスを預かる 1×1px のフォーカスシンク。
/// NVDA はネイティブ Scintilla 統合により、OS イベント（フォーカス獲得・システムキャレット
/// 移動・選択変更）に反応してフォーカスのあるエディタの生バッファを読み上げる。これは
/// アプリ側の UIA イベント抑止では止められないため、CSVモード中はフォーカス自体を本
/// コントロールへ退避して全経路を遮断し、読み上げを Announcer に一本化する。
/// Dock=Fill のエディタより後に親へ追加されることで Z 順の背面に隠れ、視覚影響もない。
/// TabStop=false のため通常モードの Tab 順には乗らず、フォーカスはコードからのみ与える。
/// </summary>
public sealed class CsvFocusSink : Control
{
    public CsvFocusSink()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = false;
        ImeMode = ImeMode.Disable;          // CSVモードの素キー（C/R/G等）が IME に食われて VK_PROCESSKEY 化するのを防ぐ
        Size = new Size(1, 1);
        Location = new Point(0, 0);
        AccessibleName = "CSV表";           // 着地時に SR が読む名前（設計書の UX 決定事項）
        AccessibleRole = AccessibleRole.Pane;
    }
}
