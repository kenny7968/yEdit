# Linter / Formatter 導入 設計書

- **作成日**: 2026-07-18
- **対象**: yEdit リポジトリ全体(src 4 + tests 5)
- **区分**: 開発環境整備(挙動不変を保証)
- **前提**: .NET 9 / C# WinForms 主体・CI と `tools/pre-merge-check.ps1` は既に `-warnaserror` 稼働中

## 0. スコープと決定事項サマリ

**採用スタック(バランス派)**:
- **Format**: CSharpier(opinionated formatter・議論を消す)
- **Lint**: Roslynator.Analyzers + SonarAnalyzer.CSharp(ビルトイン Roslyn は既に稼働)
- **規約源**: `.editorconfig` 新規追加

**主要判断**:
- 初回整形: **一括整形 PR 1 本 + `.git-blame-ignore-revs` 登録**(blame 温存)
- アナライザ導入: **完全導入(全指摘を潰してから main マージ)**
- ローカルゲート: **Husky.Net pre-commit + `tools/pre-merge-check.ps1` 統合 + CI verify**
- PR 分割: **5 本の分割 PR で段階マージ**(下記 §9)

**非対象(scope out)**:
- StyleCop.Analyzers / Meziantou.Analyzer(バランス派スコープ外)
- Roslynator.Formatting.Analyzers(CSharpier と二重整形になるため)
- Directory.Packages.props による中央集権バージョン管理
- SonarCloud/SonarQube サーバ連携

## 1. 全体構成(追加/変更するファイル)

```
<repo>\
├─ .editorconfig                    ← 新規: 規約源(唯一)
├─ .git-blame-ignore-revs           ← 新規: 一括整形 commit を登録
├─ Directory.Build.props            ← 新規: 全 csproj 共通プロパティ + アナライザ配布
├─ .config/dotnet-tools.json        ← 新規: csharpier / husky を local tool として pin
├─ .husky/                          ← 新規: pre-commit hook 設置場所
│  ├─ pre-commit                    ←     husky が発火する shell スクリプト
│  └─ task-runner.json              ←     hook 定義 (csharpier format ${staged})
├─ .csharpierrc.json                ← 新規: CSharpier 設定
├─ .csharpierignore                 ← 新規: 除外パス
├─ tools/pre-merge-check.ps1        ← 変更: tool restore + csharpier check 追加
├─ .github/workflows/ci.yml         ← 変更: tool restore + csharpier check 追加
└─ src/**/*.csproj, tests/**/*.csproj ← 変更: 共通プロパティを Directory.Build.props に寄せる
```

**方針**: グローバルツール(`dotnet tool install -g`)は不使用。**local tool manifest** で CI/開発者マシン間のバージョンを揃える。

## 2. `.editorconfig` の規約方針

**基本原則**: 既存コードの実態と合わせる(初回整形の差分を最小化)。既存 = 4 space indent / CRLF / **UTF-8 (BOM 無しが実測)**。

```ini
root = true

[*]
indent_style = space
indent_size = 4
end_of_line = crlf
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true

[*.{yml,yaml,json,md}]
indent_size = 2
charset = utf-8

[*.ps1]
end_of_line = crlf

[*.cs]
# 命名規約 / using 順 / var 使用等 = MS 既定 (Recommended) をそのまま採用
# CSharpier がインデント/改行/空白を完全所有するので editorconfig 側では最小限
dotnet_diagnostic.IDE0055.severity = none  # フォーマット系は CSharpier に委ねる (二重整形を防止)
```

**理由**:
- CSharpier がフォーマットを完全所有するので、`.editorconfig` の空白/インデント指示は「IDE 表示用」の意味しか持たないが、コアな粒度(indent 4 + CRLF)は残す(dotnet format との衝突を避けるため CSharpier 側もこの前提で動く)
- `IDE0055`(書式ルール)を無効化 = Roslyn 内蔵の書式ルールと CSharpier が競合しないようにする(CSharpier 公式推奨)
- 命名規約は Recommended まま = 独自規約を最初から強要せず、既存コードとの摩擦を最小化

