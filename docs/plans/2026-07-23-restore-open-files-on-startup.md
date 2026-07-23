# 起動時に前回開いていたファイルを開く 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 通常終了時に開いていたタブ列(パスあり ファイル+無題タブ本文+アクティブ+カーソル位置)を settings.json と別ファイル(last-session-buffers.json)へ保存し、次回起動時に復元する。既定オフ・バックアップ復元が優先。

**Architecture:** Core.Session に純ロジックを閉じ(SessionTabRecord / LastSessionSnapshot / LastSessionBuffersStore)、AppSettings 拡張は既存 SettingsStore.Normalize の防御パターンを踏襲。復元経路は FileController.RestoreLastSession に集約し、既存 TryOpenOrActivate / DocumentManager.CreateNew の seam を再利用。MainForm は OnFormClosing / OnShown へ配線 3〜4 行を足すだけ。EditorControl には行/桁指定でキャレット移動する薄い 1 API(`SetCaretByLineColumn`)を追加。

**Tech Stack:** C# / .NET 9 WinForms / xUnit / System.Text.Json / TDD(Task ごとにテスト先行)

**設計書:** `docs/plans/2026-07-23-restore-open-files-on-startup-design.md`

**タスクレビュー方針(CLAUDE.md §4 準拠):** 各 Task 完了時に「仕様レビュー → コード品質レビュー → 脆弱性レビュー」の 3 段レビューを別エージェント(superpowers:code-reviewer)で実施し、指摘を反映してから次 Task へ進む。

---

## Task 1: Core.Session の型と Store 骨格を追加

**Files:**
- Create: `src/yEdit.Core/Session/SessionTabRecord.cs`
- Create: `src/yEdit.Core/Session/LastSessionSnapshot.cs`
- Create: `src/yEdit.Core/Session/LastSessionBuffersStore.cs`
- Create: `tests/yEdit.Core.Tests/Session/LastSessionBuffersStoreTests.cs`

**Step 1: 失敗テストを書く**

`tests/yEdit.Core.Tests/Session/LastSessionBuffersStoreTests.cs`:

```csharp
using System.IO;
using Xunit;
using yEdit.Core.Session;

namespace yEdit.Core.Tests.Session;

public class LastSessionBuffersStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var map = LastSessionBuffersStore.Load(TempPath());
        Assert.Empty(map);
    }

    [Fact]
    public void Save_Then_Load_Roundtrips()
    {
        string path = TempPath();
        try
        {
            var src = new Dictionary<string, string>
            {
                ["k1"] = "hello",
                ["k2"] = "こんにちは\n2行目",
            };
            LastSessionBuffersStore.Save(path, src);
            var loaded = LastSessionBuffersStore.Load(path);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("hello", loaded["k1"]);
            Assert.Equal("こんにちは\n2行目", loaded["k2"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json");
            var map = LastSessionBuffersStore.Load(path);
            Assert.Empty(map);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, string.Empty);
            var map = LastSessionBuffersStore.Load(path);
            Assert.Empty(map);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesParentDirectoryIfMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string path = Path.Combine(dir, "last-session-buffers.json");
        try
        {
            var map = new Dictionary<string, string> { ["k"] = "v" };
            LastSessionBuffersStore.Save(path, map);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Delete_MissingFile_IsNoOp()
    {
        LastSessionBuffersStore.Delete(TempPath()); // 例外にならなければ OK
    }

    [Fact]
    public void Delete_ExistingFile_Removes()
    {
        string path = TempPath();
        File.WriteAllText(path, "{}");
        LastSessionBuffersStore.Delete(path);
        Assert.False(File.Exists(path));
    }
}
```

**Step 2: テスト実行して失敗を確認**

```
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~LastSessionBuffersStoreTests
```
Expected: コンパイルエラー(`LastSessionBuffersStore` 未定義)

**Step 3: 型と Store を実装**

`src/yEdit.Core/Session/SessionTabRecord.cs`:

```csharp
namespace yEdit.Core.Session;

/// <summary>
/// タブ 1 個の永続表現。通常終了時に AppSettings.LastSession の一部として settings.json へ保存し、
/// 次回起動時に前回タブ列を復元する。無題タブの本文本体は BufferKey で LastSessionBuffersStore を参照する
/// (=大きな本文で settings.json が肥大化するのを避ける)。
/// </summary>
public sealed record SessionTabRecord(
    string? Path,
    int UntitledNumber,
    string? BufferKey,
    bool IsActive,
    int CaretLine,
    int CaretColumn
);
```

`src/yEdit.Core/Session/LastSessionSnapshot.cs`:

```csharp
namespace yEdit.Core.Session;

/// <summary>通常終了時のタブ列スナップショット(順序=タブ順)。</summary>
public sealed record LastSessionSnapshot(List<SessionTabRecord> Tabs);
```

`src/yEdit.Core/Session/LastSessionBuffersStore.cs`:

```csharp
using System.Text.Json;

namespace yEdit.Core.Session;

/// <summary>
/// 無題タブの本文を BufferKey→本文 のマップとして単一 JSON ファイルへ保存する。
/// 破損時は空 Dict を返し呼び出し側が grace degradation で continue する(空タブは復元せず skip する
/// 契約は FileController.RestoreLastSession 側にある)。設計書 §2.4。
/// </summary>
public static class LastSessionBuffersStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>%APPDATA%\yEdit\last-session-buffers.json(SettingsStore.DefaultPath と同ディレクトリ)。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "yEdit",
            "last-session-buffers.json"
        );

    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, string>();
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>();
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options);
            return map ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static void Save(string path, IReadOnlyDictionary<string, string> map)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(map, Options);
        File.WriteAllText(path, json);
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 削除失敗は致命でない(次回 Load が Deserialize しても既存本文は使われないため無害)
        }
    }
}
```

**Step 4: テスト成功を確認**

```
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~LastSessionBuffersStoreTests
```
Expected: PASS(7 テスト)

**Step 5: コミット**

```bash
git add src/yEdit.Core/Session tests/yEdit.Core.Tests/Session
git commit -m "feat(session): SessionTabRecord/LastSessionSnapshot/BuffersStore を追加"
```

**Step 6: 3 段レビュー(仕様・品質・脆弱性)**

superpowers:code-reviewer を起動し、以下を確認:
- 仕様: 設計書 §2.2〜2.4 と合致(record 定義・API 形状・破損時の grace degradation)
- 品質: null 耐性・try/catch の粒度・命名
- 脆弱性: JSON 破損時の DoS(サイズ制限は Task 6 で扱う)/ ファイルパス injection なし

指摘は fixup commit で反映する。

---

## Task 2: AppSettings 拡張と SettingsStore.Normalize 補正

**Files:**
- Modify: `src/yEdit.Core/Settings/AppSettings.cs`(2 プロパティ追加)
- Modify: `src/yEdit.Core/Settings/SettingsStore.cs`(Normalize 拡張)
- Modify: `tests/yEdit.Core.Tests/Settings/AppSettingsTests.cs`(既定値の追加)
- Modify: `tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs`(Normalize テストを追加)

