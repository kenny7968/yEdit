# テスト戦略 Phase 2 Stage 6: CsvController シーム導入+テスト 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** CsvController の唯一残る Form 境界(セル指定ダイアログ `CsvGoToCellDialog`)を `ICellPicker` で注入化し、App.Tests から CSV モードの状態機械(モード進入/退出・移動・端メッセージ・GoToCell の 3 分岐・BeginEdit の起動配線・クランプ・parse-error 後始末)を固定する。挙動不変。

**Architecture:** ストラングラー方式のシーム導入。`ICellPicker.Pick(owner, currentRow1, currentCol1) → CellPickResult`(Canceled/InvalidFormat/Ok の 3 相 record・Stage 5 の `RestoreOutcome` と同型)を追加し、`CsvGoToCellDialog` は薄い Adapter `WinFormsCellPicker` の中でのみ生成される。`CsvController` は `Form.FindForm()` 経由の owner 取得を含めて Form 境界への直参照ゼロに落ちる。テストは実 DocumentManager+実 EditorControl(=Csv パースの真実源)+Fake 境界(FakeAnnouncer/FakeCellPicker)で駆動する。

**Tech Stack:** .NET 9 / WinForms / xUnit v2(STA ヘルパ=`Sta.Run`・可視 HostForm パターン=`TestHost.CreateWithDocs`)

- 日付: 2026-07-14
- 上位文書: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` §2.2・§3・§4 Stage 6・§5(SR 経路への影響あり=マージ前 L5 実機スポット必須)
- ベースライン: main `aef52b3`(Stage 5 マージ済み+ハッシュ追記済み)・テスト数 891(Core 573+Editor 218+App 100)

---

## 0. 設計精密化(上位文書 §2.2 からの追記)

上位文書 §2.2 は `ICellPicker` を「`Func<(int row,int col), (int,int)?>` 相当」とだけ書いている。現行 `CsvController.GoToCell` は **3 分岐**(Cancel=無音・InvalidFormat="書式が不正です"・Ok+範囲外="範囲外です")を通知文言で区別しており、単純な `(int,int)?` では Cancel と InvalidFormat が畳まれて挙動退行する。

以下 3 点で精密化する。

1. **`CellPickResult` を 3 相 record にする**(Stage 5 の `RestoreOutcome` と同型)。Kind=Canceled/InvalidFormat/Ok。Cancel と Invalid を混同させない。
2. **Ok の 1 始まり座標を record に含める**(呼び出し側の変換責務なし=`CsvGoToCellDialog.TryGetCell` の 1 始まり出力をそのまま運ぶ)。
3. **CsvController の ctor は `ICellPicker cellPicker` を追加**(ファクトリ化しない。`Pick` は ShowDialog 同期で毎回生成/破棄する短命リソースなので、`Func<ICellPicker>` の Lazy 生成は不要)。**名前付き引数化を推奨**(Stage 4 の教訓=同型 delegate の位置取り違え検出不能への対処と同じ配慮)。

`CsvGoToCellDialog` 本体の UI(TableLayoutPanel/AccessibleName/IME 抑止)は無変更。Adapter が `TryGetCell` の bool を `CellPickResult.InvalidFormat` にマップするだけ。

---

## 規約(全 Task 共通)

- ブランチ: `feature/test-strategy-phase2-stage6`(同一ディレクトリのフィーチャーブランチ→main へ no-ff マージ=いつもの運用)
- コミットメッセージは日本語。末尾に `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` を付ける
- 各 Task 末尾で `dotnet build yEdit.sln -c Release -warnaserror` が 0 警告であること
- git status に見えている untracked の `installer/`・`publish/` はこの作業と無関係。**絶対にコミットに含めない**(`git add` はパス指定で行う)
- 特徴付けテストが赤になった場合: 原則テスト側の期待を現行挙動へ合わせる。**ただし CSV セル位置(DocumentState.CsvRow/Col)の書き戻し・SR 誤読み抑止フラグ(`RaiseUiaSelectionEvents`)・ReadOnly トグルの順序・通知文言の赤は SR 退行リスク=実装バグの可能性があるため、修正せずユーザーへ報告する**(Stage 4/5 の同型規約の CSV 版)

---

### Task 1: ブランチ作成

**Step 1: main から作業ブランチを切る**

Run:
```powershell
git switch -c feature/test-strategy-phase2-stage6 main
```
Expected: `Switched to a new branch 'feature/test-strategy-phase2-stage6'`

---

### Task 2: シーム定義(未配線・コンパイルのみ)

**Files:**
- Create: `src/yEdit.App/Abstractions/ICellPicker.cs`

**Step 1: ICellPicker+CellPickResult を定義**

Create `src/yEdit.App/Abstractions/ICellPicker.cs`:

```csharp
namespace yEdit.App;

/// <summary>セル指定ダイアログの結果 kind(Phase 2 Stage 6・上位文書 §2.2)。</summary>
public enum CellPickKind
{
    /// <summary>ユーザーが Cancel/Esc/×ボタンで閉じた(無音扱い)。</summary>
    Canceled,
    /// <summary>OK を押したが入力書式が不正("行,列" として解釈不能)。呼び出し側が「書式が不正です」を通知する。</summary>
    InvalidFormat,
    /// <summary>OK+書式 OK。<see cref="Row1"/>/<see cref="Col1"/> は 1 始まり。範囲外判定は呼び出し側(CsvController)が行う。</summary>
    Ok,
}

