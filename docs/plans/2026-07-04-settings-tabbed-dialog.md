# 設定ダイアログのタブ化 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 設定ダイアログを 4 カテゴリのタブ構成（基本 / 編集 / 禁則処理 / 表示）に再構成し、`ISettingsTab` 抽象で今後のタブ追加を「クラス 1 個追加 ＋ 配列に 1 行」で完結させる。

**Architecture:** `src/yEdit.App/Settings/` フォルダを新設し、`ISettingsTab` インターフェース＋タブ 4 クラス＋共通レイアウトヘルパ＋薄い骨格の `SettingsDialog` に分割。既存 `src/yEdit.App/SettingsDialog.cs` は削除し、公開 API（コンストラクタ・`Result` プロパティ）は維持することで `MainForm.OpenSettings()` の呼び出しコードは無変更。

**Tech Stack:** C# / .NET 9 / Windows Forms（TabControl / TabPage / TableLayoutPanel / FlowLayoutPanel）

**設計書:** `docs/plans/2026-07-04-settings-tabbed-dialog-design.md`

**ブランチ:** `refactor/setting-dailog`

**制約と方針:**
- App 側テスト（`yEdit.App.Tests`）は存在しないため、UI 動作は **手動検証** で担保する（本プロジェクトの慣行）。Core の既存テスト（`tests/yEdit.Core.Tests/`）は無改変で緑を維持。
- `dotnet build` は 0 警告を維持（現行方針）。
- コミットはタスク単位。各タスク後にビルド 0 警告を確認。
- タブ追加時のアクセスキー衝突を避けるため **タブヘッダに `&` は付けない**（Ctrl+Tab / 左右矢印で切替）。既存のコントロールアクセスキー（`&F` `&E` `&L` `&W` `&K` `&1` `&2` `&3`）はそのままタブ内で保持。

---

## Task 1: ISettingsTab インターフェース と共通レイアウトヘルパ

**Files:**
- Create: `src/yEdit.App/Settings/ISettingsTab.cs`
- Create: `src/yEdit.App/Settings/SettingsTabLayoutHelper.cs`

**Step 1: Settings フォルダを作成**

Run:
```powershell
New-Item -ItemType Directory -Path "src/yEdit.App/Settings" -Force
New-Item -ItemType Directory -Path "src/yEdit.App/Settings/Tabs" -Force
```

**Step 2: `src/yEdit.App/Settings/ISettingsTab.cs` を作成**

```csharp
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
```

**Step 3: `src/yEdit.App/Settings/SettingsTabLayoutHelper.cs` を作成**

```csharp
namespace yEdit.App.Settings;

/// <summary>タブ内 2 列 TableLayoutPanel の行追加ヘルパ。全 4 タブで共用する。</summary>
internal static class SettingsTabLayoutHelper
{
    /// <summary>ラベル＋任意コントロールを 1 行として追加する。TabIndex はラベル→コントロールの順に採番。</summary>
    public static void AddRow(TableLayoutPanel root, int row, string label, Control control, int tabBase)
    {
        var lbl = new Label { Text = label, AutoSize = true, TabIndex = tabBase };
        control.TabIndex = tabBase + 1;
        root.Controls.Add(lbl, 0, row);
        root.Controls.Add(control, 1, row);
    }

    /// <summary>タブ内 TableLayoutPanel の共通生成。2 列・AutoSize・Padding 統一。</summary>
    public static TableLayoutPanel NewRoot() => new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 2,
        Padding = new Padding(12),
    };
}
```

**Step 4: ビルドしてコンパイル通過を確認**

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -v minimal`
Expected: `Build succeeded` / `0 Warning(s)` / `0 Error(s)`

**Step 5: コミット**

```powershell
git add src/yEdit.App/Settings/ISettingsTab.cs src/yEdit.App/Settings/SettingsTabLayoutHelper.cs
git commit -m @'
設定タブ化: ISettingsTab インターフェース＋レイアウトヘルパを追加

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 2: BasicSettingsTab（基本 = 既定文字コード・既定改行）

**Files:**
- Create: `src/yEdit.App/Settings/Tabs/BasicSettingsTab.cs`