**Step 1: 失敗テストを書く**

`tests/yEdit.Core.Tests/Settings/AppSettingsTests.cs` に追加:

```csharp
[Fact]
public void Defaults_RestoreOpenFilesOnStartup_IsFalse()
{
    var s = new AppSettings();
    Assert.False(s.RestoreOpenFilesOnStartup);
}

[Fact]
public void Defaults_LastSession_IsNull()
{
    var s = new AppSettings();
    Assert.Null(s.LastSession);
}
```

`tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs` に追加(既存の `Load_normalizes_corrupt_numeric_and_null_fields` 近くに):

```csharp
[Fact]
public void Load_Normalizes_LastSession_Skips_BlankPath_And_Clamps_NegativeNumbers()
{
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    try
    {
        // Tabs=[有効パス, 空白パス(=skip), 無題+負値カレット(=clamp), 無題+負連番(=clamp)]
        File.WriteAllText(path,
            "{\"LastSession\":{\"Tabs\":["
                + "{\"Path\":\"C:\\\\a.txt\",\"UntitledNumber\":0,\"BufferKey\":null,\"IsActive\":true,\"CaretLine\":10,\"CaretColumn\":5},"
                + "{\"Path\":\"   \",\"UntitledNumber\":0,\"BufferKey\":null,\"IsActive\":false,\"CaretLine\":0,\"CaretColumn\":0},"
                + "{\"Path\":null,\"UntitledNumber\":1,\"BufferKey\":\"k1\",\"IsActive\":false,\"CaretLine\":-1,\"CaretColumn\":-5},"
                + "{\"Path\":null,\"UntitledNumber\":-3,\"BufferKey\":\"k2\",\"IsActive\":false,\"CaretLine\":0,\"CaretColumn\":0}"
                + "]}}");
        var s = SettingsStore.Load(path);
        Assert.NotNull(s.LastSession);
        Assert.Equal(3, s.LastSession!.Tabs.Count); // 空白 Path はスキップ
        Assert.Equal(@"C:\a.txt", s.LastSession.Tabs[0].Path);
        Assert.Null(s.LastSession.Tabs[1].Path);
        Assert.Equal(0, s.LastSession.Tabs[1].CaretLine); // 負値→0
        Assert.Equal(0, s.LastSession.Tabs[1].CaretColumn); // 負値→0
        Assert.Equal(0, s.LastSession.Tabs[2].UntitledNumber); // 負値→0
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

[Fact]
public void Load_LastSession_NullTabs_BecomesEmptyList()
{
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    try
    {
        File.WriteAllText(path, "{\"LastSession\":{\"Tabs\":null}}");
        var s = SettingsStore.Load(path);
        Assert.NotNull(s.LastSession);
        Assert.Empty(s.LastSession!.Tabs);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

[Fact]
public void Roundtrip_LastSession_And_RestoreFlag()
{
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    try
    {
        var s = new AppSettings
        {
            RestoreOpenFilesOnStartup = true,
            LastSession = new LastSessionSnapshot(new List<SessionTabRecord>
            {
                new(Path: @"C:\a.txt", UntitledNumber: 0, BufferKey: null,
                    IsActive: true, CaretLine: 3, CaretColumn: 7),
                new(Path: null, UntitledNumber: 2, BufferKey: "abc",
                    IsActive: false, CaretLine: 0, CaretColumn: 0),
            }),
        };
        SettingsStore.Save(path, s);
        var loaded = SettingsStore.Load(path);
        Assert.True(loaded.RestoreOpenFilesOnStartup);
        Assert.NotNull(loaded.LastSession);
        Assert.Equal(2, loaded.LastSession!.Tabs.Count);
        Assert.Equal(@"C:\a.txt", loaded.LastSession.Tabs[0].Path);
        Assert.True(loaded.LastSession.Tabs[0].IsActive);
        Assert.Equal(3, loaded.LastSession.Tabs[0].CaretLine);
        Assert.Equal(7, loaded.LastSession.Tabs[0].CaretColumn);
        Assert.Equal("abc", loaded.LastSession.Tabs[1].BufferKey);
        Assert.Equal(2, loaded.LastSession.Tabs[1].UntitledNumber);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
```

必要な using を SettingsStoreTests に追加(既存 `using yEdit.Core.Settings;` に加えて `using yEdit.Core.Session;`)。

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~AppSettingsTests|FullyQualifiedName~SettingsStoreTests
```
Expected: `RestoreOpenFilesOnStartup` / `LastSession` プロパティ未定義でコンパイルエラー

**Step 3: AppSettings と Normalize を拡張**

`src/yEdit.Core/Settings/AppSettings.cs` の末尾(RecentFiles の下)に追加:

```csharp
    /// <summary>起動時に前回開いていたタブ列を復元するか(既定 false=既存挙動維持)。設計書 2026-07-23。</summary>
    public bool RestoreOpenFilesOnStartup { get; set; }

    /// <summary>通常終了時のタブ列スナップショット。null=保存なし(設定 OFF or 未終了)。設計書 2026-07-23。</summary>
    public yEdit.Core.Session.LastSessionSnapshot? LastSession { get; set; }
```

`Clone()` は MemberwiseClone + RecentFiles 複製のみで、LastSession は record(不変)なので参照コピーで足りる(復元経路は Load 直後の 1 回だけ読むため mutation なし)=変更不要。

`src/yEdit.Core/Settings/SettingsStore.cs` の `using` に追加:

```csharp
using yEdit.Core.Session;
```

`Normalize` メソッド末尾の `s.WrapColumn = WrapGeometry.ClampColumns(...)` の直後に追加:

```csharp
        NormalizeLastSession(s);
        return s;
    }

    /// <summary>
    /// LastSession の防御的補正。
    /// - Tabs が null → 空リスト
    /// - Path が IsNullOrWhiteSpace → その SessionTabRecord を skip(復元経路で空タブ追加を避ける)
    /// - UntitledNumber<0 / CaretLine<0 / CaretColumn<0 → 0 に clamp
    /// 設計書 §2.3。
    /// </summary>
    private static void NormalizeLastSession(AppSettings s)
    {
        if (s.LastSession is null) return;
        if (s.LastSession.Tabs is null)
        {
            s.LastSession = new LastSessionSnapshot(new List<SessionTabRecord>());
            return;
        }
        var cleaned = new List<SessionTabRecord>(s.LastSession.Tabs.Count);
        foreach (var t in s.LastSession.Tabs)
        {
            // Path があるが空白のみ=不完全レコード → skip
            if (t.Path is not null && string.IsNullOrWhiteSpace(t.Path))
                continue;
            cleaned.Add(t with
            {
                UntitledNumber = Math.Max(0, t.UntitledNumber),
                CaretLine = Math.Max(0, t.CaretLine),
                CaretColumn = Math.Max(0, t.CaretColumn),
            });
        }
        s.LastSession = new LastSessionSnapshot(cleaned);
    }
