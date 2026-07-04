# 設定ダイアログ新項目（6 タブ構成）実装プラン

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 承認済み設計（`docs/plans/2026-07-04-settings-new-items-design.md`）に基づき、設定ダイアログへ 9 項目を追加し 6 タブ構成にする。

**Architecture:** Core（AppSettings/SettingsStore/SrRouteSelector）→ Editor（ScintillaHost 行番号）→ App（EditorAppearance・タブ UI・CSV 自動モード・BackupCoordinator・SR 経路配線）の順に、常にビルド緑を保って積み上げる。Core はテストファースト、UI 層は手動検証（本プロジェクトの慣行）。

**Tech Stack:** .NET 9 / WinForms / Scintilla5.NET 6.1.2（desjarlais フォーク）/ xUnit。ビルドは `dotnet build yEdit.sln`、テストは `dotnet test yEdit.sln`（リポジトリルートで実行）。

**前提知識（このリポジトリ固有）:**

- 設定は `%APPDATA%\yEdit\settings.json`。`SettingsStore.Load` が `Normalize` で壊れ値を既定へ補正する。
- 設定ダイアログはタブ化済み。タブ追加 = `ISettingsTab` 実装クラス 1 個 ＋ `SettingsDialog._tabs` に 1 行。
- SR（スクリーンリーダー）経路は起動時に 1 回だけ確定する（起動時確定方針）。`SrContext.Detect` → 受動読み（`ScintillaHost.ApplySrAdaptation`）と能動発声（`AnnouncerFactory`）が同じ判定を消費。
- コミットメッセージは日本語・末尾に `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`。

---

### Task 1: AppSettings 新キー（テストファースト）

**Files:**
- Modify: `tests/yEdit.Core.Tests/Settings/AppSettingsTests.cs`
- Modify: `src/yEdit.Core/Settings/AppSettings.cs`

**Step 1: 失敗するテストを書く**

`AppSettingsTests.cs` の class 末尾（`Clone_is_independent_of_original` の後）に追加:

```csharp
    [Fact]
    public void New_setting_keys_have_expected_defaults()
    {
        var def = new AppSettings();
        Assert.False(def.CsvAutoModeOnOpen);
        Assert.Equal(4, def.TabWidth);
        Assert.False(def.TabsToSpaces);
        Assert.False(def.ShowLineNumbers);
        Assert.False(def.HighlightCurrentLine);
        Assert.Equal(1, def.CaretWidth);
        Assert.False(def.ShowWhitespace);
        Assert.True(def.BackupEnabled);
        Assert.Equal(300, def.BackupIntervalSeconds);   // 30→300 へ変更（設計 2026-07-04）
        Assert.True(def.ConfirmRestoreOnStartup);
        Assert.Equal("nvda", def.PreferredScreenReader);
    }

    [Fact]
    public void Clone_copies_new_setting_keys()
    {
        var s = new AppSettings
        {
            CsvAutoModeOnOpen = true, TabWidth = 8, TabsToSpaces = true,
            ShowLineNumbers = true, HighlightCurrentLine = true, CaretWidth = 3,
            ShowWhitespace = true, ConfirmRestoreOnStartup = false, PreferredScreenReader = "pctalker",
        };
        var c = s.Clone();
        Assert.True(c.CsvAutoModeOnOpen);
        Assert.Equal(8, c.TabWidth);
        Assert.True(c.TabsToSpaces);
        Assert.True(c.ShowLineNumbers);
        Assert.True(c.HighlightCurrentLine);
        Assert.Equal(3, c.CaretWidth);
        Assert.True(c.ShowWhitespace);
        Assert.False(c.ConfirmRestoreOnStartup);
        Assert.Equal("pctalker", c.PreferredScreenReader);
    }
```

**Step 2: 失敗を確認**

Run: `dotnet test yEdit.sln --filter "FullyQualifiedName~AppSettingsTests"`
Expected: **コンパイルエラー**（`CsvAutoModeOnOpen` 等が未定義）。TDD ではコンパイルエラーも「失敗」として扱う。

**Step 3: 最小実装**

`AppSettings.cs` — `BackupIntervalSeconds` の既定を変更:

```csharp
    /// <summary>自動バックアップの間隔（秒）。</summary>
    public int BackupIntervalSeconds { get; set; } = 300;
```

`RecentFiles` プロパティの**直前**に新キーを追加:

```csharp
    /// <summary>.csv ファイルを開いたとき自動的に CSV モードにするか（開く系のみ・grep ジャンプ除外）。</summary>
    public bool CsvAutoModeOnOpen { get; set; } = false;

    /// <summary>タブ幅（桁数・範囲 1〜16）。表示と禁則整形の両方が使う。</summary>
    public int TabWidth { get; set; } = 4;
    /// <summary>Tab キー入力をスペースにするか（既存のタブ文字は変換しない）。</summary>
    public bool TabsToSpaces { get; set; } = false;

    /// <summary>行番号マージンを表示するか。</summary>
    public bool ShowLineNumbers { get; set; } = false;
    /// <summary>現在行を強調表示するか（色はテーマから自動算出）。</summary>
    public bool HighlightCurrentLine { get; set; } = false;
    /// <summary>キャレットの太さ（px・範囲 1〜5）。弱視のキャレット視認性対策。</summary>
    public int CaretWidth { get; set; } = 1;
    /// <summary>空白（全半角）と改行記号を可視化するか。</summary>
    public bool ShowWhitespace { get; set; } = false;

    /// <summary>起動時にバックアップを復元するか確認する（false なら確認なしで全復元）。</summary>
    public bool ConfirmRestoreOnStartup { get; set; } = true;

    /// <summary>優先するスクリーンリーダー（"nvda" | "pctalker"）。反映は再起動後。</summary>
    public string PreferredScreenReader { get; set; } = "nvda";
```

**Step 4: テストが通ることを確認**

Run: `dotnet test yEdit.sln --filter "FullyQualifiedName~AppSettingsTests"`
Expected: PASS（4 テスト）

`Clone()` は `MemberwiseClone` ベースなので新キー（値型と string）は自動で複製される。コード変更は不要。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Settings/AppSettings.cs tests/yEdit.Core.Tests/Settings/AppSettingsTests.cs
git commit -m "設定拡張: AppSettings に新キー 9 種を追加（バックアップ間隔の既定 30→300）"
```

---

### Task 2: SettingsStore.Normalize の健全化（テストファースト）

**Files:**
- Modify: `tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs`
- Modify: `src/yEdit.Core/Settings/SettingsStore.cs`

**Step 1: 失敗するテストを書く**

`SettingsStoreTests.cs` の class 末尾に追加（既存テストの try/finally パターンを踏襲）:

```csharp
    [Fact]
    public void Load_normalizes_new_keys_out_of_range()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path,
                "{\"TabWidth\":0,\"CaretWidth\":99,\"PreferredScreenReader\":\"jaws\"}");
            var s = SettingsStore.Load(path);
            Assert.Equal(4, s.TabWidth);                    // 範囲外→既定
            Assert.Equal(1, s.CaretWidth);                  // 範囲外→既定
            Assert.Equal("nvda", s.PreferredScreenReader);  // 未知値→既定
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_normalizes_null_preferred_screen_reader()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"PreferredScreenReader\":null}");
            var s = SettingsStore.Load(path);
            Assert.Equal("nvda", s.PreferredScreenReader);  // 明示 null→既定
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_preserves_valid_new_keys()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path,
                "{\"TabWidth\":8,\"CaretWidth\":5,\"PreferredScreenReader\":\"pctalker\"," +
                "\"CsvAutoModeOnOpen\":true,\"TabsToSpaces\":true,\"ShowLineNumbers\":true," +
                "\"HighlightCurrentLine\":true,\"ShowWhitespace\":true,\"ConfirmRestoreOnStartup\":false}");
            var s = SettingsStore.Load(path);
            Assert.Equal(8, s.TabWidth);
            Assert.Equal(5, s.CaretWidth);
            Assert.Equal("pctalker", s.PreferredScreenReader);
            Assert.True(s.CsvAutoModeOnOpen);
            Assert.True(s.TabsToSpaces);
            Assert.True(s.ShowLineNumbers);
            Assert.True(s.HighlightCurrentLine);
            Assert.True(s.ShowWhitespace);
            Assert.False(s.ConfirmRestoreOnStartup);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

