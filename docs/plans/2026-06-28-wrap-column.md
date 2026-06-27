# 折り返し桁数の設定（指定文字数での表示折り返し）Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 設定ダイアログに「指定文字数で折り返す」チェックと折り返し桁数の入力を新設し、指定時は編集画面で指定桁数の表示折り返し（本文不変）を行う。

**Architecture:** Scintilla の表示折り返しはテキスト描画領域幅で折り返すため、`SCI_SETMARGINRIGHT`（右側の空白マージン）で描画幅を「桁数×半角1文字幅」に制限して N 桁折り返しを実現する（案A）。設定は `AppSettings` の2キーで全タブ共通・永続化し、既存の `EditorAppearance.Apply` 経路で全タブへ適用する。桁幅・右マージンの純算術は `yEdit.Core` に切り出して xUnit でテストし、Scintilla グルー（WinForms）はビルド＋実機 SR 検証で確認する。

**Tech Stack:** C# / .NET 9 / WinForms / Scintilla5.NET（desjarlais フォーク）/ xUnit。設計資料: `docs/plans/2026-06-28-wrap-column-design.md`。

**確定仕様:** 表示だけの折り返し（保存内容不変）／半角換算の桁数（全角=2桁）／任意の文字で折る（`SC_WRAP_CHAR`）／初期値80桁・範囲10〜1000／全タブ共通・永続化／上下矢印は表示行単位（Scintilla 既定）。

**ブランチ:** `feature/wrap-column`（作成済み。設計ドキュメントはコミット済み）。

**共通コマンド（リポジトリルート `<repo>`）:**
- Core テスト: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
- 全体ビルド: `dotnet build yEdit.sln -c Debug`（**0 警告**であること。本プロジェクトの規律）

---

## Task 1: Core — `WrapGeometry` 純算術（TDD）

桁幅・右マージン・桁クランプの純関数。Scintilla 非依存でテスト可能。

**Files:**
- Test: `tests/yEdit.Core.Tests/Settings/WrapGeometryTests.cs`（新規）
- Create: `src/yEdit.Core/Settings/WrapGeometry.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Settings/WrapGeometryTests.cs`:
```csharp
using yEdit.Core.Settings;
using Xunit;

namespace yEdit.Core.Tests.Settings;

public class WrapGeometryTests
{
    [Theory]
    [InlineData(80, 8, 640)]
    [InlineData(40, 7, 280)]
    [InlineData(10, 10, 100)]
    public void TargetWidthPx_is_columns_times_halfwidth(int columns, int halfWidth, int expected)
        => Assert.Equal(expected, WrapGeometry.TargetWidthPx(columns, halfWidth));

    [Theory]
    [InlineData(1000, 640, 360)]   // 広い: 余剰を右マージンへ
    [InlineData(640, 640, 0)]      // ぴったり: マージン0
    [InlineData(300, 640, 0)]      // 狭い: 負にならず0（ウィンドウ幅で折る）
    public void RightMargin_is_nonnegative_surplus(int textAreaPx, int targetPx, int expected)
        => Assert.Equal(expected, WrapGeometry.RightMargin(textAreaPx, targetPx));

    [Theory]
    [InlineData(80, 80)]
    [InlineData(5, 10)]        // 下限クランプ
    [InlineData(0, 10)]
    [InlineData(-3, 10)]
    [InlineData(99999, 1000)] // 上限クランプ
    public void ClampColumns_bounds_to_10_1000(int input, int expected)
        => Assert.Equal(expected, WrapGeometry.ClampColumns(input));
}
```

**Step 2: テストを実行して失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~WrapGeometry"`
Expected: コンパイルエラー（`WrapGeometry` 型が存在しない）で FAIL。

**Step 3: 最小実装**

`src/yEdit.Core/Settings/WrapGeometry.cs`:
```csharp
namespace yEdit.Core.Settings;

/// <summary>
/// 指定桁の表示折り返しに使う純算術（Scintilla 非依存・テスト対象）。
/// 桁は半角換算（全角=2桁）。桁幅は半角1文字のピクセル幅を単位とする。
/// </summary>
public static class WrapGeometry
{
    /// <summary>桁数 × 半角1文字幅(px) → 目標テキスト領域幅(px)。</summary>
    public static int TargetWidthPx(int columns, int halfWidthPx) => columns * halfWidthPx;

    /// <summary>テキスト領域幅と目標幅から右マージン(px)。広い分だけ空白に充てる（負にしない）。</summary>
    public static int RightMargin(int textAreaPx, int targetPx) => Math.Max(0, textAreaPx - targetPx);

    /// <summary>折り返し桁数を許容範囲（10〜1000）へクランプ。破損設定・範囲外対策。</summary>
    public static int ClampColumns(int columns) => Math.Clamp(columns, 10, 1000);
}
```

