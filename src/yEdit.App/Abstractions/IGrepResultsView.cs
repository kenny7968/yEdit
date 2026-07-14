using yEdit.Core.Search;

namespace yEdit.App;

/// <summary>
/// GrepResultsWindow 生成時に渡す Controller 側コールバック束(Phase 2 Stage 7・上位文書 §2.2)。
/// 結果一覧のアクティベート(Enter/ダブルクリック)からジャンプ動作(<see cref="GrepController"/> ctor 引数の
/// jumpTo)への 1 経路を delegate 化する。GrepCallbacks と対称。
/// </summary>
public sealed record GrepResultsCallbacks(Action<GrepHit> OnActivate);

/// <summary>
/// GrepResultsWindow の Controller 向け表面(Phase 2 設計書 §2.2)。
/// Populate は結果流し込み・ShowResults はモードレス表示。IsDisposed は再生成判定
/// (owner クローズ等での破棄検出)を従来コードのまま保存するために載せる。
/// </summary>
public interface IGrepResultsView
{
    bool IsDisposed { get; }
    void Populate(string pattern, string folder, GrepOutcome outcome);
    void ShowResults(IWin32Window owner);
}
