# P7/P8 申し送り 5 項目 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** P8 レビュー Minor-4/5(EditorControl v2/Core 内部品質)と P7 チェックリスト申し送りの App 層 3 項目(F3経路整理・Event統一・SaveAs BOM UI)を feature/p7p8-followups 5 コミットで対応する。

**Architecture:** 疎結合順(Event統一→BOM UI→F3経路→Minor-4→Minor-5)で 1 コミット 1 項目。各コミット後に build 0 warning + 該当テスト緑を確認。App 層自動テスト基盤は本計画スコープ外(別セッション申し送り継続)。

**Tech Stack:** .NET 9 / C# 12 / WinForms / xUnit / yEdit.Core.Layout(自作編集エンジン P0〜P8 完了)

**設計書**: `docs/plans/2026-07-13-p7p8-followups-design.md`(コミット `d84ce3f`)
**ブランチ**: `feature/p7p8-followups`(既に切替済み)

---

## Task 1: Event delegate 統一

**目的**: `DocumentManager` の `Action<Document>` 2 種を `EventHandler<Document>` へ統一。
`BeforeActiveChange` の `Action?` は「引数無しの意図的例外」とコメントで明示。

**Files:**
- Modify: `src/yEdit.App/DocumentManager.cs:44-47, 78, 143`
- Modify: `src/yEdit.App/MainForm.cs`(EditorGotFocus / KeyBasedSwitch 購読箇所)

### Step 1.1: DocumentManager の event 宣言と Invoke 変更

`src/yEdit.App/DocumentManager.cs:44` 付近を編集:

```csharp
    /// <summary>アクティブ Document のエディタが Win32 フォーカスを得た。CSVモード中の
    /// シンク退避判断は上位(MainForm)が行う(_csv.IsEditing を参照できるのが上位のため)。</summary>
    public event EventHandler<Document>? EditorGotFocus;

    /// <summary>キー起因(Ctrl+Tab/Ctrl+1..9)のタブ切替時に発火。MainForm が Announcer でタブ名を読ませる。</summary>
    public event EventHandler<Document>? KeyBasedSwitch;
```

`src/yEdit.App/DocumentManager.cs:78` (発火箇所):
```csharp
        editor.GotFocus += (_, _) =>
        {
            if (ReferenceEquals(doc, Active)) EditorGotFocus?.Invoke(this, doc);
        };
```

`src/yEdit.App/DocumentManager.cs:143` (`AnnounceThenFocus` 内):
```csharp
        if (_tabs.SelectedIndex != prevIndex && Active is { } d) KeyBasedSwitch?.Invoke(this, d);
```

`BeforeActiveChange` は変更しない。`Action?` のままにする理由をコメントで明示:

```csharp
    /// <summary>アクティブタブが切り替わる直前のフック(F2 編集中なら中断させる等)。
    /// マウス操作は Deselecting で、キーボード/プログラム経路は各選択メソッドから発火する。</summary>
    /// <remarks>P7/P8 申し送り Event 統一: sender/args とも意味を持たないため EventHandler 化せず
    /// <see cref="Action"/> のまま維持。他の event が EventHandler 系に統一されている中の意図的例外。</remarks>
    public Action? BeforeActiveChange { get; set; }
```

### Step 1.2: MainForm の購読側を修正

`src/yEdit.App/MainForm.cs` の `_docs.KeyBasedSwitch += …` を検索して修正:

```csharp
        _docs.KeyBasedSwitch += (_, doc) => _announcer.Say(doc.State.DisplayName);
```

`EditorGotFocus` の購読があれば同様に修正(現状 MainForm での購読はコメント通り「実質死」だが、
念のため grep で確認)。

### Step 1.3: ビルドとテスト

Run:
```
dotnet build -c Release
dotnet test
```

Expected: 0 warning、Core 601 + Editor 226 = 827 tests 全緑(現状と同数・機能変更なし)。

### Step 1.4: コミット

