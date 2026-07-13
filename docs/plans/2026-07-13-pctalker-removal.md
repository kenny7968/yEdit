# PC-Talker 専用実装の排除 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** PC-Talker 専用実装(検出・直叩き発声・能動発声トリガ・イベント土台)を全削除し、全 SR 共有の汎用 UIA 経路のみを残す。

**Architecture:** 設計書 `docs/plans/2026-07-13-pctalker-removal-design.md` に従い、3 コミット(①App 層摘出 → ②イベント土台全削除 → ③ドキュメント整理)+ユーザー確認必須の説明書更新で構成。削除のみで新機能はない(TDD 対象なし)。各コミットで Release ビルド 0 警告+関連テスト緑+grep ゼロを検証する。

**Tech Stack:** .NET 9 / WinForms / xUnit。ゲートは `tools/pre-merge-check.ps1`(Release ビルド -warnaserror → Core.Tests → Editor.Tests → App.Tests)。

**前提:** ブランチ `feature/remove-pctalker-support` 上で作業(作成済み・設計書コミット済み)。行番号はすべて 2026-07-13 時点。編集前に必ず該当箇所を Read して現物と照合すること。

**触ってはいけないもの(温存・設計書 §3):**
- `src/yEdit.Accessibility/` 一式(コメントの PC-Talker 言及も歴史的経緯として温存)
- `UiaAnnouncer` / `AnnouncerBase` / `IAnnouncer` の実装(コメント修正のみ)
- MSAA 抑制(`WM_GETTEXT` 非応答)・`RaiseUiaSelectionEvents` の挙動
- `EditorControl.cs:1408-1414` のフォーカス時 UIA イベント発火(コメントの PC-Talker 言及は歴史的経緯として温存)
- `GrepResultsWindow.cs:8` / `RestoreDialog.cs:8` / `NavigationCommands.cs:84` の PC-Talker 言及(「ネイティブに読める」系の事実記述で無害・温存)

---

## Task 1: App 層の PC-Talker 摘出(コミット①)

**Files:**
- Delete: `src/yEdit.App/Speech/PcTalkerSpeech.cs`
- Delete: `src/yEdit.App/Speech/PcTalkerAnnouncer.cs`
- Modify: `src/yEdit.App/Speech/AnnouncerFactory.cs`(全面書き換え)
- Modify: `src/yEdit.App/MainForm.cs:29-33, 56-75`
- Modify: `src/yEdit.App/Speech/IAnnouncer.cs:6`
- Modify: `src/yEdit.App/Speech/UiaAnnouncer.cs:7`
- Modify: `src/yEdit.App/CsvController.cs:16, 63, 101`

**Step 1: 2 ファイルを削除**

```powershell
git rm src/yEdit.App/Speech/PcTalkerSpeech.cs src/yEdit.App/Speech/PcTalkerAnnouncer.cs
```

**Step 2: AnnouncerFactory.cs を以下の内容に全面書き換え**

```csharp
namespace yEdit.App.Speech;

/// <summary>
/// 呼び出し元 Label に束縛した IAnnouncer(UIA 通知)を生成する。
/// PC-Talker サポート廃止(docs/plans/2026-07-13-pctalker-removal-design.md)により経路分岐は撤去し、
/// 常に UiaAnnouncer を返す。static 解消等の構造整理はテスト戦略 Phase 2 Stage 2(縮小版)で判断する。
/// </summary>
internal static class AnnouncerFactory
{
    /// <summary>指定 Label に束縛した UiaAnnouncer を生成する。</summary>
    public static IAnnouncer Create(Label label) => new UiaAnnouncer(label);
}
```

**Step 3: MainForm.cs を編集(2 箇所)**

3a. 29-33 行: `_announcer` の行コメントを修正し、`_isPcTalker` フィールド(コメント 3 行+宣言)を削除。