**Step 2: 失敗を確認**

Run: `dotnet test yEdit.sln --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: `Load_normalizes_new_keys_out_of_range` と `Load_normalizes_null_preferred_screen_reader` が FAIL（Normalize 未実装のため 0/99/"jaws"/null がそのまま返る）。`Load_preserves_valid_new_keys` は PASS してよい。

**Step 3: 最小実装**

`SettingsStore.cs` の `Normalize` 内、`s.WrapColumn = WrapGeometry.ClampColumns(s.WrapColumn);` の**直前**に追加:

```csharp
        if (s.TabWidth is < 1 or > 16) s.TabWidth = def.TabWidth;
        if (s.CaretWidth is < 1 or > 5) s.CaretWidth = def.CaretWidth;
        // 未知値・明示 null とも既定へ（"nvda" / "pctalker" のみ有効）。
        if (s.PreferredScreenReader is not ("nvda" or "pctalker")) s.PreferredScreenReader = def.PreferredScreenReader;
```

**Step 4: テストが通ることを確認**

Run: `dotnet test yEdit.sln --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: PASS（全 13 テスト）

**Step 5: コミット**

```bash
git add src/yEdit.Core/Settings/SettingsStore.cs tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs
git commit -m "設定拡張: SettingsStore.Normalize に新キーの健全化を追加"
```

---

### Task 3: SrRoute / SrRouteSelector 新設（Core・テストファースト）

既存 `SrSpeechSelector` はこのタスクでは**削除しない**（App が参照中のためビルドを緑に保つ）。削除は Task 4。

**Files:**
- Create: `src/yEdit.Core/Speech/SrRoute.cs`
- Create: `src/yEdit.Core/Speech/SrRouteSelector.cs`
- Create: `tests/yEdit.Core.Tests/Speech/SrRouteSelectorTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Speech/SrRouteSelectorTests.cs`:

```csharp
using yEdit.Core.Speech;

namespace yEdit.Core.Tests.Speech;

/// <summary>
/// 決定表（設計 2026-07-04 §読み上げタブ）:
/// 優先 SR が稼働 or どちらも非稼働 → 優先 SR の経路。もう片方のみ稼働 → 検出された方（救済）。
/// </summary>
public class SrRouteSelectorTests
{
    [Theory]
    // 優先 = NVDA（既定）
    [InlineData(true,  true,  false, SrRoute.Nvda)]     // NVDA のみ → NVDA（現行同）
    [InlineData(true,  false, true,  SrRoute.PcTalker)] // PC-Talker のみ → PC-Talker（救済・現行同）
    [InlineData(true,  true,  true,  SrRoute.Nvda)]     // 両方 → 優先 NVDA（現行同）
    [InlineData(true,  false, false, SrRoute.Nvda)]     // どちらも非稼働 → NVDA（後から起動に対応）
    // 優先 = PC-Talker
    [InlineData(false, true,  false, SrRoute.Nvda)]     // NVDA のみ → NVDA（救済）
    [InlineData(false, false, true,  SrRoute.PcTalker)] // PC-Talker のみ → PC-Talker
    [InlineData(false, true,  true,  SrRoute.PcTalker)] // 両方 → 優先 PC-Talker（設定が勝つ・新規挙動）
    [InlineData(false, false, false, SrRoute.PcTalker)] // どちらも非稼働 → PC-Talker（後から起動に対応）
    public void Select_resolves_route(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning, SrRoute expected)
        => Assert.Equal(expected, SrRouteSelector.Select(preferNvda, nvdaRunning, pcTalkerRunning));
}
```

**Step 2: 失敗を確認**

Run: `dotnet test yEdit.sln --filter "FullyQualifiedName~SrRouteSelectorTests"`
Expected: コンパイルエラー（`SrRoute` / `SrRouteSelector` 未定義）

**Step 3: 最小実装**

`src/yEdit.Core/Speech/SrRoute.cs`:

```csharp
namespace yEdit.Core.Speech;

/// <summary>
/// 起動時に確定する読み上げ経路。受動読み（UIA プロバイダ可否 = ScintillaHost.ApplySrAdaptation）と
/// 能動発声（Announcer 選択）は常にペアで同じ経路に従う。
/// </summary>
public enum SrRoute
{
    /// <summary>NVDA 経路: UIA プロバイダを出さずネイティブ Scintilla 読みに任せ、能動発声は UIA 通知。</summary>
    Nvda,
    /// <summary>PC-Talker 経路: 自前 UIA プロバイダ提供＋ネイティブ MSAA 抑制、能動発声は PCTKPReadW 直叩き。</summary>
    PcTalker,
}
```

`src/yEdit.Core/Speech/SrRouteSelector.cs`:

```csharp
namespace yEdit.Core.Speech;

/// <summary>
/// 「優先するスクリーンリーダー」設定と起動時のプロセス検出から読み上げ経路を選ぶ純ロジック
/// （WinForms 非依存・単体テスト可能）。判定は App 層の SrContext が起動時に 1 回行う。
/// 規則（検出フォールバック付き・設計 2026-07-04）:
/// 優先 SR が稼働している、またはどちらも稼働していない → 優先 SR の経路。
/// もう片方だけが稼働 → 検出された方の経路（既定 NVDA のままの PC-Talker ユーザーを壊さない救済）。
/// </summary>
public static class SrRouteSelector
{
    public static SrRoute Select(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning)
    {
        if (preferNvda)
            return (!nvdaRunning && pcTalkerRunning) ? SrRoute.PcTalker : SrRoute.Nvda;
        return (nvdaRunning && !pcTalkerRunning) ? SrRoute.Nvda : SrRoute.PcTalker;
    }
}
```

**Step 4: テストが通ることを確認**

Run: `dotnet test yEdit.sln --filter "FullyQualifiedName~SrRouteSelectorTests"`
Expected: PASS（8 ケース）

**Step 5: コミット**

```bash
git add src/yEdit.Core/Speech/SrRoute.cs src/yEdit.Core/Speech/SrRouteSelector.cs tests/yEdit.Core.Tests/Speech/SrRouteSelectorTests.cs
git commit -m "設定拡張: SrRouteSelector（優先SR＋検出フォールバックの純ロジック）を追加"
```