```

**Step 4: テスト成功を確認**

```
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~AppSettingsTests|FullyQualifiedName~SettingsStoreTests
```
Expected: 全 PASS

**Step 5: コミット**

```bash
git add src/yEdit.Core/Settings tests/yEdit.Core.Tests/Settings
git commit -m "feat(settings): RestoreOpenFilesOnStartup/LastSession キーと Normalize 補正を追加"
```

**Step 6: 3 段レビュー**

- 仕様: 設計書 §2.1・§2.3 と合致(既定値・Normalize 補正)
- 品質: 防御ロジックの網羅性・null 耐性・record with の使い方
- 脆弱性: 攻撃 JSON(超巨大 Tabs 配列)対策=Task 6 の buffers サイズ上限で扱うため本 Task では追加不要(要トラッキング)

---

## Task 3: EditorControl に行/桁指定のキャレット移動 API を追加

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.Caret.cs`(SetCaretByLineColumn 追加)
- Modify: `tests/yEdit.Editor.Tests/EditorControlCaretTests.cs` or 該当テスト(なければ新規)

まず既存の Editor.Tests 構成を確認:

```
ls tests/yEdit.Editor.Tests
```

`GoToLine` テストが置かれているファイルに追記する(なければ `SetCaretByLineColumnTests.cs` を新設)。

**Step 1: 失敗テストを書く**

```csharp
using Xunit;
using yEdit.Editor;

namespace yEdit.Editor.Tests;

public class SetCaretByLineColumnTests
{
    private static EditorControl NewEditorWith(string text)
    {
        var ed = new EditorControl();
        ed.Text = text; // SetSource + SetSavePoint 相当(既存挙動)
        return ed;
    }

    [Fact]
    public void MovesToLineStart_WhenColumnIsZero()
    {
        using var ed = NewEditorWith("abc\r\ndef\r\nghi");
        ed.SetCaretByLineColumn(1, 0);
        Assert.Equal(1, ed.CurrentLine);
        Assert.Equal(0, ed.GetColumn(ed.CurrentPosition));
    }

    [Fact]
    public void MovesToColumnWithinLine()
    {
        using var ed = NewEditorWith("abcdef\r\nghijkl");
        ed.SetCaretByLineColumn(0, 3);
        Assert.Equal(0, ed.CurrentLine);
        Assert.Equal(3, ed.GetColumn(ed.CurrentPosition));
    }

    [Fact]
    public void ClampsColumnToLineEnd_WhenColumnExceedsLineWidth()
    {
        using var ed = NewEditorWith("abc\r\nghijkl");
        // 行 0 の長さは 3。column=999 → 3 に clamp(改行の前)
        ed.SetCaretByLineColumn(0, 999);
        Assert.Equal(0, ed.CurrentLine);
        Assert.Equal(3, ed.GetColumn(ed.CurrentPosition));
    }

    [Fact]
    public void ClampsLineToLast_WhenLineExceedsLineCount()
    {
        using var ed = NewEditorWith("a\r\nb\r\nc");
        ed.SetCaretByLineColumn(999, 0);
        Assert.Equal(2, ed.CurrentLine);
    }

    [Fact]
    public void ClampsNegativeLineAndColumn_ToZero()
    {
        using var ed = NewEditorWith("abc");
        ed.SetCaretByLineColumn(-5, -3);
        Assert.Equal(0, ed.CurrentLine);
        Assert.Equal(0, ed.GetColumn(ed.CurrentPosition));
    }

    [Fact]
    public void EmptyBuffer_DoesNotThrow()
    {
        using var ed = new EditorControl(); // SetSource 未実施
        ed.SetCaretByLineColumn(0, 0); // no-op で例外なし
    }
}
```

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj --filter FullyQualifiedName~SetCaretByLineColumn
```
Expected: `SetCaretByLineColumn` 未定義でコンパイルエラー

**Step 3: 実装**

`src/yEdit.Editor/EditorControl.Caret.cs` の `GoToLine` の直後に追加:

```csharp
    /// <summary>
    /// 0-based の行/桁を指定してキャレットを移動する。復元経路(FileController.RestoreLastSession)で
    /// 前回終了時のカーソル位置を再現するために追加。line/col とも実バッファ範囲へクランプ、
    /// col は行末(改行手前)を上限とする=次行に食み出さない。設計書 2026-07-23 §3.4。
    /// </summary>
    public void SetCaretByLineColumn(int line, int col)
    {
        if (_buffer is null)
            return;
        var snap = _buffer.Current;
        int lc = snap.LineCount;
        if (lc <= 0)
            return;
        int clampedLine = Math.Clamp(line, 0, lc - 1);
        int lineStart = snap.GetLineStart(clampedLine);
        int lineEnd = snap.GetLineEnd(clampedLine, includeBreak: false);
        int maxCol = Math.Max(0, lineEnd - lineStart);
        int clampedCol = Math.Clamp(col, 0, maxCol);
        SetCaretCharOffset(lineStart + clampedCol);
    }
```

**Step 4: テスト成功**

```
dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj --filter FullyQualifiedName~SetCaretByLineColumn
```
Expected: 6 PASS

**Step 5: コミット**

```bash
git add src/yEdit.Editor/EditorControl.Caret.cs tests/yEdit.Editor.Tests
git commit -m "feat(editor): SetCaretByLineColumn を追加(復元経路のカーソル位置再現用)"
```

**Step 6: 3 段レビュー**

- 仕様: line/col の clamp が設計書 §3.4 と一致
- 品質: 既存 GoToLine と同経路(SetCaretCharOffset 経由)で副作用パターン統一
- 脆弱性: 範囲外入力でも例外を投げない(復元経路の攻撃 JSON を吸収)

---

## Task 4: FileController の LoadInto エラーダイアログ抑止フラグ

**Files:**
- Modify: `src/yEdit.App/FileController.cs`
- Modify: `tests/yEdit.App.Tests/FileControllerTests.cs`

**Step 1: 失敗テストを書く**

`FileControllerTests.cs` の末尾に追加:

```csharp
[Fact]
public void LoadInto_SuppressErrorPrompt_SwallowsErrorDialog_ButStillReturnsFalse() =>
    Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string missing = tmp.File("no-such-file.txt");

        // 通常経路: エラーダイアログが 1 個出る
        host.File.TryOpenOrActivate(missing);
        Assert.Contains(host.Prompt.Log, e => e.Kind == "Error");

        host.Prompt.Log.Clear();

        // 抑止 ON: ダイアログは出ないが失敗自体は伝播する
        host.File.WithLoadErrorPromptSuppressed(() =>
        {
            var result = host.File.TryOpenOrActivate(missing);
            Assert.Null(result);
        });
        Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Error");

        // 抑止解除後: 再びダイアログが出る(finally での復元確認)
        host.File.TryOpenOrActivate(missing);
        Assert.Contains(host.Prompt.Log, e => e.Kind == "Error");
    });
