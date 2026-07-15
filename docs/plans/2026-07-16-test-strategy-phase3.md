# テスト戦略 Phase 3: SR 呼び出しパターン回帰スイート+性能ゲート CI 組み込み 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** UIA クライアント経路の一次スクリーニング用ラッパ `tools/sr-regression.ps1`(既存 2 本の統合)と、性能ゲートの手動 CI ワークフロー `.github/workflows/bench.yml` を新設し、Phase 3 設計書 §1・§2 を実装として着地させる。挙動不変(本体コードは無変更)。

**Architecture:** 2 本のスクリプトを 1 本のアグリゲータ(`sr-regression.ps1`)から順次実行して総合 EXIT を返す方式。アグリゲータ側で `yEdit.Editor.Smoke` を Release ビルド→出力パスを子スクリプトに `-Exe` で受け渡し(既定パスの分散を根絶)。CI ワークフローは `workflow_dispatch` 手動トリガのみで、`continue-on-error: true` のジョブとしてホステッドランナーの EXIT はブロッカーにしない。ローカル実行を正とする Phase 3 設計書 §2 の方針をそのまま反映。

**Tech Stack:** PowerShell 5.1 / GitHub Actions(windows-latest)/ .NET 9 / yEdit.Editor.Smoke(--uia モード)

- 日付: 2026-07-16
- 上位文書:
  - `docs/plans/2026-07-13-test-strategy-design.md` §4(Phase 3 の位置づけ)
  - `docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md` §1・§2(本 Phase の設計書)
- ベースライン: main `be99132`(テストレビュー回収 Batch E 完了)・テスト数 954+App 拡張分(直近では 226→239=`test-review-cleanup-complete.md` 記録)
- 着手動機: ユーザー判断で **能動的着手**(設計書 §4 の 3 条件は未該当だが、害が小さいため今のうちに整備しておく)

---

## 0. 設計精密化(上位文書 §1・§2 からの追記=4 点)

上位文書は「アグリゲータで PASS/FAIL 集計・古い worktree パスを修正・`workflow_dispatch`・`continue-on-error: true`」までを規定している。実装として着地させる際の追加判断を 4 点明記する。

1. **既定 `$Exe` パスは Release + `$PSScriptRoot` 相対に統一**: 現状の 3 本(word-sim / verify-uia-editor / walk-test-editor)はいずれも worktree 時代の `<repo>\.worktrees\custom-editcontrol-design\...\Debug\...` を hardcode している。統一先=`(Split-Path -Parent $PSScriptRoot) + '\tests\yEdit.Editor.Smoke\bin\Release\net9.0-windows\yEdit.Editor.Smoke.exe'`(pre-merge-check.ps1 と同型の repo root 解決)。Release にする理由=アグリゲータが `dotnet build -c Release` で 1 回だけビルドしてパスを子スクリプトへ渡す運用で二重ビルドを避けるため+Phase 2 の pre-merge-check.ps1 と揃えて他ローカルビルドと共有できるため。

2. **`walk-test-editor.ps1` は廃止**(ユーザー確定 2026-07-16): PC-Talker サポート廃止([[pctalker-removal]])に伴い、PC-Talker の 1 文字走査の再現テストは維持動機を失った。汎用 UIA クライアント堅牢性の目的では word-sim.ps1(TextUnit.Word のスパン保持)が同等かそれ以上の網羅性を持つ(Case 3 の Move スパン保持は本質的に同じロジックを踏む)。**削除する**(履歴は git 上に残る=将来復活時は git log で参照可能)。

3. **アグリゲータの責務は「順序・パス配布・集計」のみ**(スクリプト本体には触らない): sr-regression.ps1 は (a) Release ビルド → (b) verify-uia-editor.ps1 → (c) word-sim.ps1 の順で子プロセスとして実行し、各 EXIT を集計して総合 EXIT を返す。**子スクリプトの中身は既定 `$Exe` の 1 行修正のみ**(判定ロジック改変は本 Phase の責務外=既存挙動を尊重)。設計書 §1「実行タイミング=a11y 関連変更のマージ前+リリース前・pre-merge には組み込まない」の運用に合わせるため、pre-merge-check.ps1 には呼び出しを追加しない。

