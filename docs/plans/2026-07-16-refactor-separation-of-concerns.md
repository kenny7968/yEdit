# 責務分離リファクタリング Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 責務分離が弱い 6 項目(EditorControl 3396 行分割+中小 5 項目)を、Phase 1(中小 5 項目・先行)→ Phase 2(EditorControl partial 分割)→ Phase 3(Controller 委譲)の順に、挙動不変で解消する。本実装計画では **Phase 1 の 5 サブ(1a-1e)を TDD step-by-step で完全に扱う**。Phase 2・3 は各サブの目次+DoD のみ記載し、Phase 1 完了時に別ファイルで詳細化する(JIT)。

**Architecture:** 段階マージ方式([[phase-work-git-flow]] 準拠)。1 サブ Phase = 1 フィーチャーブランチ = 1 no-ff マージ。各サブ Phase 完了時に別エージェント(`superpowers:code-reviewer`)によるレビューを行い、`tools/pre-merge-check.ps1` で緑を確認してから main へマージ。Phase 1 は全て挙動不変(SR 発声文言・フォーカス遷移・保存挙動・キャレット位置を変えない)。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit / STA テストヘルパ(`Sta.Run` + `HostForm.CreateWithDocs`) / Fake 群(`FakeAnnouncer`/`FakePrompt`/`FakeFileDialogService`/`FakeBackupWriter`/`FakeTimeProvider`/`FakeRestorePrompt`)+ 新規 `FakeBackupTraceSink`。

**上位文書:**
- 設計書: `docs/plans/2026-07-16-refactor-separation-of-concerns-design.md`

**テスト数遷移(Phase 1):** 954 → 957(1a) → 962(1b) → 966(1c) → 972(1d) → 974(1e) = **974**(Phase 1 完了時)

---

## Task 0: 事前ゲート+作業ブランチ運用

**目的:** main から作業を始める準備。

**Step 0.1: 事前ゲート**

```powershell
git status         # クリーン(publish/ installer/ は untracked=無視)
git log --oneline -1
# 期待: HEAD=45110e1(設計書コミット)
tools/pre-merge-check.ps1
# 期待: 全緑・Release 0 警告・954 tests
```

**Step 0.2: 運用ルール確認**

- 各サブ Phase(1a〜1e)ごとに独立ブランチを切る:`feature/refactor-1a-mainform-readonly`、`feature/refactor-1b-backup-trace` など。
- 各サブ Phase 完了時に `superpowers:requesting-code-review` を起動して別エージェントに review を依頼。
- レビュー「マージ可」+ `tools/pre-merge-check.ps1` 緑 → main へ `git merge --no-ff` でマージ。
- L5 実機検証は **1b のみ**(バックアップ経路)。他は SR 経路不変。
- サブ Phase 完了ごとに MEMORY.md を更新(全 Phase 完了時に一括でも可)。

**Commit:** なし(準備のみ)

---

## Task 1a: MainForm null! Controller → readonly 化

**目的:** `MainForm` の Controller 6 個(`_file`/`_search`/`_grep`/`_backup`/`_csv`/`_kinsoku`)が `= null!` 宣言されている状態を、内側 ctor で全て初期化することで `readonly` 化する。挙動不変・純リファクタ。

**Files:**
- Modify: `src/yEdit.App/MainForm.cs:14-20`(フィールド宣言)、`:46-109`(ctor 本体)
- Test: 既存 `App.Tests/MainFormSmokeTests.cs`(緑継続)+ 新規 assertion 追加

**L5 実機検証:** 不要(SR 経路不変)

### Step 1a.1: ブランチ作成

```powershell
git checkout -b feature/refactor-1a-mainform-readonly
git log --oneline -1
# 期待: HEAD が main の 45110e1
```

### Step 1a.2: 失敗テストを書く(readonly 契約の型テスト)

`tests/yEdit.App.Tests/MainFormSmokeTests.cs` の末尾に以下を追加。

```csharp
[Fact]
public void MainForm_ControllerFields_AreReadOnly()
{
    // Task 1a: null! 代入経路を止め、6 Controller を readonly 化する契約を固定。
    // 実装後は宣言時か ctor 初期化リストで確定代入 = readonly が復活する。
    var type = typeof(MainForm);
    var flags = System.Reflection.BindingFlags.Instance
              | System.Reflection.BindingFlags.NonPublic;
    string[] controllerFields = {
        "_file", "_search", "_grep", "_backup", "_csv", "_kinsoku"
    };
    foreach (var name in controllerFields)
    {
        var field = type.GetField(name, flags);
        Assert.NotNull(field);
        Assert.True(field!.IsInitOnly, $"{name} must be readonly");
    }
}
```

### Step 1a.3: テストを走らせて失敗確認