**Step 1: `src/yEdit.App/Settings/Tabs/BasicSettingsTab.cs` を作成**

```csharp
using yEdit.App.Settings;
using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App.Settings.Tabs;

/// <summary>「基本」タブ。既定の文字コードと既定の改行を扱う。</summary>
public sealed class BasicSettingsTab : ISettingsTab
{
    public string Title => "基本";

    private static readonly IReadOnlyList<EncodingCatalog.EncodingOption> Encodings = EncodingCatalog.SelectableEncodings;
    private static readonly (string Name, int Id)[] Eols =
    {
        ("CRLF（Windows）", 0), ("LF（Unix）", 1), ("CR（旧 Mac）", 2),
    };

    private readonly ComboBox _encoding = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "既定の文字コード" };
    private readonly ComboBox _eol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "既定の改行" };

    public Control BuildPage()
    {
        foreach (var e in Encodings) _encoding.Items.Add(e.DisplayName);
        foreach (var (name, _) in Eols) _eol.Items.Add(name);

        var root = SettingsTabLayoutHelper.NewRoot();
        SettingsTabLayoutHelper.AddRow(root, 0, "既定の文字コード(&E):", _encoding, tabBase: 0);
        SettingsTabLayoutHelper.AddRow(root, 1, "既定の改行(&L):", _eol, tabBase: 2);
        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        int encSel = 0;
        for (int i = 0; i < Encodings.Count; i++)
            if (Encodings[i].CodePage == s.DefaultCodePage) { encSel = i; break; }
        _encoding.SelectedIndex = encSel;

        int eolSel = 0;
        for (int i = 0; i < Eols.Length; i++)
            if (Eols[i].Id == s.DefaultLineEnding) { eolSel = i; break; }
        _eol.SelectedIndex = eolSel;
    }

    public void SaveTo(AppSettings r)
    {
        r.DefaultCodePage = Encodings[_encoding.SelectedIndex].CodePage;
        r.DefaultLineEnding = Eols[_eol.SelectedIndex].Id;
    }
}
```

**Step 2: ビルドして 0 警告を確認**

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -v minimal`
Expected: `Build succeeded` / `0 Warning(s)` / `0 Error(s)`

**Step 3: コミット**

```powershell
git add src/yEdit.App/Settings/Tabs/BasicSettingsTab.cs
git commit -m @'
設定タブ化: BasicSettingsTab（基本 = 既定文字コード/既定改行）を追加

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 3: EditSettingsTab（編集 = 折り返し）

**Files:**
- Create: `src/yEdit.App/Settings/Tabs/EditSettingsTab.cs`

**Step 1: `src/yEdit.App/Settings/Tabs/EditSettingsTab.cs` を作成**

現行 `SettingsDialog.cs:74-75` の "OFF 時は桁数入力を無効化" の挙動をそのまま踏襲する。

```csharp
using yEdit.App.Settings;
using yEdit.Core.Settings;

namespace yEdit.App.Settings.Tabs;

/// <summary>「編集」タブ。表示折り返しの ON/OFF と桁数を扱う。</summary>
public sealed class EditSettingsTab : ISettingsTab
{
    public string Title => "編集";

    private readonly CheckBox _wrapEnabled = new() { Text = "指定文字数で折り返す(&W)", AutoSize = true };
    private readonly NumericUpDown _wrapColumn = new()
    {
        Minimum = 10, Maximum = 1000, Width = 100, AccessibleName = "折り返し桁数",
    };

    public Control BuildPage()
    {
        _wrapEnabled.CheckedChanged += (_, _) => _wrapColumn.Enabled = _wrapEnabled.Checked;

        var root = SettingsTabLayoutHelper.NewRoot();

        // 1 行目: チェックボックス（ラベル兼用）。TabIndex=0。
        _wrapEnabled.TabIndex = 0;
        root.Controls.Add(_wrapEnabled, 0, 0);

        // 2 行目: 「折り返し桁数(&K):」ラベル ＋ NumericUpDown。
        var wrapPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 1 };
        var wrapLbl = new Label { Text = "折り返し桁数(&K):", AutoSize = true, TabIndex = 1, Anchor = AnchorStyles.Left };
        _wrapColumn.TabIndex = 2;
        wrapPanel.Controls.Add(wrapLbl);
        wrapPanel.Controls.Add(_wrapColumn);
        root.Controls.Add(wrapPanel, 1, 0);

        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _wrapEnabled.Checked = s.WrapColumnEnabled;
        _wrapColumn.Value = Math.Clamp(s.WrapColumn, (int)_wrapColumn.Minimum, (int)_wrapColumn.Maximum);
        _wrapColumn.Enabled = _wrapEnabled.Checked;   // 初期状態でも ON/OFF を反映
    }

    public void SaveTo(AppSettings r)
    {
        r.WrapColumnEnabled = _wrapEnabled.Checked;
        r.WrapColumn = (int)_wrapColumn.Value;
    }
}
```