4. **bench.yml は 2 ステップ独立(--typing と --layout)+ジョブ全体 `continue-on-error`**: 設計書 §2 は「両方をレポートとして表示・EXIT 1 は参考情報」を規定。実装は step 単位で `dotnet run --project tests/yEdit.Core.Bench -c Release -- --typing` と `... -- --layout` の 2 ステップに分け、それぞれ `continue-on-error: true` を付ける。ジョブ末尾に「ローカル実行を正とする」旨のバナーを Write-Output で出す(将来の運用者が混乱しないための最小の注記)。Bench の実装は現状 `--typing` / `--layout` / `--mb <N>` の 3 モードで、`--typing` は単独で早期 return する仕様(Program.cs L36-53)なので 2 ステップ順序に依存はない。

## 1. スコープ

- **新規**: `tools/sr-regression.ps1`(アグリゲータ・約 60〜80 行)・`.github/workflows/bench.yml`(手動性能ゲート・約 40 行)
- **修正**: `tools/verify-uia-editor.ps1`(既定 `$Exe` の 1 行)・`tools/word-sim.ps1`(既定 `$Exe` の 1 行)
- **削除**: `tools/walk-test-editor.ps1`
- **ドキュメント更新**: `docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md`(§1 統合対象表から walk-test-editor.ps1 の行を削除・§4 着手条件下に「2026-07-16 実装完了」の追記)・メモリ `memory/test-strategy.md` の Phase 3 状態更新
- **触らないもの**: pre-merge-check.ps1(sr-regression.ps1 は組み込まない・設計書 §1)・ci.yml/release.yml(Phase 3 の bench は独立ワークフロー・設計書 §2)・本体コード(挙動不変)・テストプロジェクト全体(単体テストの追加はしない=ツーリング/ワークフローのみの Phase)

## 2. テスト戦略

- **単体テストは追加しない**: 本 Phase の変更は tooling+CI ワークフローのみで、本体コードに触らない。App/Core/Editor Tests の増減はゼロ。
- **手動検証(Task 4・Task 5 の verify)**: 
  - sr-regression.ps1 → ローカルで実行し EXIT 0(全 PASS)を確認
  - bench.yml → 初回 push 時に手動 dispatch し、レポートが表示されジョブが `continue-on-error` で成功扱いになることを確認(**Task の verify に組み込むのは困難=push 前は原則ローカル再現できないため、README/コミット本文の申し送りとしてユーザーへ移譲**)
- **pre-merge-check.ps1**: 各 Task 末尾で 0 警告+全テスト緑を確認(本体無変更なので絶対に赤くならないが、SDD の規約として保険で走らせる)
- **L5 実機 SR 手動チェックリスト**: 不要(SR 経路への変更なし・設計書 §1 の「判定の限界」節に該当)

## 3. 規約(全 Task 共通)

- ブランチ: `feature/test-strategy-phase3`(同一ディレクトリのフィーチャーブランチ→main へ no-ff マージ=いつもの運用)
- コミットメッセージは日本語。末尾に `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` を付ける
- 各 Task 末尾で `powershell -File tools\pre-merge-check.ps1` が緑であること(本体無変更で赤にならないはずだが SDD 規約として)
- git status に見えている untracked の `installer/`・`publish/` はこの作業と無関係。**絶対にコミットに含めない**(`git add` はパス指定で行う)
- スクリプトの新規/修正は ASCII-safe な PowerShell 5.1 構文(既存 2 本と同じ流儀=`Write-Output '[PASS]'` の枠線は ASCII 固定、コメント/メッセージは UTF-8 BOMless)

---

### Task 1: ブランチ作成

**Step 1: main から作業ブランチを切る**

