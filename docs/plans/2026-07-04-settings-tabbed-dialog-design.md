# 設定ダイアログのタブ化 — 設計書

- 日付: 2026-07-04
- ブランチ: `refactor/setting-dailog`
- 対象: `src/yEdit.App/SettingsDialog.cs`（既存の単一パネル・フラット構成）
- 呼び出し側: `src/yEdit.App/MainForm.cs:365` `OpenSettings()`（シグネチャ・使用方法とも不変）

## 目的

現行の設定ダイアログは 1 枚の `TableLayoutPanel` にフォント・配色テーマ・既定文字コード・既定改行・折り返し設定・禁則文字を並べた 200 行弱のフラット構成。項目の増加に伴い視覚的にも SR 上も見通しが悪くなるため、カテゴリ別のタブに再構成する。あわせて **今後タブが増えたときに拡張しやすい構造** を導入する。

## 到達点（DoD）

- 設定ダイアログが `TabControl` で 4 タブに分かれている: `基本` / `編集` / `禁則処理` / `表示`
- 既存の設定項目は下表のとおり各タブに配置される
- `SettingsDialog` は「タブの一覧」を保持してオーケストレーションするだけの薄い骨格になり、各タブは `ISettingsTab` 実装として独立クラス化されている
- 新タブ追加は「クラス 1 個追加 ＋ `_tabs` 配列に 1 行追加」で完結する
- `MainForm.OpenSettings()` の呼び出しコードは変更なし（`SettingsDialog` のコンストラクタ・`Result` プロパティのシグネチャ据え置き）
- 既存アクセスキー（`&F` `&E` `&L` `&W` `&K` `&1` `&2` `&3`）はタブ内で有効。タブヘッダには Alt アクセスキーを付けない（Ctrl+Tab / 左右矢印で切替）

## 設計判断（採用したもの／却下したもの）

- **タブ抽象**: `ISettingsTab` インターフェースを導入し、タブごとにクラス分離する。
  - 代替案（却下）: `SettingsDialog` 内に `BuildBasicPage()` 等のメソッドを 4 つ並べる案。差分は最小だが、Result 組み立てと LoadFrom が 1 箇所に集中し、拡張時に本体を毎回開く必要があるため見送り。
- **タブヘッダのアクセスキー**: 付けない。既存の Alt+E（既定文字コード）・Alt+K（折り返し桁数）と衝突するリスクを避け、標準の Ctrl+Tab / 左右矢印で切替する。NVDA/PC-Talker とも Ctrl+Tab を正しく読むため a11y 影響なし。
- **選択タブの永続化**: 実装しない。常に先頭「基本」で開く。YAGNI。
- **App 側テスト**: `yEdit.App.Tests` プロジェクトは存在せず、UI は手動検証が本プロジェクトの慣行。Core 側は無改変のため既存テスト（`tests/yEdit.Core.Tests/Settings/`）に影響なし。

## タブ構成と担当フィールド

現行 `SettingsDialog.cs:94-107` の `Result` 組み立てを 4 タブに割り当てる。

| タブ | AppSettings フィールド | UI コントロール |
|---|---|---|
| `基本` | `DefaultCodePage` / `DefaultLineEnding` | `_encoding` (ComboBox) / `_eol` (ComboBox) |
| `編集` | `WrapColumnEnabled` / `WrapColumn` | `_wrapEnabled` (CheckBox) / `_wrapColumn` (NumericUpDown) |
| `禁則処理` | `KinsokuLineStartChars` / `KinsokuLineEndChars` / `KinsokuHangChars` | `_kinsokuStart` / `_kinsokuEnd` / `_kinsokuHang` (TextBox × 3) |
| `表示` | `FontName` / `FontSize` / `Theme` | `_fontLabel` / `_fontButton` (FontDialog) / `_theme` (ComboBox) |

現行にある補助定義の移動先:

- `Encodings` / `Eols` 静的テーブル → `BasicSettingsTab` の `private static`
- `IndexOfTheme` / `UpdateFontLabel` / `PickFont` / `SafeFont` → `DisplaySettingsTab` の `private`
- `AddRow` ヘルパ（`Label` ＋任意 `Control` を 2 列 `TableLayoutPanel` に足す小関数） → 共通ヘルパ `SettingsTabLayoutHelper.AddRow(...)` として `Settings/` 直下に切り出す。全 4 タブで再利用

## ファイル配置と名前空間

`src/yEdit.App/` 直下に `Settings/` フォルダを新設。名前空間は `yEdit.App.Settings`（既存の `Speech/` = `yEdit.App.Speech` に倣う）。

```
src/yEdit.App/Settings/
  ISettingsTab.cs               ← インターフェース
  SettingsDialog.cs             ← 既存 src/yEdit.App/SettingsDialog.cs から移設、骨格のみに縮小
  SettingsTabLayoutHelper.cs    ← AddRow 共通ヘルパ
  Tabs/
    BasicSettingsTab.cs
    EditSettingsTab.cs
    KinsokuSettingsTab.cs
    DisplaySettingsTab.cs
```

`yEdit.Core.Settings`（`AppSettings` / `SettingsStore` の名前空間）と `yEdit.App.Settings` は別階層のため `using` は共存可能。`MainForm.cs` は `using yEdit.App.Settings;` を追加するだけ。

