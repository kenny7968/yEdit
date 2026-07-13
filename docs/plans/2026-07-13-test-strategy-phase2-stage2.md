# テスト戦略 Phase 2 Stage 2(縮小版): Speech 構造整理+Announcer 契約テスト 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** PC-Talker 廃止後に残った Speech サブシステム(AnnouncerFactory/UiaAnnouncer/AnnouncerBase)の構造を整理し、残存挙動を App.Tests の契約テストで固定する。

**Architecture:** リファクタ前に現行挙動の特徴付けテスト(green から開始)を入れ、以降の構造変更(Factory 廃止・Raise インライン化・Smoke 改名)で緑を維持することで「挙動不変」を機械的に担保する。公開挙動・通知文言・SR 発声経路は一切変えない。

**Tech Stack:** .NET 9 / WinForms / xUnit v2(STA ヘルパ=`Sta.Run`)/ InternalsVisibleTo

- 日付: 2026-07-13
- 上位文書: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` §4 Stage 2+「Stage 2 再スコープ(2026-07-13)」
- 再スコープ元: `docs/plans/2026-07-13-pctalker-removal-design.md` §5
- ベースライン: main `01bea88`・テスト数 800(Core 570+Editor 216+App 14)

---

## 0. スコープ確定(再スコープ文の具体化)

再スコープ文の各項目を 2026-07-13 のコード精査で以下のとおり確定した。

| 再スコープ文 | 精査結果 | 本計画での扱い |
|---|---|---|
| ISrRoute シームは導入しない | 判定対象消滅を確認(PcTalkerSpeech.cs は削除済み) | 対象外(何もしない) |
| AnnouncerFactory の構造整理(static 解消 or 直接生成) | 排除コミット①で分岐撤去済み=現在はロジックゼロの 1 行 static。呼び出し元は `MainForm.cs:56` と `GrepDialog.cs:40` の 2 箇所のみ | **Factory を削除し `new UiaAnnouncer(label)` の直接生成へ**(YAGNI: ロジックゼロの間接層は不要) |
| `UiaAnnouncer.Raise` の private 化 or Speak へのインライン化 | 呼び出し元は同クラス内 `Speak` のみ(grep `\bRaise\(` で確認)。static 分離は消滅した PcTalkerAnnouncer の視覚退避用だった | **Speak へインライン化し static ヘルパを削除** |
| Smoke `UseUiaAnnouncer` の改名(例: MarkUiaTitle) | 実態=`OnShown` でタイトルに `[UIA] ` を付けるだけ(UIA プロバイダは EditorControl に常時配線済み) | **`MarkUiaTitle` へ改名+コメントを実態に合わせる** |
| `FakeAnnouncer` による通知配線テスト | Stage 2 時点で FakeAnnouncer を注入できる IAnnouncer 消費者は SearchController(Stage 4)/CsvController(Stage 6)のみ。MainForm の配線(KeyBasedSwitch→Say 等)は composition root 未分離で対象外(Stage 8) | **「残存 Speech サブシステムの契約テスト」と読み替える**: AnnouncerBase の通知契約(視覚無条件・発声は非空のみ)+UiaAnnouncer の安全性(UIA 非対応環境で落ちない)を計 5 件で特徴付け。FakeAnnouncer の実使用は Stage 4 以降 |
| 「ActiveCaretEnteredEmptyLine/ActiveWordNavigated の転送テスト」不要 | イベント自体が排除コミット②で全削除済みを確認 | 対象外(何もしない) |

**テスト数**: 800 → **805**(App 14→19・純増 +5)。

**触らないもの**: `yEdit.Accessibility`(UIA プロバイダ)・`AnnouncerBase` の契約ロジック・`IAnnouncer` インターフェース定義(コメントのみ修正)・MSAA 抑制・CSV の RaiseUiaSelectionEvents。

## 規約(全 Task 共通)

- ブランチ: `feature/test-strategy-phase2-stage2`(同一ディレクトリのフィーチャーブランチ→main へ no-ff マージ=いつもの運用)
- コミットメッセージは日本語。末尾に `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` を付ける
- 各 Task 末尾で `dotnet build yEdit.sln -c Release -warnaserror` が 0 警告であること
- git status に見えている untracked の `installer/`・`publish/` はこの作業と無関係。**絶対にコミットに含めない**(`git add` はパス指定で行う)

---

### Task 1: ブランチ作成

**Step 1: main から作業ブランチを切る**

Run:
```powershell
git -C <repo> switch -c feature/test-strategy-phase2-stage2 main
```
Expected: `Switched to a new branch 'feature/test-strategy-phase2-stage2'`

---

### Task 2: Announcer 契約の特徴付けテスト(リファクタ前・green から開始)

このテストは**現行挙動の特徴付け**であり、書いた時点で PASS するのが正しい(新機能の TDD ではなくリファクタの安全網)。以降の Task 3〜4 の構造変更で緑を維持することが目的。

**Files:**
- Modify: `src/yEdit.App/yEdit.App.csproj`(InternalsVisibleTo 追加)
- Create: `tests/yEdit.App.Tests/AnnouncerTests.cs`

**Step 1: yEdit.App.csproj に InternalsVisibleTo を追加**

`src/yEdit.App/yEdit.App.csproj` の `</Project>` 直前(既存 ItemGroup の後)に追加:

```xml
  <ItemGroup>
    <!-- Speech 系(AnnouncerBase/UiaAnnouncer)は internal のため、契約テスト用に公開する。
         Core/Editor/Accessibility と同じテスト専用の慣例 -->
    <InternalsVisibleTo Include="yEdit.App.Tests" />
  </ItemGroup>
```

**Step 2: 契約テストを書く**

Create `tests/yEdit.App.Tests/AnnouncerTests.cs`:

```csharp
using yEdit.App.Speech;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 2(縮小版): 残存 Speech サブシステムの契約テスト。
/// AnnouncerBase の通知契約「視覚表示(label.Text)は無条件・発声(Speak)は非空のときだけ」と、
/// UiaAnnouncer の安全性(UIA 通知非対応環境でも握りつぶして視覚のみ)を特徴付ける。
/// Core が検証済みの文言生成は対象外(責務=Speech 層の契約)。
/// </summary>
public class AnnouncerTests
{
    /// <summary>Speak 呼び出しを記録する AnnouncerBase 派生(基底の契約検証用)。</summary>
    private sealed class RecordingAnnouncer : AnnouncerBase
    {
        public List<string> Spoken { get; } = new();
        public RecordingAnnouncer(Label label) : base(label) { }
        protected override void Speak(string message) => Spoken.Add(message);
    }

    // ===== AnnouncerBase の通知契約 =====

    [Fact]
    public void Say_NonEmpty_UpdatesLabel_AndSpeaksOnce() => Sta.Run(() =>
    {
        using var label = new Label();
        var announcer = new RecordingAnnouncer(label);
        announcer.Say("3 件中 1 件目");
        Assert.Equal("3 件中 1 件目", label.Text);          // 視覚は無条件(晴眼/弱視も第一級)
        Assert.Equal(new[] { "3 件中 1 件目" }, announcer.Spoken);
    });

    [Fact]
    public void Say_Empty_ClearsLabel_WithoutSpeaking() => Sta.Run(() =>
    {
        using var label = new Label { Text = "前回の通知" };
        var announcer = new RecordingAnnouncer(label);
        announcer.Say("");
        Assert.Equal("", label.Text);                        // 空=視覚クリアのみ
        Assert.Empty(announcer.Spoken);                      // 発声なし
    });

    [Fact]
    public void Say_Null_ClearsLabel_WithoutSpeaking() => Sta.Run(() =>
    {
        using var label = new Label { Text = "前回の通知" };
        var announcer = new RecordingAnnouncer(label);
        announcer.Say(null!);                                // 防御(message ?? "")の特徴付け
        Assert.Equal("", label.Text);
        Assert.Empty(announcer.Spoken);
    });

    // ===== UiaAnnouncer の安全性 =====

    [Fact]
    public void UiaAnnouncer_Say_SetsLabelText_AndDoesNotThrow_WithoutUiaSupport() => Sta.Run(() =>
    {
        using var label = new Label();                       // ハンドル未生成=UIA 通知は失敗し得る環境
        var announcer = new UiaAnnouncer(label);
        announcer.Say("検索結果 3 件");                      // 握りつぶし契約=例外を漏らさない
        Assert.Equal("検索結果 3 件", label.Text);
    });

    [Fact]
    public void UiaAnnouncer_Say_Empty_ClearsLabel() => Sta.Run(() =>
    {
        using var label = new Label { Text = "前回の通知" };
        var announcer = new UiaAnnouncer(label);
        announcer.Say("");
        Assert.Equal("", label.Text);
    });
}
```

**Step 3: テスト実行(green を確認=特徴付けの成立)**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~AnnouncerTests"
```
Expected: **Passed! 5 件**(現行コードのまま通る。落ちる場合は特徴付けが現実と食い違っている=実装を直すのではなくテストの期待を現行挙動に合わせて直す)

**Step 4: App.Tests 全体が緑であることを確認**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release
```
Expected: Passed! **19 件**(既存 14+新規 5)

**Step 5: Commit**

```powershell
git -C <repo> add src/yEdit.App/yEdit.App.csproj tests/yEdit.App.Tests/AnnouncerTests.cs
git -C <repo> commit -m "test: Announcer 契約の特徴付けテスト 5 件を追加(InternalsVisibleTo 導入)"
```

---

### Task 3: AnnouncerFactory 廃止(UiaAnnouncer 直接生成へ)

**Files:**
- Delete: `src/yEdit.App/Speech/AnnouncerFactory.cs`
- Modify: `src/yEdit.App/MainForm.cs:29,56`
- Modify: `src/yEdit.App/GrepDialog.cs:40`
- Modify: `src/yEdit.App/Speech/IAnnouncer.cs:6`(コメントのみ)

**Step 1: MainForm の生成箇所を直接生成に変更**

`src/yEdit.App/MainForm.cs:29` のフィールドコメントを変更:

```csharp
// 変更前
    private IAnnouncer _announcer = null!; // AnnouncerFactory.Create で生成（下記コンストラクタ参照）
// 変更後
    private IAnnouncer _announcer = null!; // コンストラクタで UiaAnnouncer を直接生成（下記参照）
```

`src/yEdit.App/MainForm.cs:56` を変更:

```csharp
// 変更前
        _announcer = AnnouncerFactory.Create(_announceLabel);
// 変更後
        _announcer = new UiaAnnouncer(_announceLabel);
```

**Step 2: GrepDialog の生成箇所を直接生成に変更**

`src/yEdit.App/GrepDialog.cs:40` を変更:

```csharp
// 変更前
        _announcer = AnnouncerFactory.Create(_status);
// 変更後
        _announcer = new UiaAnnouncer(_status);
```

**Step 3: IAnnouncer.cs のコメントを現状に合わせる**

`src/yEdit.App/Speech/IAnnouncer.cs:6` を変更:

```csharp
// 変更前
/// 実体は AnnouncerFactory が生成（UiaAnnouncer）。
// 変更後
/// 実体は UiaAnnouncer（利用側が通知先 Label を渡して直接生成する）。
```

**Step 4: AnnouncerFactory.cs を削除**

Run:
```powershell
git -C <repo> rm src/yEdit.App/Speech/AnnouncerFactory.cs
```

**Step 5: ビルド+テストで挙動不変を確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Passed! 19 件(AnnouncerFactory 参照が残っているとビルドエラー=消し漏れ検出)

**Step 6: Commit**

```powershell
git -C <repo> add src/yEdit.App/MainForm.cs src/yEdit.App/GrepDialog.cs src/yEdit.App/Speech/IAnnouncer.cs
git -C <repo> commit -m "refactor: AnnouncerFactory を廃止し UiaAnnouncer の直接生成へ(分岐消滅後のロジックゼロ間接層の除去)"
```

---

### Task 4: UiaAnnouncer.Raise を Speak へインライン化

static ヘルパ `Raise(Label, string)` は、消滅した PcTalkerAnnouncer が視覚退避用に呼ぶための分離だった。現在の呼び出し元は同クラス内 `Speak` のみのため、インライン化して間接層を除去する。

**Files:**
- Modify: `src/yEdit.App/Speech/UiaAnnouncer.cs`

**Step 1: UiaAnnouncer.cs を全面書き換え**

```csharp
using System.Windows.Forms.Automation;

namespace yEdit.App.Speech;

/// <summary>
/// UIA 通知（RaiseAutomationNotification）で読ませる Announcer。NVDA・その他SR・既定。
/// 空ガード・視覚表示は <see cref="AnnouncerBase"/> が担う。
/// </summary>
internal sealed class UiaAnnouncer : AnnouncerBase
{
    public UiaAnnouncer(Label label) : base(label) { }

    /// <summary>Label の UIA プロバイダから通知を上げる。非対応環境では握りつぶし（視覚のみ）。</summary>
    protected override void Speak(string message)
    {
        try
        {
            _label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message);
        }
        catch { /* 通知非対応環境では視覚表示のみ */ }
    }
}
```

**Step 2: ビルド+テストで挙動不変を確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~AnnouncerTests"
```
Expected: 0 警告・Passed! 5 件(特に `UiaAnnouncer_Say_SetsLabelText_AndDoesNotThrow_WithoutUiaSupport` が緑=握りつぶし契約の維持)