```powershell
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj `
  --filter FullyQualifiedName~MainForm_ControllerFields_AreReadOnly
# 期待: FAIL(現状は readonly ではない=IsInitOnly=false)
```

### Step 1a.4: MainForm を readonly 化

`src/yEdit.App/MainForm.cs:14-20` を書き換え。

```csharp
// Before (現状)
private readonly DocumentManager _docs;
private FileController _file = null!;      // コンストラクタで生成
private SearchController _search = null!;
private GrepController _grep = null!;
private BackupCoordinator _backup = null!;
private CsvController _csv = null!;
private KinsokuFormatController _kinsoku = null!;

// After
private readonly DocumentManager _docs;
private readonly FileController _file;
private readonly SearchController _search;
private readonly GrepController _grep;
private readonly BackupCoordinator _backup;
private readonly CsvController _csv;
private readonly KinsokuFormatController _kinsoku;
```

`ctor(AppSettings, string settingsPath)` の内側で全 Controller を代入(現状の `:67-92` の順序をそのまま維持=一度に readonly 化しても definite assignment は成立する)。既存の順序:

1. `_docs = new DocumentManager(CreateEditor);`
2. `_announcer = new UiaAnnouncer(_announceLabel);`
3. `_file = new FileController(...)`
4. `_search = new SearchController(...)`
5. `_grep = new GrepController(...)`
6. `_backup = new BackupCoordinator(...)`
7. `_csv = new CsvController(...)`
8. `_kinsoku = new KinsokuFormatController(...)`

**注意点:** `_docs.BeforeActiveChange = () => _csv.AbortEdit();`(`MainForm.cs:93`)は `_csv` 代入後に来ているので順序 OK。もし ctor 内で `_docs.ActiveDocumentChanged += ...` が Controller 生成前に発火する余地があれば ctor 順の再配置が必要だが、現状はイベントを発火するオブジェクト自体が ctor 末尾まで生成されないため問題なし。

### Step 1a.5: テスト再実行

```powershell
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj `
  --filter FullyQualifiedName~MainForm_ControllerFields_AreReadOnly
# 期待: PASS
```

### Step 1a.6: 全体ゲート

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・0 警告・957 tests(954+3 assertion。単一 Fact だが Fact 内 loop = 1 test 数 = 954+1 の可能性あり。実際の増加分を確認)
```

### Step 1a.7: コミット

```powershell
git add src/yEdit.App/MainForm.cs tests/yEdit.App.Tests/MainFormSmokeTests.cs
git commit -m "refactor(app): MainForm の null! Controller を readonly 化 (Task 1a)"
```

### Step 1a.8: 別エージェント review + main マージ

`superpowers:requesting-code-review` を起動して別エージェントに review 依頼 → 「マージ可」で `git checkout main && git merge --no-ff feature/refactor-1a-mainform-readonly`。

---

## Task 1b: BackupCoordinator catch{} に診断導線

**目的:** `BackupCoordinator.cs` の 4 箇所 silent catch を `IBackupTraceSink` 経由で診断可能にする。既定実装は `System.Diagnostics.Trace.TraceWarning` で本番挙動不変。

**Files:**
- Create: `src/yEdit.App/Abstractions/IBackupTraceSink.cs`
- Create: `src/yEdit.App/DebugBackupTraceSink.cs`
- Modify: `src/yEdit.App/BackupCoordinator.cs:113,117,134,155`(catch 4 箇所)、ctor 追加引数
- Modify: `src/yEdit.App/MainForm.cs:86-90`(BackupCoordinator ctor 呼び出し)
- Create: `tests/yEdit.App.Tests/Fakes/FakeBackupTraceSink.cs`
- Modify: `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`(新規テスト追加)

**L5 実機検証:** 必要(バックアップ経路の変更 = 挙動不変であることを NVDA 環境で確認)

### Step 1b.1: ブランチ作成

```powershell
git checkout main
git checkout -b feature/refactor-1b-backup-trace
```

### Step 1b.2: 抽象を先に作る

`src/yEdit.App/Abstractions/IBackupTraceSink.cs` を新規作成:

```csharp
namespace yEdit.App;

/// <summary>
/// BackupCoordinator の silent catch を診断可能にする trace sink(Task 1b)。
/// 本番挙動は不変(既定 sink は Trace.TraceWarning のみ)。テストでは FakeBackupTraceSink で
/// 発火回数と category を assert する。
/// </summary>
public interface IBackupTraceSink
{
    /// <summary>非致命な失敗を通知する。category は "sweep-temp"/"load-all"/"restore-item"/"restore-item-later" のいずれか。</summary>
    void Warn(string category, string detail, Exception? ex);
}
```

`src/yEdit.App/DebugBackupTraceSink.cs` を新規作成:

```csharp
using System.Diagnostics;

