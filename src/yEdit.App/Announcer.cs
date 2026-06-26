namespace yEdit.App;

/// <summary>
/// SR への能動的な通知。底部 Label を視覚表示しつつ SR に読ませる（共通実装は SrNotify）。
/// 照会ホットキー・モード切替・grep ジャンプ等から呼ぶ。
/// </summary>
public sealed class Announcer
{
    private readonly Label _label;

    public Announcer(Label label) => _label = label;

    public void Say(string message) => SrNotify.Raise(_label, message);
}