> **BOM を導入しない理由**: 実測で `*.cs` の 0/251 ファイルが BOM 無し。ここで `utf-8-bom` を宣言すると、以降 IDE で保存された `.cs` ファイルに BOM が付与されて履歴に無関係な差分ノイズが混入する。BOM 統一が望ましいなら PR2 とは別の専用移行 commit + `.git-blame-ignore-revs` 登録として扱う(現状スコープ外)。

## 3. `Directory.Build.props` の共通プロパティ集約

各 csproj で個別定義されている共通項を 1 箇所に集約。

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>

    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
    <!-- IDExxxx (書式・style) は CSharpier + IDE で運用。
         ビルドでの強制はアナライザ (CAxxxx, RCSxxxx, Sxxxx) だけに絞る -->

    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <!-- Roslynator / SonarAnalyzer を全プロジェクトに配布 (PR3/PR4 で追加) -->
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
</Project>
```

**各 csproj で個別に残すもの**:
- `TargetFramework`(net9.0 vs net9.0-windows で個別)
- `UseWindowsForms` / `UseWPF`(プロジェクト毎)
- `AllowUnsafeBlocks`(yEdit.Editor のみ)
- `OutputType` / `AssemblyName`(yEdit.App のみ)
- `IsPackable`(tests のみ)
- 個別 `PackageReference`(Markdig / UTF.Unknown / WebView2 / xunit 等)
- `InternalsVisibleTo` / `ProjectReference`

**バージョン方針**: Roslynator/Sonar は Directory.Build.props 内でハードコード。Directory.Packages.props 中央管理は今回の scope 外。

**PR1 段階**: 共通化 + `.editorconfig` 導入だけで挙動不変(アナライザ PackageReference は PR3/PR4 で追加)。

> **Note on `AnalysisLevel`**: 当初は PR1 で `<AnalysisLevel>latest-recommended</AnalysisLevel>` を導入する案だったが、実装時に **18 件の CA 違反**(CA1805/CA2249/CA1716/CA1875/CA1305/CA1861/CA1711)が既存コードで発生することを発見。ビルトイン CA ルールの有効化は概念的に「アナライザルール ON」と等価なので、Roslynator/Sonar と同じ PR3 に集約する。PR1 は共通化のみに徹し、`AnalysisLevel` は PR3 で追加する(§6 参照)。

## 4. CSharpier 設定

> **Note (2026-07-18 PR2 実装時)**: CSharpier 0.x → 1.x 移行で CLI が subcommand 化(`--pipe-multiple-files` → `format ${staged}`)。husky.net の `pathMode` も `absolute`/`relative` のみ有効で `staged` は不可(`${staged}` 変数で代替)。本設計書はこの変更を反映済み。

**ツール pin**(`.config/dotnet-tools.json`):

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "csharpier": { "version": "1.3.0", "commands": ["dotnet-csharpier"] },
    "husky":     { "version": "0.9.1", "commands": ["husky"] }
  }
}
```

**プロジェクト設定**(`.csharpierrc.json`):

```json
{
  "printWidth": 100,
  "endOfLine": "crlf"
}
```

- `printWidth`: 100(diff 可読性を優先)
- `endOfLine`: `.editorconfig` と一致

**除外**(`.csharpierignore`):

```
**/obj/
**/bin/
**/*.Designer.cs
# MSBuild XML: CSharpier 1.x が対応追加したが、csproj/props の空行はグループ化に使う慣行があるため除外。EOL は .editorconfig で担保。
**/*.csproj
**/*.props
```

**運用**:
- `dotnet csharpier format .` = 全ファイル整形(初回一括整形 = PR2 で 1 回だけ)
- `dotnet csharpier check .` = CI/pre-merge で verify(差分あれば非 0 終了)
- `dotnet csharpier format ${staged}` = Husky pre-commit で staged ファイルのみ

**Designer.cs**: 現状ほぼ無いはずだが、混入時の事故防止で除外。PR2 時に glob で最終確認。

