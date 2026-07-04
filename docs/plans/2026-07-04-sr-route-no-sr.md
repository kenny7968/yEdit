# SR 非稼働時の汎用 UIA 経路（SrRoute.Uia）導入 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** SR 非検出時に自前 UIA プロバイダを提供する第 3 の経路 `SrRoute.Uia` を導入し、SR なし・ナレーター/JAWS 等の環境での退行（既定設定で UIA 不提供）を解消する。

**Architecture:** 設計 = docs/plans/2026-07-04-sr-route-no-sr-design.md。`SrRoute` enum に第 3 値 `Uia` を追加し、`SrRouteSelector.Select` を新規則「検出された SR の経路。両方稼働なら優先設定。どちらも非検出なら汎用 UIA 経路」へ置き換える。App 層の導出（`SrContext.UseNativeReading` / `Mode`）は**無変更**で第 3 値に対して自然に正しい挙動（UIA プロバイダ提供＋UIA 通知 Announcer＋空行能動発声なし）になる。

**Tech Stack:** .NET 9 / WinForms / xUnit。ビルド・テストは `dotnet` CLI（リポジトリルート <repo> で実行）。

**前提（実装者向けコンテキスト）:**
- 作業ブランチ: `fix/sr-route-no-sr`（main から作成済み・設計書コミット `21b3b54` あり）。
- `SrRoute` は起動時に 1 回だけ確定する読み上げ経路。受動読み（UIA プロバイダ提供可否 = `ScintillaHost.ApplySrAdaptation`）と能動発声（`AnnouncerFactory` の Announcer 選択）が常にペアで従う。
- NVDA 検出時に UIA を撤収する理由は実測済みの競合（HANDOFF-scintilla-uia.md §13.2、NVDA ソースと 2026-07-04 照合済み）。**この撤収は変更しない**。
- `SrContext.Detect` は `Program.Main` が UI 開始前に 1 回だけ呼ぶ。`Program.cs`・`AnnouncerFactory`・`MainForm`・`ScintillaHost`・設定 UI は**触らない**。

---

### Task 1: 失敗するテストを先に用意（enum 追加＋期待値更新）

**Files:**
- Modify: `src/yEdit.Core/Speech/SrRoute.cs`
- Test: `tests/yEdit.Core.Tests/Speech/SrRouteSelectorTests.cs`

**Step 1: `SrRoute` に第 3 値 `Uia` を追加**

テストが新値を参照してコンパイルできるようにする最小追加（挙動変更なし）。`SrRoute.cs` の enum 末尾 `PcTalker,` の次に追加:

```csharp
    /// <summary>汎用 UIA 経路: SR 非検出時。自前 UIA プロバイダ提供、能動発声は UIA 通知（SR なし・ナレーター/JAWS 等の UIA 系 SR で安全）。</summary>
    Uia,
```

**Step 2: テストの決定表を新規則へ更新**

`SrRouteSelectorTests.cs` を以下の内容に全置換（変わる期待値は非稼働 2 ケースのみ。他 6 ケースは不変＝回帰確認）:

```csharp
using yEdit.Core.Speech;
using Xunit;

namespace yEdit.Core.Tests.Speech;

/// <summary>
/// 決定表（設計 2026-07-04 sr-route-no-sr）:
/// 検出された SR の経路。両方稼働なら「優先するスクリーンリーダー」設定が決める。
/// どちらも非検出なら汎用 UIA 経路。
/// </summary>
public class SrRouteSelectorTests
{
    [Theory]
    // 優先 = NVDA（既定）
    [InlineData(true,  true,  false, SrRoute.Nvda)]     // NVDA のみ → NVDA
    [InlineData(true,  false, true,  SrRoute.PcTalker)] // PC-Talker のみ → PC-Talker（検出優先）
    [InlineData(true,  true,  true,  SrRoute.Nvda)]     // 両方 → 優先 NVDA
    [InlineData(true,  false, false, SrRoute.Uia)]      // どちらも非稼働 → 汎用 UIA（SR なし・他 UIA 系 SR で安全）
    // 優先 = PC-Talker
    [InlineData(false, true,  false, SrRoute.Nvda)]     // NVDA のみ → NVDA（検出優先）
    [InlineData(false, false, true,  SrRoute.PcTalker)] // PC-Talker のみ → PC-Talker
    [InlineData(false, true,  true,  SrRoute.PcTalker)] // 両方 → 優先 PC-Talker（設定が勝つ）
    [InlineData(false, false, false, SrRoute.Uia)]      // どちらも非稼働 → 汎用 UIA
    public void Select_resolves_route(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning, SrRoute expected)
        => Assert.Equal(expected, SrRouteSelector.Select(preferNvda, nvdaRunning, pcTalkerRunning));
}
```

**Step 3: テストを実行して失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter SrRouteSelectorTests`
Expected: **FAIL 2 件 / PASS 6 件**。`(true, false, false)` が `Nvda` を返して `Uia` 期待で失敗、`(false, false, false)` が `PcTalker` を返して失敗。それ以外の失敗が出たら手を止めて原因を調べること。

---

### Task 2: `SrRouteSelector.Select` を新規則へ実装

**Files:**
- Modify: `src/yEdit.Core/Speech/SrRouteSelector.cs`

**Step 1: 実装を置き換え**

ファイル全体を以下に置換（doc コメントの規則文も新規則へ）:

```csharp
namespace yEdit.Core.Speech;