**Step 4: テストを実行して成功を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~WrapGeometry"`
Expected: PASS（10 件）。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Settings/WrapGeometry.cs tests/yEdit.Core.Tests/Settings/WrapGeometryTests.cs
git commit -m "折り返し桁数: WrapGeometry 純算術を追加（桁幅/右マージン/桁クランプ・TDD）"
```

---

## Task 2: Core — `AppSettings` 新キー ＋ `Normalize` 健全化（TDD）

折り返し設定の永続化キーと、破損値のクランプを追加する。

**Files:**
- Test: `tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs`（追記）
- Modify: `src/yEdit.Core/Settings/AppSettings.cs`
- Modify: `src/yEdit.Core/Settings/SettingsStore.cs`（`Normalize` に1行）

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs` に追記:
```csharp
    [Fact]
    public void Defaults_wrap_is_disabled_with_80_columns()
    {
        var def = new AppSettings();
        Assert.False(def.WrapColumnEnabled);
        Assert.Equal(80, def.WrapColumn);
    }

    [Fact]
    public void Save_then_load_roundtrips_wrap_settings()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings { WrapColumnEnabled = true, WrapColumn = 60 };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.WrapColumnEnabled);
            Assert.Equal(60, loaded.WrapColumn);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_clamps_out_of_range_wrap_column()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"WrapColumnEnabled\":true,\"WrapColumn\":99999}");
            var s = SettingsStore.Load(path);
            Assert.Equal(1000, s.WrapColumn);   // 上限へクランプ
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

**Step 2: テストを実行して失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: コンパイルエラー（`WrapColumnEnabled` / `WrapColumn` が存在しない）で FAIL。

**Step 3: 実装**

`src/yEdit.Core/Settings/AppSettings.cs` の `RecentFiles` の前（クラス末尾付近）に追加:
```csharp
    /// <summary>編集画面で指定桁数の表示折り返しを行うか（保存内容は不変）。</summary>
    public bool WrapColumnEnabled { get; set; } = false;
    /// <summary>表示折り返しの桁数（半角換算・全角=2桁）。既定80・範囲10〜1000。</summary>
    public int WrapColumn { get; set; } = 80;
```

`src/yEdit.Core/Settings/SettingsStore.cs` の `Normalize` 内、`return s;` の直前に追加:
```csharp
        s.WrapColumn = WrapGeometry.ClampColumns(s.WrapColumn);  // 範囲外/破損値を 10〜1000 へ
```

**Step 4: テストを実行して成功を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
Expected: 既存含め全て PASS（新規3件 ＋ 既存）。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Settings/AppSettings.cs src/yEdit.Core/Settings/SettingsStore.cs tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs
git commit -m "折り返し桁数: AppSettings に WrapColumnEnabled/WrapColumn を追加し Normalize でクランプ"
```

---

## Task 3: Editor — `Sci` 定数 ＋ `ScintillaHost.ApplyWrapColumn`

Scintilla への適用本体。WinForms/ハンドル依存のため単体テストは行わず、ビルド＋実機で確認（Task 7）。

**Files:**
- Modify: `src/yEdit.Editor/yEdit.Editor.csproj`（Core 参照を追加）
- Modify: `src/yEdit.Editor/Sci.cs`
- Modify: `src/yEdit.Editor/ScintillaHost.cs`

**Step 1: Editor から Core を参照可能にする**

`src/yEdit.Editor/yEdit.Editor.csproj` の `<ItemGroup>` 内、`yEdit.Accessibility` 参照の下に追加:
```xml
    <ProjectReference Include="..\yEdit.Core\yEdit.Core.csproj" />
```
（Core は UI 非依存の最下層。循環参照は生じない。`WrapGeometry` を本番でも使う。）

**Step 2: `Sci.cs` に定数追加**

