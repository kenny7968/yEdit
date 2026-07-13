# テスト戦略 Phase 1(ローカルゲート+CI)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** main マージ前のローカルゲートスクリプトと、push/PR で Core+Editor テストが走る GitHub Actions を新設し、release.yml のテストゲートを拡張する。

**Architecture:** ローカルゲート(`tools/pre-merge-check.ps1`)を正とし、GitHub CI(`ci.yml` 新設+`release.yml` 拡張)を push 時・リリース時の防衛線とする。ビルドは `-warnaserror` で 0 警告を強制。ホステッドランナーで不安定になり得る Editor.Tests は `Category!=LocalOnly` フィルタで将来隔離できる形にしておく(現時点で隔離対象はゼロ)。

**Tech Stack:** PowerShell 5.1(ローカル)/GitHub Actions windows-latest+pwsh/dotnet 9/xUnit

**設計書:** `docs/plans/2026-07-13-test-strategy-design.md` §2

**制約(重要):** local main は origin へ未 push(200+ コミット先行)。ブランチを push すると未公開履歴がすべて公開されるため、**CI ワークフローの実機検証は行わない**。YAML は目視レビューのみとし、初回実機検証は「ユーザーが次に push するとき」に申し送る。

---

### Task 1: ローカルゲートスクリプト `tools/pre-merge-check.ps1`

**Files:**
- Create: `tools/pre-merge-check.ps1`

**Step 1: スクリプトを作成**

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
  main マージ前のローカルゲート。Release ビルド 0 警告+全テスト緑を確認する。
  失敗があれば EXIT 1。典拠: docs/plans/2026-07-13-test-strategy-design.md §2.1
.EXAMPLE
  powershell -File tools\pre-merge-check.ps1
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "NG: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
}

Invoke-Step 'Release ビルド(警告=エラー)' {
    dotnet build (Join-Path $repoRoot 'yEdit.sln') -c Release -warnaserror
}
Invoke-Step 'Core.Tests' {
    dotnet test (Join-Path $repoRoot 'tests/yEdit.Core.Tests') -c Release --no-build
}
Invoke-Step 'Editor.Tests' {
    dotnet test (Join-Path $repoRoot 'tests/yEdit.Editor.Tests') -c Release --no-build
}
Write-Host 'OK: pre-merge チェック全通過' -ForegroundColor Green
exit 0
```

設計メモ:
- ローカルゲートは CI と違い **LocalOnly フィルタを付けない**(全件実行がローカルの責務。設計書 §2.2)。
- `--no-build` はビルドステップ済みのため。テストプロジェクトは yEdit.sln に含まれており、sln の Release ビルドでテスト DLL も生成される。
- PowerShell 5.1 で動くこと(pwsh 専用構文 `&&` や三項演算子を使わない)。

**Step 2: 失敗伝播をレビューで確認**

`Invoke-Step` が `$LASTEXITCODE -ne 0` で `exit 1` すること、3 ステップすべてが `Invoke-Step` 経由であることを目視確認。

**Step 3: 成功パスを実行して検証**

Run: `powershell -File tools\pre-merge-check.ps1`(タイムアウト 10 分で実行)
Expected: 3 ステップとも通過し、最後に `OK: pre-merge チェック全通過`、`$LASTEXITCODE` = 0。
(main 相当のコードベースは 0 警告・827 テスト緑の実績があるため、失敗したらスクリプト側の問題を疑う)

**Step 4: コミット**

```
git add tools/pre-merge-check.ps1
git commit -m "ローカルゲート tools/pre-merge-check.ps1 を追加(ビルド0警告+Core/Editorテスト)"
```

---

### Task 2: GitHub Actions `ci.yml` 新設

**Files:**
- Create: `.github/workflows/ci.yml`

**Step 1: ワークフローを作成**

```yaml
name: ci

on:
  push:
    branches: ['**']    # 全ブランチ。tags は release.yml の担当
  pull_request:
    branches: [main]