**Step 3: Commit**

```powershell
git -C <repo> add src/yEdit.App/Speech/UiaAnnouncer.cs
git -C <repo> commit -m "refactor: UiaAnnouncer.Raise を Speak へインライン化(退避呼び出し元の消滅による単一呼び出し化)"
```

---

### Task 5: Smoke の UseUiaAnnouncer を MarkUiaTitle へ改名

実態(タイトルへ `[UIA] ` を付けるだけ。UIA プロバイダは EditorControl に常時配線済み)に名前とコメントを合わせる。製品コード(yEdit.App)には触れない。

**Files:**
- Modify: `tests/yEdit.Editor.Smoke/MainForm.cs:153-164`
- Modify: `tests/yEdit.Editor.Smoke/Program.cs:39`

**Step 1: MainForm.cs のプロパティを改名しコメントを実態に合わせる**

`tests/yEdit.Editor.Smoke/MainForm.cs:153-165` 付近を変更:

```csharp
// 変更前
    // P5 Task 13: smoke --uia モード。SR で本文/選択/位置/座標を読める状態にする。
    // 起動側(Program.cs)が MainForm 生成後に true にする。
    // (WFO1000 回避 = デザイナ非対応の宣言)
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool UseUiaAnnouncer { get; set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (UseUiaAnnouncer && !Text.StartsWith("[UIA] ", StringComparison.Ordinal))
            Text = $"[UIA] {Text}";
    }

// 変更後
    // P5 Task 13: smoke --uia モードの目印。UIA プロバイダは EditorControl に常時配線済みのため、
    // このフラグの実態は「タイトルバーへ [UIA] を付けて起動モードを判別できるようにする」だけ。
    // 起動側(Program.cs)が MainForm 生成後に true にする。
    // (WFO1000 回避 = デザイナ非対応の宣言)
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool MarkUiaTitle { get; set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (MarkUiaTitle && !Text.StartsWith("[UIA] ", StringComparison.Ordinal))
            Text = $"[UIA] {Text}";
    }
```