Run:
```powershell
git switch -c feature/test-strategy-phase3 main
```
Expected: `Switched to a new branch 'feature/test-strategy-phase3'`

**Step 2: 事前状態を記録**

Run:
```powershell
git log -1 --oneline
```
Expected: `be99132 テストレビュー回収 Batch E: BackupStore 抽象化再評価の判断=見送り(Task 13)` またはユーザーが更に進めていれば最新の main コミット

---

### Task 2: walk-test-editor.ps1 削除+子スクリプト既定 `$Exe` 修正

**Files:**
- Delete: `tools/walk-test-editor.ps1`
- Modify: `tools/verify-uia-editor.ps1:4`(param の既定値 1 行)
- Modify: `tools/word-sim.ps1:12`(if 分岐の既定値 1 行)

**Step 1: walk-test-editor.ps1 を削除**

Run:
```powershell
git rm tools\walk-test-editor.ps1
```
Expected: `rm 'tools/walk-test-editor.ps1'`

**Step 2: verify-uia-editor.ps1 の既定 `$Exe` を repo 相対 Release に変更**

Edit `tools/verify-uia-editor.ps1`:

Old (L4):
```powershell
    [string]$Exe = "<repo>\.worktrees\custom-editcontrol-design\tests\yEdit.Editor.Smoke\bin\Debug\net9.0-windows\yEdit.Editor.Smoke.exe"
```

New (L4):
```powershell
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\yEdit.Editor.Smoke\bin\Release\net9.0-windows\yEdit.Editor.Smoke.exe')
```

**Step 3: word-sim.ps1 の既定 `$Exe` を repo 相対 Release に変更**

Edit `tools/word-sim.ps1`:

Old (L12):
```powershell
if (-not $Exe) { $Exe = "<repo>\.worktrees\custom-editcontrol-design\tests\yEdit.Editor.Smoke\bin\Debug\net9.0-windows\yEdit.Editor.Smoke.exe" }
```

New (L12):
```powershell
if (-not $Exe) { $Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\yEdit.Editor.Smoke\bin\Release\net9.0-windows\yEdit.Editor.Smoke.exe') }
```

**Step 4: 既存の参照が残っていないか確認**

Run:
```powershell
Get-ChildItem -Recurse -File -Include *.ps1,*.md | Select-String -Pattern 'walk-test-editor\.ps1'
```
Expected: 出力は設計書 `docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md` の統合対象表の 1 行のみ(Task 5 で削除)。ソースツリーの他ファイルにヒットが無ければ OK。

**Step 5: ビルドを回して既定 `$Exe` の指す先を実在させる(次 Task で動作確認に使う)**

Run:
```powershell
dotnet build tests\yEdit.Editor.Smoke -c Release
```
Expected: `Build succeeded.`+`0 Warning(s)`+`0 Error(s)`。ビルド後 `tests\yEdit.Editor.Smoke\bin\Release\net9.0-windows\yEdit.Editor.Smoke.exe` が存在すること(次の verify で `Test-Path` チェック)。

**Step 6: 修正済み 2 本を単独実行して既定パス修正が実際に動くか確認**

Run:
```powershell
powershell -File tools\verify-uia-editor.ps1
```
Expected: `[PASS] Found EditorControl element (AutomationId=editor)` を含む出力・EXIT 0(**フォアグラウンドのデスクトップセッションが必要**なので、Claude Code の非対話環境で自動テストとして走らせるとフォーカスが取れず失敗する可能性がある。実行時に手動確認へ切り替える判断をユーザーに求めてよい)。

Run:
```powershell
powershell -File tools\word-sim.ps1
```
Expected: `[PASS] Case1 Expand(Word)@abc -> 'abc'` ほか 6 ケースの PASS。EXIT 0。

**Step 7: pre-merge ゲート**

Run:
```powershell
powershell -File tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`+EXIT 0(本体無変更なので当然通る)

**Step 8: コミット**