```

**Step 2: 失敗を確認**

Expected: `WithLoadErrorPromptSuppressed` 未定義でコンパイルエラー

**Step 3: 実装**

`FileController.cs` に private field を追加:

```csharp
    // 復元経路(RestoreLastSession)専用: LoadInto の catch 内 _prompt.Error を一時抑止し、
    // 失敗パスを failedPaths に集約する(単発ダイアログを避けまとめて 1 個で通知するため)。
    private bool _suppressLoadErrorPrompt;
```

`LoadInto` の catch 内 `_prompt.Error(...)` をガード:

```csharp
        catch (Exception ex)
            when (ex is System.IO.IOException or UnauthorizedAccessException
                       or System.Security.SecurityException or NotSupportedException
                       or DocumentTooLargeException)
        {
            if (!_suppressLoadErrorPrompt)
            {
                _prompt.Error(
                    $"開けませんでした: {SanitizeForDisplay.OneLine(ex.Message, 200)}",
                    "エラー"
                );
            }
            return false;
        }
```

同様に `TryProbeReachability` の内側の `_prompt.Error` もガードする(復元経路で UNC 到達不能もダイアログ抑止対象):

```csharp
    private bool TryProbeReachability(string path)
    {
        if (RemotePathDetector.IsRemote(path)
            && !_reachabilityProbe.ProbeWithTimeout(path, TimeSpan.FromSeconds(5)))
        {
            if (!_suppressLoadErrorPrompt)
            {
                _prompt.Error(
                    $"ネットワークパスに到達できません: {SanitizeForDisplay.OneLine(path, 200)}",
                    "エラー"
                );
            }
            return false;
        }
        return true;
    }
```

テスト seam として public メソッドを追加:

```csharp
    /// <summary>
    /// action の実行中だけ LoadInto の catch 内 _prompt.Error を抑止する(復元経路の内部利用と
    /// テスト seam を兼ねる)。finally で必ず復元。
    /// </summary>
    public void WithLoadErrorPromptSuppressed(Action action)
    {
        bool prev = _suppressLoadErrorPrompt;
        _suppressLoadErrorPrompt = true;
        try { action(); }
        finally { _suppressLoadErrorPrompt = prev; }
    }
```

**Step 4: テスト成功**

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter FullyQualifiedName~LoadInto_SuppressErrorPrompt
```
Expected: PASS

**Step 5: コミット**

```bash
git add src/yEdit.App/FileController.cs tests/yEdit.App.Tests/FileControllerTests.cs
git commit -m "feat(file): LoadInto の error prompt を復元経路で抑止する seam を追加"
```

**Step 6: 3 段レビュー**

- 仕様: 抑止対象は復元経路のみ(既存の開く/最近/grep/開き直しは挙動不変)
- 品質: finally での復元漏れなし・nested 呼び出しでも安全(prev を保存)
- 脆弱性: 抑止中に例外が上に抜けても finally で復元される

---

## Task 5: FileController.RestoreLastSession 実装

**Files:**
- Modify: `src/yEdit.App/FileController.cs`
- Modify: `tests/yEdit.App.Tests/FileControllerTests.cs`

**Step 1: 失敗テストを書く**

`FileControllerTests.cs` に追加(先に一部代表を示す。実装時は主要 8 ケースを網羅):

```csharp
[Fact]
public void RestoreLastSession_OpensPathTabs_ClosesInitialEmpty() =>
    Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string p1 = tmp.File("a.txt");
        string p2 = tmp.File("b.txt");
        File2.WriteAllText(p1, "AAA");
        File2.WriteAllText(p2, "BBB");
        var initialEmpty = host.Docs.CreateNew();
        host.Docs.Active!.State.Path = null; // untitled

        var snap = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(Path: p1, UntitledNumber: 0, BufferKey: null,
                IsActive: false, CaretLine: 0, CaretColumn: 0),
            new(Path: p2, UntitledNumber: 0, BufferKey: null,
                IsActive: true, CaretLine: 0, CaretColumn: 2),
        });

        var failed = host.File.RestoreLastSession(
            snap, new Dictionary<string, string>(), initialEmpty);

        Assert.Empty(failed);
        Assert.Equal(2, host.Docs.Count); // initialEmpty は閉じられ、a.txt と b.txt が開く
        Assert.Equal(p2, host.Docs.Active!.State.Path); // IsActive=true の b.txt
        Assert.Equal(2, host.Docs.Active!.Editor.GetColumn(host.Docs.Active!.Editor.CurrentPosition));
    });

[Fact]
public void RestoreLastSession_FailedPathsAggregated_NoIndividualDialog() =>
    Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string ok = tmp.File("ok.txt");
        File2.WriteAllText(ok, "OK");
        string missing = tmp.File("missing.txt");
        var initialEmpty = host.Docs.CreateNew();

        var snap = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(missing, 0, null, false, 0, 0),
            new(ok, 0, null, true, 0, 0),
        });
        var failed = host.File.RestoreLastSession(
            snap, new Dictionary<string, string>(), initialEmpty);

        Assert.Single(failed);
        Assert.Equal(missing, failed[0]);
        Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Error"); // 集約=個別ダイアログなし
        Assert.Equal(1, host.Docs.Count); // ok.txt のみ復元(initialEmpty は閉じた)
    });

[Fact]
public void RestoreLastSession_UntitledBufferMissing_SkipsRecord() =>
    Sta.Run(() =>
    {
        using var host = new Host();
        var initialEmpty = host.Docs.CreateNew();

        // BufferKey=k1 だが buffers に含まれない → skip
        var snap = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(null, 1, "k1", true, 0, 0),
        });
        var failed = host.File.RestoreLastSession(
            snap, new Dictionary<string, string>(), initialEmpty);

        Assert.Empty(failed);
        Assert.Equal(1, host.Docs.Count); // initialEmpty がそのまま残る(復元 0)
        Assert.Same(initialEmpty, host.Docs.Documents[0]);
    });

[Fact]
public void RestoreLastSession_UntitledContentPresent_RestoresContent_ModifiedFalse() =>
    Sta.Run(() =>
    {
        using var host = new Host();
        var initialEmpty = host.Docs.CreateNew();

        var snap = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(null, 2, "k1", true, 0, 4),
        });
        var buffers = new Dictionary<string, string> { ["k1"] = "hello world" };

        var failed = host.File.RestoreLastSession(snap, buffers, initialEmpty);

        Assert.Empty(failed);
        Assert.Equal(1, host.Docs.Count); // initialEmpty は閉じ、無題タブ 1 個
        var doc = host.Docs.Active!;
        Assert.Null(doc.State.Path);
        Assert.Equal(2, doc.State.UntitledNumber);
        Assert.Equal("hello world", doc.Editor.SnapshotText);
        Assert.False(doc.Editor.Modified); // SetSavePoint 済み
        Assert.Equal(4, doc.Editor.GetColumn(doc.Editor.CurrentPosition));
    });

[Fact]
public void RestoreLastSession_NothingRestored_KeepsInitialEmpty() =>
    Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var initialEmpty = host.Docs.CreateNew();

        // すべて失敗 or skip = 復元 0
        var snap = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(tmp.File("missing.txt"), 0, null, false, 0, 0),
            new(null, 1, "kx", false, 0, 0), // BufferKey 欠落
        });
        var failed = host.File.RestoreLastSession(
            snap, new Dictionary<string, string>(), initialEmpty);

        Assert.Single(failed);
        Assert.Equal(1, host.Docs.Count);
        Assert.Same(initialEmpty, host.Docs.Documents[0]); // 保持
    });

[Fact]
public void RestoreLastSession_CaretPosition_ClampsOutOfRange() =>
    Sta.Run(() =>
    {
        using var host = new Host();
        var initialEmpty = host.Docs.CreateNew();

        var snap = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(null, 1, "k1", true, 999, 999),
        });
        var buffers = new Dictionary<string, string> { ["k1"] = "abc" };

        host.File.RestoreLastSession(snap, buffers, initialEmpty);

        var doc = host.Docs.Active!;
        Assert.Equal(0, doc.Editor.CurrentLine); // 1 行しかない
        Assert.Equal(3, doc.Editor.GetColumn(doc.Editor.CurrentPosition)); // "abc" 行末
    });
```