**Step 2: Program.cs の設定箇所を追随**

`tests/yEdit.Editor.Smoke/Program.cs:39` を変更:

```csharp
// 変更前
    var form = new MainForm(initialPath) { UseUiaAnnouncer = true };
// 変更後
    var form = new MainForm(initialPath) { MarkUiaTitle = true };
```

**Step 3: 消し漏れがないことを確認**

Run:
```powershell
git -C <repo> grep -n "UseUiaAnnouncer" -- "*.cs"
```
Expected: ヒットなし(exit code 1)

**Step 4: Smoke を含むビルドで確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: 0 警告

**Step 5: Commit**

```powershell
git -C <repo> add tests/yEdit.Editor.Smoke/MainForm.cs tests/yEdit.Editor.Smoke/Program.cs
git -C <repo> commit -m "refactor: Smoke の UseUiaAnnouncer を MarkUiaTitle へ改名(実態=タイトル表示のみに名前を一致)"
```

---

### Task 6: ローカルゲート+設計書へ実施記録

**Files:**
- Modify: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`(「Stage 2 再スコープ」節の直後に実施記録を追記)

**Step 1: ローカルゲートを全実行**

Run:
```powershell
powershell -File <repo>\tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`(Release 0 警告・Core 570+Editor 216+App 19=805 緑)

**Step 2: 設計書に実施記録を追記**

`docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` の「Stage 2 再スコープ(2026-07-13)」節の直後に追記:

```markdown
### Stage 2(縮小版)実施記録(2026-07-13)