/// <summary>
/// 「優先するスクリーンリーダー」設定と起動時のプロセス検出から読み上げ経路を選ぶ純ロジック
/// （WinForms 非依存・単体テスト可能）。判定は App 層の SrContext が起動時に 1 回行う。
/// 規則（設計 2026-07-04 sr-route-no-sr）:
/// 検出された SR の経路。両方稼働なら「優先するスクリーンリーダー」設定が決める。
/// どちらも非検出なら汎用 UIA 経路（SR なし・ナレーター/JAWS 等の UIA 系 SR で安全）。
/// </summary>
public static class SrRouteSelector
{
    public static SrRoute Select(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning)
    {
        if (nvdaRunning && pcTalkerRunning) return preferNvda ? SrRoute.Nvda : SrRoute.PcTalker;
        if (nvdaRunning) return SrRoute.Nvda;
        if (pcTalkerRunning) return SrRoute.PcTalker;
        return SrRoute.Uia;
    }
}
```

**Step 2: テストが通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter SrRouteSelectorTests`
Expected: PASS 8 件。

**Step 3: Core 全テストで回帰確認**

Run: `dotnet test tests/yEdit.Core.Tests`
Expected: 全緑（現行 289 件規模）。

**Step 4: コミット**

```powershell
git add src/yEdit.Core/Speech/SrRoute.cs src/yEdit.Core/Speech/SrRouteSelector.cs tests/yEdit.Core.Tests/Speech/SrRouteSelectorTests.cs
git commit -m "SR経路: SR非検出時の汎用UIA経路(SrRoute.Uia)を新設"
```

---

### Task 3: `SrContext` の既定値と doc コメントを新規則へ

App 層の導出ロジック（`UseNativeReading` / `Mode`）は**変更しない**。変えるのは未検出時既定値と規則を説明する doc コメントのみ。

**Files:**
- Modify: `src/yEdit.App/Speech/SrContext.cs`

**Step 1: クラス doc コメントの規則文を更新**

旧（10〜12 行目付近）:

```
/// 規則（検出フォールバック付き・設計 2026-07-04）: 優先 SR が稼働 or どちらも非稼働 → 優先 SR の経路。
/// もう片方のみ稼働 → 検出された方の経路（救済）。純ロジックは Core の SrRouteSelector。
```

新:

```
/// 規則（設計 2026-07-04 sr-route-no-sr）: 検出された SR の経路。両方稼働なら「優先するスクリーンリーダー」
/// 設定が決める。どちらも非検出なら汎用 UIA 経路（SrRoute.Uia）。純ロジックは Core の SrRouteSelector。
```

**Step 2: `Route` の既定値を `Uia` へ**

旧:

```csharp
    /// <summary>確定済みの読み上げ経路（未検出時は無害な既定 = NVDA 経路）。</summary>
    public static SrRoute Route { get; private set; } = SrRoute.Nvda;
```

新（意味の一貫性のため。`Detect` は必ず呼ばれるので挙動影響なし）:

```csharp
    /// <summary>確定済みの読み上げ経路（未検出時は無害な既定 = 汎用 UIA 経路）。</summary>
    public static SrRoute Route { get; private set; } = SrRoute.Uia;
```

**Step 3: `IsNvdaRunning` の doc コメントから旧規則の一文を削除**

旧:

```csharp
    /// <summary>NVDA 本体プロセスが動いているか。判定の要は「NVDA が動いているか」だけ。</summary>
```

新（新規則では PC-Talker 検出も判定に効くため「〜だけ」は誤りになる）:

```csharp
    /// <summary>NVDA 本体プロセスが動いているか。</summary>
```

**Step 4: ソリューション全体をビルドして 0 警告を確認**

Run: `dotnet build yEdit.sln`
Expected: 0 Warning / 0 Error（このリポジトリは 0 警告を維持する規約）。

**Step 5: 全テスト実行**

Run: `dotnet test yEdit.sln`
Expected: 全緑。

**Step 6: コミット**

```powershell
git add src/yEdit.App/Speech/SrContext.cs
git commit -m "SrContext: 未検出時既定をSrRoute.Uiaへ・docコメントを新規則に更新"
```

---

### Task 4: ドキュメント更新（HANDOFF §13.4・引き継ぎ文書）

**Files:**
- Modify: `docs/HANDOFF-scintilla-uia.md`（§13.4）
- Modify: `docs/plans/2026-07-04-validate-uia-handoff.md`（§3(2)）

**Step 1: HANDOFF §13.4 の SR 適応表を 3 経路に更新**

旧（§13.4 冒頭の表）:

```
**起動時に SR を判定して構成を切替**（クラスは常に元の "Scintilla"）:
| 判定 | ServeUiaProvider | 読まれ方 |
|---|---|---|
| **NVDA 起動中** | **false** | NVDA が**ネイティブ Scintilla**を読む（64bit でも読めた・実機確認） |
| それ以外（PC-Talker 等） | **true** | PC-Talker が**我々の UIA**を読む |
```

