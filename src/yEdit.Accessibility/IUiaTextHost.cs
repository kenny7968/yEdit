using System.Windows;

namespace yEdit.Accessibility;

/// <summary>
/// 自作テキストコントロールが実装し、UIA プロバイダ層が読み書きに使う抽象シーム。
/// 本番エディタでは、この実装を差し替えるだけでプロバイダ層をそのまま流用できる。
///
/// 注意: ここのメンバは UI スレッド外（UIA の RPC スレッド）から呼ばれ得る。
/// 実装側はスナップショット（不変文字列参照）／キャッシュ値で安全に応答すること。
/// </summary>
public interface IUiaTextHost
{
    /// <summary>本文全体のスナップショット。呼び出しごとに不変な文字列を返すこと。</summary>
    string GetText();

    /// <summary>本文長（UTF-16 コード単位）。</summary>
    int TextLength { get; }

    /// <summary>現在の選択（Start &lt;= End、End は排他）。キャレットのみのときは Start==End。</summary>
    (int Start, int End) GetSelection();

    /// <summary>選択／キャレットを設定する（実装は UI スレッドへマーシャリングして適用）。</summary>
    void SetSelection(int start, int end);

    /// <summary>コントロール全体のスクリーン座標矩形（UI スレッドで更新したキャッシュ値）。</summary>
    Rect BoundingRectangle { get; }

    /// <summary>
    /// [start,end) のスクリーン座標矩形を UIA 形式の double 配列 (x,y,w,h, x,y,w,h, ...) で返す。
    /// 無いときは長さ0配列。RPC スレッドから呼ばれるためキャッシュ値で安全に応答すること。
    /// </summary>
    double[] GetBoundingRectangles(int start, int end);

    /// <summary>ウィンドウハンドル（キャッシュ値）。</summary>
    nint Handle { get; }

    /// <summary>フォーカス状態（キャッシュ値）。</summary>
    bool HasFocus { get; }

    /// <summary>報告する ControlType の Id（本番は Document）。</summary>
    int ControlTypeId { get; }

    /// <summary>UIA の Name プロパティ（SR がフォーカス時などに読む名前）。</summary>
    string Name { get; }

    /// <summary>UIA の AutomationId プロパティ（自動化ツール向け識別子・読み上げ対象外）。</summary>
    string AutomationId { get; }

    /// <summary>コントロールにフォーカスを与える（UI スレッドへマーシャリング）。</summary>
    void SetFocus();
}