```powershell
git add tools\verify-uia-editor.ps1 tools\word-sim.ps1
# walk-test-editor.ps1 は Step 1 で git rm 済みなので既にステージ済み
git status
git commit -m @'
chore(tools): Phase 3 準備=walk-test-editor.ps1 廃止+既定 $Exe を repo 相対 Release へ統一

- walk-test-editor.ps1 削除(PC-Talker サポート廃止に伴う不要化=word-sim.ps1 で堅牢性は担保)
- verify-uia-editor.ps1・word-sim.ps1 の既定 $Exe を worktree 時代のパスから
  $PSScriptRoot 相対 Release ビルド出力へ統一(pre-merge-check.ps1 と同型)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 3: `tools/sr-regression.ps1` アグリゲータ新設

**Files:**
- Create: `tools/sr-regression.ps1`

**Step 1: スクリプトを新規作成**

Create `tools/sr-regression.ps1`:

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
  Phase 3 SR 呼び出しパターン回帰スイート。UIA プロバイダに対する SR 側の呼び出しを
  UIA クライアントとして再現し、a11y 関連変更(yEdit.Accessibility / EditorControl の UIA 部 /
  Speech 系)のマージ前+リリース前の一次スクリーニングとして使う。
  典拠: docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md §1
.DESCRIPTION
  実行内容:
    1. yEdit.Editor.Smoke を Release ビルド(1 回のみ・子スクリプトが二重ビルドしない)
    2. verify-uia-editor.ps1 を実行(TextPattern/GetSelection/RangeFromPoint 疎通)
    3. word-sim.ps1 を実行(NVDA の TextUnit.Word 6 ケース)
  総合 EXIT: 全 PASS で 0・1 つでも FAIL なら 1。
  実行タイミング(設計書 §1): a11y 関連変更のマージ前+リリース前・pre-merge には組み込まない
  (UIA クライアントはフォアグラウンドのデスクトップセッションが必要で、常時実行に向かない)。
  判定の限界(設計書 §1 明記): 「UIA プロバイダが SR 呼び出しに正しく応答するか」までしか
  検証しない。「SR が実際に発声するか」(PC-Talker の空行問題のような発声側事象)は検出できない。
  PASS でも L5 の人手確認は省略しない。
.EXAMPLE
  powershell -File tools\sr-regression.ps1
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$smokeExe = Join-Path $repoRoot 'tests\yEdit.Editor.Smoke\bin\Release\net9.0-windows\yEdit.Editor.Smoke.exe'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Body
    return $LASTEXITCODE
}

$fail = 0

$rc = Invoke-Step 'Release ビルド(yEdit.Editor.Smoke)' {
    dotnet build (Join-Path $repoRoot 'tests\yEdit.Editor.Smoke') -c Release
}
if ($rc -ne 0) {
    Write-Host "NG: ビルド失敗 (exit $rc)" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $smokeExe)) {
    Write-Host "NG: ビルド後 exe が見つからない: $smokeExe" -ForegroundColor Red
    exit 1
}

$rc = Invoke-Step 'verify-uia-editor.ps1' {
    powershell -File (Join-Path $PSScriptRoot 'verify-uia-editor.ps1') -Exe $smokeExe
}
if ($rc -ne 0) { $fail++ ; Write-Host "  → FAIL (exit $rc)" -ForegroundColor Red }

$rc = Invoke-Step 'word-sim.ps1' {
    powershell -File (Join-Path $PSScriptRoot 'word-sim.ps1') -Exe $smokeExe
}
if ($rc -ne 0) { $fail++ ; Write-Host "  → FAIL (exit $rc)" -ForegroundColor Red }

Write-Host ''
if ($fail -eq 0) {
    Write-Host 'OK: SR 回帰スイート 全通過(ただし L5 人手確認は別途必要)' -ForegroundColor Green
    exit 0
} else {
    Write-Host "NG: $fail 件の子スクリプトが FAIL" -ForegroundColor Red
    exit 1
}
```

**Step 2: 実行して EXIT 0 を確認**

