# Linter / Formatter 導入 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** yEdit リポジトリに CSharpier(formatter)+ Roslynator + SonarAnalyzer(linter)+ Husky.Net(pre-commit hook)を導入し、`.editorconfig` を規約源とする体制を確立する。

**Architecture:** バランス派スタック(CSharpier + Roslynator + SonarAnalyzer)。5 本の分割 PR で段階的に main へマージ。ローカルゲートは Husky pre-commit + `tools/pre-merge-check.ps1` 統合、CI では `dotnet csharpier check` を verify として追加。既存 `-warnaserror` がアナライザのゲートを兼ねる。

**Tech Stack:** .NET 9 SDK / dotnet-csharpier 0.30.6 / Roslynator.Analyzers 4.12.9 / SonarAnalyzer.CSharp 10.4.0 / Husky.Net 0.7.2

**設計書**: `docs/plans/2026-07-18-lint-format-adoption-design.md`

**ブランチ運用**: 既存規約(`[[phase-work-git-flow]]`)= フィーチャーブランチ→ main へ no-ff マージ。PR 毎に別ブランチを切る。

---

## 前提条件

- リポジトリ ルート: `<repo>`
- 現在ブランチ: `feature/lint-format-adoption`(設計書 commit `1bcb808` を含む)
- 各 PR は本ブランチから枝分かれさせるか、PR1 の内容をベースに順次進める(下記各 PR の Task 0 参照)
- pre-merge-check.ps1 は main マージ前に必ず通す(既存ローカルゲート)
- コマンドは PowerShell 5.1 前提。Bash 例が必要な場合は明記する
- CSharpier 実行の場所: **リポジトリルート** (`<repo>`)

---

# PR1: 規約源の集約(コード変更ほぼゼロ)

**目的**: `.editorconfig` と `Directory.Build.props` を新設し、既存 csproj から共通プロパティを Directory.Build.props に集約する。この段階では **アナライザは追加しない**(挙動不変)。

**ブランチ**: `feature/lint-format-pr1-editorconfig`

**DoD**:
- `dotnet build yEdit.sln -c Release -warnaserror` が 0 warning で通る
- `powershell -File tools\pre-merge-check.ps1` が全ステップ緑
- 全 csproj から共通プロパティが Directory.Build.props に寄せられている
- コード側の diff はゼロ(csproj と新規 3 ファイルのみ)

### Task 1.0: ブランチ作成

**Step 1: 現在のブランチ状態確認**

```powershell
git status
git branch --show-current
```

Expected: `feature/lint-format-adoption` に居て clean

**Step 2: PR1 用ブランチを切る**

```powershell
git checkout -b feature/lint-format-pr1-editorconfig
```

### Task 1.1: `.editorconfig` を作成

**Files:**
- Create: `<repo>\.editorconfig`

**Step 1: ファイルを新規作成**

```ini
root = true

[*]
indent_style = space
indent_size = 4
end_of_line = crlf
charset = utf-8-bom
trim_trailing_whitespace = true
insert_final_newline = true

[*.{yml,yaml,json,md}]
indent_size = 2
charset = utf-8

[*.ps1]
end_of_line = crlf

[*.cs]
# フォーマット系ルールは CSharpier に完全委譲(二重整形防止)
dotnet_diagnostic.IDE0055.severity = none
```

**Step 2: build & test で挙動不変を確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: 0 warning, 0 error

### Task 1.2: `Directory.Build.props` を作成(アナライザ無し版)

**Files:**
- Create: `<repo>\Directory.Build.props`

**Step 1: ファイルを新規作成**

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>

    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>

    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

**注意**: この段階では Roslynator/SonarAnalyzer の PackageReference と `<AnalysisLevel>latest-recommended</AnalysisLevel>` は **入れない**(PR3/PR4 で追加)。理由 = 前者は外部アナライザで新規指摘を発生させる、後者はビルトイン CA ルールを追加有効化して既存コード違反(実装時に 18 件検出: CA1805/CA2249/CA1716/CA1875/CA1305/CA1861/CA1711)が発生するため。両者とも PR1 の「挙動不変」原則と衝突する。

**Step 2: build で挙動不変を確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: 0 warning, 0 error(各 csproj の重複プロパティは無視される)

### Task 1.3: `.git-blame-ignore-revs` を空ファイルで作成