**MSBuild XML の扱い(PR2 実装で確定)**: CSharpier 1.x は `.csproj`/`.props` も整形対象になったため、初回一括整形では 10 XML ファイル(9 csproj + Directory.Build.props)も含まれた。以降は `.csharpierignore` で除外して手書き csproj の空行(要素グループ化に使う慣行)を保全する。EOL は `.editorconfig` の CRLF 宣言で担保する。CSharpier が今後さらに新ファイル型を追加した場合の silent scope expansion にも同様に対応する(allowlist ではなく blocklist 方針)。

## 5. Husky.Net + pre-commit フック

**セットアップ**(PR2 で 1 回):

```bash
dotnet new tool-manifest
dotnet tool install husky
dotnet tool install csharpier
dotnet husky install
dotnet husky add pre-commit
```

**Hook 定義**(`.husky/task-runner.json`):

```json
{
  "$schema": "https://alirezanet.github.io/Husky.Net/schema.json",
  "tasks": [
    {
      "name": "csharpier-format-staged",
      "group": "pre-commit",
      "command": "dotnet",
      "args": ["csharpier", "format", "${staged}"],
      "include": ["**/*.cs"]
    }
  ]
}
```

**Hook スクリプト**(`.husky/pre-commit`):

```sh
#!/usr/bin/env sh
. "$(dirname "$0")/_/husky.sh"
dotnet husky run --group pre-commit
```

**動作**:
1. `git commit` 時に staged された `*.cs` だけを CSharpier に流す
2. 整形結果を `git add` で戻して commit 続行
3. staged が数ファイルなら数百 ms 想定

**開発者初回セットアップ**:

```powershell
dotnet tool restore
dotnet husky install
```

これを README / docs に「clone 後 1 回叩く」として案内(PR5)。

**フック回避**: `git commit --no-verify` で skip 可能。**CI で `dotnet csharpier check .` が走るので push 時点でリジェクト**される安全網あり。

**Windows 互換性**: Git for Windows 同梱 `sh` で動作(Husky.Net 公式サポート)。

## 6. Roslynator 設定・除外方針

**PR3 で `AnalysisLevel=latest-recommended` も同時に Directory.Build.props に追加する**(§3 Note 参照)。PR1 で先送りしたビルトイン CA ルール ~18 件(CA1805/CA2249/CA1716/CA1875/CA1305/CA1861/CA1711)も Roslynator 導入と同じサイクルでトリアージする(修正 / 局所抑止 / 恒久 disable)。

**パッケージ**: `Roslynator.Analyzers`(500+ ルール、コアパック)。

**Formatting.Analyzers は導入しない**(CSharpier と二重整形になるため)。

**既定ポリシー**:
- 全ルール `warning` レベル(=`-warnaserror` で自動的にエラー化)
- **導入時点で全指摘を潰す**(PR3 のスコープ・ビルトイン CA 分も含む)

**恒久的に無効化する ID 候補**(このリポジトリ実態に合わないもの):

```ini
# WinForms/UI スレッド前提のコードには不適合
dotnet_diagnostic.RCS1090.severity = none  # ConfigureAwait(false) 強制

# フォーマット系: CSharpier に委譲
dotnet_diagnostic.RCS1029.severity = none  # バイナリ演算子の位置
```

**個別指摘への対処原則**:
1. **修正が妥当** → コード側を直す
2. **原則は妥当だがこのファイルだけ違う** → `#pragma warning disable RCSxxxx` + コメント
3. **プロジェクト全体で不適合** → `.editorconfig` で `severity = none` + **理由コメント必須**

**PR3 で残ることが想定される主要カテゴリ**:
- 簡潔化提案(`??`, pattern matching, target-typed `new()`)
- 冗長キャストの除去
- LINQ 最適化(`.Any()` vs `.Count() > 0`)
- 未使用パラメータ・未使用 using

**除外ルールの記録**: 「なぜ切ったか」を `.editorconfig` の各 `severity = none` 行に **コメント必須**(将来の再検討可能性を残す)。

## 7. SonarAnalyzer.CSharp 設定・除外方針

**パッケージ**: `SonarAnalyzer.CSharp`(無償版、ローカルビルドで完結。SonarCloud/Qube サーバ不要)。