namespace yEdit.App;

/// <summary>
/// 既定の trace sink。System.Diagnostics.Trace.TraceWarning に流す(本番 log 転送はオプション)。
/// </summary>
public sealed class DebugBackupTraceSink : IBackupTraceSink
{
    public void Warn(string category, string detail, Exception? ex)
    {
        var msg = ex is null
            ? $"[backup:{category}] {detail}"
            : $"[backup:{category}] {detail} :: {ex.GetType().Name}: {ex.Message}";
        Trace.TraceWarning(msg);
    }
}
```

### Step 1b.3: Fake sink を作る(テスト用)

`tests/yEdit.App.Tests/Fakes/FakeBackupTraceSink.cs` を新規作成:

```csharp
using yEdit.App;

namespace yEdit.App.Tests.Fakes;

/// <summary>
/// BackupCoordinator の catch 4 箇所が確かに Trace を呼ぶことを assert するための Fake。
/// カテゴリ・回数・保持した例外を検証可能に露出する。
/// </summary>
internal sealed class FakeBackupTraceSink : IBackupTraceSink
{
    public List<(string Category, string Detail, Exception? Ex)> Warnings { get; } = new();
    public void Warn(string category, string detail, Exception? ex)
        => Warnings.Add((category, detail, ex));
}
```

### Step 1b.4: 失敗テストを書く(catch 4 箇所の trace 発火)

`tests/yEdit.App.Tests/BackupCoordinatorTests.cs` に 4 テストを追加(SweepTempFiles 失敗・LoadAll 失敗・restore 失敗・restore-later 失敗の順)。

各テストは既存の `HostForm.CreateWithDocs` パターンを使い、`FakeRestorePrompt` の restore delegate を throw させる。詳細は既存の `OfferRestoreOnStartup_*` テストの姉妹形として書く。以下は 1 例(残り 3 テストも同形):

```csharp
[Fact]
public void OfferRestoreOnStartup_RestoreDelegateThrows_WarnsViaTraceSink()
{
    // Task 1b: catch (Exception) that swallowed restore-item failures now traces through IBackupTraceSink.
    Sta.Run(() =>
    {
        using var host = HostForm.CreateWithDocs();
        var trace = new FakeBackupTraceSink();
        var writer = new FakeBackupWriter();
        // 予め 1 レコード書き込んでおく(restore 対象)
        writer.SeedRecord(id: "r1", originalPath: "C:/dummy.txt");
        var restorePrompt = new FakeRestorePrompt(
            action: RestoreAction.Restore, checkedIds: new[] { "r1" });
        var coord = new BackupCoordinator(
            host.Docs, enabled: true, intervalSeconds: 60,
            TimeProvider.System, () => writer, restorePrompt,
            directory: writer.Directory, traceSink: trace);
        int restored = coord.OfferRestoreOnStartup(
            host.Form,
            rec => throw new InvalidOperationException("boom"),
            confirm: true);
        Assert.Equal(0, restored);
        Assert.Contains(trace.Warnings, w => w.Category == "restore-item"
            && w.Ex is InvalidOperationException);
    });
}
```

**残り 3 テスト:**
- `OfferRestoreOnStartup_SweepFails_WarnsViaTraceSink`(BackupStore.SweepTempFiles 失敗)
- `OfferRestoreOnStartup_LoadAllFails_WarnsViaTraceSink`(BackupStore.LoadAll 失敗)
- `OfferRestoreOnStartup_LaterRestoreThrows_WarnsViaTraceSink`(confirm=false 経路)

BackupStore は静的なので、SweepTempFiles / LoadAll 失敗をテストするには BackupStore にも間接呼び出しを噛ませるか、`_dir` を存在しないパスに変えて I/O 例外を誘導する。**設計判断**: 存在しないディレクトリを渡して自然に IOException を起こす経路が最も低侵襲。

### Step 1b.5: テストを走らせて失敗確認

```powershell
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj `
  --filter FullyQualifiedName~WarnsViaTraceSink
# 期待: 4 FAIL(まだ traceSink 引数が存在しないため compile error)
```

### Step 1b.6: BackupCoordinator を修正

**Step 1b.6.1: ctor 追加引数**

`BackupCoordinator.cs` の ctor に `IBackupTraceSink? traceSink = null` を追加(既定 null → `DebugBackupTraceSink`)。フィールド `_trace` を新設。

```csharp
private readonly IBackupTraceSink _trace;

public BackupCoordinator(
    DocumentManager docs, bool enabled, int intervalSeconds,
    TimeProvider clock, Func<IBackupWriter> writerFactory,
    IRestorePrompt restorePrompt, string? directory = null,
    IBackupTraceSink? traceSink = null)
{
    ...
    _trace = traceSink ?? new DebugBackupTraceSink();
    ...
}
```