**Files:**
- Create: `<repo>\.git-blame-ignore-revs`

**Step 1: ヘッダ付き空ファイル作成**

```
# git blame 除外対象の commit を列挙する。
# ここに列挙された commit の変更は `git blame --ignore-revs-file` で無視される。
# 大量整形(CSharpier 初回)等の履歴汚染を防止するため。
#
# ローカルで有効化するには一度だけ:
#   git config blame.ignoreRevsFile .git-blame-ignore-revs
#
# 以下、commit hash を 1 行 1 件で列挙する(コメント可)。
```

### Task 1.4: 各 csproj から共通プロパティを削除

**Files (全 9 個):**
- Modify: `src/yEdit.Accessibility/yEdit.Accessibility.csproj`
- Modify: `src/yEdit.Core/yEdit.Core.csproj`
- Modify: `src/yEdit.Editor/yEdit.Editor.csproj`
- Modify: `src/yEdit.App/yEdit.App.csproj`
- Modify: `tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
- Modify: `tests/yEdit.Core.Bench/yEdit.Core.Bench.csproj`
- Modify: `tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj`
- Modify: `tests/yEdit.Editor.Smoke/yEdit.Editor.Smoke.csproj`
- Modify: `tests/yEdit.App.Tests/yEdit.App.Tests.csproj`

**Step 1: 各 csproj から以下を削除(Directory.Build.props で共通化されるため)**

対象プロパティ:
- `<Nullable>enable</Nullable>` (**yEdit.Accessibility は `disable` なので削除禁止**)
- `<ImplicitUsings>enable</ImplicitUsings>`

**残すもの**:
- `TargetFramework` (プロジェクト毎に違う: net9.0 vs net9.0-windows)
- `UseWindowsForms` / `UseWPF` (プロジェクト毎)
- `AllowUnsafeBlocks` (yEdit.Editor)
- `OutputType` / `AssemblyName` (yEdit.App)
- `IsPackable` (tests)
- `Nullable=disable` (yEdit.Accessibility は独自の disable なので Directory.Build.props の enable を明示 override するため残す)

**具体例: `src/yEdit.Core/yEdit.Core.csproj` の変更前後**

Before:
```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

After:
```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
</PropertyGroup>
```

**yEdit.Accessibility は例外**: `<Nullable>disable</Nullable>` を残す(Directory.Build.props で共通に enable してるので、override として明示的に disable する)。

**Step 2: 全プロジェクト build 確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: 0 warning, 0 error

**Step 3: 全テスト実行**

```powershell
powershell -File tools\pre-merge-check.ps1
```

Expected: `OK: pre-merge チェック全通過`

### Task 1.5: commit

**Step 1: 変更内容を確認**

```powershell
git status
git diff --stat
```

Expected: 新規 3 ファイル(`.editorconfig`, `Directory.Build.props`, `.git-blame-ignore-revs`)+ 9 csproj 変更

**Step 2: commit**