必要な using を追加: `using yEdit.Core.Session;`

**Step 2: 失敗を確認**

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter FullyQualifiedName~RestoreLastSession
```
Expected: `RestoreLastSession` 未定義でコンパイルエラー

**Step 3: 実装**

`FileController.cs` の `RestoreFromBackup` メソッドの直後に:

```csharp
    /// <summary>
    /// 通常終了時に保存した LastSessionSnapshot を新タブへ復元する。
    /// - パスあり: TryOpenOrActivate(既存経路) で開く。失敗時は failedPaths に集約(単発ダイアログ抑止)。
    /// - 無題タブ: BufferKey が buffers に無ければ skip(空タブを追加しない=設計書 §4 E4/E5)。
    /// 復元タブが 1 個以上できた場合、ctor で作った initialEmpty(空無題タブ)を閉じる。
    /// アクティブタブは IsActive=true のレコードに対応する doc(なければ復元順の最後)。
    /// 設計書 2026-07-23 §3.4。
    /// </summary>
    public IReadOnlyList<string> RestoreLastSession(
        yEdit.Core.Session.LastSessionSnapshot snap,
        IReadOnlyDictionary<string, string> buffers,
        Document? initialEmpty
    )
    {
        var failedPaths = new List<string>();
        Document? activeDoc = null;
        int openedCount = 0;

        WithLoadErrorPromptSuppressed(() =>
        {
            foreach (var rec in snap.Tabs)
            {
                if (rec.Path is not null)
                {
                    var doc = TryOpenOrActivate(rec.Path);
                    if (doc is null)
                    {
                        failedPaths.Add(rec.Path);
                        continue;
                    }
                    doc.Editor.SetCaretByLineColumn(rec.CaretLine, rec.CaretColumn);
                    openedCount++;
                    if (rec.IsActive) activeDoc = doc;
                }
                else
                {
                    // 無題タブ: BufferKey 未指定 or store から欠落 → skip(空タブを追加しない)
                    if (rec.BufferKey is null
                        || !buffers.TryGetValue(rec.BufferKey, out var content))
                        continue;
                    var doc = RestoreUntitledTab(rec, content);
                    openedCount++;
                    if (rec.IsActive) activeDoc = doc;
                }
            }
        });

        if (activeDoc is not null)
            _docs.Activate(activeDoc);
        if (openedCount > 0 && initialEmpty is not null)
            _docs.TryClose(initialEmpty, _ => true); // 空無題タブは無条件破棄
        _metaChanged();
        return failedPaths;
    }

    private Document RestoreUntitledTab(yEdit.Core.Session.SessionTabRecord rec, string content)
    {
        var s = _settings();
        var doc = _docs.CreateNew();
        doc.State.Path = null;
        doc.State.UntitledNumber = rec.UntitledNumber > 0 ? rec.UntitledNumber : ++_untitledSeq;
        if (rec.UntitledNumber > _untitledSeq)
            _untitledSeq = rec.UntitledNumber; // 以後の新規無題と衝突しないように連番を追従
        doc.State.Encoding = EncodingCatalog.Get(s.DefaultCodePage);
        doc.State.HasBom = false;
        doc.State.LineEnding = (LineEnding)s.DefaultLineEnding;
        doc.Editor.SetOrReplaceSource(TextBuffer.FromString(content));
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        doc.Editor.SetSavePoint(); // Modified=false で開始(通常終了時の状態を再現)
        doc.Editor.SetCaretByLineColumn(rec.CaretLine, rec.CaretColumn);
        DocumentManager.UpdateLabel(doc);
        return doc;
    }
```

**Step 4: テスト成功**

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter FullyQualifiedName~RestoreLastSession
```
Expected: 6 PASS(+ 既存 all-green)

**Step 5: コミット**

```bash
git add src/yEdit.App/FileController.cs tests/yEdit.App.Tests/FileControllerTests.cs
git commit -m "feat(file): RestoreLastSession 実装(パスあり/無題タブ復元・失敗パス集約)"
```

**Step 6: 3 段レビュー**

- 仕様: 設計書 §3.4 と一致(パスあり/無題タブ/IsActive/initialEmpty 破棄)
- 品質: `WithLoadErrorPromptSuppressed` の再利用・_untitledSeq の追従が RestoreFromBackup と対称
- 脆弱性: 攻撃 JSON(超巨大 Tabs)は上限を Task 6 で扱う=本 Task では該当なし。BufferKey は Dictionary.Get のみで injection なし

---

## Task 6: MainForm 保存経路(OnFormClosing 統合)

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`
- Modify: `tests/yEdit.App.Tests/MainFormSmokeTests.cs`

**Step 1: 失敗テストを書く**

`MainFormSmokeTests.cs` に追加:

```csharp
[Fact]
public void OnFormClosing_RestoreEnabled_SavesLastSessionAndBuffers() =>
    Sta.Run(() =>
    {
        using var tmp = new TempDir();
        string txt = tmp.File("a.txt");
        File2.WriteAllText(txt, "hello");
        string buffersPath = Path.Combine(tmp.Root, "last-session-buffers.json");

        var settings = NewSettings(csvAutoModeOnOpen: false);
        settings.RestoreOpenFilesOnStartup = true;

        using (var form = ShowMainForm(settings, tmp.SettingsPath))
        {
            // last-session-buffers の path を試験用パスへ差し替える機構(Task 6 で追加=下記実装参照)
            form.SetLastSessionBuffersPathForTest(buffersPath);
            form.FileForTest.TryOpenOrActivate(txt);
            form.Close();
        }

        var loaded = SettingsStore.Load(tmp.SettingsPath);
        Assert.True(loaded.RestoreOpenFilesOnStartup);
        Assert.NotNull(loaded.LastSession);
        Assert.Contains(loaded.LastSession!.Tabs, t => t.Path == txt);
    });