---

### Task 4: 起動順変更と SrContext の経路化（SrSpeechSelector 削除）

**Files:**
- Modify: `src/yEdit.App/Program.cs`
- Modify: `src/yEdit.App/Speech/SrContext.cs`（全面書き換え）
- Modify: `src/yEdit.App/MainForm.cs:39-41`（コンストラクタ）と `:98`（CreateEditor）
- Modify: `src/yEdit.Editor/ScintillaHost.cs:93-97`（ApplySrAdaptation の引数名）
- Delete: `src/yEdit.Core/Speech/SrSpeechSelector.cs`
- Delete: `tests/yEdit.Core.Tests/Speech/SrSpeechSelectorTests.cs`

**Step 1: Program.cs を書き換える**

設定読込を Main へ前倒しし、優先 SR を判定へ渡す:

```csharp
using yEdit.App.Speech;
using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Shift_JIS/EUC-JP を使うため CodePagesEncodingProvider を登録（Core も内部登録するが明示）。
        EncodingCatalog.EnsureRegistered();
        // 設定を先に読み、「優先するスクリーンリーダー」を SR 判定へ渡す（起動時確定方針・読込は起動で1回だけ）。
        var settings = SettingsStore.Load(SettingsStore.DefaultPath);
        SrContext.Detect(preferNvda: settings.PreferredScreenReader != "pctalker");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(settings));
    }
}
```

**Step 2: SrContext.cs を Route ベースへ書き換える**

```csharp
using System.Diagnostics;
using yEdit.Core.Speech;

namespace yEdit.App.Speech;

/// <summary>
/// 起動時に一度だけ確定する SR 経路。受動読み（UIA プロバイダの提供可否 =
/// <see cref="yEdit.Editor.ScintillaHost.ApplySrAdaptation"/>）と能動通知（発声モード =
/// <see cref="AnnouncerFactory"/>）の両経路が、ここで確定した同じ <see cref="Route"/> を消費する。
/// 規則（検出フォールバック付き・設計 2026-07-04）: 優先 SR が稼働 or どちらも非稼働 → 優先 SR の経路。
/// もう片方のみ稼働 → 検出された方の経路（救済）。純ロジックは Core の SrRouteSelector。
/// 起動後の SR 起動/終了には追従しない（起動時確定方針）。「優先するスクリーンリーダー」の変更は再起動後に有効。
/// <see cref="Detect"/> は Program.Main が UI 開始前に1回だけ呼ぶ。
/// </summary>
internal static class SrContext
{
    /// <summary>確定済みの読み上げ経路（未検出時は無害な既定 = NVDA 経路）。</summary>
    public static SrRoute Route { get; private set; } = SrRoute.Nvda;

    /// <summary>受動読みをネイティブ Scintilla に任せるか（ScintillaHost.ApplySrAdaptation へ渡す）。</summary>
    public static bool UseNativeReading => Route == SrRoute.Nvda;

    /// <summary>確定済みの発声モード（AnnouncerFactory・空行発声の判定が消費）。</summary>
    public static SpeechMode Mode => Route == SrRoute.PcTalker ? SpeechMode.PcTalker : SpeechMode.Uia;

    /// <summary>SR 経路を判定して確定する。Program.Main の UI 開始前に1回だけ呼ぶこと。</summary>
    public static void Detect(bool preferNvda)
        => Route = SrRouteSelector.Select(preferNvda, IsNvdaRunning(), PcTalkerSpeech.IsRunning());

    /// <summary>NVDA 本体プロセスが動いているか。判定の要は「NVDA が動いているか」だけ。</summary>
    private static bool IsNvdaRunning()
    {
        try { return Process.GetProcessesByName("nvda").Length > 0; }
        catch { return false; }
    }
}
```

**Step 3: MainForm を追従させる**

コンストラクタ（`MainForm.cs:39-41`）— 引数で受け取り、二重読込を廃止:

```csharp
    public MainForm(AppSettings settings)
    {
        _settings = settings;   // Program.Main が読込済み（優先 SR を SR 判定へ渡すため先読みしている）
```

`CreateEditor`（`MainForm.cs:98`）:

```csharp
        e.ApplySrAdaptation(useNativeReading: SrContext.UseNativeReading); // ハンドル生成前に起動時確定の SR 適応を反映
```

`MainForm.cs:57` の `SrContext.Mode == SpeechMode.PcTalker` は導出プロパティ経由で**無変更のまま動く**。

**Step 4: ScintillaHost.ApplySrAdaptation の引数名を実態に合わせる**

`ScintillaHost.cs:93-97`（意味が「NVDA が稼働しているか」から「ネイティブ読みに任せるか」へ変わったため）:

```csharp
    /// <summary>
    /// 起動時に確定した SR 経路を UIA/MSAA の提供可否へ反映する（確定アーキテクチャ）。
    /// ネイティブ読み（NVDA 経路）→ 我々は引っ込む。それ以外（PC-Talker 経路）→ UIA 提供。
    /// 判定は App 層（SrContext）が起動時に1回だけ行い、全タブへ同じ値を渡す（タブ間一貫）。
    /// ハンドル生成前に呼ぶこと（WM_GETOBJECT 前に値を確定させる）。
    /// </summary>
    public void ApplySrAdaptation(bool useNativeReading)
    {
        ServeUiaProvider = !useNativeReading;
        SuppressClientMsaa = useNativeReading;
    }
```

クラス内の関連コメント（`ScintillaHost.cs:73` / `:80` の `nvdaRunning` 言及）も「ネイティブ読み（NVDA 経路）」表現に合わせて更新する。

**Step 5: 旧セレクタを削除**

```bash
git rm src/yEdit.Core/Speech/SrSpeechSelector.cs tests/yEdit.Core.Tests/Speech/SrSpeechSelectorTests.cs
```

**Step 6: ビルドと全テスト**

Run: `dotnet build yEdit.sln; if ($?) { dotnet test yEdit.sln }`
Expected: ビルド 0 警告・全テスト PASS（SrSpeechSelectorTests の 4 件は削除済み、SrRouteSelectorTests の 8 件が代替）

**Step 7: コミット**

```bash
git add -A
git commit -m "設定拡張: 優先SRを起動時SR判定へ配線（設定読込をMainへ前倒し・SrSpeechSelector撤去）"
```

---

### Task 5: ScintillaHost に行番号マージン表示を追加

**Files:**
- Modify: `src/yEdit.Editor/ScintillaHost.cs`

**Step 1: フィールドとプロパティを追加**

「表示折り返し」フィールド群（`ScintillaHost.cs:38-40` 付近）の直後に:

```csharp
    // ---- 行番号マージン（表示設定・UI スレッド専用） ----
    private bool _showLineNumbers;
    private int _lineNumberDigits;   // 確保済みの桁数（変化時のみ幅を再計算）
```

`ApplyWrapColumn` の直前（「表示折り返し」セクションの手前）にプロパティと更新メソッドを追加:

```csharp
    // ==================== 行番号マージン（表示設定・UI スレッド専用） ====================

    /// <summary>
    /// 行番号マージンの表示。ON では行数の桁数に応じて幅を自動調整する（本文変更時に追従）。
    /// 配色は STYLE_LINENUMBER が StyleClearAll で既定スタイルから伝播するためテーマに追従する。
    /// 設定のたびに幅を再計測するので、テーマ/フォント適用（StyleClearAll）の後に設定すること。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set
        {
            _showLineNumbers = value;
            _lineNumberDigits = 0;   // フォント/テーマ変更後の再適用でも幅を測り直す
            UpdateLineNumberMargin();
        }
    }

    /// <summary>行番号マージン幅を桁数に合わせて更新する（OFF は幅 0）。桁数が変わったときだけ再計測する。</summary>
    private void UpdateLineNumberMargin()
    {
        if (!_showLineNumbers)
        {
            if (Margins[0].Width != 0)
            {
                Margins[0].Width = 0;
                RecomputeWrapMargin();   // 左マージン群の幅変化を折返し右マージンへ反映
            }
            return;
        }
        int digits = Math.Max(3, Lines.Count.ToString().Length);   // 最低 3 桁分を確保（幅の頻繁な伸縮を避ける）
        if (digits == _lineNumberDigits) return;
        _lineNumberDigits = digits;
        Margins[0].Type = MarginType.Number;
        Margins[0].Width = TextWidth(Style.LineNumber, new string('9', digits)) + 4;
        RecomputeWrapMargin();
    }
```

**Step 2: 本文変更で桁数に追従させる**

`OnTextChangedEvt`（`ScintillaHost.cs:245-250`）へ 1 行追加:

```csharp
    private void OnTextChangedEvt(object? sender, EventArgs e)
    {
        RefreshSnapshot();
        RefreshSelection();
        if (_showLineNumbers) UpdateLineNumberMargin();   // 行数の桁数変化に追従（変化時のみ幅再計算）
        RaiseUia(TextPatternIdentifiers.TextChangedEvent);
    }
```

**Step 3: ビルド確認**

Run: `dotnet build yEdit.sln`
Expected: 0 警告（Editor 層は単体テストなし・手動検証は Task 13）

**Step 4: コミット**

```bash
git add src/yEdit.Editor/ScintillaHost.cs
git commit -m "設定拡張: ScintillaHost に行番号マージン表示（桁数自動追従）を追加"
```

---

### Task 6: EditorAppearance.Apply の拡張（タブ・キャレット・強調・可視化・行番号）

**Files:**
- Modify: `src/yEdit.App/EditorAppearance.cs`

**Step 1: Apply を拡張**

`EditorAppearance.cs` の `Apply` を以下へ（`ApplyWrapColumn` は**最後のまま**。行番号は StyleClearAll の後・ApplyWrapColumn の前）:

```csharp
    public static void Apply(ScintillaHost ed, AppSettings settings)
    {
        var theme = AppearanceThemes.ById(settings.Theme);
        Color fore = FromRgb(theme.ForeRgb);
        Color back = FromRgb(theme.BackRgb);

        var def = ed.Styles[Style.Default];
        def.Font = settings.FontName;
        def.SizeF = settings.FontSize > 0 ? settings.FontSize : 12f; // 破損設定で不可視にしない
        def.ForeColor = fore;
        def.BackColor = back;
        ed.StyleClearAll();          // 既定スタイルを全スタイルへ伝播（配色を一律に）
        ed.CaretForeColor = fore;    // キャレットも前景色に合わせて視認性を保つ
        // 選択範囲は前景/背景を反転して高コントラストにする（弱視で選択を視認しやすく）。
        ed.SelectionTextColor = back;
        ed.SelectionBackColor = fore;

        // タブ・キャレット・空白可視化（設定 2026-07-04）。
        ed.TabWidth = settings.TabWidth;
        ed.UseTabs = !settings.TabsToSpaces;   // 新規 Tab 入力にのみ効く（既存のタブ文字は変換しない）
        ed.CaretWidth = Math.Clamp(settings.CaretWidth, 1, 5);
        ed.CaretLineVisible = settings.HighlightCurrentLine;
        if (settings.HighlightCurrentLine)
            ed.CaretLineBackColor = Blend(back, fore, 0.12);   // テーマから自動算出（カスタム色 UI なし・設計合意）
        ed.ViewWhitespace = settings.ShowWhitespace ? WhitespaceMode.VisibleAlways : WhitespaceMode.Invisible;
        ed.ViewEol = settings.ShowWhitespace;

        // 行番号は StyleClearAll の後（行番号スタイル確定後に幅を測る）・折り返しの前
        // （折返し右マージンの計算が左マージン群の幅を含むため）。
        ed.ShowLineNumbers = settings.ShowLineNumbers;

        // 表示折り返し（指定桁・本文不変）。フォント適用後に半角幅を測るためここで最後に呼ぶ。
        ed.ApplyWrapColumn(settings.WrapColumnEnabled ? settings.WrapColumn : 0);
    }

    /// <summary>base に accent を ratio(0..1) だけ混ぜた色。現在行強調の自動算出用（全 4 テーマで破綻しない淡い強調）。</summary>
    private static Color Blend(Color baseColor, Color accent, double ratio) => Color.FromArgb(255,
        (int)Math.Round(baseColor.R + (accent.R - baseColor.R) * ratio),
        (int)Math.Round(baseColor.G + (accent.G - baseColor.G) * ratio),
        (int)Math.Round(baseColor.B + (accent.B - baseColor.B) * ratio));
```

**Step 2: ビルド確認**

Run: `dotnet build yEdit.sln`
Expected: 0 警告

**Step 3: コミット**

```bash
git add src/yEdit.App/EditorAppearance.cs
git commit -m "設定拡張: EditorAppearance にタブ幅/キャレット/現在行強調/空白可視化/行番号を配線"
```

---

### Task 7: 禁則整形のタブ幅連動

**Files:**
- Modify: `src/yEdit.App/MainForm.cs:475-478`（FormatWithKinsoku）

**Step 1: タブ幅を渡す**

`FormatWithKinsoku` の `KinsokuFormatter.Format` 呼び出しへ最終引数を追加:

```csharp
        string formatted = KinsokuFormatter.Format(
            target, _settings.WrapColumn,
            _settings.KinsokuLineStartChars, _settings.KinsokuLineEndChars, _settings.KinsokuHangChars,
            eol, _settings.TabWidth);   // タブ幅は表示設定と連動（画面の見た目どおりに整形する。従来は既定 8 固定）
```

`KinsokuFormatter.Format` は `tabWidth` パラメータ実装・テスト済み（`KinsokuFormatter.cs:21`）。Core 変更なし。

**Step 2: ビルドとテスト**

Run: `dotnet build yEdit.sln; if ($?) { dotnet test yEdit.sln --filter "FullyQualifiedName~KinsokuFormatterTests" }`
Expected: ビルド 0 警告・PASS

**Step 3: コミット**

```bash
git add src/yEdit.App/MainForm.cs
git commit -m "設定拡張: 禁則整形のタブ幅を設定値に連動（従来は 8 固定）"
```

---

### Task 8: 既存 3 タブへの項目追加（基本・編集・表示）