**Step 1b.6.2: 4 箇所の catch を書き換え**

```csharp
// Line 113: SweepTempFiles
try { BackupStore.SweepTempFiles(_dir); }
catch (Exception ex) { _trace.Warn("sweep-temp", _dir, ex); }

// Line 117: LoadAll(失敗時 return 0 の挙動は保持)
try { records = BackupStore.LoadAll(_dir); }
catch (Exception ex) { _trace.Warn("load-all", _dir, ex); return 0; }

// Line 134: restore-item (confirm=false 経路)
catch (Exception ex) { _trace.Warn("restore-item-later", rec.Id, ex); }

// Line 155: restore-item (Restore 経路)
catch (Exception ex) { _trace.Warn("restore-item", rec.Id, ex); }
```

**Step 1b.6.3: MainForm の注入配線**

`MainForm.cs:86-90` の `BackupCoordinator` 生成呼び出しに `traceSink:` を渡さない(既定 = DebugBackupTraceSink)。**変更なし。既定引数で本番挙動を維持する**。

### Step 1b.7: テスト再実行

```powershell
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj `
  --filter FullyQualifiedName~WarnsViaTraceSink
# 期待: 4 PASS
```

### Step 1b.8: 全体ゲート

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・0 警告・962 tests(957+5=954+1[1a]+4[1b]=+5 の想定)
```

### Step 1b.9: L5 実機検証

- NVDA を起動し、本アプリでバックアップ復元経路を1度通す。
- Trace 出力が視覚/SR 挙動に影響しないこと(既存挙動と同じ)を確認。
- DebugView 等で Trace 出力が飛んでいることを目視確認(任意)。

### Step 1b.10: コミット + main マージ

```powershell
git add src/yEdit.App/Abstractions/IBackupTraceSink.cs `
        src/yEdit.App/DebugBackupTraceSink.cs `
        src/yEdit.App/BackupCoordinator.cs `
        tests/yEdit.App.Tests/Fakes/FakeBackupTraceSink.cs `
        tests/yEdit.App.Tests/BackupCoordinatorTests.cs
git commit -m "refactor(backup): silent catch を IBackupTraceSink で診断可能に (Task 1b)"
```

別エージェント review → main へ no-ff マージ。

---

## Task 1c: 旧 `TextFileService.Save(string, string, …)` を Buffer 版に集約

**目的:** `TextFileService.cs:273-313` の `Save(path, string text, …)` は body+preamble+payload の 2〜3 倍メモリ。呼び出し元を Buffer 版 `Save(path, TextBuffer, …)` に集約し、旧版を internal に格下げ(または fallback 専用に閉じる)。

**Files:**
- Modify: `src/yEdit.Core/Text/TextFileService.cs:273-313`
- Modify: 旧版呼び出し元(次ステップで grep で特定)
- Test: `tests/yEdit.Core.Tests/Text/TextFileServiceTests.cs`(既存が Buffer 版でも通ることを確認)

**L5 実機検証:** 不要(I/O 経路の変更・SR 経路不変)

### Step 1c.1: ブランチ作成 + 呼び出し元 grep

```powershell
git checkout main
git checkout -b feature/refactor-1c-save-consolidation

# 呼び出し元 grep(EncodingCatalog.EnsureRegistered() 呼び出しごと切り出したメソッドの呼び手を探す)
```

Grep ツール で以下パターンを走らせ、旧版の呼び出し元を列挙する。

- Pattern: `TextFileService\.Save\s*\(`
- 対象: `src/**/*.cs`, `tests/**/*.cs`
- 期待結果を PR description に転記する。

**予想される所在:**
- `TextFileService.cs:383` の共有違反 fallback 内(Buffer 版から旧版を呼んでいる)
- `Core.Tests/Text/TextFileServiceTests.cs` の複数箇所(既存 byte 検証テスト)

**判断:** 呼び出し元が「fallback 内 + テストのみ」なら旧版は既に internal 相当。この場合は本 Task を「旧版を `internal` にダウングレード + xmldoc に fallback 専用と明記」に絞り込む。もし本番コードから旧版を呼んでいる箇所が見つかれば、Buffer 版へ書き換える(該当箇所を Task 1c で確定してから対応)。

### Step 1c.2: 失敗テストを書く(可視性契約)

`tests/yEdit.Core.Tests/Text/TextFileServiceTests.cs` の末尾に以下追加:

```csharp
[Fact]
public void Save_StringText_IsInternalOnly()
{
    // Task 1c: 旧版 Save(path, string, Encoding, hasBom) は共有違反 fallback 用の
    // internal 実装に閉じ、public API 面からは Buffer 版のみ露出する。
    var flags = System.Reflection.BindingFlags.Public
              | System.Reflection.BindingFlags.NonPublic
              | System.Reflection.BindingFlags.Static;
    var methods = typeof(TextFileService).GetMethods(flags)
        .Where(m => m.Name == "Save").ToList();
    var stringOverload = methods.FirstOrDefault(m =>
        m.GetParameters().Length == 4 &&
        m.GetParameters()[1].ParameterType == typeof(string));
    Assert.NotNull(stringOverload);
    Assert.False(stringOverload!.IsPublic, "string 版 Save は public であってはならない(fallback 専用)");
}
```

### Step 1c.3: テストを走らせて失敗確認

```powershell
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj `
  --filter FullyQualifiedName~Save_StringText_IsInternalOnly
# 期待: FAIL(現状は public)
```

### Step 1c.4: 旧版を internal 化 + fallback 契約明記

`TextFileService.cs:273-313` の `public static void Save(...)` を `internal static void Save(...)` に変更。xmldoc に「共有違反フォールバック専用。新規呼び出しは Save(path, TextBuffer, ...) を使うこと」を追記。

**依存確認:** `yEdit.Core.csproj` の `InternalsVisibleTo` に `yEdit.Core.Tests` は既に含まれているので、既存テスト(byte 検証)の呼び出しは compile 可能なまま。App 層から呼んでいる箇所があれば Buffer 版に書き換える(Step 1c.1 の grep 結果次第)。

### Step 1c.5: 追加テスト(Buffer 版が既存 string 版と同じ byte を出力)

`tests/yEdit.Core.Tests/Text/TextFileServiceTests.cs` に **同じ本文/エンコーディング/BOM で string 版と Buffer 版が同一バイト列を出力する** 対称テストを 4 パターン追加:

- UTF-8 (no BOM)
- UTF-8 (BOM あり)
- SJIS
- EUC-JP

```csharp
[Theory]
[InlineData(65001, false)]
[InlineData(65001, true)]
[InlineData(932, false)]
[InlineData(51932, false)]
public void Save_BufferAndString_YieldSameBytes(int codePage, bool hasBom)
{
    EncodingCatalog.EnsureRegistered();
    var enc = EncodingCatalog.Get(codePage);
    var text = "日本語混じり\nline2\n";

    using var tmpBuffer = TempFile.Create();
    using var tmpString = TempFile.Create();
    var buffer = TextBuffer.FromString(text);

    TextFileService.Save(tmpBuffer.Path, buffer, enc, hasBom);   // public Buffer 版
    // internal string 版はテストからは呼べるが、InternalsVisibleTo 経由。
    // 契約テストなので、代わりに Buffer 版が「string 版と同等バイト」であることを
    // 既存 string 版 fixture(共有違反 fallback 経由で byte を吐く経路)と比較する。
    byte[] bufferBytes = File.ReadAllBytes(tmpBuffer.Path);
    byte[] expected = ExpectedBytesFor(text, enc, hasBom);
    Assert.Equal(expected, bufferBytes);
}
```

`ExpectedBytesFor` は既存の byte 生成 helper を再利用(既に `TextFileServiceTests` にある想定・なければ private static method で追加)。

### Step 1c.6: テスト再実行

```powershell
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj `
  --filter FullyQualifiedName~TextFileService
# 期待: 既存全緑 + Save_StringText_IsInternalOnly PASS + Save_BufferAndString_YieldSameBytes 4 PASS
```

### Step 1c.7: 全体ゲート

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・0 警告・966 tests(962+4[1c])
```

### Step 1c.8: コミット + main マージ

```powershell
git add src/yEdit.Core/Text/TextFileService.cs `
        tests/yEdit.Core.Tests/Text/TextFileServiceTests.cs
git commit -m "refactor(io): 旧 Save(string,...) を internal 化 + Buffer 版に集約 (Task 1c)"
```

別エージェント review → main へ no-ff マージ。

---

## Task 1d: CsvParser.Parse(TextSnapshot) + Document.ParseCsv 全文字列化撤廃

**目的:** `Document.cs:38-47` が 1GB CSV で OOM リスクを持つ `Editor.SnapshotText` に依存。`CsvParser.Parse(TextSnapshot)` オーバーロードを追加し、chunk 読みで全文字列化を排除する。

**Files:**
- Modify: `src/yEdit.Core/Csv/CsvParser.cs`(オーバーロード追加)
- Modify: `src/yEdit.App/Document.cs:33-47`(呼び出し切替 + 参照同一性判定を Root へ)
- Test: `tests/yEdit.Core.Tests/Csv/CsvParserTests.cs`(オーバーロード対称テスト + chunk 境界テスト)

**L5 実機検証:** 不要(CSV パースは SR 経路に触れない)

### Step 1d.1: ブランチ作成

```powershell
git checkout main
git checkout -b feature/refactor-1d-csv-snapshot-parse
```