[Fact]
public void OnFormClosing_RestoreDisabled_ClearsLastSessionAndDeletesBuffers() =>
    Sta.Run(() =>
    {
        using var tmp = new TempDir();
        string buffersPath = Path.Combine(tmp.Root, "last-session-buffers.json");
        // 事前に buffers.json 残骸を作っておく → 設定 OFF で消えるはず
        File2.WriteAllText(buffersPath, "{\"k\":\"stale\"}");

        var settings = NewSettings(csvAutoModeOnOpen: false);
        settings.RestoreOpenFilesOnStartup = false;
        settings.LastSession = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(@"C:\stale.txt", 0, null, true, 0, 0),
        });

        using (var form = ShowMainForm(settings, tmp.SettingsPath))
        {
            form.SetLastSessionBuffersPathForTest(buffersPath);
            form.Close();
        }

        var loaded = SettingsStore.Load(tmp.SettingsPath);
        Assert.False(loaded.RestoreOpenFilesOnStartup);
        Assert.Null(loaded.LastSession);
        Assert.False(File2.Exists(buffersPath)); // 残骸削除
    });
```

**Step 2: 失敗を確認**

Expected: `SetLastSessionBuffersPathForTest` 未定義でコンパイルエラー

**Step 3: 実装**

`MainForm.cs` に追加(private field:)

```csharp
    // テストが実 %APPDATA% を汚さないための seam。null=既定パス。
    private string? _lastSessionBuffersPathOverride;
    private string LastSessionBuffersPath =>
        _lastSessionBuffersPathOverride ?? yEdit.Core.Session.LastSessionBuffersStore.DefaultPath;

    internal void SetLastSessionBuffersPathForTest(string path) =>
        _lastSessionBuffersPathOverride = path;
```

`OnFormClosing` の末尾、既存の `SaveSettingsSafe();` の**直前**に追加:

```csharp
        // 通常終了時に前回セッションを保存(設定 OFF なら既存残骸を消す=設計書 §3.1)
        if (_settings.RestoreOpenFilesOnStartup)
        {
            var (snap, buffers) = BuildLastSessionSnapshot();
            _settings.LastSession = snap;
            SaveLastSessionBuffersSafe(buffers);
        }
        else
        {
            _settings.LastSession = null;
            DeleteLastSessionBuffersSafe();
        }
```

`MainForm` に private methods を追加(既存の `SaveSettingsSafe` の近くに):

```csharp
    /// <summary>
    /// 現在のタブ列から LastSessionSnapshot と無題本文マップを組み立てる純関数。
    /// 無題本文が 1M chars を超えた場合は BufferKey=null に落として枠だけ保存する(BK-M-3 と同方針)。
    /// 設計書 §3.1。
    /// </summary>
    private const int MaxSessionUntitledContentChars = 1024 * 1024;

    private (yEdit.Core.Session.LastSessionSnapshot Snap,
             Dictionary<string, string> Buffers) BuildLastSessionSnapshot()
    {
        var tabs = new List<yEdit.Core.Session.SessionTabRecord>();
        var buffers = new Dictionary<string, string>();
        var active = _docs.Active;
        foreach (var doc in _docs.Documents)
        {
            int line = doc.Editor.CurrentLine;
            int col = doc.Editor.GetColumn(doc.Editor.CurrentPosition);
            string? bufferKey = null;
            if (doc.State.Path is null)
            {
                string content = doc.Editor.SnapshotText;
                if (content.Length <= MaxSessionUntitledContentChars)
                {
                    bufferKey = System.Guid.NewGuid().ToString("N");
                    buffers[bufferKey] = content;
                }
                else
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "yEdit: last-session-content-skipped (untitled {0}, {1} chars)",
                        doc.State.UntitledNumber, content.Length);
                }
            }
            tabs.Add(new yEdit.Core.Session.SessionTabRecord(
                Path: doc.State.Path,
                UntitledNumber: doc.State.Path is null ? doc.State.UntitledNumber : 0,
                BufferKey: bufferKey,
                IsActive: ReferenceEquals(doc, active),
                CaretLine: line,
                CaretColumn: col
            ));
        }
        return (new yEdit.Core.Session.LastSessionSnapshot(tabs), buffers);
    }

    private void SaveLastSessionBuffersSafe(IReadOnlyDictionary<string, string> map)
    {
        try
        {
            yEdit.Core.Session.LastSessionBuffersStore.Save(LastSessionBuffersPath, map);
        }
        catch
        {
            System.Diagnostics.Trace.TraceWarning(
                "yEdit: failed to save last-session-buffers.json");
        }
    }

    private void DeleteLastSessionBuffersSafe()
    {
        try
        {
            yEdit.Core.Session.LastSessionBuffersStore.Delete(LastSessionBuffersPath);
        }
        catch
        {
            /* 削除失敗は致命でない */
        }
    }
```

必要な using(MainForm.cs 先頭に追加):

```csharp
using yEdit.Core.Session;
```

上の実装コードで `yEdit.Core.Session.*` を完全修飾しているため using 追加は必須ではないが、可読性のため追加する。

**Step 4: テスト成功**

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter FullyQualifiedName~OnFormClosing_Restore
```
Expected: 2 PASS

**Step 5: コミット**

```bash
git add src/yEdit.App/MainForm.cs tests/yEdit.App.Tests/MainFormSmokeTests.cs
git commit -m "feat(main): OnFormClosing で LastSession/buffers を保存(設定 OFF で残骸削除)"
```

**Step 6: 3 段レビュー**

- 仕様: 設計書 §3.1 と一致(保存条件・残骸削除・上限クリップ)
- 品質: private 化・Trace ログの sink 統一・null 耐性
- 脆弱性: 無題本文 1M chars 上限で settings.json 肥大攻撃を防ぐ(BK-M-3 と対称)

---

## Task 7: MainForm 復元経路(OnShown 統合)

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`
- Modify: `tests/yEdit.App.Tests/MainFormSmokeTests.cs`

**Step 1: 失敗テストを書く**

```csharp
[Fact]
public void OnShown_RestoreEnabled_NoBackup_RestoresPreviousTabs() =>
    Sta.Run(() =>
    {
        using var tmp = new TempDir();
        string p1 = tmp.File("a.txt");
        File2.WriteAllText(p1, "AAA");

        var settings = NewSettings(csvAutoModeOnOpen: false);
        settings.RestoreOpenFilesOnStartup = true;
        settings.LastSession = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(p1, 0, null, true, 0, 0),
        });

        using var form = ShowMainForm(settings, tmp.SettingsPath);
        // 復元経路発火 → a.txt が開いている・空無題タブは閉じられている
        Assert.Contains(form.FileForTest.DocsForTest, d => d.State.Path == p1);
        Assert.Single(form.FileForTest.DocsForTest);
    });