```powershell
git add .editorconfig Directory.Build.props .git-blame-ignore-revs
git add src/**/*.csproj tests/**/*.csproj
git commit -m @'
build(config): .editorconfig + Directory.Build.props を導入(PR1)

- .editorconfig で規約源を一元化(indent 4/CRLF/UTF-8 BOM)
- Directory.Build.props に共通プロパティ(Nullable/ImplicitUsings/LangVersion/warn-as-error)を集約
- 各 csproj から共通プロパティを削除(挙動不変)
- .git-blame-ignore-revs を空ファイルで用意(PR2 で一括整形 commit を登録予定)

アナライザ(Roslynator/SonarAnalyzer)は PR3/PR4 で追加する。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

### Task 1.6: main へ no-ff マージ

**Step 1: pre-merge-check 最終確認**

```powershell
powershell -File tools\pre-merge-check.ps1
```

Expected: 全ステップ緑

**Step 2: main へマージ**

```powershell
git checkout main
git merge --no-ff feature/lint-format-pr1-editorconfig -m "Merge branch 'feature/lint-format-pr1-editorconfig' (Lint/Format PR1: 規約源の集約)"
```

**Step 3: マージ後確認**

```powershell
git log --oneline -5
powershell -File tools\pre-merge-check.ps1
```

Expected: main で最終確認緑

---

# PR2: CSharpier + Husky + 一括整形

**目的**: CSharpier で全ソースを整形、Husky.Net で pre-commit hook を配置、`pre-merge-check.ps1` に verify ステップを追加。**一括整形 commit を `.git-blame-ignore-revs` に登録**して blame を保護する。

**ブランチ**: `feature/lint-format-pr2-csharpier`(main から)

**DoD**:
- `dotnet csharpier check .` が 0 差分
- `dotnet build -warnaserror` が 0 warning
- 全テスト緑(挙動不変を保証)
- `.husky/pre-commit` が正しく発火する(手動テスト済み)
- `.git-blame-ignore-revs` に一括整形 commit の hash が登録済み
- `pre-merge-check.ps1` に csharpier check ステップ追加済み

### Task 2.0: ブランチ作成

```powershell
git checkout main
git checkout -b feature/lint-format-pr2-csharpier
```

### Task 2.1: `dotnet new tool-manifest`

**Files:**
- Create: `.config/dotnet-tools.json`

**Step 1: manifest 作成**

```powershell
dotnet new tool-manifest
```

Expected: `.config/dotnet-tools.json` が生成される(空の tools エントリ)

### Task 2.2: csharpier / husky を local tool として install

**Step 1: csharpier install**

```powershell
dotnet tool install csharpier
```

Expected: `.config/dotnet-tools.json` に csharpier エントリが追加される

**Step 2: husky install**

```powershell
dotnet tool install husky
```

Expected: 同上で husky エントリが追加される

**Step 3: install 結果確認**

```powershell
cat .config/dotnet-tools.json
dotnet tool restore
dotnet csharpier --version
dotnet husky --help
```

Expected: バージョン表示 + `dotnet husky` のヘルプが出る

### Task 2.3: `.csharpierrc.json` と `.csharpierignore` を作成

**Files:**
- Create: `<repo>\.csharpierrc.json`
- Create: `<repo>\.csharpierignore`

**Step 1: `.csharpierrc.json`**

```json
{
  "printWidth": 100,
  "endOfLine": "crlf"
}
```

**Step 2: `.csharpierignore`**

```
**/obj/
**/bin/
**/*.Designer.cs
```

**Step 3: Designer ファイル存在確認**(除外漏れの事前検出)

```powershell
Get-ChildItem -Path src, tests -Recurse -Include *.Designer.cs -ErrorAction SilentlyContinue
```

Expected: 出力ゼロ(このリポジトリはデザイナ生成を使っていない想定)。もし出た場合は `.csharpierignore` の妥当性を再確認。

### Task 2.4: Husky セットアップ

**Files:**
- Create: `.husky/pre-commit`
- Create: `.husky/task-runner.json`
- Create: `.husky/_/*` (husky が自動生成)

**Step 1: husky install で `.husky/` 初期化**

```powershell
dotnet husky install
```

Expected: `.husky/` ディレクトリと `_/husky.sh` が生成される

**Step 2: pre-commit hook 追加**

```powershell
dotnet husky add pre-commit
```

Expected: `.husky/pre-commit` が雛形として生成される

**Step 3: `.husky/pre-commit` を編集**

```sh
#!/usr/bin/env sh
. "$(dirname "$0")/_/husky.sh"
dotnet husky run --group pre-commit
```

**Step 4: `.husky/task-runner.json` を作成**

```json
{
  "tasks": [
    {
      "name": "csharpier-format-staged",
      "group": "pre-commit",
      "command": "dotnet",
      "args": ["csharpier", "--pipe-multiple-files"],
      "include": ["**/*.cs"],
      "pathMode": "staged"
    }
  ]
}
```

### Task 2.5: hook が動くことを手動確認(整形前)

**目的**: 一括整形前に hook 単体で動作確認する(整形後だと差分が出ずに動作確認できない)。

**Step 1: 意図的に整形違反ファイルを 1 つ作る**

`src/yEdit.Core/_HookProbe.cs` を作成(あとで削除):

```csharp
namespace yEdit.Core;
public class _HookProbe   {   public   int   X { get   ; set;   } }
```

**Step 2: staged して commit してみる**

```powershell
git add src/yEdit.Core/_HookProbe.cs
git commit -m "probe: hook test (will revert)"
```

Expected: hook が発火して `_HookProbe.cs` が整形される + commit が続行

**Step 3: 内容確認**

```powershell
Get-Content src/yEdit.Core/_HookProbe.cs
```

Expected: 空白/インデントが整形されている

**Step 4: 動作確認 commit を revert**

```powershell
git reset --hard HEAD~1
Remove-Item src/yEdit.Core/_HookProbe.cs -ErrorAction SilentlyContinue
```

Expected: リポジトリが Task 2.4 完了時点に戻る

### Task 2.6: 一括整形の実行

**Step 1: 事前状態確認**

```powershell
git status
```

Expected: `.config/`, `.csharpierrc.json`, `.csharpierignore`, `.husky/` が untracked。ソースは clean。

**Step 2: 一括整形実行**

```powershell
dotnet csharpier .
```

Expected: 大量のファイルが「Formatted」で表示される。エラー無し(ファイル毎の parse エラーがあれば止める)。

**Step 3: 差分規模を確認**

```powershell
git diff --stat | Select-Object -Last 5
```

Expected: 数十〜百ファイル・数千〜万行の変更

### Task 2.7: build & test で挙動不変を確認

**Step 1: build**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: 0 warning, 0 error(整形は挙動を変えない)

**Step 2: 全テスト実行**

```powershell
powershell -File tools\pre-merge-check.ps1
```

Expected: 全ステップ緑(**この時点では pre-merge-check.ps1 に csharpier check は未追加**)

**Step 3: 万一 test 赤化した場合**

- 整形結果を人手で確認(文字列リテラルの改行等が変わっていないか)
- `.csharpierignore` に問題ファイルを追加 or CSharpier 設定の見直し
- 対処後 Task 2.6 からやり直し

### Task 2.8: 整形結果を commit(=blame 無視対象)

**Step 1: 整形 diff だけを commit(hook/config 系は分離)**

まず整形結果だけステージ:

```powershell
git add -u  # 変更されたトラッキング済みファイルのみ
git status  # untracked は .config/, .csharpierrc.json, .csharpierignore, .husky/ のはず
```

Expected: staged = ソース整形差分のみ / untracked = tool 系ファイル

**Step 2: 整形 commit**

```powershell
git commit -m @'
style: CSharpier で全ソースを一括整形