/// <summary>
/// セル指定ダイアログの結果 record。Kind ごとに Row1/Col1 の意味が変わる(Ok 以外は既定 0)。
/// Stage 5 の <see cref="RestoreOutcome"/> と同型(sentinel readonly+Ok ファクトリ)。
/// </summary>
public sealed record CellPickResult(CellPickKind Kind, int Row1, int Col1)
{
    public static readonly CellPickResult Canceled = new(CellPickKind.Canceled, 0, 0);
    public static readonly CellPickResult InvalidFormat = new(CellPickKind.InvalidFormat, 0, 0);
    public static CellPickResult Ok(int row1, int col1) => new(CellPickKind.Ok, row1, col1);
}

/// <summary>
/// CSV モードのセル指定移動(G キー)ダイアログの Controller 向け表面。
/// 実装は既存 <c>CsvGoToCellDialog</c> をラップする Adapter(<c>WinFormsCellPicker</c>)。
/// 現在セルは 1 始まりで渡す(現行ダイアログの初期値表示と同じ座標系)。
/// </summary>
public interface ICellPicker
{
    CellPickResult Pick(IWin32Window owner, int currentRow1, int currentCol1);
}
```

**Step 2: ビルド確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: 0 警告(新規ファイルは未参照でもコンパイルされる)

**Step 3: Commit**

```powershell
git add src/yEdit.App/Abstractions/ICellPicker.cs
git commit -m "feat: ICellPicker/CellPickResult シームを追加(Stage 6・未配線)"
```

---

### Task 3: WinFormsCellPicker Adapter+CsvController 注入化+MainForm 配線(1 コミットにまとめる)

置換は**機械的**に行う。条件分岐・文言・順序を一切変えない(diff レビューで確認できる粒度)。

**Files:**
- Create: `src/yEdit.App/WinFormsCellPicker.cs`
- Modify: `src/yEdit.App/CsvController.cs`(ctor+GoToCell のみ)
- Modify: `src/yEdit.App/MainForm.cs:66`(ICellPicker を渡す)

**Step 1: WinFormsCellPicker を追加**

Create `src/yEdit.App/WinFormsCellPicker.cs`:

```csharp
namespace yEdit.App;

/// <summary>
/// セル指定ダイアログの WinForms Adapter(Phase 2 Stage 6・上位文書 §2.2)。
/// <see cref="CsvGoToCellDialog"/> を ShowDialog し、DialogResult+TryGetCell の 2 段判定を
/// App 層公開の <see cref="CellPickResult"/> にマップする(Cancel/InvalidFormat/Ok の 3 相を保存)。
/// </summary>
public sealed class WinFormsCellPicker : ICellPicker
{
    public CellPickResult Pick(IWin32Window owner, int currentRow1, int currentCol1)
    {
        using var dlg = new CsvGoToCellDialog(currentRow1, currentCol1);
        if (dlg.ShowDialog(owner) != DialogResult.OK) return CellPickResult.Canceled;
        if (!dlg.TryGetCell(out int r1, out int c1)) return CellPickResult.InvalidFormat;
        return CellPickResult.Ok(r1, c1);
    }
}
```

**Step 2: CsvController を注入化(ctor+GoToCell のみ差し替え)**

Modify `src/yEdit.App/CsvController.cs`:

- **フィールド追加**(既存フィールド群の直後):
  ```csharp
  private readonly ICellPicker _cellPicker;
  ```
- **ctor 差し替え**:
  ```csharp
  public CsvController(DocumentManager docs, IAnnouncer announcer, ICellPicker cellPicker)
  {
      _docs = docs;
      _announcer = announcer;
      _cellPicker = cellPicker;
  }
  ```
- **GoToCell 差し替え**(現行 `L124-134` の 3 分岐を保存):
  ```csharp
  /// <summary>セル指定移動(G)。「行,列」入力→範囲検証→移動。ダイアログは ICellPicker 経由(Stage 6)。</summary>
  public void GoToCell()
  {
      if (!TryContext(out var ed, out var csv, out var row, out var col)) return;
      var result = _cellPicker.Pick(ed.FindForm()!, row + 1, col + 1);
      switch (result.Kind)
      {
          case CellPickKind.Canceled:
              return; // 無音(現行挙動)
          case CellPickKind.InvalidFormat:
              _announcer.Say(CsvAnnounceFormatter.BadCellFormat);
              return;
          case CellPickKind.Ok:
              var t = csv.GoTo(result.Row1 - 1, result.Col1 - 1);
              if (t is null) { _announcer.Say(CsvAnnounceFormatter.OutOfRange); return; }
              ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
              return;
      }
  }
  ```
  補足: `ed.FindForm()!` は現行 `dlg.ShowDialog(ed.FindForm())` と 1:1(TestHost の可視 Form 配下では常に非 null。null 許容シグネチャ整合のため `!` を付ける)。

**Step 3: MainForm の生成箇所(`MainForm.cs:66`)を注入化**

Modify `src/yEdit.App/MainForm.cs:66`:

```csharp
// 変更前:
_csv = new CsvController(_docs, _announcer);
// 変更後(名前付き引数化=§0 精密化 3):
_csv = new CsvController(docs: _docs, announcer: _announcer, cellPicker: new WinFormsCellPicker());
```

**Step 4: シームの完全性を grep で確認**

Run:
```powershell
git grep -n "CsvGoToCellDialog" -- "src/yEdit.App/CsvController.cs"
```
Expected: ヒットなし(exit code 1)

Run:
```powershell
git grep -n "new CsvGoToCellDialog" -- "src/yEdit.App"
```
Expected: `src/yEdit.App/WinFormsCellPicker.cs` の 1 行のみ(MainForm 直 new なし)

Run:
```powershell
git grep -n "\.ShowDialog" -- "src/yEdit.App/CsvController.cs"
```
Expected: ヒットなし(exit code 1)

**Step 5: ビルド+既存全テストで挙動不変を確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.Core.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.Editor.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Core 573+Editor 218+App 100=891 全緑

**Step 6: Commit**

```powershell
git add src/yEdit.App/WinFormsCellPicker.cs src/yEdit.App/CsvController.cs src/yEdit.App/MainForm.cs
git commit -m "refactor: CsvController の GoToCell ダイアログを ICellPicker で注入化(挙動不変)"
```

---

### Task 4: FakeCellPicker を追加

**Files:**
- Create: `tests/yEdit.App.Tests/Fakes/FakeCellPicker.cs`

**Step 1: FakeCellPicker を作成**

Create `tests/yEdit.App.Tests/Fakes/FakeCellPicker.cs`:

```csharp
namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="ICellPicker"/> のテスト用フェイク。次回の Pick で返す結果を <see cref="NextResult"/> に
/// 事前登録する(既定は Canceled)。Coordinator が渡した現在セル(1 始まり)は
/// <see cref="LastCurrentRow1"/>/<see cref="LastCurrentCol1"/> で検証できる。
/// </summary>
public sealed class FakeCellPicker : ICellPicker
{
    public CellPickResult NextResult { get; set; } = CellPickResult.Canceled;
    public int PickCount;
    public int LastCurrentRow1;
    public int LastCurrentCol1;