```bash
git add src/yEdit.App/DocumentManager.cs src/yEdit.App/MainForm.cs
git commit -m "$(cat <<'EOF'
P7/P8 申し送り Task 1: DocumentManager の event 統一

Action<Document> だった EditorGotFocus / KeyBasedSwitch を EventHandler<Document>
に統一。BeforeActiveChange は sender/args とも意味を持たないため Action? のまま
意図的例外として維持し、その旨を doc コメントで明示。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: SaveAs BOM UI(EncodingCatalog + SaveAsDialog + FileController)

**目的**: SaveAs ダイアログの文字コード選択肢を UTF-8 (BOM なし) / UTF-8 (BOM) 別エントリに展開し、
`SaveAsDialog.SelectedHasBom` を公開。FileController が State.HasBom を更新して WriteToPath へ伝播させる。

**Files:**
- Modify: `src/yEdit.Core/Text/EncodingCatalog.cs`
- Create: `tests/yEdit.Core.Tests/Text/SaveAsSelectableEncodingsTests.cs`
- Modify: `src/yEdit.App/SaveAsDialog.cs`
- Modify: `src/yEdit.App/FileController.cs:191-233`

### Step 2.1: EncodingCatalog に SaveAsEncodingOption を追加(TDD 準備)

`src/yEdit.Core/Text/EncodingCatalog.cs:49` の後に追加:

```csharp
    /// <summary>SaveAs 用の選択肢。UTF-8 のみ BOM 有無で 2 エントリに展開し、
    /// 他の CodePage はそのまま(BOM 概念が意味を持たないため HasBom=false 固定)。</summary>
    public readonly record struct SaveAsEncodingOption(int CodePage, bool HasBom, string DisplayName);

    /// <summary>SaveAs で使う文字コード選択肢(表示順)。
    /// 既存の <see cref="SelectableEncodings"/> は開き直し/設定/ステータス表示で使う BOM 無視の一覧。
    /// 本一覧は SaveAs 専用で BOM 有無を明示させるため別プロパティで公開する。</summary>
    public static IReadOnlyList<SaveAsEncodingOption> SaveAsSelectableEncodings { get; } = new[]
    {
        new SaveAsEncodingOption(65001, false, "UTF-8 (BOM なし)"),
        new SaveAsEncodingOption(65001, true,  "UTF-8 (BOM)"),
        new SaveAsEncodingOption(932,   false, "Shift_JIS"),
        new SaveAsEncodingOption(51932, false, "EUC-JP"),
    };