[Fact]
public void OnShown_RestoreEnabled_BackupPresent_SkipsRestore() =>
    Sta.Run(() =>
    {
        // BackupEnabled=true で restored>0 になる経路を作る(orphan バックアップを事前に置く)。
        // 実装容易性のため、ここでは代替として backup がある「かのように」振る舞う
        // seam を MainForm 側に追加(SetRestoredCountOverrideForTest)。
        using var tmp = new TempDir();
        string p1 = tmp.File("a.txt");
        File2.WriteAllText(p1, "AAA");

        var settings = NewSettings(csvAutoModeOnOpen: false);
        settings.RestoreOpenFilesOnStartup = true;
        settings.LastSession = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(p1, 0, null, true, 0, 0),
        });

        using var form = ShowMainForm_WithBackupCountOverride(settings, tmp.SettingsPath, restoredOverride: 3);
        // restored=3 → 前回タブ復元スキップ・a.txt は開かない
        Assert.DoesNotContain(form.FileForTest.DocsForTest, d => d.State.Path == p1);
    });

[Fact]
public void OnShown_RestoreDisabled_DoesNotRestore() =>
    Sta.Run(() =>
    {
        using var tmp = new TempDir();
        string p1 = tmp.File("a.txt");
        File2.WriteAllText(p1, "AAA");

        var settings = NewSettings(csvAutoModeOnOpen: false);
        settings.RestoreOpenFilesOnStartup = false; // OFF
        settings.LastSession = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(p1, 0, null, true, 0, 0),
        });

        using var form = ShowMainForm(settings, tmp.SettingsPath);
        Assert.DoesNotContain(form.FileForTest.DocsForTest, d => d.State.Path == p1);
    });

[Fact]
public void OnShown_RestoreEnabled_MissingFile_ShowsAggregatedWarn() =>
    Sta.Run(() =>
    {
        using var tmp = new TempDir();
        string missing = tmp.File("no-such.txt");

        var settings = NewSettings(csvAutoModeOnOpen: false);
        settings.RestoreOpenFilesOnStartup = true;
        settings.LastSession = new LastSessionSnapshot(new List<SessionTabRecord>
        {
            new(missing, 0, null, true, 0, 0),
        });

        using var form = ShowMainForm(settings, tmp.SettingsPath);
        // 起動時に集約 Warn が出る=Prompt Log を検査したいが、MainForm は
        // MessageBoxUserPrompt 直生成のためテスト観測が難しい。ここでは復元件数 0 と
        // 起動時の空タブが残ることのみ assert する(集約 Warn は L3 seam を持たないため
        // Task 7 で「MessageBox.Show 呼び出しを IUserPrompt 経由に注入化する」小変更を
        // 加えるか、observability のみに留めるかは実装時判断=下記実装参照)。
        Assert.Single(form.FileForTest.DocsForTest); // 起動時の空無題タブが残る
    });
```

`FileController` に `DocsForTest` seam を追加(既存 `FileForTest` と同型):

```csharp
    internal IReadOnlyList<Document> DocsForTest => _docs.Documents;
```

`ShowMainForm_WithBackupCountOverride` はテスト側のヘルパで、MainForm に `SetRestoredCountOverrideForTest(int)` seam を追加して実現する。

**Step 2: 失敗を確認**

Expected: seam メソッド未定義でコンパイルエラー

**Step 3: 実装**

`MainForm.cs` に:

```csharp
    // Task 6/7: 起動時の復元経路発火制御。ctor で作る空無題タブを覚えて復元成功時に閉じるための seam。
    private Document? _startupEmptyDoc;
    // テスト用: OfferRestoreOnStartup の戻り値を任意に差し替える(バックアップ優先分岐の kill 用)。
    private int? _restoredCountOverrideForTest;
    internal void SetRestoredCountOverrideForTest(int value) =>
        _restoredCountOverrideForTest = value;
```

ctor 末尾で `_file.NewFile();` した直後に:

```csharp
        _startupEmptyDoc = _docs.Active;