Run:
```powershell
powershell -File tools\sr-regression.ps1
```
Expected: 
- `==> Release ビルド(yEdit.Editor.Smoke)` → Build succeeded
- `==> verify-uia-editor.ps1` → 4〜5 件の `[PASS]`
- `==> word-sim.ps1` → 6 件の `[PASS]`
- `OK: SR 回帰スイート 全通過(ただし L5 人手確認は別途必要)`
- EXIT 0

**注意**: フォアグラウンドセッションが必要。Claude Code の非対話ターミナルでは smoke.exe のフォーカス取得に失敗して UIA element が見つからない可能性がある(`[FAIL] Found EditorControl element` が出る)。**ユーザーの実機ターミナルで実行してもらう手順に切り替えることを Task の verify で明示する**。

**Step 3: pre-merge ゲート**

Run:
```powershell
powershell -File tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`

**Step 4: コミット**

```powershell
git add tools\sr-regression.ps1
git commit -m @'
feat(tools): Phase 3 SR 呼び出しパターン回帰スイートを追加(sr-regression.ps1)

- verify-uia-editor.ps1(TextPattern/GetSelection/RangeFromPoint 疎通)と
  word-sim.ps1(NVDA の TextUnit.Word 6 ケース)を順次実行し PASS/FAIL を集計
- Release ビルド 1 回で子スクリプトへ $Exe を配布=二重ビルド回避
- pre-merge-check.ps1 には組み込まない(UIA クライアントはフォアグラウンド
  デスクトップセッションが必要=常時実行に不向き・設計書 §1 準拠)
- L5 人手確認の代替ではない(判定の限界=SR の実発声は検出できない)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 4: `.github/workflows/bench.yml` 手動性能ゲート新設

**Files:**
- Create: `.github/workflows/bench.yml`

**Step 1: ワークフローを新規作成**

Create `.github/workflows/bench.yml`:

```yaml
name: bench

# Phase 3 性能ゲート(手動)。docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md §2
# ・トリガは workflow_dispatch のみ(push/PR には組み込まない=実行時間とランナー性能揺れのため)
# ・しきい値ゲート(EXIT 判定)はローカル実行を正とする(ホステッドランナーの EXIT 1 は参考情報)
# ・continue-on-error: true でリリースブロッカーにしない
on:
  workflow_dispatch:

permissions:
  contents: read

jobs:
  bench:
    runs-on: windows-latest
    timeout-minutes: 30
    continue-on-error: true
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.0.x

      - name: ビルド(Release)
        run: dotnet build tests/yEdit.Core.Bench -c Release

      - name: 応答性ベンチ(--typing)
        continue-on-error: true
        run: dotnet run --project tests/yEdit.Core.Bench -c Release --no-build -- --typing

      - name: レイアウト+文書ベンチ(--layout)
        continue-on-error: true
        run: dotnet run --project tests/yEdit.Core.Bench -c Release --no-build -- --layout

      - name: 運用注記
        shell: pwsh
        run: |
          Write-Output '---'
          Write-Output 'このワークフローは Phase 3 設計書 §2 のとおり参考情報として実行しています。'
          Write-Output 'しきい値ゲートはローカル(tools/pre-merge-check.ps1 併走で dotnet run --typing / --layout)を正とし、'
          Write-Output 'ホステッドランナーの EXIT 1 はリリースブロッカーにしません。'
```

**Step 2: YAML の構文確認(ローカルで)**

Run:
```powershell
# YAML パースは gh CLI があれば `gh workflow view` で確認可能だが、初回 push 前は不可。
# 最小の妥当性チェックとして構造だけ Get-Content で目視確認。
Get-Content .github\workflows\bench.yml | Select-Object -First 10
```
Expected: `name: bench` を含む先頭 10 行が正しく出る。より厳密なチェックは初回 push 時に GitHub Actions 側で判定=Task の verify に組み込まない(申し送り扱い)。

**Step 3: pre-merge ゲート**

Run:
```powershell
powershell -File tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`

**Step 4: コミット**

```powershell
git add .github\workflows\bench.yml
git commit -m @'
feat(ci): Phase 3 性能ゲート bench.yml を追加(手動 workflow_dispatch)