**Files:**
- Modify: `src/yEdit.App/Settings/Tabs/BasicSettingsTab.cs`
- Modify: `src/yEdit.App/Settings/Tabs/EditSettingsTab.cs`
- Modify: `src/yEdit.App/Settings/Tabs/DisplaySettingsTab.cs`

**Step 1: BasicSettingsTab に CSV 自動モードを追加**

フィールド追加（`_eol` の下）:

```csharp
    private readonly CheckBox _csvAutoMode = new()
    {
        Text = ".csvファイルを開いたとき自動的にCSVモードにする(&V)", AutoSize = true,
    };
```

`BuildPage` の `return root;` 直前に追加:

```csharp
        _csvAutoMode.TabIndex = 4;
        root.Controls.Add(_csvAutoMode, 0, 2);
        root.SetColumnSpan(_csvAutoMode, 2);
```

`LoadFrom` 末尾に `_csvAutoMode.Checked = s.CsvAutoModeOnOpen;`、`SaveTo` 末尾に `r.CsvAutoModeOnOpen = _csvAutoMode.Checked;` を追加。

**Step 2: EditSettingsTab にタブ幅・タブ→スペースを追加**

フィールド追加:

```csharp
    private readonly NumericUpDown _tabWidth = new()
    {
        Minimum = 1, Maximum = 16, Width = 100, AccessibleName = "タブ幅",
    };
    private readonly CheckBox _tabsToSpaces = new() { Text = "タブをスペースに変換(&S)", AutoSize = true };
```

`BuildPage` の `return root;` 直前に追加（既存の折り返し行 = row 0 の下へ。行レイアウトは既存 wrapPanel のパターンを踏襲）:

```csharp
        // 2 行目: 「タブ幅(&T):」ラベル ＋ NumericUpDown。
        var tabPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 3 };
        var tabLbl = new Label { Text = "タブ幅(&T):", AutoSize = true, TabIndex = 3, Anchor = AnchorStyles.Left };
        _tabWidth.TabIndex = 4;
        tabPanel.Controls.Add(tabLbl);
        tabPanel.Controls.Add(_tabWidth);
        root.Controls.Add(tabPanel, 0, 1);

        // 3 行目: タブ→スペース変換（新規 Tab 入力にのみ効く）。
        _tabsToSpaces.TabIndex = 5;
        root.Controls.Add(_tabsToSpaces, 0, 2);
```

`LoadFrom` 末尾:

```csharp
        _tabWidth.Value = Math.Clamp(s.TabWidth, (int)_tabWidth.Minimum, (int)_tabWidth.Maximum);
        _tabsToSpaces.Checked = s.TabsToSpaces;
```

`SaveTo` 末尾:

```csharp
        r.TabWidth = (int)_tabWidth.Value;
        r.TabsToSpaces = _tabsToSpaces.Checked;
```

**Step 3: DisplaySettingsTab に視覚系 4 項目を追加**

フィールド追加（`_theme` の下）:

```csharp
    private readonly CheckBox _showLineNumbers = new() { Text = "行番号を表示する(&N)", AutoSize = true };
    private readonly CheckBox _highlightCurrentLine = new() { Text = "現在行を強調表示する(&H)", AutoSize = true };
    private readonly NumericUpDown _caretWidth = new()
    {
        Minimum = 1, Maximum = 5, Width = 100, AccessibleName = "キャレットの太さ",
    };
    private readonly CheckBox _showWhitespace = new() { Text = "空白・改行文字を表示する(&B)", AutoSize = true };
```

`BuildPage` の `return root;` 直前に追加（既存 TabIndex は 0〜3 のため 4 以降で採番）:

```csharp
        _showLineNumbers.TabIndex = 4;
        root.Controls.Add(_showLineNumbers, 0, 2);
        root.SetColumnSpan(_showLineNumbers, 2);

        _highlightCurrentLine.TabIndex = 5;
        root.Controls.Add(_highlightCurrentLine, 0, 3);
        root.SetColumnSpan(_highlightCurrentLine, 2);

        // キャレットの太さ: ラベル ＋ NumericUpDown（px・弱視の視認性対策）。
        var caretPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 6 };
        var caretLbl = new Label { Text = "キャレットの太さ(&W):", AutoSize = true, TabIndex = 6, Anchor = AnchorStyles.Left };
        _caretWidth.TabIndex = 7;
        caretPanel.Controls.Add(caretLbl);
        caretPanel.Controls.Add(_caretWidth);
        root.Controls.Add(caretPanel, 0, 4);

        _showWhitespace.TabIndex = 8;
        root.Controls.Add(_showWhitespace, 0, 5);
        root.SetColumnSpan(_showWhitespace, 2);
```

`LoadFrom` 末尾:

```csharp
        _showLineNumbers.Checked = s.ShowLineNumbers;
        _highlightCurrentLine.Checked = s.HighlightCurrentLine;
        _caretWidth.Value = Math.Clamp(s.CaretWidth, (int)_caretWidth.Minimum, (int)_caretWidth.Maximum);
        _showWhitespace.Checked = s.ShowWhitespace;
```

`SaveTo` 末尾:

```csharp
        r.ShowLineNumbers = _showLineNumbers.Checked;
        r.HighlightCurrentLine = _highlightCurrentLine.Checked;
        r.CaretWidth = (int)_caretWidth.Value;
        r.ShowWhitespace = _showWhitespace.Checked;
```

**Step 4: ビルド確認**

Run: `dotnet build yEdit.sln`
Expected: 0 警告

**Step 5: コミット**

```bash
git add src/yEdit.App/Settings/Tabs/BasicSettingsTab.cs src/yEdit.App/Settings/Tabs/EditSettingsTab.cs src/yEdit.App/Settings/Tabs/DisplaySettingsTab.cs
git commit -m "設定拡張: 基本/編集/表示タブへ新項目の UI を追加"
```

---

### Task 9: バックアップ・読み上げタブの新設と 6 タブ化

**Files:**
- Create: `src/yEdit.App/Settings/Tabs/BackupSettingsTab.cs`
- Create: `src/yEdit.App/Settings/Tabs/SpeechSettingsTab.cs`
- Modify: `src/yEdit.App/Settings/SettingsDialog.cs:26-32`（_tabs 配列）

**Step 1: BackupSettingsTab.cs を作成**