    public CellPickResult Pick(IWin32Window owner, int currentRow1, int currentCol1)
    {
        PickCount++;
        LastCurrentRow1 = currentRow1;
        LastCurrentCol1 = currentCol1;
        return NextResult;
    }
}
```

**Step 2: ビルド確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: 0 警告

**Step 3: Commit**

```powershell
git add tests/yEdit.App.Tests/Fakes/FakeCellPicker.cs
git commit -m "test: FakeCellPicker を追加"
```

---

### Task 5: CsvControllerTests 第 1 弾(ctor/TryEnterMode/ExitMode/ToggleMode/Move/端メッセージ=14 件)

**Files:**
- Create: `tests/yEdit.App.Tests/CsvControllerTests.cs`

**Step 1: テストハーネス+第 1 弾を書く**

Create `tests/yEdit.App.Tests/CsvControllerTests.cs`:

```csharp
using yEdit.App.Tests.Fakes;
using yEdit.Core.Csv;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 6: CsvController の配線・状態機械・端メッセージ・GoToCell の 3 分岐・
/// BeginEdit の起動配線・parse-error 後始末・DocumentState 書き戻しのテスト。
/// 実 DocumentManager+実 EditorControl を STA 上で使い、Form 境界(FakeCellPicker)と
/// 通知(FakeAnnouncer)だけを偽物にする。CsvDocument の照合正しさ(Core 検証済み)は
/// 再検証しない(責務=配線・遷移・SR 誤読み抑止フラグ・通知文言・DocumentState 書き戻し)。
/// </summary>
public class CsvControllerTests
{
    /// <summary>CsvController を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeAnnouncer Announcer { get; } = new();
        public FakeCellPicker Picker { get; } = new();
        public CsvController Csv { get; }

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Csv = new CsvController(docs: Docs, announcer: Announcer, cellPicker: Picker);
        }

        /// <summary>本文に CSV テキストを載せて Active に返す(EditorControl.Text は新バッファ=Modified=false)。</summary>
        public Document NewCsvDoc(string csv)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = csv;
            return doc;
        }

        public void Dispose()
        {
            Csv.AbortEdit(); // 進行中の F2 編集を落とす(冪等)
            Form.Dispose();
        }
    }

    // 3×3 の素朴 CSV(セル値=r 行 c 列を「r,c」で表示)。改行 LF。
    private const string Grid3x3 =
        "a1,a2,a3\n" +
        "b1,b2,b3\n" +
        "c1,c2,c3";

    // ===== ctor(対応固定=Picker は ctor で呼ばれない) =====

    [Fact]
    public void Ctor_DoesNotInvokePicker_NorAnnouncer() => Sta.Run(() =>
    {
        using var host = new Host();
        Assert.Equal(0, host.Picker.PickCount);
        Assert.Empty(host.Announcer.Said);
    });

    // ===== TryEnterMode(5 分岐) =====

    [Fact]
    public void TryEnterMode_AlreadyInMode_ReturnsFalse_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        Assert.True(host.Csv.TryEnterMode(doc));
        int saidBefore = host.Announcer.Said.Count;

        Assert.False(host.Csv.TryEnterMode(doc)); // 2 回目は false・追加通知なし
        Assert.Equal(saidBefore, host.Announcer.Said.Count);
    });

    [Fact]
    public void TryEnterMode_UnparseableCsv_AnnouncesParseError_DoesNotEnter() => Sta.Run(() =>
    {
        using var host = new Host();
        // 引用符未終端 → CsvDocument.Ok=false
        var doc = host.NewCsvDoc("a1,\"b1\na2,b2");

        Assert.False(host.Csv.TryEnterMode(doc));
        Assert.False(doc.State.CsvMode);
        Assert.False(doc.Editor.ReadOnly);
        Assert.Contains(CsvAnnounceFormatter.ParseError, host.Announcer.Said);
    });

    [Fact]
    public void TryEnterMode_EmptyCsv_EntersMode_AnnouncesModeOnOnly() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(""); // Rows.Count=0

        Assert.True(host.Csv.TryEnterMode(doc));
        Assert.True(doc.State.CsvMode);
        Assert.True(doc.Editor.ReadOnly);
        Assert.False(doc.Editor.RaiseUiaSelectionEvents); // SR 誤読み抑止
        // データ無しは ModeOn のみ(セル情報なし)
        Assert.Equal(CsvAnnounceFormatter.ModeOn, host.Announcer.Said[^1]);
    });

    [Fact]
    public void TryEnterMode_ParseableCsv_EntersMode_ReadOnlyAndUiaOff_AnnouncesModeOnAndCell() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);

        Assert.True(host.Csv.TryEnterMode(doc));
        Assert.True(doc.State.CsvMode);
        Assert.True(doc.Editor.ReadOnly);
        Assert.False(doc.Editor.RaiseUiaSelectionEvents);
        // ModeOn + Cell が結合された 1 通知(現行実装=1 回 Say)
        Assert.Contains(host.Announcer.Said, s => s.StartsWith(CsvAnnounceFormatter.ModeOn) && s.Contains("行") && s.Contains("列"));
    });

    [Fact]
    public void TryEnterMode_InitialCell_IsDerivedFromCaretPosition() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        // "a1,a2,a3\nb1,b2,b3\n..." の 2 行目 "b2" 相当の位置(0 始まり=9+3=12 前後)にキャレットを寄せる。
        // 正確なオフセットは EditorControl の EOL 処理に依存するため、CsvDocument.FindCell に任せて
        // "b" が含まれる位置(text.IndexOf("b2"))へキャレットを置く。
        int caret = doc.Editor.SnapshotText.IndexOf("b2", StringComparison.Ordinal);
        doc.Editor.MoveCaretCharOffset(caret);

        Assert.True(host.Csv.TryEnterMode(doc));
        Assert.Equal(1, doc.State.CsvRow); // 0 始まり=2 行目
        Assert.Equal(1, doc.State.CsvCol); // 0 始まり=2 列目
    });

    // ===== ExitMode(ToggleMode 経由・外部 API は ToggleMode のみ) =====

    [Fact]
    public void ToggleMode_FromOn_ExitsMode_RestoresReadWriteAndUia_AnnouncesModeOff() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();

        host.Csv.ToggleMode();

        Assert.False(doc.State.CsvMode);
        Assert.False(doc.Editor.ReadOnly);
        Assert.True(doc.Editor.RaiseUiaSelectionEvents); // 通常編集の SR 挙動に戻す
        Assert.Equal(CsvAnnounceFormatter.ModeOff, host.Announcer.Said[^1]);
    });

    [Fact]
    public void ToggleMode_FromOn_MovesCaretToLastCellStart() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); // (0,0)→(0,1)="a2"
        int expected = doc.Editor.SnapshotText.IndexOf("a2", StringComparison.Ordinal);

        host.Csv.ToggleMode();

        Assert.Equal(expected, doc.Editor.CaretCharOffset);
    });

    [Fact]
    public void ToggleMode_NoActiveDoc_IsNoOp() => Sta.Run(() =>
    {
        using var host = new Host();
        // Docs.CreateNew を呼ばない(Active=null)
        host.Csv.ToggleMode();

        Assert.Empty(host.Announcer.Said); // 通知も発火しない
    });

    // ===== ToggleMode(進入方向) =====

    [Fact]
    public void ToggleMode_FromOff_EntersMode() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);

        host.Csv.ToggleMode();

        Assert.True(doc.State.CsvMode);
    });

    // ===== Move(移動+読み上げ・端メッセージ) =====

    [Fact]
    public void Move_ToAdjacentCell_UpdatesStateAndAnnouncesCell() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();

        host.Csv.Move(Direction.Right); // (0,0)→(0,1)

        Assert.Equal(0, doc.State.CsvRow);
        Assert.Equal(1, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("a2", 1, 2), host.Announcer.Said[^1]);
    });

    [Fact]
    public void Move_AtLeftEdge_AnnouncesLeftEdge_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);            // (0,0) から開始
        host.Announcer.Said.Clear();

        host.Csv.Move(Direction.Left);         // 左端

        Assert.Equal(0, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.LeftEdge, host.Announcer.Said[^1]);
    });

    [Fact]
    public void Move_NotInMode_IsNoOp() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewCsvDoc(Grid3x3); // モードには入らない

        host.Csv.Move(Direction.Right);

        Assert.Empty(host.Announcer.Said);
    });

    // ===== 端ジャンプ(6 API から代表 2 件・残りは第 2 弾で被覆) =====

    [Fact]
    public void MoveTopLeft_MovesTo_0_0() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); host.Csv.Move(Direction.Down); // (1,1) へ
        host.Announcer.Said.Clear();

        host.Csv.MoveTopLeft();

        Assert.Equal(0, doc.State.CsvRow);
        Assert.Equal(0, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("a1", 1, 1), host.Announcer.Said[^1]);
    });
}
```

**Step 2: テスト実行(green を確認=特徴付けの成立)**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~CsvControllerTests"
```
Expected: **Passed! 14 件**