変更前:
```csharp
    private IAnnouncer _announcer = null!; // 起動時に PC-Talker 稼働で選択（下記コンストラクタ参照）
    // 起動時に PC-Talker が稼働しているか（起動時確定方針・PC-Talker 起動/終了には追従しない）。
    // PC-Talker 経路では UIA の長さ0行が無音になる等の欠落を「空行」能動発声等で補うため、
    // 経路別の分岐（空行発声・単語ナビの能動発声）判定にこの1値を使う。
    private readonly bool _isPcTalker = PcTalkerSpeech.IsRunning();
```

変更後:
```csharp
    private IAnnouncer _announcer = null!; // AnnouncerFactory.Create で生成（下記コンストラクタ参照）
```

3b. 56-75 行: コンストラクタ内の 2 購読ブロック(空行能動発声+単語ナビ能動発声・各ブロック直前のコメント含む)を丸ごと削除。削除範囲は `// 空行着地の能動発声:` のコメント行から `_docs.ActiveWordNavigated += ...` ブロックの閉じ `};` まで。直前の `_docs.ActiveCaretChanged += ...` 行と直後の `// 設定は OpenSettings で...` コメント行は残す。

**Step 4: コメント修正(3 ファイル)**

- `IAnnouncer.cs:6`: `/// 実体は AnnouncerFactory が PC-Talker 稼働判定に応じて生成（UiaAnnouncer / PcTalkerAnnouncer）。` → `/// 実体は AnnouncerFactory が生成（UiaAnnouncer）。`
- `UiaAnnouncer.cs:7`: 「PC-Talker のハード失敗退避でも Raise を再利用する。」の一文を削除(前半の「空ガード・視覚表示は AnnouncerBase が担う。」は残す)
- `CsvController.cs:16`: 「PC-Talker（UIA 経路）向けの」→「UIA 系 SR 向けの」
- `CsvController.cs:63`: 「PC-Talker（UIA経路）向け防御:」→「UIA 系 SR 向け防御:」
- `CsvController.cs:101`: 「（PC-Talker の二重読み解消は実機で要確認）」→「（二重読み解消は実機で要確認）」

**Step 5: ビルドと確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```
Expected: 0 警告・0 エラー。

grep 確認(この時点で src/yEdit.App に PcTalker/PCTK が残らないこと):
```powershell
# 検索対象: src/yEdit.App — ヒット 0 件であること
```
Grep ツールで pattern `PcTalker|PCTK`、path `src/yEdit.App` → No matches。

**Step 6: App.Tests 実行**

```powershell
dotnet test tests/yEdit.App.Tests -c Release --no-build
```
Expected: 14 件全緑。

**Step 7: コミット**

```powershell
git add -A
git commit -m "PC-Talker摘出(App層): PCTKUsr.dll直叩き・稼働判定・能動発声トリガを削除"
```

---

## Task 2: イベント土台の全削除(コミット②)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(多数箇所・下記)
- Delete: `src/yEdit.Editor/WordNavigatedEventArgs.cs`
- Modify: `src/yEdit.App/DocumentManager.cs:41-42, 70-77`
- Delete: `tests/yEdit.Editor.Smoke/UiaSmokeAnnouncer.cs`
- Modify: `tests/yEdit.Editor.Smoke/MainForm.cs:153-172`
- Modify: `tests/yEdit.Editor.Smoke/Program.cs:31-35`
- Delete: `tests/yEdit.Editor.Tests/EditorControlWordNavEventTests.cs`
- Modify→分割: `tests/yEdit.Editor.Tests/EmptyLineNavigationTests.cs`(削除)+ Create: `tests/yEdit.Editor.Tests/RaiseUiaSelectionEventsTests.cs`(2 件移設)
- Delete: `src/yEdit.Core/Reading/EmptyLineDetector.cs`+`tests/yEdit.Core.Tests/Reading/EmptyLineDetectorTests.cs`

**Step 1: EditorControl.cs — WordNavigated 系を削除**

- 2516-2527 行: `WordNavigated` イベント(XML doc 含む)+`RaiseWordNavigated` メソッドを削除
- 2297-2300 行: `wordNavCandidate` の宣言+説明コメントを削除
- 2308, 2314 行: `if (ctrl && !shift) wordNavCandidate = true;` を削除(case Keys.Left / Keys.Right 内)
- 2502 行: `int beforeCaret = _caret;` を削除
- 2509-2511 行: `// P5 Task 12: 純単語ナビ...` コメント+`if (wordNavCandidate && beforeCaret != _caret) RaiseWordNavigated(...);` を削除