**既定ポリシー**:
- 全ルール `warning`(→ `-warnaserror` でエラー化)
- **導入時点で全指摘を潰す**(PR4 のスコープ)

**恒久的に無効化する ID 候補**:

```ini
# このリポジトリは docs/plans に申し送りを集約する運用なので TODO を自由に書きたい
dotnet_diagnostic.S1135.severity = none  # TODO/FIXME 検出

# 履歴用に一時的に残す慣行があるので情報レベル
dotnet_diagnostic.S125.severity = suggestion  # コメントアウトされたコード
```

**Sonar 特有の重要指摘**(潰す方向):
- **バグ系**(`S2589` 常に true な条件・`S2583` デッドコード) → 修正必須
- **コード スメル**(`S1172` 未使用引数・`S1481` 未使用変数) → 修正
- **セキュリティ系** → このプロジェクトはネット I/O 少なため影響小
- **複雑度系**(`S3776` cognitive complexity・`S138` メソッド長)
  - EditorControl・TextBuffer 等の複雑メソッドが該当する可能性大
  - **方針**: PR4 時点の実測で判断(閾値上げ vs `#pragma` 局所抑止)
  - **事前に恒久 disable はしない**

**Roslynator との重複**:
- 思想が違うので同種指摘は「両方直す」で問題ない
- 完全に同一ロジックの ID(例: RCS1213 vs S1481)は一方だけ有効化・PR4 で発覚時対応

**PR4 で残ることが想定される主要カテゴリ**:
- Cognitive complexity 超過(EditorControl 系)
- Null 誤検知(Nullable 有効でも Sonar 独自解析で警告する場合あり)
- Reserved keyword 系(`context` 変数名等)

## 8. pre-merge-check.ps1 + CI verify 統合

### pre-merge-check.ps1(PR2 で追加、PR5 で最終形)

```powershell
Invoke-Step 'Local tool restore' {
    dotnet tool restore
}
Invoke-Step 'CSharpier check (format verify)' {
    dotnet csharpier check $repoRoot
}
Invoke-Step 'Release ビルド(警告=エラー)' {
    dotnet build (Join-Path $repoRoot 'yEdit.sln') -c Release -warnaserror
}
Invoke-Step 'Core.Tests'   { dotnet test tests/yEdit.Core.Tests   -c Release --no-build }
Invoke-Step 'Editor.Tests' { dotnet test tests/yEdit.Editor.Tests -c Release --no-build }
Invoke-Step 'App.Tests'    { dotnet test tests/yEdit.App.Tests    -c Release --no-build }
```

**ステップ順序の意図**:
1. **tool restore** を最初(csharpier/husky が未インストールだと fail するため)
2. **CSharpier check** をビルド前(整形違反があるならビルド前に落とす=fast fail)
3. `dotnet build -warnaserror` が Roslynator/Sonar のゲートを兼ねる(既存構造そのまま活用)

**アナライザ verify 用の別ステップは不要**: Roslynator/Sonar は build 時に走るので `-warnaserror` が verify を兼ねる。

### CI(`.github/workflows/ci.yml` — PR5 で追加)

```yaml
- name: Local tool restore
  run: dotnet tool restore

- name: CSharpier check
  run: dotnet csharpier check .
```

既存の `dotnet build -c Release -warnaserror` ステップの**前**に挿入。

**bench.yml / release.yml への追加**:
- **release.yml**: **追加しない**(main マージ時点で verify 済み前提)
- **bench.yml**: **追加しない**(ベンチ特化 job・追加検証は ci.yml に集約)

### CLAUDE.md / docs への追記(PR5)

- 新規参加者向け: `dotnet tool restore` + `dotnet husky install` を clone 後 1 回叩く手順
- 「テストプロジェクト追加時の 3 箇所同期」に加え、**「アナライザルール変更時の記録場所 = `.editorconfig` の該当 severity 行にコメント必須」**を追加

## 9. ロールアウト詳細(PR1〜PR5)

### PR1 — 規約源の集約(コード変更ほぼゼロ)