**Step 3: Commit**

```powershell
git add tests/yEdit.App.Tests/CsvControllerTests.cs
git commit -m "test: CsvController の ctor/モード遷移/移動/端メッセージ 14 件"
```

---

### Task 6: CsvControllerTests 第 2 弾(残りの端ジャンプ・GoToCell 3 分岐・読み上げ・BeginEdit/AbortEdit・クランプ・parse-error=11 件)

**Files:**
- Modify: `tests/yEdit.App.Tests/CsvControllerTests.cs`(テスト追記)

**Step 1: 端ジャンプの残りと GoToCell の 3 分岐+対応固定を追記**

```csharp
    // ===== 端ジャンプ(残り) =====

    [Fact]
    public void MoveBottomRight_MovesToLastCell() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();

        host.Csv.MoveBottomRight();

        Assert.Equal(2, doc.State.CsvRow);
        Assert.Equal(2, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("c3", 3, 3), host.Announcer.Said[^1]);
    });

    // ===== GoToCell(3 分岐+対応固定) =====

    [Fact]
    public void GoToCell_PickerCanceled_NoAnnounce_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();
        host.Picker.NextResult = CellPickResult.Canceled;

        host.Csv.GoToCell();

        Assert.Equal(1, host.Picker.PickCount);
        Assert.Empty(host.Announcer.Said);      // Cancel は無音
        Assert.Equal(0, doc.State.CsvRow);      // 状態変化なし
        Assert.Equal(0, doc.State.CsvCol);
    });

    [Fact]
    public void GoToCell_InvalidFormat_AnnouncesBadFormat_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();
        host.Picker.NextResult = CellPickResult.InvalidFormat;

        host.Csv.GoToCell();

        Assert.Equal(CsvAnnounceFormatter.BadCellFormat, host.Announcer.Said[^1]);
        Assert.Equal(0, doc.State.CsvRow);
        Assert.Equal(0, doc.State.CsvCol);
    });

    [Fact]
    public void GoToCell_OutOfRange_AnnouncesOutOfRange_NoChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();
        host.Picker.NextResult = CellPickResult.Ok(99, 99);   // 3×3 の外

        host.Csv.GoToCell();

        Assert.Equal(CsvAnnounceFormatter.OutOfRange, host.Announcer.Said[^1]);
        Assert.Equal(0, doc.State.CsvRow);
        Assert.Equal(0, doc.State.CsvCol);
    });

    [Fact]
    public void GoToCell_Ok_MovesToTarget_AnnouncesCell() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Announcer.Said.Clear();
        host.Picker.NextResult = CellPickResult.Ok(3, 2);     // 1 始まり=(2,1) 0 始まり="c2"

        host.Csv.GoToCell();

        Assert.Equal(2, doc.State.CsvRow);
        Assert.Equal(1, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("c2", 3, 2), host.Announcer.Said[^1]);
    });

    [Fact]
    public void GoToCell_PassesCurrentCellToPicker_As1Based() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); host.Csv.Move(Direction.Down); // (1,1)
        host.Picker.NextResult = CellPickResult.Canceled;

        host.Csv.GoToCell();

        Assert.Equal(2, host.Picker.LastCurrentRow1); // 1 始まり
        Assert.Equal(2, host.Picker.LastCurrentCol1);
    });

    // ===== 読み上げ(移動なし) =====

    [Fact]
    public void ReadCurrent_AnnouncesCurrentCell_NoStateChange() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); // (0,1)
        host.Announcer.Said.Clear();

        host.Csv.ReadCurrent();

        Assert.Equal(CsvAnnounceFormatter.Cell("a2", 1, 2), host.Announcer.Said[^1]);
        Assert.Equal(0, doc.State.CsvRow);   // 位置は動かない
        Assert.Equal(1, doc.State.CsvCol);
    });

    [Fact]
    public void ReadColumnTopAndRowHead_AnnounceHeaders() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.Move(Direction.Right); host.Csv.Move(Direction.Down); // (1,1)
        host.Announcer.Said.Clear();

        host.Csv.ReadColumnTop();
        Assert.Equal(CsvAnnounceFormatter.Header("a2"), host.Announcer.Said[^1]);

        host.Csv.ReadRowHead();
        Assert.Equal(CsvAnnounceFormatter.Header("b1"), host.Announcer.Said[^1]);
    });

    // ===== BeginEdit/AbortEdit(オーバーレイの起動配線のみ検証・Enter/Esc の E2E は L5 領分) =====

    [Fact]
    public void BeginEdit_NotInMode_IsNoOp() => Sta.Run(() =>
    {
        using var host = new Host();
        host.NewCsvDoc(Grid3x3); // モードに入らない

        host.Csv.BeginEdit();

        Assert.False(host.Csv.IsEditing);
    });

    [Fact]
    public void BeginEdit_InMode_StartsOverlay_IsEditingTrue() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);

        host.Csv.BeginEdit();

        Assert.True(host.Csv.IsEditing);
        host.Csv.AbortEdit(); // 後始末(HostForm 破棄前に必ず落とす)
    });

    [Fact]
    public void AbortEdit_WhenEditing_ExitsEditing_AndIsIdempotent() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.BeginEdit();
        Assert.True(host.Csv.IsEditing);

        host.Csv.AbortEdit();
        Assert.False(host.Csv.IsEditing);

        host.Csv.AbortEdit(); // 2 回目=冪等(例外を出さない)
        Assert.False(host.Csv.IsEditing);
    });

    // ===== クランプ(本文編集で行/列が減った後の補正) =====

    [Fact]
    public void Move_AfterContentReducedRows_ClampsToLastRow() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        host.Csv.MoveBottomRight();               // (2,2)
        // 本文を 1 行だけに置換(モード中でも Text setter は無条件で通る=クランプ機構のテスト)
        doc.Editor.ReadOnly = false;
        doc.Editor.Text = "x1,x2,x3";
        doc.Editor.ReadOnly = true;
        host.Announcer.Said.Clear();

        host.Csv.ReadCurrent();                    // (2,2) → クランプ → (0,2)

        Assert.Equal(0, doc.State.CsvRow);
        Assert.Equal(2, doc.State.CsvCol);
        Assert.Equal(CsvAnnounceFormatter.Cell("x3", 1, 3), host.Announcer.Said[^1]);
    });

    // ===== parse-error 後始末(モード中に本文が引用符未終端になったケース) =====

    [Fact]
    public void AnyCommand_AfterContentBecomesUnparseable_AnnouncesParseError_ClearsHighlight() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.NewCsvDoc(Grid3x3);
        host.Csv.TryEnterMode(doc);
        // モード中に本文を書き換えて Ok=false 化(引用符未終端)
        doc.Editor.ReadOnly = false;
        doc.Editor.Text = "a1,\"broken\nx,y";
        doc.Editor.ReadOnly = true;
        doc.ClearCsvCache();                        // Snapshot の再パースを強制
        host.Announcer.Said.Clear();

        host.Csv.Move(Direction.Right);             // TryContext が ParseError を通知

        Assert.Contains(CsvAnnounceFormatter.ParseError, host.Announcer.Said);
    });
```