**Step 2: ビルドして 0 警告を確認**

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -v minimal`
Expected: `Build succeeded` / `0 Warning(s)` / `0 Error(s)`

**Step 3: コミット**

```powershell
git add src/yEdit.App/Settings/Tabs/EditSettingsTab.cs
git commit -m @'
設定タブ化: EditSettingsTab（編集 = 折り返し）を追加

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 4: KinsokuSettingsTab（禁則処理）

**Files:**
- Create: `src/yEdit.App/Settings/Tabs/KinsokuSettingsTab.cs`

**Step 1: `src/yEdit.App/Settings/Tabs/KinsokuSettingsTab.cs` を作成**

```csharp
using yEdit.App.Settings;
using yEdit.Core.Settings;

namespace yEdit.App.Settings.Tabs;

/// <summary>「禁則処理」タブ。行頭禁則・行末禁則・ぶら下げの文字セットを扱う。</summary>
public sealed class KinsokuSettingsTab : ISettingsTab
{
    public string Title => "禁則処理";

    private readonly TextBox _kinsokuStart = new() { Width = 320, AccessibleName = "行頭禁則文字" };
    private readonly TextBox _kinsokuEnd = new() { Width = 320, AccessibleName = "行末禁則文字" };
    private readonly TextBox _kinsokuHang = new() { Width = 320, AccessibleName = "ぶら下げ文字" };

    public Control BuildPage()
    {
        var root = SettingsTabLayoutHelper.NewRoot();
        SettingsTabLayoutHelper.AddRow(root, 0, "行頭禁則文字(&1):", _kinsokuStart, tabBase: 0);
        SettingsTabLayoutHelper.AddRow(root, 1, "行末禁則文字(&2):", _kinsokuEnd, tabBase: 2);
        SettingsTabLayoutHelper.AddRow(root, 2, "ぶら下げ文字(&3):", _kinsokuHang, tabBase: 4);
        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _kinsokuStart.Text = s.KinsokuLineStartChars;
        _kinsokuEnd.Text = s.KinsokuLineEndChars;
        _kinsokuHang.Text = s.KinsokuHangChars;
    }

    public void SaveTo(AppSettings r)
    {
        r.KinsokuLineStartChars = _kinsokuStart.Text;
        r.KinsokuLineEndChars = _kinsokuEnd.Text;
        r.KinsokuHangChars = _kinsokuHang.Text;
    }
}
```

**Step 2: ビルドして 0 警告を確認**

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -v minimal`
Expected: `Build succeeded` / `0 Warning(s)` / `0 Error(s)`

**Step 3: コミット**

```powershell
git add src/yEdit.App/Settings/Tabs/KinsokuSettingsTab.cs
git commit -m @'
設定タブ化: KinsokuSettingsTab（禁則処理）を追加

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 5: DisplaySettingsTab（表示 = フォント・配色テーマ）

**Files:**
- Create: `src/yEdit.App/Settings/Tabs/DisplaySettingsTab.cs`

**Step 1: `src/yEdit.App/Settings/Tabs/DisplaySettingsTab.cs` を作成**

現行 `SettingsDialog.cs:116-137` の `UpdateFontLabel` / `PickFont` / `SafeFont` と `IndexOfTheme` を移設する。