- yEdit.Core.Bench の --typing / --layout をレポート表示するだけの手動ワークフロー
- ジョブと各ベンチステップに continue-on-error: true=リリースブロッカーにしない
- しきい値ゲートはローカル実行を正とする(設計書 §2)
- ci.yml/release.yml とは独立(相互干渉なし)
- 初回動作確認は次回 push 時にユーザーが手動 dispatch で行う

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5: 設計書とメモリの更新

**Files:**
- Modify: `docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md`(§1 の統合対象表・§4 の末尾)
- Modify: `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\test-strategy.md`(Phase 3 完了の記録)

**Step 1: 設計書 §1 の統合対象表から walk-test-editor.ps1 の行を削除**

Edit `docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md`:

Old (§1「統合対象(既存資産)」の表・4 行):
```
| スクリプト | 再現している SR 挙動 |
|---|---|
| `tools/verify-uia-editor.ps1` | TextPattern/DocumentRange/GetSelection/RangeFromPoint の基本疎通 |
| `tools/walk-test-editor.ps1` | PC-Talker の 1 文字ずつの行走査(Expand(Char)→Move(Char,1) 反復) |
| `tools/word-sim.ps1` | NVDA の TextUnit.Word 挙動(Expand/Move スパン保持/MoveEndpointByUnit・6 ケース) |
| `tools/verify-msaa-client.ps1`(未コミット) | MSAA クライアント経路(統合時に採否を確定) |
```

New(2 行に集約・注記追加):
```
| スクリプト | 再現している SR 挙動 |
|---|---|
| `tools/verify-uia-editor.ps1` | TextPattern/DocumentRange/GetSelection/RangeFromPoint の基本疎通 |
| `tools/word-sim.ps1` | NVDA の TextUnit.Word 挙動(Expand/Move スパン保持/MoveEndpointByUnit・6 ケース) |

> 2026-07-16 実装時決定:
> - `tools/walk-test-editor.ps1` は削除(PC-Talker サポート廃止=[[pctalker-removal]] に伴い維持動機喪失。Move スパン保持は word-sim.ps1 Case 3 で網羅)
> - `tools/verify-msaa-client.ps1`(未コミット)は PC-Talker 廃止で除外確定
```

**Step 2: 設計書 §4 末尾に実装完了の追記**

Edit `docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md`:

§4 の末尾(既存の `3. UIA プロバイダ(yEdit.Accessibility)に大きな変更を計画している` の下)に追加:

```markdown

---

**2026-07-16 実装完了**: 上記 3 条件のいずれも該当していなかったが、害が小さいためユーザー判断で能動的に着手し実装完了(実装計画=`docs/plans/2026-07-16-test-strategy-phase3.md`)。sr-regression.ps1 と bench.yml を追加。walk-test-editor.ps1 は同時に削除。
```

**Step 3: メモリ `test-strategy.md` の Phase 2/3 状態を更新**

Edit `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\test-strategy.md`:

`description` フィールド(L3):

Old:
```
description: プロジェクト全体のテスト戦略(5層ピラミッド)。**Phase 1+Phase 2(Stage 1〜8)完了=Phase 2 終了宣言**・main マージ済(Stage 8=`fb9159b`・2026-07-15・main 未 push)。次=Phase 3(任意=SR 性能ゲート・実機退行観測時のみ検討)
```

New:
```
description: プロジェクト全体のテスト戦略(5層ピラミッド)。**Phase 1+Phase 2(Stage 1〜8)完了+Phase 3(sr-regression.ps1+bench.yml)完了**・main マージ済(Phase 3=`<マージ後コミットハッシュ>`・2026-07-16・main 未 push)。全 Phase 実装完了
```

L34 の `**Phase 2 完了(Stage 8 で終了)**` セクションの末尾に追加:

Old:
```
**Phase 2 完了(Stage 8 で終了)**: 上位文書 §4 のとおり Stage 7 完了時点で費用対効果を再評価し、薄い Stage 8(A/B/C/D 4 Task)として実施→完了。原案の他コマンド抽出(§1.1)は継続見送り。**次=Phase 3(SR 性能ゲート・任意)は実機 SR で退行が観測された場合のみ着手検討**
```

New:
```
**Phase 2 完了(Stage 8 で終了)**: 上位文書 §4 のとおり Stage 7 完了時点で費用対効果を再評価し、薄い Stage 8(A/B/C/D 4 Task)として実施→完了。原案の他コマンド抽出(§1.1)は継続見送り。

**Phase 3 完了(`<マージ後コミットハッシュ>`・2026-07-16)**: 設計書 §4 の 3 条件は未該当だったが害が小さいためユーザー判断で能動的着手・完了。①`tools/sr-regression.ps1`(verify-uia-editor.ps1+word-sim.ps1 のアグリゲータ・Release ビルド 1 回で子スクリプトへ $Exe 配布)②`.github/workflows/bench.yml`(手動 workflow_dispatch・continue-on-error でリリースブロッカーにしない=ローカル実行が正)③`tools/walk-test-editor.ps1` 削除(PC-Talker サポート廃止=[[pctalker-removal]] で維持動機喪失)。挙動不変・本体コード無変更・テスト数不変。運用=a11y 関連変更(yEdit.Accessibility / EditorControl の UIA 部 / Speech 系)のマージ前+リリース前に sr-regression.ps1 を手動実行。bench.yml は初回 dispatch 時にユーザーが動作確認。**判定の限界**: sr-regression.ps1 は「UIA プロバイダが SR 呼び出しに正しく応答するか」までで、SR の実発声は検出できない=L5 人手確認は省略しない
```

**Step 4: pre-merge ゲート**

Run:
```powershell
powershell -File tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`

**Step 5: コミット**(コミットハッシュはこの時点で確定していないので `<マージ後コミットハッシュ>` プレースホルダを含んだままコミットし、Task 6 のマージ後に追記コミットで確定させる=Phase 2 Stage 4 以降で採用済みのパターン)

```powershell
git add docs\plans\2026-07-13-test-strategy-phase3-sr-perf-design.md
git commit -m @'
docs(phase3): 設計書の統合対象表を更新+§4 に実装完了追記

- §1: walk-test-editor.ps1 / verify-msaa-client.ps1(未コミット)を統合対象表から除外
  (PC-Talker サポート廃止=[[pctalker-removal]] に伴う整理)
- §4: 3 条件未該当だったが害が小さいためユーザー判断で能動的着手した旨を追記

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

メモリの更新はコミット対象外(auto memory 側でユーザーの `%USERPROFILE%\...` 直下に書く=git 管理外)なので、Write ツールで直接更新のみ。マージ後にハッシュを確定させる追記もメモリ側だけで完結する。

---

### Task 6: 最終レビュー+main へマージ+ハッシュ追記

**Step 1: 差分の要約を作成**

Run:
```powershell
git log --oneline main..HEAD
git diff --stat main..HEAD
```
Expected:
- 4 コミット(Task 2・Task 3・Task 4・Task 5 の各 1 本)
- 変更ファイル: `tools/walk-test-editor.ps1`(削除)・`tools/verify-uia-editor.ps1`(1 行)・`tools/word-sim.ps1`(1 行)・`tools/sr-regression.ps1`(新規)・`.github/workflows/bench.yml`(新規)・`docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md`(部分修正)

**Step 2: 別エージェントによるコードレビュー依頼**

Agent tool(`superpowers:code-reviewer`)にブランチ全体をレビュー依頼(参照: [[review-by-separate-agent]])。焦点:
- パス解決(`Split-Path -Parent $PSScriptRoot`)がスクリプト移動や `pwsh` 実行時に破綻しないか
- `Invoke-Step` の `$global:LASTEXITCODE` 制御の正しさ(PS 5.1 の native command と scriptblock の混在)
- bench.yml の `--no-build` が build ステップの成果物をきちんと使うか(`dotnet run` のデフォルト挙動)
- 設計書 §1・§2・§4 との整合性
- walk-test-editor.ps1 削除に伴う参照残骸

「マージ可」判定が出るまで指摘を修正する。判定が「マージ可」に至ったらそのレビュー結果をコミットに含める必要はない(GH PR ではないため=Phase 2 と同じ流儀)。

**Step 3: レビュー指摘の反映(あれば追加コミット)**

指摘があれば個別コミットで反映。挙動に影響しない指摘は追加コミット、影響するものは仕様相談へエスカレーション。

**Step 4: main へ no-ff マージ**

Run:
```powershell
powershell -File tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`

Run:
```powershell
git switch main
git merge --no-ff feature/test-strategy-phase3 -m @'
Phase 3(SR 呼び出しパターン回帰スイート+性能ゲート CI 組み込み)完了