```csharp
using yEdit.Core.Settings;

namespace yEdit.App.Settings.Tabs;

/// <summary>「バックアップ」タブ。自動バックアップの有効/間隔と起動時復元の確認有無を扱う。</summary>
public sealed class BackupSettingsTab : ISettingsTab
{
    public string Title => "バックアップ";

    private readonly CheckBox _enabled = new() { Text = "文書のバックアップを有効にする(&B)", AutoSize = true };
    private readonly NumericUpDown _interval = new()
    {
        Minimum = 5, Maximum = 3600, Width = 100, AccessibleName = "バックアップ間隔（秒）",
    };
    private readonly CheckBox _confirmRestore = new()
    {
        Text = "起動時にバックアップを復元するか確認する(&C)", AutoSize = true,
    };

    public Control BuildPage()
    {
        _enabled.CheckedChanged += (_, _) => _interval.Enabled = _enabled.Checked;

        var root = SettingsTabLayoutHelper.NewRoot();

        // 1 行目: 有効チェック（ラベル兼用）。
        _enabled.TabIndex = 0;
        root.Controls.Add(_enabled, 0, 0);
        root.SetColumnSpan(_enabled, 2);

        // 2 行目: 「バックアップ間隔（秒）(&I):」ラベル ＋ NumericUpDown。
        var intervalPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = 1 };
        var intervalLbl = new Label { Text = "バックアップ間隔（秒）(&I):", AutoSize = true, TabIndex = 1, Anchor = AnchorStyles.Left };
        _interval.TabIndex = 2;
        intervalPanel.Controls.Add(intervalLbl);
        intervalPanel.Controls.Add(_interval);
        root.Controls.Add(intervalPanel, 0, 1);
        root.SetColumnSpan(intervalPanel, 2);

        // 3 行目: 復元確認（OFF は確認なしで全復元）。
        _confirmRestore.TabIndex = 3;
        root.Controls.Add(_confirmRestore, 0, 2);
        root.SetColumnSpan(_confirmRestore, 2);

        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _enabled.Checked = s.BackupEnabled;
        _interval.Value = Math.Clamp(s.BackupIntervalSeconds, (int)_interval.Minimum, (int)_interval.Maximum);
        _interval.Enabled = _enabled.Checked;   // 初期状態でも ON/OFF を反映
        _confirmRestore.Checked = s.ConfirmRestoreOnStartup;
    }

    public void SaveTo(AppSettings r)
    {
        r.BackupEnabled = _enabled.Checked;
        r.BackupIntervalSeconds = (int)_interval.Value;
        r.ConfirmRestoreOnStartup = _confirmRestore.Checked;
    }
}
```

**Step 2: SpeechSettingsTab.cs を作成**

```csharp
using yEdit.Core.Settings;

namespace yEdit.App.Settings.Tabs;

/// <summary>「読み上げ」タブ。優先するスクリーンリーダー（反映は再起動後）を扱う。</summary>
public sealed class SpeechSettingsTab : ISettingsTab
{
    public string Title => "読み上げ";

    // 表示順とインデックスを対応させる（0=NVDA, 1=PC-Talker）。Id は AppSettings.PreferredScreenReader の値。
    private static readonly (string Name, string Id)[] Readers =
    {
        ("NVDA（既定）", "nvda"), ("PC-Talker", "pctalker"),
    };

    private readonly ComboBox _preferred = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "優先するスクリーンリーダー",
    };

    public Control BuildPage()
    {
        foreach (var (name, _) in Readers) _preferred.Items.Add(name);

        var root = SettingsTabLayoutHelper.NewRoot();
        SettingsTabLayoutHelper.AddRow(root, 0, "優先するスクリーンリーダー(&R):", _preferred, tabBase: 0);

        // 反映は再起動後（起動時確定方針）。変更時の能動通知は MainForm.OpenSettings が行う。
        var note = new Label { Text = "この設定は yEdit の再起動後に有効になります。", AutoSize = true, TabIndex = 2 };
        root.Controls.Add(note, 0, 1);
        root.SetColumnSpan(note, 2);
        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        int sel = 0;
        for (int i = 0; i < Readers.Length; i++)
            if (Readers[i].Id == s.PreferredScreenReader) { sel = i; break; }
        _preferred.SelectedIndex = sel;
    }

    public void SaveTo(AppSettings r) => r.PreferredScreenReader = Readers[_preferred.SelectedIndex].Id;
}
```

**Step 3: SettingsDialog の _tabs へ 2 行追加**

`SettingsDialog.cs:26-32`:

```csharp
        _tabs = new ISettingsTab[]
        {
            new BasicSettingsTab(),
            new EditSettingsTab(),
            new KinsokuSettingsTab(),
            new DisplaySettingsTab(),
            new BackupSettingsTab(),
            new SpeechSettingsTab(),
        };
```

**Step 4: ビルド確認**

Run: `dotnet build yEdit.sln`
Expected: 0 警告

**Step 5: コミット**

```bash
git add src/yEdit.App/Settings/Tabs/BackupSettingsTab.cs src/yEdit.App/Settings/Tabs/SpeechSettingsTab.cs src/yEdit.App/Settings/SettingsDialog.cs
git commit -m "設定拡張: バックアップ/読み上げタブを新設し 6 タブ構成に"
```

---

### Task 10: CsvController の進入ロジック抽出（挙動不変のリファクタ）

**Files:**
- Modify: `src/yEdit.App/CsvController.cs:38-96`（ToggleMode）

**Step 1: ToggleMode を分割**

既存 `ToggleMode` を薄いディスパッチにし、ON 側を `TryEnterMode`、OFF 側を `ExitMode` へ**中身は一字一句そのまま**移す:

```csharp
    /// <summary>CSVモードを手動でトグルする。ON 時は読取専用化＋現在セルを確定して読み上げ。</summary>
    public void ToggleMode()
    {
        var doc = _docs.Active;
        if (doc is null || _editor.IsEditing) return;
        if (!doc.State.CsvMode) TryEnterMode(doc);
        else ExitMode(doc);
    }

    /// <summary>
    /// CSVモードへ入る（手動トグルと .csv 自動モードの共通経路）。解析不可なら通知して false を返し、
    /// 通常モードのまま残す。読取専用化・UIA 抑止・シンク退避・初期セル確定・読み上げは従来の ON 側と同一。
    /// </summary>
    public bool TryEnterMode(Document doc)
    {
        if (doc.State.CsvMode || _editor.IsEditing) return false;
        var csv = doc.ParseCsv();
        if (!csv.Ok)
        {
            doc.ClearCsvCache(); // モードに入らないのに失敗パース＋旧全文を文書寿命まで抱えない
            _announcer.Say(CsvAnnounceFormatter.ParseError);
            return false; // 解析不可ならモードに入らない
        }
        doc.State.CsvMode = true;
        // …（以下、既存 ToggleMode の ON 側 = CsvController.cs:53-73 をそのまま移す。読み上げ含め無変更）…
        return true;
    }

    /// <summary>CSVモードを抜けて通常編集へ戻す（既存 OFF 側の移設・無変更）。</summary>
    private void ExitMode(Document doc)
    {
        // …（既存 ToggleMode の OFF 側 = CsvController.cs:77-94 をそのまま移す）…
    }
```

注意: ON 側最後の `_announcer.Say(...ModeOn...)` 2 箇所（0 行時の早期 return 含む）はそれぞれ `return true;` に変える以外は無変更。

**Step 2: ビルドとテスト**

Run: `dotnet build yEdit.sln; if ($?) { dotnet test yEdit.sln }`
Expected: ビルド 0 警告・全テスト PASS（挙動不変のリファクタ）

**Step 3: コミット**

```bash
git add src/yEdit.App/CsvController.cs
git commit -m "リファクタ: CsvController の CSV モード進入/退出を TryEnterMode/ExitMode へ抽出（挙動不変）"
```

---

### Task 11: .csv 自動 CSV モードの配線

**Files:**
- Modify: `src/yEdit.App/FileController.cs`（ctor・TryOpenOrActivate・ReopenWithEncoding）
- Modify: `src/yEdit.App/MainForm.cs`（_file 生成・AutoEnterCsvMode 新設・OpenAndSelect）