このコミットは整形のみで挙動不変。git blame からは
.git-blame-ignore-revs 経由で除外される。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

**Step 3: commit hash を控える**

```powershell
git rev-parse HEAD
```

Expected: SHA-1 ハッシュを控えておく(次の Task 2.9 で使用)

### Task 2.9: `.git-blame-ignore-revs` に登録

**Files:**
- Modify: `<repo>\.git-blame-ignore-revs`

**Step 1: 一括整形 commit hash を追記**

`.git-blame-ignore-revs` に以下を追記(`<COMMIT_HASH>` は Task 2.8 で控えたもの):

```
# 2026-07-18: CSharpier 初回一括整形 (PR2)
<COMMIT_HASH>
```

### Task 2.10: pre-merge-check.ps1 に csharpier check 追加

**Files:**
- Modify: `<repo>\tools\pre-merge-check.ps1`

**Step 1: `Invoke-Step 'Release ビルド(警告=エラー)'` の**前**に 2 ステップ追加**

差分イメージ:

```powershell
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Step { ... }

# ↓ 以下 2 ステップを新規追加
Invoke-Step 'Local tool restore' {
    dotnet tool restore
}
Invoke-Step 'CSharpier check (format verify)' {
    dotnet csharpier check $repoRoot
}
# ↑ ここまで新規

Invoke-Step 'Release ビルド(警告=エラー)' {
    dotnet build (Join-Path $repoRoot 'yEdit.sln') -c Release -warnaserror
}
# ... 以下既存 ...
```

**Step 2: pre-merge-check を通す**

```powershell
powershell -File tools\pre-merge-check.ps1
```

Expected: 全ステップ緑(csharpier check も 0 差分)

### Task 2.11: tool 系ファイルと `.git-blame-ignore-revs` 更新を commit

**Step 1: 残りをステージ**

```powershell
git add .config/ .csharpierrc.json .csharpierignore .husky/ .git-blame-ignore-revs tools/pre-merge-check.ps1
git status
```

Expected: 全ての新規/変更ファイルが staged

**Step 2: commit**