```csharp
using yEdit.App;
using yEdit.App.Settings;
using yEdit.Core.Settings;

namespace yEdit.App.Settings.Tabs;

/// <summary>「表示」タブ。フォントと配色テーマを扱う。</summary>
public sealed class DisplaySettingsTab : ISettingsTab
{
    public string Title => "表示";

    private string _fontName = "";
    private float _fontSize = 12f;

    private readonly Label _fontLabel = new() { AutoSize = true };
    private readonly Button _fontButton = new() { Text = "変更(&F)...", AutoSize = true };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "配色テーマ" };

    public Control BuildPage()
    {
        foreach (var t in AppearanceThemes.All) _theme.Items.Add(t.DisplayName);
        _fontButton.Click += (_, _) => PickFont();

        var root = SettingsTabLayoutHelper.NewRoot();

        // フォント行: ラベル ＋ [現在表示 + 変更ボタン]。アクセスキー &F はボタン側に一本化。
        var fontLabelCol = new Label { Text = "フォント:", AutoSize = true, TabIndex = 0 };
        var fontPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 1 };
        fontPanel.Controls.Add(_fontLabel);
        fontPanel.Controls.Add(_fontButton);
        root.Controls.Add(fontLabelCol, 0, 0);
        root.Controls.Add(fontPanel, 1, 0);

        SettingsTabLayoutHelper.AddRow(root, 1, "配色(&C):", _theme, tabBase: 2);

        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _fontName = s.FontName;
        _fontSize = s.FontSize;
        UpdateFontLabel();
        _theme.SelectedIndex = IndexOfTheme(s.Theme);
    }

    public void SaveTo(AppSettings r)
    {
        r.FontName = _fontName;
        r.FontSize = _fontSize;
        r.Theme = AppearanceThemes.All[_theme.SelectedIndex].Id;
    }

    private static int IndexOfTheme(string id)
    {
        for (int i = 0; i < AppearanceThemes.All.Count; i++)
            if (AppearanceThemes.All[i].Id == id) return i;
        return 0;
    }

    private void UpdateFontLabel()
    {
        string desc = $"{_fontName}, {_fontSize:0.#} pt";
        _fontLabel.Text = desc;
        _fontButton.AccessibleName = $"フォント変更 現在 {desc}";
    }

    private void PickFont()
    {
        using var dlg = new FontDialog { Font = SafeFont(), ShowEffects = false, FontMustExist = true };
        if (dlg.ShowDialog(_fontButton.FindForm()) != DialogResult.OK) return;
        _fontName = dlg.Font.Name;
        _fontSize = dlg.Font.Size;
        UpdateFontLabel();
    }

    private Font SafeFont()
    {
        try { return new Font(_fontName, _fontSize <= 0 ? 12f : _fontSize); }
        catch { return new Font(FontFamily.GenericMonospace, 12f); }
    }
}
```

**注意点:**
- `AppearanceThemes` は `yEdit.App` 名前空間直下（`src/yEdit.App/EditorAppearance.cs` 近辺）に定義されている。`using yEdit.App;` を先頭に置いた上で `AppearanceThemes.All` を参照。もし名前空間解決がうまくいかない場合は `Grep AppearanceThemes` で正しい名前空間を確認し using を調整すること。
- `PickFont` は現行 `SettingsDialog.cs:127` で `this`（Form）を親に渡していたが、タブは Form ではないため `_fontButton.FindForm()` で親ダイアログを取得する（フォーカスが親に戻る挙動を保つ）。

**Step 2: `AppearanceThemes` の名前空間を確認**

Run:
```powershell
# 名前空間確認 — 期待は "namespace yEdit.App;"
```
Grep で `namespace` を含む `AppearanceThemes` 定義ファイルを開いて確認する。もし `yEdit.App` 直下なら現状の using で OK。異なる場合は using を調整。

**Step 3: ビルドして 0 警告を確認**

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -v minimal`
Expected: `Build succeeded` / `0 Warning(s)` / `0 Error(s)`

**Step 4: コミット**

```powershell
git add src/yEdit.App/Settings/Tabs/DisplaySettingsTab.cs
git commit -m @'
設定タブ化: DisplaySettingsTab（表示 = フォント/配色）を追加

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 6: SettingsDialog をタブ骨格に置き換え・Settings/ 配下へ移設