- **完了**: 実装計画=`docs/plans/2026-07-13-test-strategy-phase2-stage2.md`。①Announcer 契約テスト 5 件(AnnouncerBase の視覚無条件/発声は非空のみ・UiaAnnouncer の握りつぶし契約)+`InternalsVisibleTo Include="yEdit.App.Tests"` ②AnnouncerFactory 廃止(MainForm/GrepDialog で UiaAnnouncer 直接生成) ③UiaAnnouncer.Raise の Speak へのインライン化 ④Smoke `UseUiaAnnouncer`→`MarkUiaTitle` 改名。
- **テスト数**: 800 → 805(App 14→19)。ゲート全通過(Release 0 警告)。
- **読み替えの明確化**: 再スコープ文の「FakeAnnouncer による通知配線テスト」は、Stage 2 時点で注入可能な IAnnouncer 消費者が SearchController/CsvController(=Stage 4/6 の責務)のみのため「残存 Speech サブシステムの契約テスト」として実施した。FakeAnnouncer の実使用(通知文言検証)は Stage 4 以降。
```

(マージコミットのハッシュはマージ後にユーザー確認のうえ追記)

**Step 3: Commit**

```powershell
git -C <repo> add docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md docs/plans/2026-07-13-test-strategy-phase2-stage2.md
git -C <repo> commit -m "docs: Phase2 設計書に Stage 2(縮小版)実施記録を追記+実装計画を追加"
```

---

### Task 7: レビュー→実機スポット→マージ

**Step 1: 別エージェントによるコードレビュー**(いつもの運用)

ブランチ全 diff(`git diff main...feature/test-strategy-phase2-stage2`)を対象に依頼。観点: 挙動不変(通知文言・発声経路・タイミングを変えていないか)・消し漏れ・テストの妥当性。

**Step 2: NVDA 実機スポット確認(L5・ユーザー実施・軽量)**

UiaAnnouncer の Speak 経路を構造変更(インライン化)したため、能動通知の実機確認を 2 項目だけ行う:
- 検索(Ctrl+F)で「N 件中 M 件目」が読まれる
- Ctrl+Tab のタブ切替でタブ名が読まれる

**Step 3: main へ no-ff マージ**

```powershell
git -C <repo> switch main
git -C <repo> merge --no-ff feature/test-strategy-phase2-stage2 -m "テスト戦略 Phase2 Stage2(縮小版): Speech 構造整理+Announcer 契約テストをマージ"
powershell -File <repo>\tools\pre-merge-check.ps1
git -C <repo> branch -d feature/test-strategy-phase2-stage2
```
Expected: マージ後ゲート全緑

**Step 4: 実施記録へマージコミットのハッシュを追記**(小コミット)

---

## DoD(Stage 2 縮小版)

1. `tools/pre-merge-check.ps1` 全緑(Release ビルド 0 警告)
2. テスト数 800 → **805**(App 14→19・純増 +5)
3. **挙動不変**: 能動通知の文言・経路・SR 発声・視覚表示を変えない(diff レビューで機械的確認)
4. 別エージェントによるコードレビュー(マージ前)
5. NVDA 実機スポット確認(能動通知 2 項目・ユーザー実施)
6. main へ no-ff マージ+設計書へ実施記録・マージハッシュ追記

## リスクと対策

- **UiaAnnouncer テストの CI 挙動**: ハンドル未生成 Label への RaiseAutomationNotification は環境により例外→catch 握りつぶしの想定(ローカルで実証)。windows-latest 実機は未検証だが、Say の観測点(label.Text)は環境非依存。初回 push で落ちた場合のみ `Category=LocalOnly` 隔離を検討(Stage 1 と同じ運用)。
- **InternalsVisibleTo の公開面拡大**: テスト専用であり Core/Editor/Accessibility に同じ前例あり。許容。
- **Smoke 改名の影響範囲**: `tests/yEdit.Editor.Smoke` のみ=製品挙動に影響なし。`--uia` モード自体は NVDA 実機検証用に温存(排除設計書 §2 コミット②の決定どおり)。

## 申し送り(Stage 3 へ)

- 次 Stage: FileController(IUserPrompt/IFileDialogService 導入+SaveAs ロールバック最優先)= Phase 2 設計書 §4 Stage 3。PC-Talker 非依存のため再スコープ不要(排除設計書 §5 で確認済み)。
- テストユーティリティ共通化(Sta.cs の共有抽出等)は「3 プロジェクト目が現れたら」の判断基準を継続(Stage 1 実施記録)。
- レビュー由来(いずれも非ブロッカー・実施レビューでの Minor 指摘):
  - 空白のみメッセージ(`" "`)の特徴付けテスト: `AnnouncerBase.Say` のガードは `IsNullOrEmpty` であり空白のみは表示・発声される。この区別(`IsNullOrWhiteSpace` ではない)を固定するテストを Stage 4(FakeAnnouncer 実使用開始)で再検討。
  - `MainForm._announcer` の readonly 化+ctor 先頭移動: AnnouncerFactory 廃止で `new UiaAnnouncer(_announceLabel)` は依存ゼロになり、ctor 先頭移動+`null!` 撤去が可能になった。既存申し送り「ctor 時間的結合」([[settings-new-items-followups]]相当)と合流し Stage 8(MainForm 痩身)で対応。
  - GrepDialog の IAnnouncer 注入化: 現在はダイアログ内部で `new UiaAnnouncer(_status)`。GrepController の通知文言テストが必要になる Stage 7 の設計時に注入化を判断。
