using yEdit.Core.Settings;

namespace yEdit.App.Settings;

/// <summary>
/// 設定ダイアログの 1 タブぶんを担う契約。
/// タブ追加は「実装クラス 1 個 ＋ SettingsDialog._tabs に 1 行」で完結する。
/// </summary>
public interface ISettingsTab
{
    /// <summary>タブヘッダに表示する日本語ラベル（例: "基本"）。</summary>
    string Title { get; }

    /// <summary>タブページの本体コントロールを構築して返す。
    /// 呼び出しは一度だけ。返した Control は TabPage.Controls に Dock=Fill で追加される。</summary>
    Control BuildPage();

    /// <summary>ダイアログ表示時、baseline から自タブが担当する項目を読み込む。BuildPage の後に呼ばれる。</summary>
    void LoadFrom(AppSettings s);

    /// <summary>OK 押下時、自タブが担当する項目を r に書き戻す。</summary>
    void SaveTo(AppSettings r);
}