**Files:**
- Delete: `src/yEdit.App/SettingsDialog.cs`
- Create: `src/yEdit.App/Settings/SettingsDialog.cs`
- Modify: `src/yEdit.App/MainForm.cs`（`using yEdit.App.Settings;` を追加）

**Step 1: 旧 `src/yEdit.App/SettingsDialog.cs` を削除**

Run:
```powershell
git rm src/yEdit.App/SettingsDialog.cs
```

**Step 2: `src/yEdit.App/Settings/SettingsDialog.cs` を作成**

```csharp
using yEdit.App.Settings.Tabs;
using yEdit.Core.Settings;

namespace yEdit.App.Settings;

/// <summary>
/// 設定ダイアログ（タブ構成・アクセシブル）。
/// タブ実装は <see cref="ISettingsTab"/>。タブ追加は _tabs 配列に 1 行足すだけで完結する。
/// 呼び出し側（MainForm.OpenSettings）は new SettingsDialog(_settings) → dlg.Result の
/// 従来インターフェースをそのまま使う。
/// </summary>
public sealed class SettingsDialog : Form
{
    private readonly AppSettings _baseline;
    private readonly IReadOnlyList<ISettingsTab> _tabs;
    // 注: 当初 AccessibleName = "設定カテゴリ" を付けていたが、実機 SR 検証（NVDA）で
    // 「タブ切替のたびに読まれて冗長」との指摘を受け撤廃（コミット c38563b）。
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
        foreach (var t in _tabs) t.LoadFrom(_baseline);   // BuildPage の後に必ず呼ぶ
        ActiveControl = _tabControl;                       // 先頭タブ「基本」に居る
    }

    /// <summary>
    /// 編集結果の設定。ShowDialog が OK の後に読む。ダイアログで編集しない項目は元設定の値を保持する。
    /// 取得のたびに独立したインスタンスを組み立てる（保持状態を書き換えない・副作用なし）。
    /// </summary>
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

        // Dock.Bottom を先に Add してから Dock.Fill を Add する順で下部固定＋残り全部を実現。
        Controls.Add(buttons);
        Controls.Add(_tabControl);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
```

**Step 3: `src/yEdit.App/MainForm.cs` の using 修正**

`MainForm.cs` 冒頭の using 群に `using yEdit.App.Settings;` を追加する。既存の `new SettingsDialog(_settings)` 呼び出し（`MainForm.cs:367` 付近）は無変更で解決する。

具体的手順:
1. `Grep -n "using yEdit" src/yEdit.App/MainForm.cs` で using ブロックの位置を確認
2. Edit で `using yEdit.Core.Settings;` の直後（もしくは既存 using ブロック末尾）に `using yEdit.App.Settings;` を挿入

**Step 4: ビルドして 0 警告を確認**

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -v minimal`
Expected: `Build succeeded` / `0 Warning(s)` / `0 Error(s)`

もしエラーが出た場合の典型:
- `SettingsDialog` の名前解決が曖昧: 旧ファイル削除が反映されているか git status で確認
- `AppearanceThemes` 未解決: DisplaySettingsTab の using を再確認

**Step 5: 全体ソリューションビルド**

Run: `dotnet build -v minimal`
Expected: `0 Warning(s)` / `0 Error(s)`

**Step 6: Core テストが緑を維持しているか確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj -v minimal`
Expected: All tests pass（現行の Passed 件数を維持）

**Step 7: コミット**