### Step 1d.2: 失敗テストを書く(オーバーロード契約)

`tests/yEdit.Core.Tests/Csv/CsvParserTests.cs` に以下追加:

```csharp
[Fact]
public void Parse_TextSnapshotOverload_ProducesSameResultAsString()
{
    string csv = "a,b,c\n1,\"quoted, comma\",3\n\"multi\nline\",x,y\n";
    var expected = CsvParser.Parse(csv);

    var buffer = TextBuffer.FromString(csv);
    var actual = CsvParser.Parse(buffer.Current);   // 新規オーバーロード

    Assert.Equal(expected.Rows.Count, actual.Rows.Count);
    for (int i = 0; i < expected.Rows.Count; i++)
    {
        Assert.Equal(expected.Rows[i].Fields.Count, actual.Rows[i].Fields.Count);
        for (int j = 0; j < expected.Rows[i].Fields.Count; j++)
            Assert.Equal(expected.Rows[i].Fields[j].Value, actual.Rows[i].Fields[j].Value);
    }
}

[Fact]
public void Parse_TextSnapshotOverload_HandlesQuotedFieldAcrossChunkBoundary()
{
    // SnapshotReader の chunk 境界(通常 4KB / 8KB)を跨ぐ quoted field で
    // 状態機械が崩れないことを確認。fixture は境界前後にダブルクォート内改行を配置。
    var sb = new System.Text.StringBuilder();
    sb.Append(new string('a', 4090));   // chunk 境界近くまで埋める
    sb.Append(",\"quoted with , comma\nand newline\",tail\n");
    string csv = sb.ToString();
    var buffer = TextBuffer.FromString(csv);
    var parsed = CsvParser.Parse(buffer.Current);
    Assert.Equal(1, parsed.Rows.Count);
    Assert.Equal(3, parsed.Rows[0].Fields.Count);
    Assert.Equal("quoted with , comma\nand newline", parsed.Rows[0].Fields[1].Value);
}
```

### Step 1d.3: テストを走らせて失敗確認

```powershell
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj `
  --filter FullyQualifiedName~Parse_TextSnapshotOverload
# 期待: 2 FAIL(オーバーロード未実装)
```

### Step 1d.4: CsvParser にオーバーロード追加

`CsvParser.cs` に以下を追加。既存の `Parse(string)` 実装を「状態機械 + Reader インタフェース」でリファクタし、`Parse(TextSnapshot)` と `Parse(string)` が共通の内部 `ParseCore(TextReader)` を呼ぶ形に。

```csharp
public static CsvDocument Parse(TextSnapshot snapshot)
{
    ArgumentNullException.ThrowIfNull(snapshot);
    using var reader = snapshot.CreateReader();
    return ParseCore(reader);
}

// 既存の Parse(string) は wrapper 化:
public static CsvDocument Parse(string text)
{
    using var reader = new StringReader(text);
    return ParseCore(reader);
}

// 現行の状態機械をこの private static に移し込む
private static CsvDocument ParseCore(TextReader reader) { ... }
```

**注意:** 現行の `CsvParser.Parse(string)` が文字インデックスベースで書かれている場合、`TextReader` ベースへの変換で境界処理を丁寧に書き直す必要がある。`SnapshotReader` は char 単位の `Read(char[], int, int)` を提供するので、chunk 単位で状態機械にフィードする。既存の状態機械が引き継げるよう wrapper を書き直す。

### Step 1d.5: Document.ParseCsv を切替

`src/yEdit.App/Document.cs:33-47` を書き換え:

```csharp
// Before
private string? _csvCachedText;
private CsvDocument? _csvCachedDoc;

public CsvDocument ParseCsv()
{
    string text = Editor.SnapshotText;
    if (!ReferenceEquals(text, _csvCachedText))
    {
        _csvCachedDoc = CsvParser.Parse(text);
        _csvCachedText = text;
    }
    return _csvCachedDoc!;
}

// After
private TextSnapshot? _csvCachedSnap;
private CsvDocument? _csvCachedDoc;

public CsvDocument ParseCsv()
{
    var snap = Editor.CurrentBuffer.Current;
    // Root は PieceTree の immutable ルート = 編集で差し替わるので参照同一性で失効判定できる。
    if (_csvCachedSnap is null || !ReferenceEquals(snap.Root, _csvCachedSnap.Root))
    {
        _csvCachedDoc = CsvParser.Parse(snap);
        _csvCachedSnap = snap;
    }
    return _csvCachedDoc!;
}