- tools/sr-regression.ps1: verify-uia-editor.ps1+word-sim.ps1 のアグリゲータ
- .github/workflows/bench.yml: 手動 workflow_dispatch 性能ゲート
- tools/walk-test-editor.ps1: 削除(PC-Talker サポート廃止に伴う不要化)
- tools/verify-uia-editor.ps1 / word-sim.ps1: 既定 $Exe を repo 相対 Release へ統一
- 挙動不変・本体コード無変更・テスト数不変
- 判定の限界=SR の実発声は検出できない・L5 人手確認は省略しない

典拠: docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md
実装計画: docs/plans/2026-07-16-test-strategy-phase3.md
'@
```

**Step 5: マージ後のゲート**

Run:
```powershell
powershell -File tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`

**Step 6: マージハッシュを設計書とメモリに追記**

Run:
```powershell
git log -1 --format=%H
```
Expected: マージコミットの SHA(例: `abcdef1234567890...`)。

Edit `docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md` の §4 末尾で `<マージ後コミットハッシュ>` プレースホルダを実ハッシュに置換。

Edit `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\test-strategy.md` の `description` と本文の Phase 3 完了行の `<マージ後コミットハッシュ>` プレースホルダを実ハッシュに置換。

コミット:
```powershell
git add docs\plans\2026-07-13-test-strategy-phase3-sr-perf-design.md
git commit -m @'
docs(phase3): マージ後コミットハッシュを追記

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

**Step 7: 作業ブランチ削除**

Run:
```powershell
git branch -d feature/test-strategy-phase3
```
Expected: `Deleted branch feature/test-strategy-phase3 (was <hash>).`

**Step 8: 最終確認**

Run:
```powershell
git log --oneline -10
git status
```
Expected:
- 直近 10 コミットに Phase 3 のマージコミット+追記コミットが含まれる
- `working tree clean`(untracked の `installer/`・`publish/` は無視)

---

## 4. リスク・申し送り

- **sr-regression.ps1 の非対話環境での実行不安定**: Claude Code の非対話ターミナルでは UIA クライアントがフォーカス取れず失敗する可能性。Task 3 Step 2 は「ユーザーの実機ターミナルで実行してもらう手順」に切り替える判断を含めてある。
- **bench.yml の初回動作**: 次回 push 時にユーザーが手動 dispatch して動作確認する。ci.yml/release.yml と同じく初回未検証扱い([[test-strategy]] の申し送り末尾に既記載)。
- **`walk-test-editor.ps1` 廃止の非可逆性**: git 履歴には残るので将来復活時は `git log --diff-filter=D --name-only` で復元可能。復活動機として想定されるのは PC-Talker サポート再開時のみ。
- **Phase 3 の運用**: sr-regression.ps1 の実行タイミングは「a11y 関連変更のマージ前+リリース前」で、pre-merge-check.ps1 には組み込まない(設計書 §1・同じく本計画 §0-3)。今後 [[test-strategy]] メモリで参照する運用規約として明記する(Task 5)。