```powershell
git add src/yEdit.App/Settings/SettingsDialog.cs src/yEdit.App/MainForm.cs
git commit -m @'
設定タブ化: SettingsDialog をタブ骨格に置き換え、Settings/ 配下へ移設

- ISettingsTab 実装 4 タブを束ねる薄い骨格に変更
- 旧 src/yEdit.App/SettingsDialog.cs を削除
- MainForm.cs は using を追加のみ（OpenSettings 本体は無変更）

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 7: 手動 UI 検証と最終確認

**Files:** なし（実行と目視確認のみ）

**Step 1: アプリを起動**

Run:
```powershell
dotnet run --project src/yEdit.App/yEdit.App.csproj
```

**Step 2: メニューから「オプション → 設定」を開く**

期待: 4 タブ（基本 / 編集 / 禁則処理 / 表示）が表示される。

**Step 3: 各タブの初期値を確認**

| タブ | 確認項目 |
|---|---|
| 基本 | 既定の文字コード（例: UTF-8 SIG）と既定の改行（例: CRLF）が現在の設定と一致 |
| 編集 | 「指定文字数で折り返す」の ON/OFF が設定どおり／折り返し桁数の初期値／OFF 時は桁数入力が無効化 |
| 禁則処理 | 3 つの TextBox に既定値の文字列が入っている |
| 表示 | 「フォント: ＭＳ ゴシック, 12 pt」など現在フォントが表示／「配色」ドロップダウンが現在テーマを選択 |

**Step 4: タブ切替とフォーカス移動を確認**

- Ctrl+Tab で 基本 → 編集 → 禁則処理 → 表示 → 基本… と循環する
- Ctrl+Shift+Tab で逆順に循環する
- タブヘッダにフォーカスがあるとき ← / → で切替できる
- 各タブ内で Tab キーが「先頭コントロール → 次 → OK/キャンセル」と流れる

**Step 5: アクセスキーの動作確認**

各タブが選択された状態で:

| タブ | 押下 | 期待 |
|---|---|---|
| 基本 | Alt+E | 既定文字コードにフォーカス |
| 基本 | Alt+L | 既定改行にフォーカス |
| 編集 | Alt+W | 折り返し ON/OFF が切り替わる |
| 編集 | Alt+K | 折り返し桁数にフォーカス |
| 禁則処理 | Alt+1 / Alt+2 / Alt+3 | 各 TextBox にフォーカス |
| 表示 | Alt+F | フォントダイアログが開く |
| 表示 | Alt+C | 配色コンボにフォーカス |

**Step 6: 実際に値を変更 → OK で反映を確認**

- 表示タブでフォントを別のものに変更 → OK → エディタのフォントが変わっている
- 表示タブで配色テーマを別のものに変更 → OK → 全ドキュメントに反映
- 編集タブで折り返しを ON/OFF 切替 → OK → 反映
- 禁則処理タブで文字列を編集 → OK → 保存されている（設定を再度開いて確認）
- 基本タブで文字コード変更 → OK → 新規ファイル時のデフォルトに反映（`ファイル → 新規` などで確認）

**Step 7: キャンセルで元に戻ることを確認**

各タブで値を変更 → キャンセル → 再度開いて元の値のまま。

**Step 8: SR 発声の確認（可能な範囲で）**

NVDA / PC-Talker のいずれかを起動して:
- ダイアログを開くと "設定" が読まれる
- タブ名 "基本" "編集" "禁則処理" "表示" が読まれる（Ctrl+Tab のたびに読む）
- 各コントロールに移ると `AccessibleName` の内容が読まれる

**Step 9: すべて OK なら追加のコミットは不要。問題があれば修正して追加コミット。**

**Step 10: 最終ステータス確認**

Run:
```powershell
git log --oneline refactor/setting-dailog ^main
git status
```

Expected:
- タスク 1〜6 の 6 コミットが並ぶ
- working tree clean

---

## 完了条件

- [x] Task 1〜6 のコミットが順に並んでいる
- [x] `dotnet build` が 0 警告 / 0 エラー
- [x] `dotnet test tests/yEdit.Core.Tests/` が全緑
- [x] 手動 UI 検証（Task 7）の全項目が期待どおり
- [x] `MainForm.cs` の `OpenSettings()` 呼び出しコードは無変更
- [x] `src/yEdit.App/Settings/` 配下に 6 ファイル・旧 `SettingsDialog.cs` は削除済み

## 完了後の申し送り（メモリ更新）

このリファクタが `refactor/setting-dailog` → `main` にマージされた後、`production-build-started` メモリに「設定ダイアログのタブ化（ISettingsTab 抽象）」を追記する。将来 "保存" タブ等を追加する際の実装例を残す意味で、design.md へのパス（`docs/plans/2026-07-04-settings-tabbed-dialog-design.md`）と実装計画（本ファイル）を参照リンクとして記録する。
