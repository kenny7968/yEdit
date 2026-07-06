using yEdit.Core.Settings;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 設定（フォント・配色テーマ・表示折り返し・現在行強調・キャレット太さ等）をエディタへ適用する薄いブリッジ。
/// 実処理は <see cref="EditorControl.ApplyAppearance"/> に集約されているため、ここでは呼び出しの一本化のみ担う
/// （Task 15 で全 App 層から本クラスの呼び出しを撤去し、直接 <c>ed.ApplyAppearance(settings)</c> に置き換える予定）。
/// </summary>
public static class EditorAppearance
{
    public static void Apply(EditorControl ed, AppSettings settings)
        => ed.ApplyAppearance(settings);
}