## インターフェース契約

```csharp
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

    /// <summary>ダイアログ表示時、baseline から自タブが担当する項目を読み込む。</summary>
    void LoadFrom(AppSettings s);

    /// <summary>OK 押下時、自タブが担当する項目を r に書き戻す。</summary>
    void SaveTo(AppSettings r);
}
```

**タブ実装側の規約:**
- コントロールはフィールドとして保持し `BuildPage()` は初回のみレイアウトを組む
- `AccessibleName` は現行値をそのまま維持
- `TabIndex` はタブ内でローカルに 0 から採番（タブ間はまたがらない）
- `LoadFrom` は `BuildPage` の後（＝コントロール構築後）に呼ぶことを `SettingsDialog` 側で保証
- 既存アクセスキー（`&F` `&E` `&L` `&W` `&K` `&1` `&2` `&3`）はタブごとに保持

## SettingsDialog の骨格

```csharp
namespace yEdit.App.Settings;

public sealed class SettingsDialog : Form
{
    private readonly AppSettings _baseline;
    private readonly IReadOnlyList<ISettingsTab> _tabs;
    // 注: 当初 AccessibleName = "設定カテゴリ" を付けていたが、実機 SR 検証（NVDA）で
    // 「タブ切替のたびに『設定カテゴリ』が読まれて冗長」との指摘を受け撤廃した
    // （コミット c38563b）。タブヘッダ（TabPage.Text）＝カテゴリ名で識別十分。
    private readonly TabControl _tabControl = new()
    {
        Dock = DockStyle.Fill,
    };

    public SettingsDialog(AppSettings s)
    {
        _baseline = s.Clone();
        _tabs = new ISettingsTab[]
        {
            new BasicSettingsTab(),
            new EditSettingsTab(),
            new KinsokuSettingsTab(),
            new DisplaySettingsTab(),
        };

        Text = "設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildLayout();
        foreach (var t in _tabs) t.LoadFrom(_baseline);   // BuildPage 後に必ず呼ぶ
        ActiveControl = _tabControl;                       // 開いた直後は先頭タブ「基本」
    }

    public AppSettings Result
    {
        get
        {
            var r = _baseline.Clone();
            foreach (var t in _tabs) t.SaveTo(r);
            return r;
        }
    }

    private void BuildLayout()
    {
        foreach (var t in _tabs)
        {
            var page = new TabPage(t.Title) { UseVisualStyleBackColor = true };
            var body = t.BuildPage();
            body.Dock = DockStyle.Fill;
            page.Controls.Add(body);
            _tabControl.TabPages.Add(page);
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8),
        };
        buttons.Controls.AddRange(new Control[] { ok, cancel });

        Controls.Add(_tabControl);   // Dock.Fill を後に Add
        Controls.Add(buttons);       // Dock.Bottom を先に Add した順に手前へ
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
```

## 新タブ追加の手順（拡張性の実例）

例として「保存(&S)」タブを追加する場合:

1. `src/yEdit.App/Settings/Tabs/SaveSettingsTab.cs` を新規作成し `ISettingsTab` を実装
2. `SettingsDialog.cs` の `_tabs` 配列に `new SaveSettingsTab()` を 1 行追加
3. 既存タブと `SettingsDialog` 本体は無変更

表示順は `_tabs` 配列順（配列上の位置がそのままタブ順序）。

## 検証方針

- **Core 側テスト**: `AppSettings` / `SettingsStore` は無改変のため既存テストに影響なし。追加テスト不要
- **App 側テスト**: `yEdit.App.Tests` は無し。UI は手動検証（本プロジェクトの慣行）
- **手動 UI 検証項目:**
  - 各タブが表示され、初期値が現行と一致（フォント名/サイズ表示・折り返し ON/OFF・折り返し桁数・禁則文字列・文字コード/改行/テーマの初期選択）
  - OK 押下時の `Result` が現行実装と等価: 何も変えなければ元と等値、1 項目だけ変更すれば差分はその 1 項目のみ
  - Ctrl+Tab / Ctrl+Shift+Tab でタブ切替、Tab キーでタブ内フォーカス移動
  - 既存アクセスキー Alt+F / Alt+W / Alt+K / Alt+1 / Alt+2 / Alt+3 等が該当タブ上で機能
  - SR 発声: NVDA / PC-Talker で TabControl のタブ名（"基本" 等）が読まれる
  - キャンセルで元設定が保たれる
  - フォントダイアログ（`DisplaySettingsTab`）が機能し、選択後のラベル更新・`AccessibleName` 更新が現行同等

## 影響範囲

- 追加: `src/yEdit.App/Settings/` 配下 6 ファイル
- 削除: `src/yEdit.App/SettingsDialog.cs`（`Settings/` 配下に移設）
- 変更: `src/yEdit.App/MainForm.cs` の `using yEdit.App.Settings;` 追加のみ（`OpenSettings()` 本体は無変更）
- 無変更: `yEdit.Core` 全体、`AppSettings` / `SettingsStore` / `AppearanceThemes` / `EncodingCatalog`、その他 App 内の他クラス