```

`OnShown` を差し替え(既存 restored の直後に分岐追加):

```csharp
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _docs.Active?.FocusTarget.Focus();
        UpdateTitle();
        UpdateStatus();

        if (_restoreOffered) return;
        _restoreOffered = true;

        int restored;
        if (_restoredCountOverrideForTest is int overrideValue)
        {
            restored = overrideValue;
        }
        else
        {
            restored = _backup.OfferRestoreOnStartup(
                this, _file.RestoreFromBackup, _settings.ConfirmRestoreOnStartup);
            if (restored > 0)
                _announcer.Say($"バックアップを {restored} 件復元しました");
        }

        // バックアップ復元が 0 件のときだけ、前回タブ復元を試みる(設計書 §3.2 / §4.4)
        if (restored == 0
            && _settings.RestoreOpenFilesOnStartup
            && _settings.LastSession is { Tabs.Count: > 0 } snap)
        {
            TryRestoreLastSession(snap);
        }
    }

    private void TryRestoreLastSession(yEdit.Core.Session.LastSessionSnapshot snap)
    {
        try
        {
            var buffers = yEdit.Core.Session.LastSessionBuffersStore.Load(LastSessionBuffersPath);
            var failed = _file.RestoreLastSession(snap, buffers, _startupEmptyDoc);
            yEdit.Core.Session.LastSessionBuffersStore.Delete(LastSessionBuffersPath);
            if (failed.Count > 0)
                ShowFailedRestoreDialog(failed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "yEdit: restore-last-session failed: {0}",
                yEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200));
        }
    }

    private void ShowFailedRestoreDialog(IReadOnlyList<string> failed)
    {
        const int Cap = 10;
        var shown = failed.Take(Cap)
            .Select(p => yEdit.Core.Text.SanitizeForDisplay.OneLine(p, 200));
        var body = "以下のファイルを開けませんでした:\n\n  " + string.Join("\n  ", shown);
        if (failed.Count > Cap)
            body += $"\n  ... 他 {failed.Count - Cap} 件";
        body += "\n\nこれらは復元対象からはずしました。";
        MessageBox.Show(this, body, "一部のファイルを開けませんでした",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
```

`ShowMainForm_WithBackupCountOverride` を `MainFormSmokeTests.cs` に追加:

```csharp
    private static MainForm ShowMainForm_WithBackupCountOverride(
        AppSettings settings, string settingsPath, int restoredOverride)
    {
        var form = new MainForm(settings, settingsPath);
        form.SetRestoredCountOverrideForTest(restoredOverride);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        return form;
    }
```

**Step 4: テスト成功**

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter FullyQualifiedName~OnShown_Restore
```
Expected: 4 PASS

**Step 5: コミット**

```bash
git add src/yEdit.App/MainForm.cs tests/yEdit.App.Tests/MainFormSmokeTests.cs src/yEdit.App/FileController.cs
git commit -m "feat(main): OnShown で前回タブ復元(バックアップ復元がある場合はスキップ)"
```

**Step 6: 3 段レビュー**

- 仕様: 復元順序が設計書 §4.4 に一致(バックアップ優先→前回タブ→通常起動)
- 品質: try/catch の粒度・ShowFailedRestoreDialog の Cap 10・Sanitize
- 脆弱性: エラーメッセージへの制御文字/BiDi 混入対策(SanitizeForDisplay.OneLine)・パス表示上限

---

## Task 8: BasicSettingsTab に CheckBox を追加

**Files:**
- Modify: `src/yEdit.App/Settings/Tabs/BasicSettingsTab.cs`

UI コードのため単体テストは追加しない(既存の BasicSettingsTab に他項目のテストがないため対称)。動作は MainFormSmokeTests / L5 手動確認で担保。

**Step 1: 実装**

`BasicSettingsTab.cs`:

```csharp
    private readonly CheckBox _restoreOnStartup = new()
    {
        Text = "起動時に前回開いていたファイルを開く(&R)",
        AutoSize = true,
    };
```

`BuildPage`:

```csharp
        _csvAutoMode.TabIndex = 4;
        root.Controls.Add(_csvAutoMode, 0, 2);
        root.SetColumnSpan(_csvAutoMode, 2);

        _restoreOnStartup.TabIndex = 5;
        root.Controls.Add(_restoreOnStartup, 0, 3);
        root.SetColumnSpan(_restoreOnStartup, 2);
        return root;
```

`LoadFrom`:

```csharp
        _csvAutoMode.Checked = s.CsvAutoModeOnOpen;
        _restoreOnStartup.Checked = s.RestoreOpenFilesOnStartup;
```

`SaveTo`:

```csharp
        r.CsvAutoModeOnOpen = _csvAutoMode.Checked;
        r.RestoreOpenFilesOnStartup = _restoreOnStartup.Checked;
```

`Dispose`:

```csharp
        _encoding.Dispose();
        _eol.Dispose();
        _csvAutoMode.Dispose();
        _restoreOnStartup.Dispose();
```

**Step 2: ビルド確認**

```
dotnet build src/yEdit.App/yEdit.App.csproj
```
Expected: 0 warnings

**Step 3: コミット**

```bash
git add src/yEdit.App/Settings/Tabs/BasicSettingsTab.cs
git commit -m "feat(settings): 基本タブに「起動時に前回開いていたファイルを開く」を追加"
```

**Step 4: 3 段レビュー**

- 仕様: ラベル・既定値・アクセラレータキー
- 品質: TabIndex 連番・Dispose の追加漏れなし
- 脆弱性: なし(UI 表示のみ)

---

## Task 9: 説明書の更新

**Files:**
- Modify: `説明書/yEdit説明書.md`

**注意(CLAUDE.md §8):** 説明書はユーザー編集版が正。ここでは「たたき台」を追記し、ユーザー校閲を前提とする。

**Step 1: 基本タブ表に 1 行追加**

`説明書/yEdit説明書.md` の基本タブテーブル(現行 194 行付近)に:

```markdown
| 起動時に前回開いていたファイルを開く | 前回終了時に開いていたタブを再度開く | オフ |
```

**Step 2: バックアップ節に補足を追加**

現行「設定で『起動時に復元するか確認する』をオフにすると、確認なしで自動的に全件復元されます。」の直後に:

```markdown

「起動時に前回開いていたファイルを開く」を有効にしている場合でも、異常終了によるバックアップが残っているときは、バックアップからの復元が優先されます(前回開いていたファイルは自動では開かれません)。
```

**Step 3: コミット**

```bash
git add 説明書/yEdit説明書.md
git commit -m "docs: 説明書に「起動時に前回開いていたファイルを開く」を追記(基本タブ+バックアップ節)"
```

**Step 4: レビュー**

ユーザー校閲を仰ぐ(CLAUDE.md §8)。表現の微調整はユーザー判断。

---

## Task 10: 最終ブランチレビュー + 品質ゲート + PR

**Step 1: pre-merge-check を実行**

```
pwsh -File tools/pre-merge-check.ps1
```
Expected: `EXIT 0`(0 warnings + 全テスト緑)

**Step 2: ブランチ全体レビュー(別エージェント)**

superpowers:code-reviewer(または一般 subagent)へ:
- ブランチ diff 全体を提示
- 設計書と実装の一致
- 挙動不変契約(既定 OFF での既存挙動維持)の確認
- コード品質・脆弱性の総点検

指摘は fixup commit で反映する。

**Step 3: PR 作成**

```bash
git push -u origin feature/restore-open-files-on-startup
```

PR description(日本語・目的・レビュー経緯・申し送りを記載):

```markdown
## 目的

通常終了時に開いていたタブ(パスあり ファイル+無題タブ本文+アクティブ+カーソル位置)を保存し、
次回起動時に復元する。既定オフ・バックアップ復元が優先。

## 設計

- 設計書: docs/plans/2026-07-23-restore-open-files-on-startup-design.md
- 実装計画: docs/plans/2026-07-23-restore-open-files-on-startup.md
- ハイブリッド構成:
  - settings.json → 有効フラグ + タブメタ(パス/無題番号/BufferKey/IsActive/CaretLine/CaretColumn)
  - %APPDATA%\yEdit\last-session-buffers.json → 無題タブ本文(BufferKey→本文)

## 復元順序

1. `OfferRestoreOnStartup` の戻り値 > 0(異常終了バックアップあり) → 前回タブ復元スキップ
2. 設定 ON かつ LastSession あり → 前回タブ復元
3. どちらも該当しなければ通常起動(空無題タブ 1 個)

## エラー処理

- パス欠落/UNC 到達不能/破損: 単発ダイアログを抑止し、集約 Warn 1 個で通知(先頭 10 件+他 N 件)
- 無題本文欠落: 該当レコード skip(空タブを追加しない)
- 想定外例外: 通常起動へフォールバック

## テスト

- L1: LastSessionBuffersStore / AppSettings / SettingsStore.Normalize
- L2: EditorControl.SetCaretByLineColumn
- L3: FileController.RestoreLastSession / MainForm 保存経路・復元経路(バックアップ優先分岐含む)
- L4: 対象外
- L5: SR 経路変更ゼロのため不要判定(判定根拠は設計書 §6.4)

## 申し送り

- 文字コード指定の記憶(前回「開き直す」で明示指定した codepage) は将来検討
- 復元件数の Announcer 通知は本 PR では入れず後続で判断
- buffers.json の暗号化・パーミッション制限は将来のセキュリティ強化課題
```

**Step 4: マージ**

ユーザー承認後にマージ。

---

## 参考: 実装順序と依存

```
Task 1 (Core.Session 骨格)
  ↓
Task 2 (AppSettings + Normalize)
  ↓
Task 3 (Editor.SetCaretByLineColumn) ─┐
                                      │
Task 4 (FileController _suppressLoadErrorPrompt seam)
  ↓                                   │
Task 5 (FileController.RestoreLastSession)  ← Task 3 に依存
  ↓
Task 6 (MainForm OnFormClosing 保存)
  ↓
Task 7 (MainForm OnShown 復元) ← Task 5/6 に依存
  ↓
Task 8 (BasicSettingsTab UI)
  ↓
Task 9 (説明書)
  ↓
Task 10 (最終レビュー + 品質ゲート + PR)
```

各 Task は挙動不変(設定 OFF 時) を最優先とし、既存テスト全緑を維持する。