新:

```
**起動時に SR を判定して構成を切替**（クラスは常に元の "Scintilla"）:
| 判定 | ServeUiaProvider | 読まれ方 |
|---|---|---|
| **NVDA 経路**（NVDA 検出。両方稼働で優先=NVDA を含む） | **false** | NVDA が**ネイティブ Scintilla**を読む（64bit でも読めた・実機確認） |
| **PC-Talker 経路**（PC-Talker 検出。両方稼働で優先=PC-Talker を含む） | **true** | PC-Talker が**我々の UIA**を読む |
| **汎用 UIA 経路**（どちらも非検出・2026-07-04 新設） | **true** | ナレーター/JAWS 等の UIA 系 SR・SR なしで安全 |
```

**Step 2: 「判定の要」の行を新規則へ更新（旧 API 名 `ScreenReaders.IsNvdaRunning()` の残骸修正を含む）**

旧:

```
- 判定の要は「**NVDA が動いているか**」だけ: `ScreenReaders.IsNvdaRunning()` = プロセス `nvda` の有無。NVDA があれば譲り、無ければ UIA を出す（PC-Talker・SRなし・他UIA系SR で安全）。
```

新:

```
- 経路解決規則（2026-07-04 改定・docs/plans/2026-07-04-sr-route-no-sr-design.md）: **検出された SR の経路。両方稼働なら「優先するスクリーンリーダー」設定が決める。どちらも非検出なら汎用 UIA 経路**（純ロジック = Core の `SrRouteSelector.Select`）。NVDA 検出はプロセス `nvda` の有無（`SrContext` 内 private）。「NVDA があれば譲り、無ければ UIA を出す」原則は不変（PC-Talker・SRなし・他UIA系SR で安全）。
```

**Step 3: 引き継ぎ文書 §3(2) に対応済みを追記**

`docs/plans/2026-07-04-validate-uia-handoff.md` の「### (2) SR 非稼働時の既定経路の退行修正（他 SR 対応上まずい）」セクション末尾（`- テスト: ...` の行の後）に追記:

```
- **対応済み（2026-07-04・fix/sr-route-no-sr）**: 汎用 UIA 経路 `SrRoute.Uia` を新設し「検出された SR の経路。両方稼働なら優先設定。どちらも非検出なら汎用 UIA 経路」に改定。能動/受動の軸分離は不要だった（第 3 値で両導出が自然に正しくなる）。設計 = docs/plans/2026-07-04-sr-route-no-sr-design.md。
```

**Step 4: コミット**

```powershell
git add docs/HANDOFF-scintilla-uia.md docs/plans/2026-07-04-validate-uia-handoff.md
git commit -m "docs: HANDOFF §13.4を3経路の新規則に更新・引き継ぎ(2)を対応済みに"
```

---

### Task 5: 検証とマージ（マージ前 DoD）

**Step 1: SR なし環境での UIA 提供をスクリプト確認**

NVDA・PC-Talker が**どちらも動いていない**状態で実施（NVDA が動いていると NVDA 経路になり False が正しい値になるため注意）。`tools/verify-msaa-client.ps1`（未追跡・作業ツリーに残置）の計測部を流用し、既定設定でビルドした yEdit を起動してエディタ hwnd の `UiaHasServerSideProvider` を確認。

Expected: `UiaHasServerSideProvider == True`（修正前の main では False＝退行の実証）。

スクリプトが無い/合わない場合は `docs/plans/2026-07-04-validate-uia-handoff.md` §2 の方法欄を基に再作成する。

**Step 2: 別エージェントによるコードレビュー（プロジェクト規約）**

superpowers:requesting-code-review の手順に従い、ブランチの全差分（設計書コミット含む）をレビュー依頼。指摘があれば修正して再実行。

**Step 3: 実機 SR 検証（ユーザー実施・マージのゲート）**

ここで**必ず手を止めてユーザーに依頼**する。チェック観点:

1. NVDA 稼働で起動 → 従来どおり読める（経路不変の回帰確認: ←→ 1 文字・↑↓ 1 行・空行「ブランク」・他ウィンドウ往復後の読み上げ継続）
2. PC-Talker 稼働で起動 → 従来どおり読める（同上＋空行「空行」能動発声）
3. 可能なら: SR なし既定設定で起動しナレーターで読み上げ確認（新経路の実機確認）

**Step 4: main へ no-ff マージ（ユーザー OK 後）**

```powershell
git checkout main
git merge --no-ff fix/sr-route-no-sr -m "Merge branch 'fix/sr-route-no-sr': SR非検出時の汎用UIA経路(SrRoute.Uia)導入"
dotnet build yEdit.sln
dotnet test yEdit.sln
```

Expected: マージ後も 0 警告・全テスト緑。

**Step 5: メモリ更新**

`validate-uia-msaa.md` の「(2) 実装待ち」を「実装済・mainマージ済」へ更新し、`MEMORY.md` の該当行も揃える。