**Step 2: テスト実行(全 25 件 green を確認)**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~CsvControllerTests"
```
Expected: **Passed! 25 件**

**Step 3: App.Tests 全体+ビルドを確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Passed! **125 件**(既存 100+新規 25)

**Step 4: Commit**

```powershell
git add tests/yEdit.App.Tests/CsvControllerTests.cs
git commit -m "test: CsvController の端ジャンプ/GoToCell 3 分岐/読み上げ/BeginEdit/クランプ/parse-error 11 件"
```

---

### Task 7: ローカルゲート+設計書へ実施記録

**Files:**
- Modify: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`(「Stage 5 実施記録」節の直後に追記)

**Step 1: ローカルゲートを全実行**

Run:
```powershell
powershell -File <repo>\tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`(Release 0 警告・Core 573+Editor 218+App 125=916 緑)

**Step 2: 設計書に実施記録(暫定)を追記**

`docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` の「Stage 5 実施記録」節の直後に追記:

```markdown

### Stage 6 実施記録(2026-07-14)

- **完了**: 実装計画=`docs/plans/2026-07-14-test-strategy-phase2-stage6.md`(上位文書 §2.2 からの精密化 3 点=①CellPickResult の 3 相 record 化(Canceled/InvalidFormat/Ok)で通知文言 3 分岐を保存 ②1 始まり座標を Ok に含めて呼び出し側の変換責務を消す ③CsvController.ctor は ICellPicker を単純注入(ShowDialog 短命リソースのため Func 化しない・名前付き引数化=Stage 4 教訓の CSV 版)。 ①ICellPicker+CellPickResult シーム追加 ②WinFormsCellPicker Adapter+CsvController.GoToCell の 3 分岐置換+MainForm 配線(1 コミット・挙動不変) ③FakeCellPicker 追加 ④CsvControllerTests 25 件(ctor/TryEnterMode 5 分岐/ExitMode/ToggleMode/Move+端メッセージ/端ジャンプ 2/GoToCell 4 分岐+対応固定/読み上げ 2/BeginEdit・AbortEdit/クランプ/parse-error)。
- **テスト数**: 891 → **916**(App 100→125・純増 +25)。ゲート全通過(Release 0 警告)。
- **L5 実機 SR スポット確認**: 必須(§5 のとおり CSV は SR 経路に触れる)。マージ前に NVDA で下記を確認する:
  - CSV ファイルを開いて CSV モードに入る(ModeOn + セル読み)
  - 矢印キーで移動(セル値+行列読み)
  - 左端/右端/先頭/最終行で端メッセージ
  - G キーでセル指定移動(Ok/Cancel/範囲外/書式不正の各分岐)
  - F2 でセル編集開始→Enter で確定→再ハイライト・セル読み
  - モード OFF に戻して通常編集の SR 挙動に復帰
```