```powershell
git commit -m @'
build(lint-format): CSharpier + Husky.Net + pre-merge-check 統合(PR2)

- .config/dotnet-tools.json で csharpier / husky を local pin
- .csharpierrc.json (printWidth 100 / CRLF) + .csharpierignore
- .husky/pre-commit + task-runner.json で staged *.cs を自動整形
- tools/pre-merge-check.ps1 に tool restore + csharpier check ステップ追加
- .git-blame-ignore-revs に一括整形 commit を登録

開発者初回セットアップ: `dotnet tool restore` + `dotnet husky install`

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

### Task 2.12: main へ no-ff マージ

**Step 1: 最終確認**

```powershell
powershell -File tools\pre-merge-check.ps1
```

**Step 2: main へマージ**

```powershell
git checkout main
git merge --no-ff feature/lint-format-pr2-csharpier -m "Merge branch 'feature/lint-format-pr2-csharpier' (Lint/Format PR2: CSharpier + Husky + 一括整形)"
```

**Step 3: マージ後 blame ignore 反映(開発者ローカル1回だけ)**

```powershell
git config blame.ignoreRevsFile .git-blame-ignore-revs
git log --oneline -5
```

---

# PR3: Roslynator 導入

**目的**: `Roslynator.Analyzers` を全プロジェクトに配布し、警告を全潰し(修正 or 恒久 disable + 理由コメント)して main へマージ。

**Note**: 本 PR では Roslynator の追加と同時に `<AnalysisLevel>latest-recommended</AnalysisLevel>` も Directory.Build.props に追加する(PR1 実装時に発見された既存 18 件のビルトイン CA 違反 — CA1805/CA2249/CA1716/CA1875/CA1305/CA1861/CA1711 — もここで一緒にトリアージする。Roslynator RCSxxxx と同じ「修正 / 局所抑止 / 恒久 disable」フローで扱う)。

**ブランチ**: `feature/lint-format-pr3-roslynator`(main から)

**DoD**:
- Directory.Build.props に `Roslynator.Analyzers` PackageReference と `<AnalysisLevel>latest-recommended</AnalysisLevel>` 追加済み
- `dotnet build -warnaserror` が 0 warning
- 全テスト緑
- 恒久 disable した ID(RCSxxxx / CAxxxx いずれも)は `.editorconfig` に理由コメント付きで記録

**特性**: 指摘数は事前予測不能。以下のタスクは**指摘発見 → 分類 → 対処 → 再ビルド**のループを含む。

### Task 3.0: ブランチ作成

```powershell
git checkout main
git checkout -b feature/lint-format-pr3-roslynator
```

### Task 3.1: Directory.Build.props に Roslynator と AnalysisLevel を追加

**Files:**
- Modify: `<repo>\Directory.Build.props`

**Step 1: `<PropertyGroup>` に `AnalysisLevel` を追加、かつ `<ItemGroup>` を追加**

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>

    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>

    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Roslynator.Analyzers" Version="4.12.9">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

**注意**:
- `Roslynator.Formatting.Analyzers` は **含めない**(CSharpier と二重整形になるため)。
- `<AnalysisLevel>latest-recommended</AnalysisLevel>` は本 PR で初導入(PR1 では意図的に見送り)。**Roslynator RCSxxxx と併せて、ビルトイン CA ルール(CA1805 等)の指摘も同じフローでトリアージする**。

### Task 3.2: 初回ビルドで警告を収集

**Step 1: 警告一覧をログに落とす**

```powershell
dotnet build yEdit.sln -c Release 2>&1 | Out-File <repo>\_roslynator-warnings.log
```

**注意**: 意図的に `-warnaserror` を**外している**(全警告を出させるため)。

**Step 2: 警告 ID の集計**

```powershell
Select-String -Path <repo>\_roslynator-warnings.log -Pattern '\bRCS\d{4}\b' -AllMatches |
  ForEach-Object { $_.Matches } | ForEach-Object { $_.Value } |
  Group-Object | Sort-Object Count -Descending | Format-Table Count, Name
```

Expected: RCSxxxx 別の指摘数一覧

**Step 3: 分類方針を決める(判断が必要)**

各 ID について以下 3 択:
1. **修正**(コードを直す)
2. **局所抑止**(`#pragma warning disable RCSxxxx` を該当箇所に付ける + コメント)
3. **恒久 disable**(`.editorconfig` に `dotnet_diagnostic.RCSxxxx.severity = none` + 理由コメント)