**Step 2: EditorControl.cs — CaretEnteredEmptyLine 系を削除**

- イベント宣言(825 行)とその XML doc(`/// <summary>` 開始〜825 行。805 行付近から。現物を Read して doc コメント全体を特定)を削除
- `RaiseCaretEnteredEmptyLineIfNeeded` メソッド(2722-2755 行・XML doc 含む)を削除
- OnKeyDown 呼び出し部: 2498-2501 行(コメント+`int fromLine = ...` 捕獲)と 2508 行(`RaiseCaretEnteredEmptyLineIfNeeded(fromLine);`)を削除。`if (resetDesired)...BringCaretIntoView();` と `e.Handled = true;` は残す
- OnMouseDown 呼び出し部: 2033-2035 行のコメント+`int fromLine = ...` と 2046 行の呼び出しを削除
- 2280-2281 行の OnKeyDown 用 doc コメント内の `RaiseCaretEnteredEmptyLineIfNeeded()` への言及を削除(文を `- <c>BringCaretIntoView()</c> は Task 7 で本実装。...` の形に整えるか、項目ごと削除)

**Step 3: EditorControl.cs — `_lastCaretLine` フィールドを全撤去**

grep pattern `_lastCaretLine` の全ヒットを削除する(2026-07-13 時点: 宣言+doc コメント 75-88 行/リセット 211, 271 行/setter 同期+コメント 953-957, 994-995, 1019-1020, 1045-1046 行/`CurrentLine` の remarks 874-877 行/上記 Step 1-2 で削除済みの箇所)。

- setter 同期は「`// Task 13 レビュー I-1: ...` コメント+`_lastCaretLine = _buffer.Current.GetLineIndexOfChar(_caret);`」のペアを 4 箇所とも削除
- `CurrentLine` プロパティ(879 行)の `<remarks>` ブロック(873-877 行)は内容全体が `_lastCaretLine` の説明のため丸ごと削除(`<summary>` は残す)
- 211, 271 行の `_lastCaretLine = 0;` を削除

**Step 4: EditorControl.cs:841 のコメント修正**

`RaiseUiaSelectionEvents` の doc: 「PC-Talker が行を読むのを防ぐ」→「SR が行を読むのを防ぐ」。

**Step 5: WordNavigatedEventArgs.cs と Core 死にコードを削除**

```powershell
git rm src/yEdit.Editor/WordNavigatedEventArgs.cs
git rm src/yEdit.Core/Reading/EmptyLineDetector.cs tests/yEdit.Core.Tests/Reading/EmptyLineDetectorTests.cs
```
(`src/yEdit.Core/Reading/` が空になる場合はフォルダも消えることを確認)

**Step 6: DocumentManager.cs を編集**

- 41-42 行: `ActiveCaretEnteredEmptyLine` / `ActiveWordNavigated` のイベント宣言 2 行を削除
- 70-77 行: `editor.CaretEnteredEmptyLine += ...` と `editor.WordNavigated += ...` の 2 購読ブロックを削除(65 行の「キャレット移動はアクティブ分のみ上位へ。」コメントと `editor.UpdateUI += ...`、78 行以降の `editor.GotFocus += ...` は残す)

**Step 7: Smoke を編集**

```powershell
git rm tests/yEdit.Editor.Smoke/UiaSmokeAnnouncer.cs
```

- `MainForm.cs:153-155` コメント: `// P5 Task 13: smoke --uia モード。SR で本文/選択/位置/座標を読める状態にする。起動側(Program.cs)が MainForm 生成後に true にする。` に差し替え(WordNavigated/UiaSmokeAnnouncer への言及を除去)
- `MainForm.cs:160`: `private UiaSmokeAnnouncer? _uiaAnnouncer;` を削除
- `MainForm.cs:162-172` OnShown: `_uiaAnnouncer` 生成とコメント(167-169 行)を削除し、`if (UseUiaAnnouncer ...)` ブロックは `[UIA]` タイトルプレフィックスのみ残す:

```csharp
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (UseUiaAnnouncer && !Text.StartsWith("[UIA] ", StringComparison.Ordinal))
            Text = $"[UIA] {Text}";
    }
```

- `Program.cs:31-35` コメント: `// タイトルバーに [UIA] プレフィックスが付き、UiaSmokeAnnouncer が CaretEnteredEmptyLine / WordNavigated を購読して発声補完する。` の 2 行を `// タイトルバーに [UIA] プレフィックスが付く。` に差し替え(--uia モード自体は SR 実機検証用に温存)

**Step 8: Editor テストの削除と移設**

```powershell
git rm tests/yEdit.Editor.Tests/EditorControlWordNavEventTests.cs
git rm tests/yEdit.Editor.Tests/EmptyLineNavigationTests.cs
```

Create `tests/yEdit.Editor.Tests/RaiseUiaSelectionEventsTests.cs`(EmptyLineNavigationTests から 2 件を移設・ヘルパは MakeControl のみ必要):

```csharp
namespace yEdit.Editor.Tests;

/// <summary>
/// RaiseUiaSelectionEvents プロパティ受け口の契約テスト(既定 true・読み書き可能)。
/// CSV モード(CsvController)が誤読み抑止に使う温存機能。
/// EmptyLineNavigationTests から移設(PC-Talker サポート廃止=CaretEnteredEmptyLine 削除に伴い
/// docs/plans/2026-07-13-pctalker-removal-design.md)。
/// </summary>
public class RaiseUiaSelectionEventsTests
{
    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new Form();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    [Fact]
    public void RaiseUiaSelectionEvents_DefaultIsTrue() => Sta.Run(() =>
    {
        var (f, c) = MakeControl("abc");
        using (f) using (c) { Assert.True(c.RaiseUiaSelectionEvents); }
    });

    [Fact]
    public void RaiseUiaSelectionEvents_CanBeSet() => Sta.Run(() =>
    {
        var (f, c) = MakeControl("abc");
        using (f) using (c)
        {
            c.RaiseUiaSelectionEvents = false;
            Assert.False(c.RaiseUiaSelectionEvents);
            c.RaiseUiaSelectionEvents = true;
            Assert.True(c.RaiseUiaSelectionEvents);
        }
    });
}
```

**Step 9: ビルド+テスト+grep 確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告/Core **569**/Editor **216**/App **14** 全緑。

Grep 確認(すべて No matches であること):
- pattern `CaretEnteredEmptyLine|WordNavigated|_lastCaretLine|EmptyLineDetector|UiaSmokeAnnouncer`、path `src` および `tests`
- pattern `PcTalker|PCTK`、path `src` および `tests`

**Step 10: コミット**

```powershell
git add -A
git commit -m "PC-Talker摘出(イベント土台): CaretEnteredEmptyLine/WordNavigated と死にコードを削除"
```

---

## Task 3: ドキュメント・ツール整理(コミット③)

**Files:**
- Delete: `tools/verify-msaa-client.ps1`(untracked のため単純削除)
- Modify: `docs/plans/2026-07-06-p6-manual-checklist.md`(冒頭に注記)
- Modify: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`(Stage 2 再スコープ追記)

**Step 1: 陳腐化スクリプト削除**

```powershell
Remove-Item tools/verify-msaa-client.ps1
```
(untracked なので git 操作不要。`installer/`・`publish/` は本件と無関係のため触らない)

**Step 2: p6-manual-checklist.md 冒頭(「**対象**」の段落の直後)に注記を追加**

```markdown
> **2026-07-13 追記**: PC-Talker サポート廃止(`docs/plans/2026-07-13-pctalker-removal-design.md`)により、以後の実施では PC-Talker 列はスキップ対象。過去の実施記録は当時のまま(改変しない)。
```

**Step 3: Phase 2 設計書に再スコープ追記(末尾の「### Stage 1 実施記録」の後に新セクション)**

```markdown
### Stage 2 再スコープ(2026-07-13)

PC-Talker サポート廃止(`docs/plans/2026-07-13-pctalker-removal-design.md`)により本 Stage を縮小する。