(マージコミットのハッシュはマージ後にユーザー確認のうえ追記)

**Step 3: Commit**

```powershell
git add docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md docs/plans/2026-07-14-test-strategy-phase2-stage6.md
git commit -m "docs: Phase2 設計書に Stage 6 実施記録を追記+実装計画を追加"
```

---

### Task 8: レビュー→L5 実機 SR スポット→マージ

**Step 1: 別エージェントによるコードレビュー**(いつもの運用)

ブランチ全 diff(`git diff main...feature/test-strategy-phase2-stage6`)を対象に依頼。観点:

- **挙動不変**:
  - `GoToCell` の 3 分岐(Cancel=無音・InvalidFormat=BadCellFormat・Ok+範囲外=OutOfRange・Ok+範囲内=移動+セル読み)の順序・通知・状態更新
  - `TryEnterMode` の 3 分岐(既にモード中→false・parse 不可→ParseError+モード変化なし・空 CSV→ModeOn のみ・データあり→ModeOn+Cell)
  - `ExitMode` の順序: CsvMode=false → ReadOnly=false → ClearHighlight → キャレット復帰 → RaiseUiaSelectionEvents=true → Focus → ClearCsvCache → Say(ModeOff)
  - `BeginEdit` は現行の Begin 呼びを保存(overlay 起動配線=IsEditing=true)。Enter/Esc の E2E は L5 領分で本 Stage 対象外