**恒久 disable の初期候補**(設計書 §6):
- `RCS1090` (ConfigureAwait 強制) — WinForms UI スレッド前提のため不適合
- `RCS1029` (バイナリ演算子位置) — CSharpier に委譲

### Task 3.3: 恒久 disable を `.editorconfig` に登録

**Files:**
- Modify: `<repo>\.editorconfig`

**Step 1: `[*.cs]` セクションに追記**

```ini
[*.cs]
dotnet_diagnostic.IDE0055.severity = none

# --- Roslynator 恒久 disable(理由付き) ---
# WinForms UI スレッド前提のためライブラリ側 ConfigureAwait 強制は不要
dotnet_diagnostic.RCS1090.severity = none
# バイナリ演算子の改行位置は CSharpier に完全委譲
dotnet_diagnostic.RCS1029.severity = none

# ↓ Task 3.4 で追加が発生する可能性あり
```

**Step 2: 再ビルド**

```powershell
dotnet build yEdit.sln -c Release 2>&1 | Out-File <repo>\_roslynator-warnings.log
Select-String -Path <repo>\_roslynator-warnings.log -Pattern '\bRCS\d{4}\b' -AllMatches |
  ForEach-Object { $_.Matches } | ForEach-Object { $_.Value } |
  Group-Object | Sort-Object Count -Descending | Format-Table Count, Name
```

Expected: disable した ID が消えている

### Task 3.4: 残警告をカテゴリ別に修正(反復)

**Step 1: ID 毎に「修正 vs 局所抑止 vs 恒久 disable」を判定**

判定ガイド:
- 全プロジェクトで頻出 & 修正が semantic に等価 → **修正** をコード側に適用
- 特定ファイル/メソッドだけ違う理由がある → **局所抑止**
- リポジトリ規約と根本的に合わない → **恒久 disable**

**Step 2: 修正を実施**

- 修正は 1 ID ごとに 1 commit 推奨(レビュー容易性のため)
- 各 commit のメッセージに **修正した ID** と件数を明記
- `#pragma warning disable` を使う場合は必ず対応する `restore` とセットで、理由をコメント

例:

```powershell
git commit -m @'
refactor: RCS1077 (unused local) の指摘を修正(N 件)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

**Step 3: 修正毎に再ビルド**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: 徐々に警告 → エラー数が減っていく

**Step 4: 全指摘を潰し切る**

Task 3.4 の Step 1〜3 を警告 0 になるまで反復。

### Task 3.5: 最終 verify

**Step 1: 警告 0 確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: 0 warning, 0 error

**Step 2: pre-merge-check 全緑**

```powershell
powershell -File tools\pre-merge-check.ps1
```

Expected: `OK: pre-merge チェック全通過`

**Step 3: ログファイルを削除**

```powershell
Remove-Item <repo>\_roslynator-warnings.log
```

### Task 3.6: 最終 commit + main へ no-ff マージ

**Step 1: Directory.Build.props と .editorconfig の追加分をまとめて commit(修正 commit と別にしたい場合)**

Task 3.4 で細切れに commit していれば、この Step は Directory.Build.props/.editorconfig を最初の commit にまとめて `git rebase -i` する選択もあるが、**Windows 環境で -i オプションを避ける** ため、通常の順次 commit のまま進める(履歴が細切れでも問題ない)。

**Step 2: main へマージ**

```powershell
git checkout main
git merge --no-ff feature/lint-format-pr3-roslynator -m "Merge branch 'feature/lint-format-pr3-roslynator' (Lint/Format PR3: Roslynator 導入)"
```

---

# PR4: SonarAnalyzer.CSharp 導入

**目的**: `SonarAnalyzer.CSharp` を全プロジェクトに配布し、警告を全潰しして main へマージ。

**ブランチ**: `feature/lint-format-pr4-sonar`(main から)

**DoD**: PR3 と同じ枠(0 warning + tests 全緑 + 恒久 disable の理由コメント)

**構成**: PR3 と同じ流れなので、指示は簡潔に。

### Task 4.0: ブランチ作成

```powershell
git checkout main
git checkout -b feature/lint-format-pr4-sonar
```

### Task 4.1: Directory.Build.props に SonarAnalyzer 追加

**Files:**
- Modify: `<repo>\Directory.Build.props`

**Step 1: `<ItemGroup>` に PackageReference 追加**

```xml
<ItemGroup>
  <PackageReference Include="Roslynator.Analyzers" Version="4.12.9">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
  <PackageReference Include="SonarAnalyzer.CSharp" Version="10.4.0.108396">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