```

### Step 2.2: 失敗するテストを書く

`tests/yEdit.Core.Tests/Text/SaveAsSelectableEncodingsTests.cs` を新規作成:

```csharp
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class SaveAsSelectableEncodingsTests
{
    [Fact]
    public void HasExpectedFourEntries()
    {
        var opts = EncodingCatalog.SaveAsSelectableEncodings;
        Assert.Equal(4, opts.Count);
    }

    [Fact]
    public void FirstIsUtf8NoBom()
    {
        var e = EncodingCatalog.SaveAsSelectableEncodings[0];
        Assert.Equal(65001, e.CodePage);
        Assert.False(e.HasBom);
        Assert.Equal("UTF-8 (BOM なし)", e.DisplayName);
    }

    [Fact]
    public void SecondIsUtf8WithBom()
    {
        var e = EncodingCatalog.SaveAsSelectableEncodings[1];
        Assert.Equal(65001, e.CodePage);
        Assert.True(e.HasBom);
        Assert.Equal("UTF-8 (BOM)", e.DisplayName);
    }

    [Fact]
    public void ThirdIsShiftJisNoBom()
    {
        var e = EncodingCatalog.SaveAsSelectableEncodings[2];
        Assert.Equal(932, e.CodePage);
        Assert.False(e.HasBom);
        Assert.Equal("Shift_JIS", e.DisplayName);
    }

    [Fact]
    public void FourthIsEucJpNoBom()
    {
        var e = EncodingCatalog.SaveAsSelectableEncodings[3];
        Assert.Equal(51932, e.CodePage);
        Assert.False(e.HasBom);
        Assert.Equal("EUC-JP", e.DisplayName);
    }

    [Fact]
    public void OnlyUtf8HasBomVariant()
    {
        var opts = EncodingCatalog.SaveAsSelectableEncodings;
        int utf8Count = 0;
        int nonUtf8HasBomCount = 0;
        foreach (var o in opts)
        {
            if (o.CodePage == 65001) utf8Count++;
            else if (o.HasBom) nonUtf8HasBomCount++;
        }
        Assert.Equal(2, utf8Count);
        Assert.Equal(0, nonUtf8HasBomCount);
    }
}
```

### Step 2.3: テスト実行(Step 2.1 で追加済みなので全緑になるはず)

Run:
```
dotnet test tests/yEdit.Core.Tests --filter FullyQualifiedName~SaveAsSelectableEncodingsTests
```

Expected: 6 tests all pass. もし fail するなら Step 2.1 の実装を修正。

### Step 2.4: SaveAsDialog を SaveAsSelectableEncodings に切替

`src/yEdit.App/SaveAsDialog.cs` を編集:

```csharp
    private static readonly IReadOnlyList<EncodingCatalog.SaveAsEncodingOption> EncodingChoices
        = EncodingCatalog.SaveAsSelectableEncodings;

    // ... (LineEndingChoices はそのまま)

    public string SelectedPath => _path.Text;
    public int SelectedCodePage => EncodingChoices[_encoding.SelectedIndex].CodePage;
    public bool SelectedHasBom => EncodingChoices[_encoding.SelectedIndex].HasBom;
    public LineEnding SelectedLineEnding => LineEndingChoices[_lineEnding.SelectedIndex].Value;

    public SaveAsDialog(string? initialPath, int currentCodePage, bool currentHasBom, LineEnding currentLineEnding)
    {
        Text = "名前を付けて保存";
        // ... (中略・既存の Form 属性設定はそのまま)

        _path.Text = initialPath ?? "";

        int encSel = 0;
        for (int i = 0; i < EncodingChoices.Count; i++)
        {
            var e = EncodingChoices[i];
            _encoding.Items.Add(e.DisplayName);
            // (codePage, hasBom) 完全一致の行を初期選択。UTF-8 では BOM 有無で 2 行あるので厳密一致が必要。
            // 非 UTF-8 は HasBom=false 固定のエントリしか無いので実質 CodePage 一致で決まる。
            if (e.CodePage == currentCodePage && e.HasBom == currentHasBom) encSel = i;
        }
        _encoding.SelectedIndex = encSel;

        // (改行の初期選択・BuildLayout 呼び出しは既存のまま)
```

### Step 2.5: FileController が HasBom を受け渡すよう修正

`src/yEdit.App/FileController.cs:193` を修正:

```csharp
        using var dlg = new SaveAsDialog(doc.State.Path, doc.State.Encoding.CodePage, doc.State.HasBom, doc.State.LineEnding);
        if (dlg.ShowDialog(_owner) != DialogResult.OK) return false;
        if (string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            MessageBox.Show("ファイル名を指定してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var newEncoding = EncodingCatalog.Get(dlg.SelectedCodePage);

        // C-2 追補 I-2: 選択エンコードで表せない文字があれば警告(既存のまま)
        if (dlg.SelectedCodePage != 65001 && !CanEncodeBuffer(doc.Editor.CurrentBuffer, newEncoding))
        {
            // ... (既存のまま)
        }

        // 新エンコード/改行/BOM を State に反映してから WriteToPath へ。
        // C-2 追補 I-1: WriteToPath 失敗時は元の Encoding/LineEnding/HasBom へロールバック。
        var oldEncoding = doc.State.Encoding;
        var oldLineEnding = doc.State.LineEnding;
        var oldHasBom = doc.State.HasBom;
        doc.State.Encoding = newEncoding;
        doc.State.LineEnding = dlg.SelectedLineEnding;
        doc.State.HasBom = dlg.SelectedHasBom;

        if (!WriteToPath(doc, dlg.SelectedPath))
        {
            doc.State.Encoding = oldEncoding;
            doc.State.LineEnding = oldLineEnding;
            doc.State.HasBom = oldHasBom;
            return false;
        }
        doc.State.Path = dlg.SelectedPath;
        _docs.UpdateLabel(doc);
        _metaChanged();
        RegisterRecent(dlg.SelectedPath);
        return true;
```

### Step 2.6: ビルドとテスト

Run:
```
dotnet build -c Release
dotnet test
```

Expected: 0 warning、Core 601+6 = 607 + Editor 226 = 833 tests 全緑。

### Step 2.7: コミット

```bash
git add src/yEdit.Core/Text/EncodingCatalog.cs tests/yEdit.Core.Tests/Text/SaveAsSelectableEncodingsTests.cs src/yEdit.App/SaveAsDialog.cs src/yEdit.App/FileController.cs
git commit -m "$(cat <<'EOF'
P7/P8 申し送り Task 2: SaveAs で BOM 有無を明示指定できる UI

EncodingCatalog に SaveAsSelectableEncodings を追加し UTF-8 を BOM なし/BOM 有りの
2 エントリに展開。SaveAsDialog は SelectedHasBom を公開し、FileController が
State.HasBom を更新して WriteToPath へ伝播させる。既存の SelectableEncodings
(開き直し/設定/ステータス表示)は BOM 無視のまま維持=UTF-8 の BOM 概念が意味を持たない
場面には影響なし。ロールバックも Encoding/LineEnding と同扱いで HasBom を追加。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: F3-after-Hide 通知経路の整理

**目的**: `SearchController.Announce` が Hidden な `_dialog.RaiseNotification` を呼ぶ経路を、
共有 Announcer 直接経由に一本化。実挙動は不変(Announcer は元々同じインスタンスで発声する)。

**Files:**
- Modify: `src/yEdit.App/SearchController.cs`
- Modify: `src/yEdit.App/MainForm.cs`(SearchController 生成順の入替 + Announcer 注入)

### Step 3.1: SearchController に IAnnouncer を注入

`src/yEdit.App/SearchController.cs` の using / field / ctor / Announce メソッドを修正:

```csharp
using System.Text.RegularExpressions;
using yEdit.App.Speech;   // 追加
using yEdit.Core.Csv;
using yEdit.Core.Search;
using yEdit.Editor;

namespace yEdit.App;

public sealed class SearchController
{
    private readonly DocumentManager _docs;
    private readonly Form _owner;
    private readonly IAnnouncer _announcer;   // 追加
    private FindReplaceDialog? _dialog;
    private MatchSpan? _lastHit;
    private (int Start, int End)? _selectionScope;

    public SearchController(DocumentManager docs, Form owner, IAnnouncer announcer)
    {
        _docs = docs;
        _owner = owner;
        _announcer = announcer;
        _docs.ActiveDocumentChanged += (_, _) =>
        {
            _lastHit = null;
            _selectionScope = null;
            if (_dialog?.Visible == true) UpdateCount();
        };
    }

    // ... (中略・既存のロジックはそのまま)

    /// <summary>ステータス Label を更新しつつ SR にライブ通知(Say 契約: 空は視覚クリアのみ・発声なし)。
    /// P7/P8 申し送り: G-2 で「次を検索」後にダイアログを Hide するため、Hidden な _dialog を
    /// 経由せず MainForm 共有 Announcer 直接発声で経路を整理。実挙動は不変(元々同じ Announcer)。</summary>
    internal void Announce(string message) => _announcer.Say(message);
}
```

### Step 3.2: MainForm の Announcer 生成順と SearchController 引数を修正

`src/yEdit.App/MainForm.cs` の ctor を編集: `_announcer` の生成を `_search` より前へ移動し、
`_search` に `_announcer` を渡す:

```csharp
        _docs = new DocumentManager(CreateEditor);
        // ... (event 購読はそのまま)

        // 設定は OpenSettings で参照が差し替わるため Func で都度解決させる。
        _file = new FileController(_docs, this, () => _settings,
            SaveSettingsSafe, RebuildRecentMenu, () => { UpdateTitle(); UpdateStatus(); },
            AutoEnterCsvMode);
        _announcer = AnnouncerFactory.Create(_announceLabel);   // ← 移動: _search より前
        _search = new SearchController(_docs, this, _announcer); // ← 引数追加
        _grep = new GrepController(_docs, this,
            hit => OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength));
        _backup = new BackupCoordinator(_docs, _settings.BackupEnabled, _settings.BackupIntervalSeconds);
        _csv = new CsvController(_docs, _announcer);
```

`_announcer` のフィールド初期化コメント(現状 `= null!;`)と生成タイミングの整合を確認。
`_docs.KeyBasedSwitch += (_, doc) => _announcer.Say(doc.State.DisplayName);` はラムダで
遅延評価なので、生成順序変更の影響なし。

### Step 3.3: ビルドとテスト

Run:
```
dotnet build -c Release
dotnet test
```

Expected: 0 warning、833 tests 全緑。

### Step 3.4: 動作スモーク

以下を手動確認できるならしておく(自動テストなし):
1. yEdit 起動 → Ctrl+F → 適当な文字列で「次を検索」を端まで押す
2. 端到達で「これ以上見つかりません」が発声される(視覚的にも底部 `_announceLabel` に表示)

`_dialog` が Hide されているので `RaiseNotification` 経由でも音は出るが、
今回の変更で経路が Hidden な UI を経由しなくなる。

### Step 3.5: コミット

```bash
git add src/yEdit.App/SearchController.cs src/yEdit.App/MainForm.cs
git commit -m "$(cat <<'EOF'
P7/P8 申し送り Task 3: SearchController の Announce を共有 Announcer に直結

G-2「検索モードで次を検索後に自動 Hide」で SearchController.Announce が Hidden な
_dialog.RaiseNotification を経由していた経路を、MainForm 共有 Announcer 直結に整理。
IAnnouncer を ctor 注入し、MainForm では _announcer の生成を _search より前へ
移動して依存順を成立させる。実挙動は不変(_dialog 内部 Announcer と MainForm
Announcer は同一 Label に束縛される同型インスタンス)。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Minor-4 VisualSegments.FindContaining 抽出(TDD)

**目的**: 視覚セグメント探索を `yEdit.Core.Layout.VisualSegments.FindContaining` に集約し、
`EditorControl.TryFindVisualSegmentCore` / `VerticalNavigation.FindSegIndex` /
`NavigationCommands.MoveHomeSmart(wrap overload)` の 3 経路から呼び出す。

**Files:**
- Create: `src/yEdit.Core/Layout/VisualSegments.cs`
- Create: `tests/yEdit.Core.Tests/Layout/VisualSegmentsTests.cs`
- Modify: `src/yEdit.Core/Editing/VerticalNavigation.cs:91-100`
- Modify: `src/yEdit.Core/Editing/NavigationCommands.cs:113-119`
- Modify: `src/yEdit.Editor/EditorControl.cs:3217-3232`

### Step 4.1: 失敗するテストを書く

`tests/yEdit.Core.Tests/Layout/VisualSegmentsTests.cs` を新規作成:

```csharp
using yEdit.Core.Layout;
using Xunit;

namespace yEdit.Core.Tests.Layout;

public class VisualSegmentsTests
{
    [Fact]
    public void SingleSeg_Interior_ReturnsIndex0()
    {
        var segs = new[] { new WrapSegment(0, 10) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 5);
        Assert.Equal(0, idx);
        Assert.Equal(new WrapSegment(0, 10), seg);
    }

    [Fact]
    public void SingleSeg_AtEnd_ReturnsLastSeg()
    {
        var segs = new[] { new WrapSegment(0, 10) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 10);
        Assert.Equal(0, idx);
        Assert.Equal(new WrapSegment(0, 10), seg);
    }

    [Fact]
    public void TwoSegs_LastCharOfFirst_ReturnsFirst()
    {
        var segs = new[] { new WrapSegment(0, 5), new WrapSegment(5, 5) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 4);
        Assert.Equal(0, idx);
        Assert.Equal(new WrapSegment(0, 5), seg);
    }

    [Fact]
    public void TwoSegs_BoundaryOffset_ReturnsSecond()
    {
        // offset==5 は前 seg の segEnd と一致 → 次 seg 先頭扱い
        var segs = new[] { new WrapSegment(0, 5), new WrapSegment(5, 5) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 5);
        Assert.Equal(1, idx);
        Assert.Equal(new WrapSegment(5, 5), seg);
    }

    [Fact]
    public void TwoSegs_InteriorOfSecond_ReturnsSecond()
    {
        var segs = new[] { new WrapSegment(0, 5), new WrapSegment(5, 5) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 8);
        Assert.Equal(1, idx);
        Assert.Equal(new WrapSegment(5, 5), seg);
    }

    [Fact]
    public void TwoSegs_AtLineEnd_ReturnsLast()
    {
        // 行末位置(= 最終 segEnd)は最終 seg 扱い
        var segs = new[] { new WrapSegment(0, 5), new WrapSegment(5, 5) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 10);
        Assert.Equal(1, idx);
        Assert.Equal(new WrapSegment(5, 5), seg);
    }

    [Fact]
    public void EmptySegs_Throws()
    {
        var segs = System.Array.Empty<WrapSegment>();
        Assert.Throws<System.ArgumentException>(() => VisualSegments.FindContaining(segs, 0));
    }
}
```

### Step 4.2: 実行して失敗を確認

Run:
```
dotnet test tests/yEdit.Core.Tests --filter FullyQualifiedName~VisualSegmentsTests
```

Expected: FAIL「型 or メソッドが見つからない」。

### Step 4.3: 最小実装

`src/yEdit.Core/Layout/VisualSegments.cs` を新規作成:

```csharp
namespace yEdit.Core.Layout;

/// <summary>視覚セグメント列(<see cref="LineLayout.Wrap"/> 出力)への共通照会を集約する。
/// EditorControl の TryFindVisualSegmentCore / VerticalNavigation.FindSegIndex /
/// NavigationCommands.MoveHomeSmart(wrap overload) から共有する。</summary>
public static class VisualSegments
{
    /// <summary>offsetInLine を含む視覚セグメントの (index, segment) を返す。</summary>
    /// <remarks>行末位置(=最終 segEnd)は最終セグメント扱い。
    /// 空 segs は非対応=<see cref="LineLayout.Wrap"/> は空入力でも [(0,0)] を返す契約なので
    /// 呼び出し側で空 segs を渡さないことを保証する。</remarks>
    public static (int Index, WrapSegment Segment) FindContaining(
        IReadOnlyList<WrapSegment> segs, int offsetInLine)
    {
        for (int i = 0; i < segs.Count; i++)
        {
            int segEnd = segs[i].OffsetInLine + segs[i].Length;
            if (offsetInLine < segEnd || i == segs.Count - 1) return (i, segs[i]);
        }
        // segs.Count == 0 の場合のみ到達=呼び出し側契約違反。
        throw new System.ArgumentException("segs must not be empty", nameof(segs));
    }
}
```

### Step 4.4: テスト実行

Run:
```
dotnet test tests/yEdit.Core.Tests --filter FullyQualifiedName~VisualSegmentsTests
```

Expected: 7 tests all pass.

### Step 4.5: VerticalNavigation.FindSegIndex を差し替え

`src/yEdit.Core/Editing/VerticalNavigation.cs:91-100` を編集:

```csharp
    /// <summary>caretInLine を含む視覚セグメントの index を返す。行末位置(=最終 segEnd)は最終セグメント扱い。</summary>
    private static int FindSegIndex(IReadOnlyList<WrapSegment> segs, int caretInLine)
        => VisualSegments.FindContaining(segs, caretInLine).Index;
```

### Step 4.6: NavigationCommands.MoveHomeSmart(wrap overload) を差し替え

`src/yEdit.Core/Editing/NavigationCommands.cs:113-119` を編集:

```csharp
        var segs = LineLayout.Wrap(lineText.AsSpan(), maxWidthPx, metrics);
        int caretInLine = caret - lineStart;

        // キャレットが属する視覚セグメントを VisualSegments 共通ヘルパで解決
        var (segIdx, seg) = VisualSegments.FindContaining(segs, caretInLine);
        int visualStart = lineStart + seg.OffsetInLine;
        int visualEnd = lineStart + seg.OffsetInLine + seg.Length;
```

`segs[segIdx]` の局所変数 `seg` を再利用する形になるので、その下の行の
`var seg = segs[segIdx];` は削除する(重複宣言エラー回避)。

### Step 4.7: EditorControl.TryFindVisualSegmentCore を差し替え

`src/yEdit.Editor/EditorControl.cs:3217-3232` を編集:

```csharp
    /// <summary>UI スレッド上での視覚セグメント検索本体(<see cref="TryFindVisualSegment"/> から Invoke マーシャリング後)。</summary>
    private yEdit.Core.Layout.WrapSegment? TryFindVisualSegmentCore(TextSnapshot snap, int line, int offsetInLine, int wrap)
    {
        var metrics = _metrics;
        int logicalStart = snap.GetLineStart(line);
        int logicalEnd = snap.GetLineEnd(line, includeBreak: false);
        if (logicalStart == logicalEnd) return null;
        string lineText = snap.GetText(logicalStart, logicalEnd - logicalStart);
        int maxWidthPx = wrap * metrics.MeasureRun("0".AsSpan());
        var segs = yEdit.Core.Layout.LineLayout.Wrap(lineText.AsSpan(), maxWidthPx, metrics);
        return yEdit.Core.Layout.VisualSegments.FindContaining(segs, offsetInLine).Segment;
    }
```

### Step 4.8: 全体テスト

Run:
```
dotnet build -c Release
dotnet test
```

Expected: 0 warning、Core 607+7 = 614 + Editor 226 = 840 tests 全緑。

### Step 4.9: コミット

```bash
git add src/yEdit.Core/Layout/VisualSegments.cs tests/yEdit.Core.Tests/Layout/VisualSegmentsTests.cs src/yEdit.Core/Editing/VerticalNavigation.cs src/yEdit.Core/Editing/NavigationCommands.cs src/yEdit.Editor/EditorControl.cs
git commit -m "$(cat <<'EOF'
P7/P8 申し送り Task 4: VisualSegments.FindContaining 抽出

視覚セグメント探索の重複ロジックを yEdit.Core.Layout.VisualSegments に集約し、
EditorControl.TryFindVisualSegmentCore / VerticalNavigation.FindSegIndex /
NavigationCommands.MoveHomeSmart(wrap overload) の 3 経路から呼び出す。契約凍結
テスト 7 件を TDD で先出しし、既存の視覚行挙動の回帰を防ぐ。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Minor-5 論理行 segs キャッシュ

**目的**: UIA `LineStartOf`/`LineEndNoBreakOf`/`LineEnd` が同一 (snap, logicalLine) を連続照会する
ホットパスで、`LineLayout.Wrap` の `string`+`List<WrapSegment>` 再アロケーションを 1 エントリキャッシュで削減。

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`(field 追加 + TryFindVisualSegmentCore 修正 + 無効化 4 箇所 + テストフック)
- Create: `tests/yEdit.Editor.Tests/EditorControlCacheTests.cs`

### Step 5.1: キャッシュフィールドとテストフックを追加

`src/yEdit.Editor/EditorControl.cs` の `_bufferSnapshot` フィールド近傍に追加:

```csharp
    // P8 Minor-5: SR の Line 単位連続読み(LineStartOf/LineEndNoBreakOf/LineEnd)で
    // 同一 (snap, logicalLine, wrap) が繰り返されるため単一エントリキャッシュ。
    // UI スレッド上でのみ更新される(TryFindVisualSegmentCore は Invoke マーシャリング後)。
    // 無効化ポイント: AfterEdit() / SetSource() / ApplyAppearance() / WrapColumns setter。
    private (yEdit.Core.Buffers.TextSnapshot Snap, int Line, int Wrap,
             System.Collections.Generic.IReadOnlyList<yEdit.Core.Layout.WrapSegment> Segs,
             string LineText)? _lastLineSegs;

    // Editor.Tests から観測するためのヒットカウンタ(internal・テスト以外の呼び出しは想定しない)。
    internal long TestHook_LastLineSegsHitCount { get; private set; }
    internal long TestHook_LastLineSegsMissCount { get; private set; }
    internal void TestHook_ResetLastLineSegsCounters()
    {
        TestHook_LastLineSegsHitCount = 0;
        TestHook_LastLineSegsMissCount = 0;
    }
```

InternalsVisibleTo が既に `yEdit.Editor.Tests` に効いていることを確認(既存 P5/P8 で使用済み)。

### Step 5.2: TryFindVisualSegmentCore にキャッシュ機構を組み込む

`src/yEdit.Editor/EditorControl.cs:3217-3232` を修正:

```csharp
    /// <summary>UI スレッド上での視覚セグメント検索本体(<see cref="TryFindVisualSegment"/> から Invoke マーシャリング後)。</summary>
    private yEdit.Core.Layout.WrapSegment? TryFindVisualSegmentCore(TextSnapshot snap, int line, int offsetInLine, int wrap)
    {
        System.Collections.Generic.IReadOnlyList<yEdit.Core.Layout.WrapSegment> segs;

        if (_lastLineSegs is { } c &&
            ReferenceEquals(c.Snap, snap) && c.Line == line && c.Wrap == wrap)
        {
            segs = c.Segs;
            TestHook_LastLineSegsHitCount++;
        }
        else
        {
            var metrics = _metrics;
            int logicalStart = snap.GetLineStart(line);
            int logicalEnd = snap.GetLineEnd(line, includeBreak: false);
            if (logicalStart == logicalEnd) return null;
            string lineText = snap.GetText(logicalStart, logicalEnd - logicalStart);
            int maxWidthPx = wrap * metrics.MeasureRun("0".AsSpan());
            segs = yEdit.Core.Layout.LineLayout.Wrap(lineText.AsSpan(), maxWidthPx, metrics);
            _lastLineSegs = (snap, line, wrap, segs, lineText);
            TestHook_LastLineSegsMissCount++;
        }

        return yEdit.Core.Layout.VisualSegments.FindContaining(segs, offsetInLine).Segment;
    }
```

### Step 5.3: 無効化ポイントを追加(4 箇所)

以下 4 箇所の末尾に `_lastLineSegs = null;` を追加する。

**(a) `AfterEdit()` メソッド末尾**:
grep で `private void AfterEdit()` を探し、既存の末尾に:
```csharp
        _lastLineSegs = null; // P8 Minor-5: 編集で snapshot が更新されたのでキャッシュ破棄
```

**(b) `SetSource(...)` メソッド末尾**:
grep で `public void SetSource(` を探し、末尾に:
```csharp
        _lastLineSegs = null; // P8 Minor-5: 全差替でキャッシュ破棄
```

**(c) `ApplyAppearance(AppSettings settings)` メソッド末尾**:
`src/yEdit.Editor/EditorControl.cs:2931` 付近。既存の実装本体末尾(Invalidate 等の直後)に:
```csharp
        _lastLineSegs = null; // P8 Minor-5: metrics/wrap 変化でキャッシュ破棄
```
※ 既に同メソッド内で `_wrapColumns` を書き換えているので、`WrapColumns` setter の無効化と重複しないよう
`ApplyAppearance` 内では setter を経由せず `_wrapColumns` を直接代入している場合はこの位置のみで OK。
コードを読んで確認する。

**(d) `WrapColumns` setter 内**:
`src/yEdit.Editor/EditorControl.cs:615-625` 付近。既存の setter で `_wrapColumns = clamped;` の
直後に:
```csharp
                _wrapColumns = clamped;
                _lastLineSegs = null; // P8 Minor-5: wrap 値変化でキャッシュ破棄
```

### Step 5.4: テストを書く(キャッシュ挙動観測)

`tests/yEdit.Editor.Tests/EditorControlCacheTests.cs` を新規作成:

```csharp
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class EditorControlCacheTests
{
    [StaFact]
    public void LastLineSegs_HitsAcrossThreeUiaLineCalls()
    {
        using var ec = new EditorControl();
        ec.CreateControl();  // Handle 生成
        ec.WrapColumns = 20;
        ec.SetSource(new TextBufferBuilder().Append("Hello, world!").Build());

        ec.TestHook_ResetLastLineSegsCounters();

        // UIA v2 の TextUnit.Line 単位読みでは同 offset に対して 3 メソッドが連続呼ばれる。
        var host = (IUiaTextHost)ec;
        _ = host.LineStartOf(5);
        _ = host.LineEndNoBreakOf(5);
        _ = host.LineEnd(5);

        // 3 コール中 miss=1, hit=2 が期待挙動
        Assert.Equal(1, ec.TestHook_LastLineSegsMissCount);
        Assert.Equal(2, ec.TestHook_LastLineSegsHitCount);
    }

    [StaFact]
    public void LastLineSegs_InvalidatesOnEdit()
    {
        using var ec = new EditorControl();
        ec.CreateControl();
        ec.WrapColumns = 20;
        ec.SetSource(new TextBufferBuilder().Append("Hello, world!").Build());

        var host = (IUiaTextHost)ec;
        _ = host.LineStartOf(5); // miss(初回) → キャッシュ充填
        _ = host.LineStartOf(5); // hit
        ec.TestHook_ResetLastLineSegsCounters();

        // 編集でキャッシュが破棄されるべき
        ec.ReplaceCharRange(0, 0, "X");

        _ = host.LineStartOf(5); // 編集後の最初のコールは miss になる
        Assert.Equal(1, ec.TestHook_LastLineSegsMissCount);
        Assert.Equal(0, ec.TestHook_LastLineSegsHitCount);
    }

    [StaFact]
    public void LastLineSegs_InvalidatesOnWrapChange()
    {
        using var ec = new EditorControl();
        ec.CreateControl();
        ec.WrapColumns = 20;
        ec.SetSource(new TextBufferBuilder().Append("Hello, world!").Build());

        var host = (IUiaTextHost)ec;
        _ = host.LineStartOf(5); // miss(初回)
        _ = host.LineStartOf(5); // hit
        ec.TestHook_ResetLastLineSegsCounters();

        ec.WrapColumns = 30;   // wrap 値変更でキャッシュ破棄

        _ = host.LineStartOf(5); // 再構築が要る=miss
        Assert.Equal(1, ec.TestHook_LastLineSegsMissCount);
        Assert.Equal(0, ec.TestHook_LastLineSegsHitCount);
    }
}
```

※ `StaFact` / `TextBufferBuilder` 等のクラス名は既存の Editor.Tests から実際に使われているものを
確認して合わせる。既存テスト `tests/yEdit.Editor.Tests/` の他ファイルを 1 つ開いて import と Fact 属性を確認してから書く。

### Step 5.5: テスト実行

Run:
```
dotnet test tests/yEdit.Editor.Tests --filter FullyQualifiedName~EditorControlCacheTests
```

Expected: 3 tests all pass.

失敗する場合の原因候補:
- SetSource 前に WrapColumns を設定するとキャッシュ判定が別トリガーで動く → SetSource 後に WrapColumns 設定に変更
- CreateControl のタイミング → Editor.Tests の他テストの初期化順を参考にする
- 空行(offset=0 で LineStartOf=0)判定に引っかかる → offset を確実に本文中央(offset=5)にする

### Step 5.6: 全体テスト

Run:
```
dotnet build -c Release
dotnet test
```

Expected: 0 warning、Core 614 + Editor 226+3 = 843 tests 全緑。

### Step 5.7: コミット

```bash
git add src/yEdit.Editor/EditorControl.cs tests/yEdit.Editor.Tests/EditorControlCacheTests.cs
git commit -m "$(cat <<'EOF'
P7/P8 申し送り Task 5: EditorControl に論理行 segs 単一エントリキャッシュ

UIA v2 の LineStartOf/LineEndNoBreakOf/LineEnd が同一 (snap, logicalLine, wrap) を
連続照会するホットパスで、LineLayout.Wrap の string+List<WrapSegment> 再アロケーションを
削減する単一エントリ「last logical line」キャッシュを追加。無効化は AfterEdit /
SetSource / ApplyAppearance / WrapColumns setter の 4 箇所。テストフック
(TestHook_LastLineSegsHit/MissCount) で hit=2/miss=1 の 3 連続コールを観測。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## 完了確認

**すべての Task 完了後**:

Run:
```
dotnet build -c Release
dotnet test
git log --oneline main..HEAD
```

Expected:
- 0 warning
- 全 tests 緑(概算 843 前後・純増のみ・退行なし)
- コミット 6 件(設計書 1 + Task 1〜5 の 5 コミット)が feature/p7p8-followups に積まれている

**別エージェント最終レビュー**: `superpowers:requesting-code-review` を発火し、
Critical / Important / Minor の判定を仰ぐ。指摘反映後に main へ `--no-ff` マージ。

**push はしない**(既存運用に準拠)。ブランチは merged safe delete。

---

## スコープ外(明示的申し送り)

- **App 層自動テスト基盤**(`tests/yEdit.App.Tests` 新設): 別セッションで単独対応。SearchController は
  EditorControl / DocumentManager 依存が強く、モック設計の検討工数が本計画の他項目と釣り合わない。
- **`FindReplaceDialog.RaiseNotification` の廃止**: GrepController がまだ使用しているため、
  今回は SearchController 経路の整理のみ。廃止は別議論。
- **Minor-5 の LRU 化**: 単一エントリで実用上十分と判定。隣接行 Line 単位読みが観測されたら再検討。