`src/yEdit.Editor/Sci.cs` の最後の定数の後に追加:
```csharp
    // 表示折り返し（指定桁）。UI スレッドからのみ送る。
    public const int SCI_SETMARGINRIGHT = 2156;   // (unused, pixelWidth) テキスト右側の空白マージン
    public const int SCI_GETMARGINLEFT = 2155;    // → 左テキストマージン(px)
    public const int SCI_GETMARGINWIDTHN = 2243;  // (margin) → 当該マージン幅(px)
    public const int SCI_TEXTWIDTH = 2276;        // (style, const char* utf8) → 文字列のピクセル幅
    public const int STYLE_DEFAULT = 32;
```

**Step 3: `ScintillaHost` にフィールドとメソッドを追加**

`src/yEdit.Editor/ScintillaHost.cs` 冒頭の `using` に（無ければ）Core を追加:
```csharp
using yEdit.Core.Settings;
```

フィールド群（既存の `_hwnd` 付近の private フィールド宣言の並びに追加）:
```csharp
    // ---- 表示折り返し（指定桁・本文不変） ----
    private int _wrapColumns;       // 0 = 無効
    private bool _wrapSizeHooked;   // SizeChanged 二重購読防止
```

メソッド（クラス内の公開ヘルパ付近に追加）:
```csharp
    /// <summary>
    /// 指定桁数の表示折り返しを適用する（本文不変・UI スレッド専用）。
    /// columns&lt;=0 で無効化。有効時は WrapMode=Char にし、半角1文字幅×桁数を目標幅として
    /// 右マージンで描画幅を制限する。ウィンドウ幅追従のため SizeChanged を購読する。
    /// </summary>
    public void ApplyWrapColumn(int columns)
    {
        if (columns <= 0)
        {
            _wrapColumns = 0;
            WrapMode = WrapMode.None;
            if (IsHandleCreated) DirectMessage(Sci.SCI_SETMARGINRIGHT, (nint)0, (nint)1); // 既定1px へ戻す
            return;
        }

        _wrapColumns = WrapGeometry.ClampColumns(columns);
        WrapMode = WrapMode.Char;
        if (!_wrapSizeHooked)
        {
            SizeChanged += (_, _) => RecomputeWrapMargin();
            _wrapSizeHooked = true;
        }
        RecomputeWrapMargin();
    }

    /// <summary>クライアント幅と桁目標から右マージン(px)を再計算して適用する（UI スレッド）。</summary>
    private void RecomputeWrapMargin()
    {
        if (_wrapColumns <= 0 || !IsHandleCreated) return;

        int halfWidth = MeasureHalfWidthPx();
        if (halfWidth <= 0) return;

        int targetPx = WrapGeometry.TargetWidthPx(_wrapColumns, halfWidth);

        // テキスト領域幅 = クライアント幅 − 左テキストマージン − 左マージン群(0..4)。
        int leftStuff = DirectMessage(Sci.SCI_GETMARGINLEFT).ToInt32();
        for (int m = 0; m < 5; m++)
            leftStuff += DirectMessage(Sci.SCI_GETMARGINWIDTHN, (nint)m).ToInt32();
        int textAreaPx = ClientSize.Width - leftStuff;

        int right = WrapGeometry.RightMargin(textAreaPx, targetPx);
        DirectMessage(Sci.SCI_SETMARGINRIGHT, (nint)0, (nint)right);
    }

    /// <summary>半角1文字（"0"）の描画幅(px)を STYLE_DEFAULT で測る。</summary>
    private int MeasureHalfWidthPx()
    {
        byte[] one = System.Text.Encoding.ASCII.GetBytes("0");
        nint buf = Marshal.AllocHGlobal(one.Length + 1);
        try
        {
            Marshal.Copy(one, 0, buf, one.Length);
            Marshal.WriteByte(buf, one.Length, 0);
            return DirectMessage(Sci.SCI_TEXTWIDTH, (nint)Sci.STYLE_DEFAULT, buf).ToInt32();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
```

注: `WrapMode` 型は ScintillaNET の列挙（`WrapMode.None` / `WrapMode.Char`）。`ScintillaHost` は `Scintilla` を継承しているため `WrapMode` プロパティ・`DirectMessage`・`ClientSize`・`SizeChanged`・`IsHandleCreated` をそのまま使える。`Marshal` は既存 using（`System.Runtime.InteropServices`）で解決済み。

**Step 4: ビルドで検証**