### Task 4.2: 初回ビルドで警告収集

**Step 1: 警告一覧をログに落とす**

```powershell
dotnet build yEdit.sln -c Release 2>&1 | Out-File <repo>\_sonar-warnings.log
```

**Step 2: Sonar 警告 ID(Sxxxx)の集計**

```powershell
Select-String -Path <repo>\_sonar-warnings.log -Pattern '\bS\d{3,4}\b' -AllMatches |
  ForEach-Object { $_.Matches } | ForEach-Object { $_.Value } |
  Group-Object | Sort-Object Count -Descending | Format-Table Count, Name
```

### Task 4.3: 恒久 disable を `.editorconfig` に登録

**Files:**
- Modify: `<repo>\.editorconfig`

**Step 1: 初期候補を追記**(設計書 §7)

```ini
# --- SonarAnalyzer 恒久 disable(理由付き) ---
# TODO/FIXME は docs/plans への申し送りとの併用で残したい
dotnet_diagnostic.S1135.severity = none
# コメントアウトされたコードは履歴用に一時的に残す慣行がある
dotnet_diagnostic.S125.severity = suggestion
```

**Step 2: 再ビルドして再集計**

Task 4.2 Step 1〜2 を再実行。

### Task 4.4: 残警告をカテゴリ別に修正(反復)

**Step 1: 判定** — PR3 Task 3.4 と同じフロー

**Sonar 特有の判定注意**:
- **cognitive complexity 系(S3776, S138)** — EditorControl 系の複雑メソッドが該当する可能性大
  - **リファクタは今回のスコープ外**。局所抑止(`#pragma warning disable S3776 // reason: 責務分離 Phase 3 で扱う`)で通す
- **Null 誤検知**(Nullable 有効なのに Sonar が警告する場合)
  - まず修正を試みる(パターンマッチで null check を明示化等)
  - どうしても解けない場合は局所抑止
- **バグ系(S2589, S2583)** — **必ず修正**(致命的な可能性)

**Step 2: 修正 → 再ビルド**をループ

### Task 4.5: 最終 verify

```powershell
dotnet build yEdit.sln -c Release -warnaserror
powershell -File tools\pre-merge-check.ps1
Remove-Item <repo>\_sonar-warnings.log
```

Expected: 全緑

### Task 4.6: main へ no-ff マージ

```powershell
git checkout main
git merge --no-ff feature/lint-format-pr4-sonar -m "Merge branch 'feature/lint-format-pr4-sonar' (Lint/Format PR4: SonarAnalyzer 導入)"
```

---

# PR5: CI verify + docs

**目的**: `.github/workflows/ci.yml` に tool restore + csharpier check を追加。CLAUDE.md に開発者セットアップ手順を追記。

**ブランチ**: `feature/lint-format-pr5-ci-docs`(main から)

**DoD**:
- CI が新規 push で tool restore + csharpier check + build + tests を全通過
- CLAUDE.md に「clone 後の初回セットアップ = `dotnet tool restore` + `dotnet husky install`」が明記されている

### Task 5.0: ブランチ作成

```powershell
git checkout main
git checkout -b feature/lint-format-pr5-ci-docs
```

### Task 5.1: `.github/workflows/ci.yml` に csharpier check を追加

**Files:**
- Modify: `<repo>\.github\workflows\ci.yml`

**Step 1: 既存の `dotnet build` ステップの前に 2 ステップ挿入**

差分イメージ:

```yaml
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.0.x

      # ↓ 以下 2 ステップを追加
      - name: Local tool restore
        run: dotnet tool restore

      - name: CSharpier check
        run: dotnet csharpier check .
      # ↑ ここまで追加

      - name: ビルド(警告=エラー)
        run: dotnet build yEdit.sln -c Release -warnaserror
```

**注意**: bench.yml / release.yml には**追加しない**(設計書 §8)。

### Task 5.2: CLAUDE.md に開発者セットアップ手順を追記

**Files:**
- Modify: `<repo>\CLAUDE.md`(存在する場合)

**Step 1: CLAUDE.md の存在確認**

```powershell
Test-Path <repo>\CLAUDE.md
```