- §2.1 の `ISrRoute` シームは**導入しない**(判定対象の PC-Talker 経路が消滅)。
- 残作業: AnnouncerFactory の構造整理(static 解消 or MainForm での直接生成)+`FakeAnnouncer` による通知配線テスト。
- Stage 1 実施記録の追加観点「ActiveCaretEnteredEmptyLine / ActiveWordNavigated の転送テスト」は対象消滅により不要。
- §3 テスト観点表の「Speech」行・§5 の「Stage 2 は SR 経路に触れるため L5 スポット確認必須」は上記縮小後の内容に読み替える。
```

**Step 4: コミット**

```powershell
git add -A
git commit -m "PC-Talker廃止: チェックリスト注記・Phase2 Stage2再スコープ・陳腐化スクリプト削除"
```

---

## Task 4: 説明書の更新(★ユーザー確認必須・サブエージェント不可)

**Files:**
- Modify: `説明書/yEdit説明書.md:26, 34, 132, 145, 235, 308-309`

**説明書の文面はユーザー編集版が正**。以下の変更案を**そのままユーザーに提示し、承認・修正を受けてから**適用すること(メインセッションで実施)。

| 行 | 現状 | 変更案 |
|---|---|---|
| 26 | NVDA と PC-Talker に対応しています。 | NVDA など、UIA(UI オートメーション)に対応したスクリーンリーダーに対応しています。 |
| 34 | NVDA および PC-Talker で動作確認しています。ただし、PC-Talkerでは… | NVDA で動作確認しています。PC-Talker はサポート対象外です。 |
| 132 | **PC-Talker が稼働**: yEdit が PC-Talker に直接読み上げを依頼する方式で動作します。 | 行ごと削除(周辺の経路説明もあわせて要確認) |
| 145 | (NVDA/PC-Talker/ナレーター)の標準機能 | (NVDA/ナレーターなど)の標準機能 |
| 235 | 優先するスクリーンリーダー(設定表の行) | 行ごと削除(**P7 で撤去済み機能の記載残り**) |
| 308-309 | PC-Talker での空行の読み上げ…調査中です。/PC-Talker で文書が空欄時… | 2 項目とも削除し、「PC-Talker はサポート対象外です。読み上げには NVDA など UIA 対応のスクリーンリーダーをご利用ください。」に置き換え |

適用後: 132 行と 235 行は前後の文脈(見出し・表の整合)を必ず確認して整形すること。

**コミット:**
```powershell
git add 説明書/yEdit説明書.md
git commit -m "説明書: 対応SRをUIA対応SR(NVDA等)に更新・PC-Talker記載を整理"
```

---

## Task 5: 検証・レビュー・マージ

**Step 1: ローカルゲート**

```powershell
powershell -File tools/pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`(Release 0 警告・Core 569+Editor 216+App 14=**799** 緑)。

**Step 2: 別エージェントによるコードレビュー**(いつもの運用)。指摘があれば対応コミットを積む。

**Step 3: NVDA 実機スポット確認(★ユーザー実施・マージ前必須)**

確認項目(L5・a11y 関連変更のため):
1. 文字/行の通常読み(矢印キー移動)
2. 空行への移動(NVDA がネイティブに「ブランク」等を読むこと)
3. 単語ナビ Ctrl+←→(NVDA が UIA 選択イベントで自力に読むこと)
4. 状態通知(検索して「N 件中 M 件目」が読まれること=UiaAnnouncer 経路)
5. CSV モードのセル読み(誤読みがないこと=RaiseUiaSelectionEvents 抑止の健在確認)

**Step 4: main へ no-ff マージ+復活用参照の記録**

```powershell
git checkout main
git merge --no-ff feature/remove-pctalker-support -m "PC-Talkerサポート廃止(専用実装の排除)をマージ"
```

マージ後、マージコミットのハッシュを `docs/plans/2026-07-13-pctalker-removal-design.md` §6 の「マージコミット: (マージ後に追記)」へ追記し、main 上で直接コミットする(installer 廃止時の記録方式と同じ)。