- **シームの完全性**:
  - `CsvController.cs` に `CsvGoToCellDialog`・`ShowDialog` のいずれもヒットしない(grep 2 本)
  - `new CsvGoToCellDialog` の唯一の生成場所は `WinFormsCellPicker.cs`(grep で 1 ヒット)
  - `MainForm.cs:66` 以外に `new CsvController` の直 new が存在しない(grep)
- **CellPickResult の Kind 3 相**: Cancel と InvalidFormat が同一値に畳まれていない(GoToCell の switch が両者を区別している)
- **テストの実効性=ミューテーション検証**(Stage 3/4/5 標準)。最低限の変異例:
  - `GoToCell` の `case CellPickKind.Canceled: return;` の削除→InvalidFormat/Ok にフォールスルー → `GoToCell_PickerCanceled_NoAnnounce_NoChange` が赤になること
  - `GoToCell` の `_announcer.Say(BadCellFormat)` を削除 → `GoToCell_InvalidFormat_AnnouncesBadFormat_NoChange` が赤になること
  - `GoToCell` の `result.Row1 - 1` を `result.Row1` に変更(1 始まり→0 始まり変換の削除) → `GoToCell_Ok_MovesToTarget_AnnouncesCell` が赤になること
  - `_cellPicker.Pick(ed.FindForm()!, row + 1, col + 1)` の `row + 1` を `row` に変更 → `GoToCell_PassesCurrentCellToPicker_As1Based` が赤になること
  - `TryEnterMode` の `doc.Editor.RaiseUiaSelectionEvents = false;` を削除 → `TryEnterMode_ParseableCsv_EntersMode_ReadOnlyAndUiaOff_...` が赤になること
  - `ExitMode` の `doc.Editor.RaiseUiaSelectionEvents = true;` を削除 → `ToggleMode_FromOn_ExitsMode_...` が赤になること
  - `TryContext` の Clamp 呼び出しを削除 → `Move_AfterContentReducedRows_ClampsToLastRow` が赤になること
- **Core 検証済み事項の再検証をしていないか**(CsvParser・CsvDocument.MoveCell/GoTo/FindCell・CsvWriter.EscapeField)

**Step 2: L5 実機 SR スポット(必須)**

上位文書 §5 のとおり CSV は SR 経路に触れる=NVDA 実機でマージ前に確認する。**NG があれば当該テストを追加してマージを見送る**。実施項目(1〜2 分):

1. CSV ファイル(例: `docs/example.csv` 相当を用意)を開く→自動 CSV モード進入で「CSVモード オン ○○ 1行1列」と読む
2. 矢印キー(→↓)でセル移動→各セルで「値 行列」を読む
3. 左端で ← を押す→「左端です」・最終行で ↓ を押す→「最終行です」
4. G キー→セル指定ダイアログが開く→有効値で OK→ジャンプ+セル読み・Esc→無音・書式不正で OK→「書式が不正です」・範囲外で OK→「範囲外です」
5. F2→オーバーレイ TextBox が出る→Enter→本文に反映+再ハイライト+セル読み・別セルで F2→Esc→取消(本文無変更・再ハイライトのみ)
6. モードトグル(手動)で ON→OFF→通常編集の SR 挙動に戻る(RaiseUiaSelectionEvents=true 復帰の実感確認)