- 存在する → 該当箇所を追記(既存の「開発環境」節がなければ新設)
- 存在しない → 追記スキップして、代わりに `README.md` に短く追記(あれば)。README も無ければ本タスクを skip して代わりに `docs/lint-format-setup.md` を新設

**Step 2: セットアップ手順を記述**

```markdown
## 開発環境セットアップ(clone 後 1 回)

依存 CLI ツール(csharpier / husky)を local tool として復元 + pre-commit フックを有効化:

```powershell
dotnet tool restore
dotnet husky install
git config blame.ignoreRevsFile .git-blame-ignore-revs
```

- `dotnet tool restore` — csharpier / husky を `.config/dotnet-tools.json` から復元
- `dotnet husky install` — `.husky/pre-commit` を Git hook として有効化
- `git config blame.ignoreRevsFile ...` — 一括整形 commit を `git blame` から除外

commit 時に staged された `*.cs` は自動的に CSharpier で整形される。
CI と `tools/pre-merge-check.ps1` は `dotnet csharpier check` で整形状態を verify する。
アナライザ変更(disable/enable/severity 変更)は `.editorconfig` に**理由コメント必須**で記録すること。
```

### Task 5.3: 検証 & commit & main merge

**Step 1: 動作確認は CI 側で。ローカルは pre-merge-check だけ確認**

```powershell
powershell -File tools\pre-merge-check.ps1
```

**Step 2: commit**

```powershell
git add .github/workflows/ci.yml CLAUDE.md
git commit -m @'
ci(lint-format): CI に CSharpier check ステップを追加 + docs(PR5)

- .github/workflows/ci.yml に tool restore + csharpier check ステップを追加
  (bench.yml / release.yml には追加しない=設計書 §8)
- CLAUDE.md に開発者セットアップ手順(tool restore + husky install + blame ignoreRevsFile)
  を追記

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

**Step 3: main へマージ**

```powershell
git checkout main
git merge --no-ff feature/lint-format-pr5-ci-docs -m "Merge branch 'feature/lint-format-pr5-ci-docs' (Lint/Format PR5: CI verify + docs)"
```

**Step 4: CI で verify(初回 push 時)**

```powershell
git push origin main
```

Expected: GitHub Actions ci workflow が緑

---

# 完了後の状態

- `.editorconfig` / `Directory.Build.props` = 唯一の規約源
- `dotnet build -warnaserror` = ビルトイン Roslyn + Roslynator + Sonar のゲート
- `dotnet csharpier check .` = CI + pre-merge で整形 verify
- `.husky/pre-commit` = commit 時に staged `*.cs` を自動整形
- 開発者は `dotnet tool restore` + `dotnet husky install` を 1 回叩けば完了

# メモリ更新(全 PR 完了後)

- `memory/MEMORY.md` に新規エントリ追加: `[Lint/Format 導入完了](lint-format-adoption-complete.md)`
- 内容: 完了日 / 適用スタック / 恒久 disable した ID 一覧と理由 / 開発者セットアップ手順の場所

# リスク対応(実行中に発生時)

| 事象 | 対処 |
|---|---|
| CSharpier が特定ファイルの parse でコケる | `.csharpierignore` に該当ファイルを追加 → 再実行 |
| CSharpier 整形でテスト赤化 | 整形結果の該当箇所を目視確認(文字列リテラルの改行等) → 必要なら該当ファイルを ignore |
| Roslynator/Sonar 警告数が想定超過 | `.editorconfig` の恒久 disable リストを拡張(**必ず理由コメント**) |
| Husky hook が Windows で動かない | `.husky/pre-commit` を削除して commit 継続。CI verify は残る=push で最終ゲート |
| cognitive complexity 指摘が EditorControl に集中 | 局所抑止 + コメント「責務分離 Phase 3 で対応済み/対応予定」を明記(リファクタは scope out) |

# 全 PR 完了までのおおまかな工数見積り

- **PR1**: 30 分〜1 時間(機械的作業)
- **PR2**: 1〜2 時間(整形結果の目視確認 + テスト再実行含む)
- **PR3**: 2〜4 時間(指摘数次第、初回は多い想定)
- **PR4**: 2〜4 時間(cognitive complexity の判断で時間が伸びる可能性)
- **PR5**: 30 分〜1 時間

**合計**: 1 セッションで PR1〜PR2 まで、PR3/PR4 は別セッションで 1 本ずつが現実的。