public void ClearCsvCache()
{
    _csvCachedSnap = null;
    _csvCachedDoc = null;
}
```

**注意:** `TextSnapshot.Root` が現状 internal の場合、`yEdit.Core.csproj` の InternalsVisibleTo に `yEdit.App` を追加するか、`TextSnapshot` に `internal Node Root { get; }` を露出する既存の可視性を活かす。既存 `EditorControl.cs:107` は既に Snap.Root を触っているため、可視性経路は確立済み。

### Step 1d.6: テスト再実行

```powershell
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj `
  --filter FullyQualifiedName~Parse_TextSnapshotOverload
# 期待: 2 PASS
```

### Step 1d.7: 全体ゲート

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・0 警告・972 tests(966+6[1d の想定: 追加 2 テスト + 既存の string 版契約が Parse(string) wrapper 化で 4 テスト増加])
```

**注意:** テスト数の増加は Parse(string) の wrapper 化に伴う既存テストのパラメタライズ数によって変動する。実際の増減は Step 1d.7 で確認して plan の見出し数字に反映(誤差 ±2 は許容)。

### Step 1d.8: コミット + main マージ

```powershell
git add src/yEdit.Core/Csv/CsvParser.cs `
        src/yEdit.App/Document.cs `
        tests/yEdit.Core.Tests/Csv/CsvParserTests.cs
git commit -m "refactor(csv): CsvParser.Parse(TextSnapshot) 追加 + Document 全文字列化撤廃 (Task 1d)"
```

別エージェント review → main へ no-ff マージ。

---

## Task 1e: DocumentManager Action/EventHandler 統一 or ドキュメント補強

**目的:** `DocumentManager.BeforeActiveChange`(`Action?`)と他 7 個の `EventHandler` の混在を、案 A(現状維持 + xmldoc 補強)または案 B(EventHandler 統一)のどちらかで確定。

**Files:**
- Modify: `src/yEdit.App/DocumentManager.cs`(案 A なら xmldoc のみ / 案 B なら型変更 + 購読側 1 箇所)
- Test: 案 B のみ `tests/yEdit.App.Tests/DocumentManagerTests.cs` に呼出契約テストを追加

**L5 実機検証:** 不要

### Step 1e.1: ブランチ作成 + 案の最終決定

```powershell
git checkout main
git checkout -b feature/refactor-1e-event-consistency
```

**判断基準:**
- 案 A(現状維持): `BeforeActiveChange` の xmldoc は既に「意図的例外」と説明済み(`DocumentManager.cs:30-31`)。追加コストなし。
- 案 B(統一): 他 event と表面を揃える。空 EventArgs のインスタンス(`EventArgs.Empty`)を発火。購読側は `MainForm.cs:93`(`_docs.BeforeActiveChange = () => _csv.AbortEdit();`)を書き換え。

**推奨は案 A**(既に xmldoc がある + YAGNI)。着手時に「案 A で確定」と PR description に明記して案 B は非採択とする。

### Step 1e.2A: 案 A の実施(xmldoc 補強)

`DocumentManager.cs:30-31` の既存 xmldoc をより明示的に(EventHandler と揃えない理由を保存):

```csharp
/// <summary>アクティブタブが切り替わる直前のフック（F2 編集中なら中断させる等）。
/// マウス操作は Deselecting で、キーボード/プログラム経路は各選択メソッドから発火する。</summary>
/// <remarks>
/// <b>設計判断(Task 1e で確認済)</b>: sender/args とも意味を持たない = <see cref="EventHandler"/> 化しない。
/// 他 7 個の event と型が違うのは意図的な例外。呼び出し側は <c>= () => ...;</c> の代入形式で購読する。
/// 案 B(<see cref="EventHandler"/> 統一)も検討したが、購読側が sender/args を無視する空実装になり
/// 益がないため見送り。将来 sender/args を使う必要が生じたら再検討する。
/// </remarks>
public Action? BeforeActiveChange { get; set; }
```

### Step 1e.3A: 契約テスト(Trivial・現状維持を機械固定)

`tests/yEdit.App.Tests/DocumentManagerTests.cs` に以下追加(案 A・現状維持の型を固定):

```csharp
[Fact]
public void BeforeActiveChange_Type_IsIntentionallyAction()
{
    // Task 1e (案 A 採択): 意図的に Action?(sender/args を持たない)ままにしている契約を
    // 機械固定する。将来 EventHandler 化する場合は本テストを必ず更新すること。
    var property = typeof(DocumentManager).GetProperty("BeforeActiveChange");
    Assert.NotNull(property);
    Assert.Equal(typeof(Action), property!.PropertyType);
}
```

### Step 1e.4: 全体ゲート

```powershell
tools/pre-merge-check.ps1
# 期待: 全緑・0 警告・974 tests(972+2[1e]。実際は 1 Fact なので +1 で 973 の可能性あり)
```

**注意:** 案 B を選んだ場合はテスト設計が変わる(EventHandler 発火時の sender/args がどう扱われるかの契約テスト)。案 A で確定した前提で本 plan は 974 とする。

### Step 1e.5: コミット + main マージ

```powershell
git add src/yEdit.App/DocumentManager.cs `
        tests/yEdit.App.Tests/DocumentManagerTests.cs
git commit -m "refactor(app): DocumentManager.BeforeActiveChange の Action 型を意図的例外として固定 (Task 1e)"
```