**Step 1: FileController にコールバックと抑止フラグを追加**

フィールド（`_metaChanged` の下）とコンストラクタ引数（末尾）に追加:

```csharp
    private readonly Action<Document> _openedFresh;  // 開く系で新規ロード成功した直後（.csv 自動モードの判定は MainForm 側）

    public FileController(
        DocumentManager docs, Form owner, Func<AppSettings> settings,
        Action saveSettings, Action recentChanged, Action metaChanged,
        Action<Document> openedFresh)
    {
        // …既存の代入…
        _openedFresh = openedFresh;
    }
```

`TryOpenOrActivate` を変更（`FileController.cs:71-82`）:

```csharp
    public Document? TryOpenOrActivate(string path, bool suppressAutoCsv = false)
    {
        var existing = _docs.FindByPath(path);
        if (existing is not null) { _docs.Activate(existing); RegisterRecent(path); return existing; }

        var prev = _docs.Active; // 読込失敗時に戻る先（直前のアクティブタブ）
        var doc = _docs.CreateNew();
        if (LoadInto(doc, path, forcedCodePage: null))
        {
            // 開く系（開く/最近）のみ .csv 自動モードの対象。grep ジャンプは選択＋エディタフォーカスを
            // 機能させるため suppressAutoCsv=true で抑止する（設計 2026-07-04）。
            if (!suppressAutoCsv) _openedFresh(doc);
            return doc;
        }
        _docs.TryClose(doc, _ => true); // 読込失敗→作りかけタブを破棄
        if (prev is not null) _docs.Activate(prev); // 直前のアクティブへ戻す
        return null;
    }
```

`ReopenWithEncoding` の成功後（`FileController.cs:97-100`）:

```csharp
        if (!LoadInto(doc, doc.State.Path, forcedCodePage: dlg.SelectedCodePage)) return;
        _openedFresh(doc);   // 開き直しも .csv 自動モードの対象（設計 2026-07-04）
        // 自動 CSV モードに入った場合は FocusTarget=シンク、入らなければエディタへ向く。
        doc.FocusTarget.Focus();
    }
```

**Step 2: MainForm を配線**

`_file` の生成（`MainForm.cs:61-62`）に引数を追加:

```csharp
        _file = new FileController(_docs, this, () => _settings,
            SaveSettingsSafe, RebuildRecentMenu, () => { UpdateTitle(); UpdateStatus(); },
            AutoEnterCsvMode);
```

（`_csv` は `_file` より後に生成されるが、コールバックはユーザー操作時にしか呼ばれないため参照解決は安全。）

`CreateEditor` の下あたりに新メソッドを追加:

```csharp
    /// <summary>開く系経路（開く/最近/開き直し）で新規ロードした直後の .csv 自動 CSV モード進入（設定 ON のときのみ）。</summary>
    private void AutoEnterCsvMode(Document doc)
    {
        if (!_settings.CsvAutoModeOnOpen) return;
        if (!string.Equals(System.IO.Path.GetExtension(doc.State.Path), ".csv", StringComparison.OrdinalIgnoreCase)) return;
        _csv.TryEnterMode(doc);   // 解析不可なら TryEnterMode が通知して通常モードのまま
    }
```

`OpenAndSelect`（`MainForm.cs:384`）を抑止付き呼び出しへ:

```csharp
        var doc = _file.TryOpenOrActivate(path, suppressAutoCsv: true);
```

**Step 3: ビルドとテスト**

Run: `dotnet build yEdit.sln; if ($?) { dotnet test yEdit.sln }`
Expected: ビルド 0 警告・全テスト PASS

**Step 4: コミット**

```bash
git add src/yEdit.App/FileController.cs src/yEdit.App/MainForm.cs
git commit -m "設定拡張: .csv 自動 CSV モード（開く系のみ・grep ジャンプ除外）を配線"
```

---

### Task 12: BackupCoordinator の設定即時反映と無確認復元

**Files:**
- Modify: `src/yEdit.App/BackupCoordinator.cs`
- Modify: `src/yEdit.App/MainForm.cs`（OnShown・OpenSettings）

**Step 1: フィールドの readonly を外しコンストラクタを再構成**

`BackupCoordinator.cs:26-28` — `_enabled` と `_writer` を可変に:

```csharp
    private bool _enabled;                       // UpdateSettings で実行時に切替可能
    private readonly System.Windows.Forms.Timer _timer = new();
    private SerialBackupWriter? _writer;         // 無効時はスレッドを生成しない（有効化時に遅延生成）
```

コンストラクタ（`:33-45`）— **無効時でもハンドラは購読しておく**（後から有効化できるように）。Tick/ActiveDocumentChanged は `Reconcile` 冒頭の `!_enabled` ガードで素通りするため無効中は無害:

```csharp
    public BackupCoordinator(DocumentManager docs, bool enabled, int intervalSeconds, string? directory = null)
    {
        _docs = docs;
        _enabled = enabled;
        _dir = directory ?? BackupStore.DefaultDirectory;

        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000; // 上限クランプで int オーバーフロー防止
        _timer.Tick += (_, _) => Reconcile();
        _docs.ActiveDocumentChanged += (_, _) => Reconcile();
        if (!_enabled) return;

        _writer = new SerialBackupWriter();
        _timer.Start();
    }
```

**Step 2: UpdateSettings を追加**

`OfferRestoreOnStartup` の直前に:

```csharp
    /// <summary>
    /// 設定ダイアログ OK 時の即時反映。間隔は常に更新し、有効/無効の切替では
    /// タイマーとライターを追従させる。無効化では既存バックアップファイルを削除しない
    /// （次回起動時の孤児提案に任せる・安全側）。
    /// </summary>
    public void UpdateSettings(bool enabled, int intervalSeconds)
    {
        if (_shutDown) return;
        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000;
        if (enabled == _enabled) return;

        _enabled = enabled;
        if (enabled)
        {
            _writer ??= new SerialBackupWriter();
            _timer.Start();
            Reconcile();   // 有効化した瞬間の未保存文書を即保護（保護窓を作らない）
        }
        else
        {
            _timer.Stop();
        }
    }
```

**Step 3: Shutdown のガードを `_shutDown` のみへ**

`Shutdown()`（`:188-197`）の先頭 `if (!_enabled || _shutDown) return;` を `if (_shutDown) return;` に変更する。
理由: セッション途中で無効化された場合でも、有効だった間に書いたバックアップ（`_map` の `HasBackup`）をクリーン終了で削除するため（従来は実行時無効化が存在しなかったので `!_enabled` ガードで十分だった）。一度も有効になっていなければ `_map` は空・`_writer` は null で各行は無害に素通りする。

**Step 4: OfferRestoreOnStartup に無確認復元を追加**

シグネチャを変更し、無確認復元の件数を返す（ダイアログ経路は 0 を返す）:

```csharp
    public int OfferRestoreOnStartup(IWin32Window owner, Func<BackupRecord, Document> restore, bool confirm)
    {
        if (!_enabled) return 0;
        try { BackupStore.SweepTempFiles(_dir); } catch { /* 残骸掃除失敗は無害 */ }

        IReadOnlyList<BackupRecord> records;
        try { records = BackupStore.LoadAll(_dir); }
        catch { return 0; }
        if (records.Count == 0) return 0;

        var ordered = records.OrderByDescending(r => r.TimestampUtc).ToList();

        // 確認 OFF: ダイアログを出さず全件復元（設計 2026-07-04）。呼び出し側が件数を能動通知する。
        if (!confirm)
        {
            int restored = 0;
            foreach (var rec in ordered)
            {
                try
                {
                    var doc = restore(rec);
                    _map[doc] = new DocBackup { Id = rec.Id, LastSig = ContentSignature.Of(doc.Editor.SnapshotText), HasBackup = true };
                    restored++;
                }
                catch
                {
                    // 1 件の不正レコードで全復元を巻き添えにしない。失敗分はバックアップを残し再挑戦可能に。
                }
            }
            return restored;
        }

        using var dlg = new RestoreDialog(ordered);
        // …（以下、既存のダイアログ経路をそのまま。switch の後に `return 0;` を追加）…
    }
```

**Step 5: MainForm を配線**

`OnShown`（`MainForm.cs:110-115`）:

```csharp
        // 前回の異常終了で残ったバックアップがあれば復元提案（起動時に一度だけ）。確認 OFF では無確認で全復元。
        if (!_restoreOffered)
        {
            _restoreOffered = true;
            int restored = _backup.OfferRestoreOnStartup(this, _file.RestoreFromBackup, _settings.ConfirmRestoreOnStartup);
            if (restored > 0) _announcer.Say($"バックアップを {restored} 件復元しました");
        }
```

`OpenSettings`（`MainForm.cs:366-374`）:

```csharp
    /// <summary>設定ダイアログを開き、OK なら全タブへ外観適用＋バックアップ設定の即時反映＋永続化する。
    /// 優先 SR の変更だけは再起動後有効のため、変更時にその旨を能動通知する。</summary>
    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var result = dlg.Result;   // Result は取得のたびに組み立てるため一度だけ読む
        bool srChanged = result.PreferredScreenReader != _settings.PreferredScreenReader;
        _settings = result;
        foreach (var doc in _docs.Documents) EditorAppearance.Apply(doc.Editor, _settings);
        _backup.UpdateSettings(_settings.BackupEnabled, _settings.BackupIntervalSeconds);
        SaveSettingsSafe();
        _announcer.Say(srChanged ? "設定を適用しました 読み上げ設定は再起動後に有効になります" : "設定を適用しました");
    }
```

**Step 6: ビルドとテスト**

Run: `dotnet build yEdit.sln; if ($?) { dotnet test yEdit.sln }`
Expected: ビルド 0 警告・全テスト PASS（BackupPlannerTests/BackupStoreTests は Coordinator 非依存で影響なし）

**Step 7: コミット**

```bash
git add src/yEdit.App/BackupCoordinator.cs src/yEdit.App/MainForm.cs
git commit -m "設定拡張: バックアップ設定の即時反映と起動時無確認復元を追加"
```

---

### Task 13: 総仕上げ（全ビルド・全テスト・手動検証）

**Step 1: クリーンビルドと全テスト**

Run: `dotnet build yEdit.sln; if ($?) { dotnet test yEdit.sln }`
Expected: ビルド **0 警告**・全テスト PASS

**Step 2: スモーク起動と手動検証**

Run: `dotnet run --project src/yEdit.App`

チェックリスト（設計書 §検証方針より）:

- [ ] 設定ダイアログが 6 タブ（基本/編集/禁則処理/表示/バックアップ/読み上げ）。Ctrl+Tab 巡回・各アクセスキー（&V &T &S &N &H &W &B &I &C &R）動作・キャンセルで元設定保持
- [ ] 表示タブ: 行番号 ON で桁数追従（100 行超の文書で幅が広がる）・現在行強調が全 4 テーマで視認可能かつ文字が読める・キャレット太さ 5 で明確に太い・空白/改行の可視化
- [ ] 編集タブ: タブ幅 4/8 で表示が変わる・タブ→スペース ON で Tab キーがスペース挿入・折り返し表示と行番号マージンの共存（折り返し桁が右へずれないこと）
- [ ] 禁則整形: タブを含む行がタブ幅 4 の見た目どおりに折られる
- [ ] 基本タブ: CSV 自動 ON で「開く」「最近」「開き直し」から .csv が CSV モードで開く（セル読み上げ）・grep ジャンプでは通常モード＋一致選択・壊れた CSV は「CSVとして解析できません」通知の上で通常モード
- [ ] バックアップタブ: 間隔変更が即時反映（タスクマネージャ不要・短い間隔にして tmp 書込で確認可）・無効化→ backups フォルダのファイルが残る・確認 OFF ＋孤児ありで起動 → 無確認復元＋「N 件復元しました」
- [ ] 読み上げタブ: 優先 SR 変更 → OK で「再起動後に有効」を含む通知・settings.json に `"PreferredScreenReader": "pctalker"` が保存される

**Step 3: 実機 SR 検証（ユーザーに依頼・マージ前 DoD）**

コードでは検証不能。以下をユーザーへ依頼する:

- [ ] NVDA のみ / PC-Talker のみ / 両方稼働 / 非稼働→後から起動 の各ケースで、優先 SR 設定どおりの経路（本文読み・能動通知）になること
- [ ] 自動 CSV モード進入時の読み上げが手動トグルと同一であること
- [ ] 無確認復元の起動直後に件数通知が聞こえること

**Step 4: 締め**

- 設計書からの逸脱があれば `docs/plans/2026-07-04-settings-new-items-design.md` に追記して同一コミットへ含める
  - 既知の軽微な逸脱（実装済み扱いでよい）: 自動 CSV の解析失敗通知は既存定数 `CsvAnnounceFormatter.ParseError`（「CSVとして解析できません」）を再利用する（設計書の文言「〜できませんでした」とは異なる。DRY 優先）
- 残作業・申し送りがあればメモリ/設計書 follow-up に記録
- マージ前に別エージェントへコードレビューを依頼（本プロジェクトの慣行）。マージは `main` へ no-ff

```bash
git add -A
git commit -m "設定拡張: 総仕上げ（検証結果の反映・設計書追記）"
```

---

## 補足: レビュー観点（レビュアー向け）

- **ビルド緑の維持**: Task 3→4 の順序が肝（SrSpeechSelector は App の参照を切ってから削除）
- **SR 経路の互換**: `SrContext.Mode` の消費箇所（MainForm.cs:57 の空行発声）が導出プロパティで従来どおり動くこと
- **既存ユーザー非破壊**: settings.json に `BackupIntervalSeconds: 30` を持つユーザーは 30 のまま（既定変更は新規のみ）。優先 SR 未設定（キーなし）は "nvda" 既定＋検出フォールバックで従来挙動を維持
- **フォーカス系**: 自動 CSV モード進入後の `FocusTarget` はシンクを指す。ReopenWithEncoding の `doc.FocusTarget.Focus()` がその前提で正しく動く
- **grep ジャンプ**: `suppressAutoCsv: true` が唯一の抑止点。既に CSV モード中のタブへのジャンプ挙動は従来からの既知申し送り（スコープ外）