- **追加**: `.editorconfig`, `Directory.Build.props`, `.git-blame-ignore-revs`(空ファイル)
- **変更**: 全 csproj から共通プロパティを Directory.Build.props に寄せる
- **DoD**: `dotnet build -warnaserror` + 全テスト緑
- **想定差分**: csproj 削減 + 新規 3 ファイル

### PR2 — CSharpier + Husky + 一括整形

- **追加**: `.config/dotnet-tools.json`, `.csharpierrc.json`, `.csharpierignore`, `.husky/*`
- **変更**: `dotnet csharpier .` の実行結果(**src/tests 全ファイル**)
- **追加**: pre-merge-check.ps1 に tool restore + csharpier check ステップ
- **追加**: `.git-blame-ignore-revs` に**一括整形の commit hash** を登録
- **DoD**: `dotnet csharpier check .` + build + tests 全緑
- **想定差分**: 巨大(数十〜百ファイル・数千〜万行)だが挙動不変
- **レビューポイント**: 「csharpier 設定は妥当か」「hook が動くか」のみ(整形差分は blame 無視前提でスキップ可)

### PR3 — Roslynator 導入

- **変更**: Directory.Build.props に `Roslynator.Analyzers` PackageReference 追加
- **変更**: 全指摘の潰し(修正 or `severity = none` + コメント)
- **DoD**: build 0 warning + tests 全緑
- **想定差分**: 指摘数次第(数百〜千件想定)
- **レビューポイント**: 「無効化した ID は妥当な理由か」「修正が semantic に等価か」

### PR4 — SonarAnalyzer 導入

- **変更**: Directory.Build.props に `SonarAnalyzer.CSharp` PackageReference 追加
- **変更**: 全指摘の潰し
- **DoD**: build 0 warning + tests 全緑
- **想定差分**: 指摘数次第、cognitive complexity 指摘が EditorControl 系に集中する可能性
- **リスクポイント**: complexity 指摘の判断(閾値上げ vs 局所抑止 vs リファクタ) → **リファクタは今回のスコープ外・局所抑止で通す**

### PR5 — CI verify + docs

- **変更**: `.github/workflows/ci.yml` に tool restore + csharpier check
- **変更**: CLAUDE.md / docs に開発者セットアップ手順追記
- **想定差分**: yml 数行 + docs 数十行

## 10. リスク・撤退プラン

| リスク | 影響 | 対処 |
|---|---|---|
| CSharpier 整形が既存テスト(文字列リテラル等)に影響 | tests 赤化 | PR2 で全テスト再実行=事前検出。整形結果を人手で吟味 |
| Roslynator/Sonar 指摘数が想定より多い | PR3/PR4 の工数膨張 | 潰し切れない ID は `severity = none` + コメントで一旦逃がす(記録は残る) |
| Husky hook が Windows で動かない | commit ブロック | 撤退可能(`.husky/pre-commit` を削除)。CI verify は残る |
| .Designer.cs 除外漏れ | デザイナ破損 | 現状デザイナファイル無しを事前確認。PR2 時に glob で再確認 |
| SR / UIA 経路の実機退行 | ユーザ影響 | 挙動不変(整形/命名整理のみ)なので理論上ゼロ。念のため PR2/PR3/PR4 後に SR L5 軽ドライブを推奨 |

**撤退プラン**: PR は全て revert 可能な単位で切っている。PR2(一括整形)の revert は blame 履歴を戻す必要があるが、`.git-blame-ignore-revs` から該当行を消せば足りる。

## 11. 完了判定

**プロジェクト全体としての完了 = PR5 のマージ**。

達成状態:
- `.editorconfig` / `Directory.Build.props` が唯一の規約源
- `dotnet build -warnaserror` が Roslynator + Sonar + ビルトイン Roslyn の指摘を全ゲート
- `dotnet csharpier check .` が CI + pre-merge-check で verify
- `git commit` 時に staged `*.cs` が自動整形される
- 開発者は `dotnet tool restore` + `dotnet husky install` を 1 回叩けばセットアップ完了

## 12. 次工程

本設計書の合意後、`writing-plans` スキルで PR1〜PR5 の詳細実装計画(タスク分割・DoD・レビュー観点)に落とす。