別エージェント review → main へ no-ff マージ。

---

## Phase 1 完了時のチェックリスト

- [ ] Task 1a〜1e 全て main マージ済
- [ ] `tools/pre-merge-check.ps1` 全緑・0 警告・974 tests(想定)
- [ ] L5 実機検証(1b のみ)完了
- [ ] MEMORY.md を更新(refactor-1 completion)
- [ ] Phase 2 の実装計画(`docs/plans/2026-07-<date>-refactor-editorcontrol-partial.md`)を writing-plans skill で新規作成する準備

---

## Phase 2 概要: EditorControl partial 分割

**位置付け:** Phase 1 完了後に着手。詳細は Phase 1 完了時に別ファイル(`docs/plans/2026-XX-XX-refactor-editorcontrol-partial.md`)で `writing-plans` skill を使い新規作成する。

**サブ Phase 目次(いずれも SR 経路不変・L5 不要):**

| サブ | ブランチ名 | DoD |
|---|---|---|
| 2a | `feature/refactor-2a-editorcontrol-ime` | EditorControl.Ime.cs 抽出・Editor.Tests 218 緑継続 |
| 2b | `feature/refactor-2b-editorcontrol-caret` | EditorControl.Caret.cs 抽出・Editor.Tests 緑継続 |
| 2c | `feature/refactor-2c-editorcontrol-input` | EditorControl.Input.cs 抽出・Editor.Tests 緑継続 |
| 2d | `feature/refactor-2d-editorcontrol-uia` | EditorControl.Uia.cs 抽出・Editor.Tests 緑継続 |
| 2e | `feature/refactor-2e-editorcontrol-paint` | EditorControl.Paint.cs 抽出・Editor.Tests 緑継続 |

**分割対象:** 設計書 §3(`docs/plans/2026-07-16-refactor-separation-of-concerns-design.md` §3)を参照。

**分割後の EditorControl.cs 想定:** ~800 行(現状 3396)。

## Phase 3 概要: Controller 委譲

**位置付け:** Phase 2 完了後に着手。詳細は Phase 2 完了時に別ファイル(`docs/plans/2026-XX-XX-refactor-editorcontrol-controllers.md`)で `writing-plans` skill を使い新規作成する。

**サブ Phase 目次:**

| サブ | ブランチ名 | L5 | DoD |
|---|---|---|---|
| 3a | `feature/refactor-3a-ime-controller` | 必要 | ImeController 抽出・IImeContext seam・Editor.Tests 緑 |
| 3b | `feature/refactor-3b-caret-controller` | 不要 | CaretController 抽出・_caret/_anchor/_desiredXpx 所有権移譲 |
| 3c | `feature/refactor-3c-input-router` | 不要 | InputRouter 抽出・キーマップ dictionary 化 |
| 3d | `feature/refactor-3d-uia-adapter` | 必要 | UiaTextHostAdapter 抽出・_bufferSnapshot/_bounds 所有権移譲 |

**分割対象:** 設計書 §4(`docs/plans/2026-07-16-refactor-separation-of-concerns-design.md` §4)を参照。

**Phase 3 完了時の Editor 層想定構成:** 設計書 §5 参照。EditorControl.cs は ~600 行、Controller 4 個で ~1750 行。

---

## リスクと緩和策(全 Phase 共通)

| リスク | 緩和策 |
|---|---|
| サブ Phase 中の別バグ修正でコンフリクト | サブ Phase を短命(1〜2 日)に保ち、main と同期しやすくする |
| Phase 1 の catch トレース(1b)で本番挙動が変わる | `DebugBackupTraceSink` は Trace.TraceWarning のみで side effect なし・L5 で実機確認 |
| Task 1d の chunk 境界で quoted field 分断バグ | Step 1d.2 の境界テストで根絶 |
| readonly 化(1a)で ctor 順序依存が顕在化 | Step 1a.4 で現状の順序を維持することで definite assignment 成立を保証 |

---

## 全体 DoD(Definition of Done・全 Phase 完了時)

- 全テスト緑(現状 954 → 想定 974(Phase 1) → +α(Phase 2) → +β(Phase 3))
- 0 警告(Release ビルド)
- EditorControl.cs 3396 → ~600 行
- Editor 層に Controller 4 個抽出済み(Ime/Caret/Input/UiaAdapter)
- 中小 5 項目全て解消
- MEMORY.md に完了記録追加