Run: `dotnet build yEdit.sln -c Debug`
Expected: 成功・**0 警告**。

**Step 5: コミット**

```bash
git add src/yEdit.Editor/yEdit.Editor.csproj src/yEdit.Editor/Sci.cs src/yEdit.Editor/ScintillaHost.cs
git commit -m "折り返し桁数: ScintillaHost.ApplyWrapColumn を追加（右マージンで桁幅を制限・Core 参照）"
```

---

## Task 4: App — `EditorAppearance.Apply` から折り返しを適用

新規タブ生成・設定変更の両経路が通る共通点に1行足し、全タブへ自動適用する。

**Files:**
- Modify: `src/yEdit.App/EditorAppearance.cs`

**Step 1: 実装**

`src/yEdit.App/EditorAppearance.cs` の `Apply` 末尾（`ed.SelectionBackColor = fore;` の後）に追加:
```csharp
        // 表示折り返し（指定桁・本文不変）。フォント適用後に半角幅を測るためここで最後に呼ぶ。
        ed.ApplyWrapColumn(settings.WrapColumnEnabled ? settings.WrapColumn : 0);
```

**Step 2: ビルドで検証**

Run: `dotnet build yEdit.sln -c Debug`
Expected: 成功・0 警告。

**Step 3: コミット**

```bash
git add src/yEdit.App/EditorAppearance.cs
git commit -m "折り返し桁数: EditorAppearance.Apply で全タブへ折り返しを適用"
```

---

## Task 5: App — `SettingsDialog` にチェックボックス＋桁数入力

「既定の改行」の下にチェックボックスと `NumericUpDown` を新設し、公開プロパティを足す。

**Files:**
- Modify: `src/yEdit.App/SettingsDialog.cs`

**Step 1: フィールドを追加**

`src/yEdit.App/SettingsDialog.cs` の `_eol` フィールド宣言の後に追加:
```csharp
    private readonly CheckBox _wrapEnabled = new() { Text = "指定文字数で折り返す(&W)", AutoSize = true };
    private readonly NumericUpDown _wrapColumn = new()
    {
        Minimum = 10, Maximum = 1000, Width = 100, AccessibleName = "折り返し桁数",
    };
```

**Step 2: コンストラクタで初期値を反映**

`SettingsDialog(AppSettings s)` 内、`_eol.SelectedIndex = eolSel;` の後・`_fontButton.Click += ...` の前に追加:
```csharp
        _wrapEnabled.Checked = s.WrapColumnEnabled;
        _wrapColumn.Value = Math.Clamp(s.WrapColumn, (int)_wrapColumn.Minimum, (int)_wrapColumn.Maximum);
        _wrapColumn.Enabled = _wrapEnabled.Checked;       // OFF 時は桁数入力を無効化
        _wrapEnabled.CheckedChanged += (_, _) => _wrapColumn.Enabled = _wrapEnabled.Checked;
```

**Step 3: 公開プロパティを追加**

既存の公開プロパティ群（`DefaultLineEnding` の後）に追加:
```csharp
    public bool WrapColumnEnabled => _wrapEnabled.Checked;
    public int WrapColumn => (int)_wrapColumn.Value;
```

**Step 4: レイアウトに行を追加**

`BuildLayout()` 内、`AddRow(root, 3, "既定の改行(&L):", _eol, tabBase: 6);` の直後に追加:
```csharp
        // 折り返し: チェックボックス（行ラベル兼用）＋ 桁数入力。
        root.Controls.Add(_wrapEnabled, 0, 4);
        var wrapPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 9 };
        var wrapLbl = new Label { Text = "折り返し桁数(&K):", AutoSize = true, TabIndex = 9, Anchor = AnchorStyles.Left };
        _wrapColumn.TabIndex = 10;
        wrapPanel.Controls.Add(wrapLbl);
        wrapPanel.Controls.Add(_wrapColumn);
        root.Controls.Add(wrapPanel, 1, 4);
        _wrapEnabled.TabIndex = 8;
```

そして同メソッド内、OK/キャンセルの `buttons` を追加している行を **row 4 → row 5** に変更:
```csharp
        root.Controls.Add(buttons, 0, 5);   // 折り返し行(4)の下へ
```

**Step 5: ビルドで検証**

Run: `dotnet build yEdit.sln -c Debug`
Expected: 成功・0 警告。

**Step 6: コミット**