**Step 3: main へ no-ff マージ**

```powershell
git switch main
git merge --no-ff feature/test-strategy-phase2-stage6 -m "テスト戦略 Phase2 Stage6: CsvController シーム導入+テスト 25 件をマージ"
powershell -File <repo>\tools\pre-merge-check.ps1
git branch -d feature/test-strategy-phase2-stage6
```
Expected: マージ後ゲート全緑(916)

**Step 4: 実施記録へマージコミットのハッシュを追記**(小コミット)

```powershell
git log --oneline -1 main    # マージコミットのハッシュを確認
# 上記ハッシュを Stage 6 実施記録に追記して commit
git add docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md
git commit -m "docs: Stage 6 実施記録にマージハッシュとマージ後ゲート結果を追記"
```

---

## DoD(Stage 6)

1. `tools/pre-merge-check.ps1` 全緑(Release ビルド 0 警告)
2. テスト数 891 → **916**(App 100→125・純増 +25)
3. **挙動不変**: GoToCell の 3 分岐(Cancel/InvalidFormat/Ok+範囲外/Ok+範囲内)/TryEnterMode の 3 分岐/ExitMode の順序/BeginEdit の overlay 起動配線(diff レビューで機械的確認)
4. **シーム完全性**: `CsvController.cs` から `CsvGoToCellDialog`・`ShowDialog` への参照ゼロ(grep 2 本で確認)
5. 別エージェントによるコードレビュー(マージ前・ミューテーション検証を標準適用)
6. **L5 実機 SR スポット確認は必須**(§5 のとおり CSV は SR 経路に触れる=NVDA でモード遷移・セル移動・端メッセージ・GoToCell 3 分岐・F2 編集を確認)
7. main へ no-ff マージ+設計書へ実施記録・マージハッシュ追記

## リスクと対策

- **CellPickResult の Kind 混同**: 3 相 record 化により Cancel と InvalidFormat の畳み込みを排除(§0 精密化 1)。ミューテーションで Cancel の `return` を削除すると `GoToCell_PickerCanceled_...` が赤になることで固定。
- **1 始まり/0 始まりの取り違え**: `CellPickResult.Ok` は 1 始まり座標を保持し、CsvController 側で `-1` して 0 始まりに変換する(現行 `dlg.TryGetCell` 出力と同じ座標系)。`GoToCell_Ok_MovesToTarget_AnnouncesCell` と `GoToCell_PassesCurrentCellToPicker_As1Based` の 2 本で往復を固定。
- **BeginEdit の E2E(Enter/Esc)は L5 領分**: `CsvCellEditor` の Enter/Esc キー処理は WinForms メッセージポンプ経由=STA ユニットテストでは信頼性が低い。ユニットテストは「Begin が呼ばれて IsEditing=true になる」までを固定し、確定/取消の本文反映+再ハイライト+セル読みは L5 実機スポットで担保する(§5 で必須化されている)。ユニットで詰めたい場合の Stage 8 候補=`CsvCellEditor.Commit/CancelEdit` の internal 化+`InternalsVisibleTo` 経由の直呼びテスト。
- **クランプ機構の被覆**: `TryContext` の `ClampRow/ClampCol`+`doc.State.CsvRow/Col` 書き戻しは `Move_AfterContentReducedRows_ClampsToLastRow` で固定。列側クランプの追加テストは Stage 8 候補として §申し送りに記録。
- **parse-error 後始末**: モード中に本文が Ok=false 化するケースは `ClearCsvCache` を明示呼びしたテストで再現(`doc.ParseCsv()` はキャッシュ Snapshot を返すため、本文変更後は cache を落とさないと再パースされない=これ自体が現行挙動)。
- **特徴付けが赤の場合**: 原則テスト側を現行挙動へ合わせる。**ただし SR 誤読み抑止フラグ・ReadOnly トグル・DocumentState 書き戻し・通知文言の赤は SR 退行リスク=修正せずユーザーへ報告**(規約)。

## 申し送り(Stage 7 以降へ)

- **次 Stage**: GrepController(IGrepView/IGrepResultsView・RunAsync 化)=上位文書 §4 Stage 7。設計時に GrepDialog の IAnnouncer 注入化(Stage 2 由来の申し送り)を判断する。
- **Stage 8 候補**:
  - `CsvCellEditor.Commit/CancelEdit` の internal 化+App.Tests からの直呼びで F2 編集の Enter/Esc 経路をユニットで固定(現状 L5 領分)。
  - 列側クランプ(`ClampCol` の col が現在行の幅を超えるケース)の追加テスト。
  - `CsvController` から `FindForm()!` の null 忘れ(picker 呼び出し時の owner)を防ぐ手当(現状 `!` で許容)=NRT 経路のクリーンアップ候補。
  - `_editor.IsEditing` を CsvController から観測する `IsEditing` プロパティ経由の guard(BeginEdit で `_editor.IsEditing return` を追加テスト)。
- **Sta.cs の共有抽出**: 3 プロジェクト目条件は継続監視(本 Stage では発動しない)。
- **CsvGoToCellDialog の UI**: 本 Phase の対象外(L5 手動)。プレゼンター抽出は必要が生じた Stage で個別判断。