jobs:
  test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.0.x

      - name: ビルド(警告=エラー)
        run: dotnet build yEdit.sln -c Release -warnaserror

      - name: Core.Tests
        run: dotnet test tests/yEdit.Core.Tests -c Release --no-build

      - name: Editor.Tests(LocalOnly 除外)
        run: dotnet test tests/yEdit.Editor.Tests -c Release --no-build --filter "Category!=LocalOnly"
```

設計メモ:
- テストは**ステップを分ける**こと。Actions の pwsh は複数行 run の途中の exit code を検査しないため、1 ステップにまとめると前段の失敗が握りつぶされる。
- `--filter "Category!=LocalOnly"` は該当 Trait を持つテストがゼロでも有効(全件実行になる)。将来ホステッドランナーで不安定なテストが見つかったら `[Trait("Category", "LocalOnly")]` を付けて隔離する(設計書 §2.2)。
- `on.push.branches` 指定によりタグ push では発火しない(release.yml と重複しない)。
- レビュー指摘により `permissions: contents: read`(トップレベル・最小権限)と `timeout-minutes: 20`(jobs.test 直下・Editor.Tests のハング型フレーク対策)を追加(計画からの意図的逸脱)。

**Step 2: 目視レビュー**

インデント・ステップ分割・フィルタ引用符を確認。**push による実機検証はしない**(未公開履歴の公開を伴うため。冒頭の制約参照)。

**Step 3: コミット**

```
git add .github/workflows/ci.yml
git commit -m "CI ワークフロー新設(push/PR で Release ビルド+Core/Editor テスト)"
```

---

### Task 3: `release.yml` のテストゲート拡張

**Files:**
- Modify: `.github/workflows/release.yml:25-26`

**Step 1: テストステップを差し替え**

現状:

```yaml
      - name: テスト(リリース前ゲート)
        run: dotnet test tests/yEdit.Core.Tests -c Release
```

変更後(**2 ステップに分割**。理由は Task 2 の設計メモと同じ):

```yaml
      - name: テスト(リリース前ゲート: Core)
        run: dotnet test tests/yEdit.Core.Tests -c Release

      - name: テスト(リリース前ゲート: Editor・LocalOnly 除外)
        run: dotnet test tests/yEdit.Editor.Tests -c Release --filter "Category!=LocalOnly"
```

**Step 2: 目視レビュー**

release.yml の他ステップ(バージョン抽出以降)に影響がないこと、インデントが既存と揃っていることを確認。

**Step 3: コミット**

```
git add .github/workflows/release.yml
git commit -m "release.yml のテストゲートに Editor.Tests を追加"
```

---

### Task 4: 最終検証(ローカルゲートの本番実行)

**Step 1: ローカルゲートをフル実行**

Run: `powershell -File tools\pre-merge-check.ps1`(タイムアウト 10 分)
Expected: EXIT 0・`OK: pre-merge チェック全通過`。これが Phase 1 の DoD(かつ本ブランチ自身のマージ前ゲートの初適用)。

**Step 2: 設計書 §6 に申し送りを追記**

`docs/plans/2026-07-13-test-strategy-design.md` §6 に追記:

```markdown
- ci.yml / release.yml 拡張の実機検証は未実施(未公開履歴の公開を避けるため push しない方針)。ユーザーが次回 origin へ push した際に初回 CI 実行を確認し、不安定テストがあれば LocalOnly 隔離を行うこと。
```

**Step 3: コミット**

```
git add docs/plans/2026-07-13-test-strategy-design.md
git commit -m "テスト戦略設計書に CI 実機検証の申し送りを追記"
```

---

## 完了後

- superpowers:finishing-a-development-branch に従い、レビュー(別エージェント)→ main へ no-ff マージ。
- マージ後の運用: **以後、main への no-ff マージ前に `tools/pre-merge-check.ps1` を必ず実行**(設計書 §2.1 の規約)。