```bash
git add src/yEdit.App/SettingsDialog.cs
git commit -m "折り返し桁数: 設定ダイアログにチェックボックスと桁数入力を追加"
```

---

## Task 6: App — `MainForm.OpenSettings` で新キーを反映

OK 後に新キーを `_settings` へ書き戻す（適用ループ・保存は既存のまま再利用）。

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`（`OpenSettings`）

**Step 1: 実装**

`src/yEdit.App/MainForm.cs` の `OpenSettings` 内、`_settings.DefaultLineEnding = dlg.DefaultLineEnding;` の後に追加:
```csharp
        _settings.WrapColumnEnabled = dlg.WrapColumnEnabled;
        _settings.WrapColumn = dlg.WrapColumn;
```
（直後の `foreach (var doc in _docs.Documents) EditorAppearance.Apply(...)` が折り返しも含めて全タブへ再適用し、`SettingsStore.Save` が永続化する。）

**Step 2: ビルドで検証**

Run: `dotnet build yEdit.sln -c Debug`
Expected: 成功・0 警告。

**Step 3: コミット**

```bash
git add src/yEdit.App/MainForm.cs
git commit -m "折り返し桁数: OpenSettings で新キーを反映（全タブ再適用・永続化）"
```

---

## Task 7: 検証・コードレビュー・マージ

**Step 1: Core テスト全緑**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
Expected: 全 PASS。

**Step 2: 全体ビルド 0 警告**

Run: `dotnet build yEdit.sln -c Debug`
Expected: 成功・0 警告。

**Step 3: 実機 SR 検証（手動・チェックリスト）**

`dotnet run --project src/yEdit.App` で起動し、設定（Ctrl+, など既存のメニュー経路）から:
- [ ] チェック OFF（既定）: 折り返さない（長い行は横スクロール）。
- [ ] チェック ON・80桁: 80桁相当で表示折り返しされる（全角=2桁換算で右端がそろう）。
- [ ] 桁数を 40 に変更 → OK: 即座に 40桁で折り返す（全タブ）。
- [ ] ウィンドウを横に伸縮 → 折り返し桁が保たれる（広いと右に余白／目標より狭いとウィンドウ幅で折る）。
- [ ] フォントを変更 → OK: 折り返し位置が新フォントの半角幅で再計算される。
- [ ] 折り返し中に上下矢印 → 表示行単位で移動。SR の行/文字読みは論理行ベースのまま不変。
- [ ] 保存して開き直す → 本文に改行は入っていない（表示だけ）。設定（ON/桁数）も復元される。
- [ ] 設定ダイアログで Tab 移動 → チェックボックス→桁数入力 の順に到達し、SR が状態/値を読む。チェック OFF で桁数入力が無効化。
- [ ] 右端の桁ずれ（スクロールバー幅由来）が許容範囲か確認。気になる場合は `RecomputeWrapMargin` に定数オフセット較正を追加（フォローアップ）。

**Step 4: コードレビュー依頼（別エージェント）**

プロジェクト規律によりマージ前に別エージェントへレビュー依頼（@superpowers:requesting-code-review）。差分・設計ドキュメント・本計画を渡し、特に Scintilla 右マージン方式の妥当性／UI スレッド規律／アクセシビリティ不変性を見てもらう。

**Step 5: main へ no-ff マージ**

レビュー反映後:
```bash
git checkout main
git merge --no-ff feature/wrap-column -m "折り返し桁数の設定（指定文字数での表示折り返し）をマージ"
```

---

## 変更ファイル一覧（再掲）

- `src/yEdit.Core/Settings/WrapGeometry.cs`（新規・Task 1）
- `tests/yEdit.Core.Tests/Settings/WrapGeometryTests.cs`（新規・Task 1）
- `src/yEdit.Core/Settings/AppSettings.cs`（Task 2）
- `src/yEdit.Core/Settings/SettingsStore.cs`（Task 2）
- `tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs`（Task 2）
- `src/yEdit.Editor/yEdit.Editor.csproj`（Task 3）
- `src/yEdit.Editor/Sci.cs`（Task 3）
- `src/yEdit.Editor/ScintillaHost.cs`（Task 3）
- `src/yEdit.App/EditorAppearance.cs`（Task 4）
- `src/yEdit.App/SettingsDialog.cs`（Task 5）
- `src/yEdit.App/MainForm.cs`（Task 6）
