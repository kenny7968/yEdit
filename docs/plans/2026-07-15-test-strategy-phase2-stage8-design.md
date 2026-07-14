# テスト戦略 Phase 2 Stage 8: 薄い MainForm 痩身+申し送り整理 設計書

- 日付: 2026-07-15
- 上位文書: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` §4 Stage 8(任意)
- 前提: Stage 7 完了(main マージ `01e52ad`・テスト数 938)

## 0. スコープと方針

Stage 8 は上位文書で「任意=Stage 7 完了時点で費用対効果を再評価する」と定められている。ユーザー承認済みの評価結果として **薄い Stage 8** を採る。原案(MainForm の全コマンド抽出)は益薄が過半のためスコープアウトし、以下 4 Task に絞る。

- **A. 型狭化+readonly 化**: Controller の `_owner` を IWin32Window へ狭め、`_announcer` を readonly 化。挙動不変・純リファクタ。
- **B. KinsokuFormatController 抽出**: 唯一の実質的痩身。分岐と通知文言が Controller テストの益に足る。
- **C. GrepController から `_jumpTo` を外す**: Stage 7 レビュー由来の設計改善(Controller が GrepHit ジャンプ経路を知らなくてよい)。
- **D. Stage 7 由来の高価値テスト追加**: 現行 GrepController の未被覆分岐(catch 内 guard・Cancel 副作用・Progress 追い越し)を kill。

**共通方針**(Stage 4〜7 と同型):
- ストラングラー方式・1 Stage=1 フィーチャーブランチ=1 no-ff マージ
- Subagent-Driven Development(SDD)+各 Task 完了時 2 段レビュー+最終ブランチレビュー「マージ可」
- ミューテーション検証(Stage 3 以降の標準)を Task B/D で実施
- **挙動不変**(公開挙動・SR 発声文言・フォーカス遷移を変えない)
- L5 不要(Task B は UiaAnnouncer 単一経路のまま・文言不変=SR 経路不変)

## 1. スコープアウト(継続申し送り)

Stage 8 に含めない項目とその理由。個別 PR で必要に応じて回収する。

### 1.1 MainForm 原案(Kinsoku 以外の全コマンド)

| コマンド | 理由 |
|---|---|
| `AnnouncePosition` | 中身は `PositionFormatter.Format` への薄いラッパ+null チェック。Core 側で既にテスト済み、MainForm 側は 4 行の配線。抽出益極小。 |
| `GoToLine` | GoToLineDialog の抽象化(IGoToLineDialog シーム)が必要。Stage 4 の IFindReplaceView と同型で 1 Task 分の工数。CSV 読み替え 1 行の分岐しかテスト対象が無いため益/工数比が悪い。 |
| `ToggleOvertype` | 3 行。抽出不要。 |
| `ShowMarkdownPreview` | MarkdownPreviewForm 抽象化が必要。UI 表示の性格が強く L5 手動領分。 |
| `CloseActiveTab` | DocumentManager.TryClose + FileController.ConfirmDiscardIfDirty の配線。既存 DocumentManagerTests / FileControllerTests で個別に緑。 |
| `OpenSettings` | SettingsDialog 抽象化+設定差し替えの broad refactor になる。Phase 2 の目的(Form/OS 境界の抽象化)は他 Stage で達成済み。 |
| `RebuildRecentMenu` / `AutoEnterCsvMode` / `UpdateTitle` / `UpdateStatus` / `SaveSettingsSafe` | UI 配線の性格が強く、抽出しても Controller はほぼ空。 |

### 1.2 Stage 5 由来の申し送り

- Lazy `_writer ??= CreateWriter()` の二重呼び非再生成テスト
- `HasBackup=false` で閉じた doc の Delete ガード被覆
- `SerialBackupWriter.Write` catch→`OnWriteFailed` の実 I/O 統合テスト
- `BackupStore.LoadAll/SweepTempFiles` の抽象化再評価

すべて挙動不変のテスト追加または独立リファクタ。Stage 8 ブランチに束ねる必然性がない。

### 1.3 Stage 6 由来の申し送り

- CsvCellEditor.Commit/CancelEdit の internal 化+F2 編集経路テスト
- 列側クランプ(ClampCol の col が現在行の幅を超えるケース)追加テスト
- `default: throw` 経路のカバレッジ完全化(合成 CellPickResult)
- `ed.FindForm()!` の null 許容契約(NRT 経路クリーンアップ)

いずれも小さいテスト追加。個別 PR で十分。

### 1.4 Stage 7 由来の申し送り(D で扱わない分)

- GrepDialog の `UiaAnnouncer` 直生成の外出し(=IAnnouncer 注入化)。Stage 2 由来。独立 PR で十分な小変更。
- `_view.Visible` の BeginClose/Cancel 経路での不変固定(Task D の範囲外)
- `ShowResults` 内の `_resultsView.IsDisposed` 分岐被覆

## 2. Task 詳細

### Task A — 型狭化+readonly 化(挙動不変・純リファクタ)

#### A-1. `MainForm._announcer` を readonly 化

現状: `private IAnnouncer _announcer = null!;`(ctor で 1 度だけ代入・以後不変)。
変更: `private readonly IAnnouncer _announcer;`(ctor 内で初期化・field 初期化子は不要)。
テスト: 既存が緑のまま。

#### A-2. Controller の `_owner` を Form → IWin32Window に狭める

対象(現状既に確認済み):
- `src\yEdit.App\SearchController.cs:16` — `private readonly Form _owner;`
- `src\yEdit.App\FileController.cs:19` — `private readonly Form _owner;`
- `src\yEdit.App\GrepController.cs:14` — `private readonly Form _owner;`

方針: フィールド型と ctor 引数を `IWin32Window` に置換。`_owner` の用途は「ダイアログの親」または「ShowAndFocus/ShowDialog の owner 引数」のみで、Form 固有メンバー(Text/Handle 以外)は参照していない。呼び出し側の `MainForm` は `Form: IWin32Window` のため呼び出し無変更。

テスト: 既存 App.Tests の TestHost.cs は `Form` を渡しているが、`IWin32Window` 引数を受けるので変更不要。

補助: FileController ctor を名前付き引数化(Stage 4/6 で確立した「同型 delegate 位置取り違え検出」対策)。呼び出し側 1 箇所を修正。

#### A-3. `SearchController.FindPrev` の `_lastHit` 三項式デッドコード除去

現状(`SearchController.cs:114-116`):
```csharp
int before = (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
    ? h.Start
    : selStart;
```

分析: `_lastHit` が三項の真経路に入る条件は「選択範囲が直前 hit と完全一致」。このとき `selStart == h.Start` が成立するので `h.Start == selStart` = 三項の両分岐が同値=常に `before = selStart` に簡約可能。**Stage 4 実施記録の申し送り記述と一致**。

変更: `int before = selStart;` の 1 行に簡約。挙動不変。Forward 側の三項式(107-109 行)は `h.Start + Math.Max(1, h.Length)`(ゼロ幅前進)のため簡約不可=温存。

テスト: SearchControllerTests の既存 32 件が緑のまま。

#### A-4. DoD

- ローカルゲート `tools/pre-merge-check.ps1` 全緑
- Release 0 警告
- テスト数不変(938)・挙動不変
- diff レビューで機械確認可能な粒度(型置換・修飾子変更・1 行簡約のみ)

### Task B — KinsokuFormatController 抽出

#### B-1. 現状(`MainForm.cs:466-495` = 30 行)

分岐:
1. アクティブなしガード(`ed is null` → return)
2. CSV モード中ガード(`doc!.State.CsvMode` → `BlockedInCsvMode` 通知 → return)
3. 選択判定(`selStart == selEnd` → 全文整形 / 部分整形)
4. 長さゼロガード(`len <= 0` → return)
5. 整形結果==原文(`formatted == target` → `"変更なし"` 通知 → return)
6. 成功(整形反映 → 部分は変化箇所選択 / 全文は先頭キャレット → `"整形しました"` 通知)

依存:
- `Document` / `EditorControl`(実物)
- `AppSettings.WrapColumn / KinsokuLineStartChars / KinsokuLineEndChars / KinsokuHangChars / TabWidth`
- `KinsokuFormatter.Format`(Core 純粋関数・テスト済み)
- `IAnnouncer`(通知文言 3 種)
- `CsvAnnounceFormatter.BlockedInCsvMode`(Core 定数)

#### B-2. Controller 設計

```csharp
namespace yEdit.App;

public sealed class KinsokuFormatController
{
    private readonly DocumentManager _docs;
    private readonly IAnnouncer _announcer;

    public KinsokuFormatController(DocumentManager docs, IAnnouncer announcer)
    {
        _docs = docs;
        _announcer = announcer;
    }

    /// <summary>選択範囲(無ければ全文)を WrapColumn 桁で禁則整形する(実改行挿入・1 Undo)。</summary>
    /// <remarks>CSV モード中は本文が読取専用のため抑止し、誤成功通知を防ぐ。</remarks>
    public void Run(AppSettings settings)
    {
        // MainForm.FormatWithKinsoku から現行ロジックを丸ごと移設(挙動不変)
    }
}
```

- **AppSettings は Run 引数**: Stage 2〜7 で AppSettings は Func 越しに参照されている(`OpenSettings` で差し替え可能)。Controller ctor に注入するとキャッシュされてしまうため、`Run` 引数で受け取る形にする(Stage 6 の `ICellPicker` 相当・呼び出し時解決)。
- **IAnnouncer は注入**(Stage 4/6 と同型)
- **DocumentManager は注入**(Active 取得のため)
- **EditorControl は Document.Editor 経由の実物**(§0 方針)

MainForm 側:
```csharp
private readonly KinsokuFormatController _kinsoku; // ctor で生成
// ...
_kinsoku = new KinsokuFormatController(_docs, _announcer);
// ...
private void FormatWithKinsoku() => _kinsoku.Run(_settings);
```

`FormatWithKinsoku` は 1 行のディスパッチだけ残る(将来の削除も可だがメニュー配線識別子として存置)。

#### B-3. テスト観点(想定 6 件)

`tests\yEdit.App.Tests\KinsokuFormatControllerTests.cs`

1. **PartialSelection_Formats_AndSelectsChangedRange** — 選択範囲を整形し、変化後の同じ範囲が再選択される+「整形しました」発声
2. **WholeText_NoSelection_Formats_AndCaretToStart** — 選択なしで全文整形。キャレットが (0,0) に移動+「整形しました」発声
3. **NoChange_AnnouncesNoChange_AndBufferUnchanged** — 整形前後で文字列一致(既に整形済み)→ `"変更なし"` 発声・本文と選択は変化しない
4. **CsvMode_Blocked_AnnouncesBlockedText_AndBufferUnchanged** — `doc.State.CsvMode = true` → `CsvAnnounceFormatter.BlockedInCsvMode` 発声・本文不変
5. **EmptySelectionAtEmptyBuffer_NoOp** — 全文空(len==0)→ 発声なし・本文不変
6. **UsesActiveDocumentEolAndTabWidth** — EOL(CRLF/LF/CR)と TabWidth が Document/Settings から正しく渡ることを 1 パラメトリックで検証(整形結果に含まれる EOL を assert)

**非対象**(Core の KinsokuFormatter 側で検証済み):
- 禁則文字(行頭・行末・追い出し)の分類
- 折返し桁の算出

#### B-4. ミューテーション検証(Stage 3 以降の標準)

品質レビューで以下 5 変異を実施:
1. CSV ガードを常時 false 化 → テスト 4 が赤(kill)
2. `formatted == target` を `!=` に反転 → テスト 3 が赤(kill)
3. `whole` の三項式を反転 → テスト 1/2 が赤(kill)
4. 通知文言 `"整形しました"` を空文字化 → テスト 1/2 が赤(kill)
5. `if (whole) ed.SelectCharRange(0, 0)` の位置を (10,0) 等に変更 → テスト 2 が赤(kill)

`"変更なし"` 発声のみを確認するテスト(No.3)は非既定位置(選択(1,3)状態)から検証開始する(Stage 6 レビュー標準)。

#### B-5. DoD

- ローカルゲート全緑・Release 0 警告
- テスト数: 938 → **944**(+6)
- App: 147 → 153
- 挙動不変(MainForm から抽出前後の動作を diff レビューで確認)
- ミューテーション 5/5 kill(spec-critical 100%)

### Task C — GrepController から `_jumpTo` を外す

#### C-1. 現状(`GrepController.cs:14/34/151`)

```csharp
private readonly Action<GrepHit> _jumpTo;
// ctor:
_jumpTo = jumpTo;
// ShowResults 内:
_resultsView = _resultsFactory(new GrepResultsCallbacks(_jumpTo));
```

MainForm 側(`MainForm.cs:59-64`):
```csharp
_grep = new GrepController(
    docs: _docs,
    owner: this,
    jumpTo: hit => OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength),
    viewFactory: cb => new GrepDialog(cb),
    resultsFactory: cb => new GrepResultsWindow(cb));
```

#### C-2. 変更設計

`GrepController` から `_jumpTo` フィールド・ctor 引数を削除。`resultsFactory` の型を変更する:

```csharp
// Before
Func<GrepResultsCallbacks, IGrepResultsView> resultsFactory

// After
Func<IGrepResultsView> resultsFactory
```

MainForm 側で GrepResultsWindow を生成する時点で `_jumpTo` を GrepResultsCallbacks に組み込む方針は 2 案:

**案 1(単純)**: `resultsFactory` を `Func<IGrepResultsView>` に戻し、GrepResultsWindow.ctor で GrepResultsCallbacks を内包(コールバックはコンストラクタ後に SetCallbacks で差し替え可能とする)。→ Stage 7 の設計原理(相互参照切断・callbacks は factory 引数)から逸脱するので不採用。

**案 2(採用)**: `resultsFactory` を `Func<Action<GrepHit>, IGrepResultsView>` に変更。MainForm が jumpTo を直接 factory へ渡し、factory が GrepResultsCallbacks を組み立てて GrepResultsWindow へ渡す。

```csharp
// MainForm ctor
_grep = new GrepController(
    docs: _docs,
    owner: this,
    viewFactory: cb => new GrepDialog(cb),
    resultsFactory: jumpTo => new GrepResultsWindow(new GrepResultsCallbacks(jumpTo)));

// GrepController.ShowResults(...)
Action<GrepHit> jumpTo = hit => OpenAndSelectViaMainForm(hit); // ← ここは _jumpTo を持たないので不可
```

**問題**: GrepController は `ShowResults` 内で GrepResultsCallbacks を組み立てているため、jumpTo を factory から渡すには GrepController 内で jumpTo を保持するか、factory シグネチャに含めるしかない。上記案 2 も本質的には GrepController が jumpTo を通過させているだけで、目的(Controller が GrepHit ジャンプ経路を「知る」ことをやめる)が達成できない。

**案 3(採用)**: `resultsFactory` を `Func<IGrepResultsView>` にし、GrepResultsCallbacks の生成責務を **factory 側**(=MainForm 側)に移す。GrepController は `IGrepResultsView` を受け取るだけ。

```csharp
// GrepController
private readonly Func<IGrepResultsView> _resultsFactory;

public GrepController(
    DocumentManager docs,
    IWin32Window owner,   // A-2 と合わせて狭化
    Func<GrepCallbacks, IGrepView> viewFactory,
    Func<IGrepResultsView> resultsFactory)
{
    _docs = docs;
    _owner = owner;
    _viewFactory = viewFactory;
    _resultsFactory = resultsFactory;
}

private void ShowResults(string pattern, string folder, GrepOutcome outcome)
{
    _resultsView = _resultsFactory();
    _resultsView.Populate(pattern, folder, outcome);   // 既存 IGrepResultsView.Populate シグネチャそのまま
    _resultsView.Show(_owner);
}

// MainForm
_grep = new GrepController(
    docs: _docs,
    owner: this,
    viewFactory: cb => new GrepDialog(cb),
    resultsFactory: () => new GrepResultsWindow(
        new GrepResultsCallbacks(hit => OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength))));
```

これで GrepController は `GrepHit`/`GrepResultsCallbacks` を知らない(**参照ゼロ**を grep で確認)。

**リスク**: 現行テスト 20 件のうち `FakeGrepResultsView` を使うテストは、GrepResultsCallbacks を直接組み立てているものと、GrepController 経由で組み立てているものがある。案 3 で影響を受けるのは前者=factory ラムダ内で FakeGrepResultsView を生成するテストのみ(挙動不変・シグネチャ変更のみ)。

#### C-3. テスト観点(既存 20 件のリファクタ+新規 1 件)

- 既存 20 件: factory ラムダ + テスト用 jumpTo キャプチャの受け渡しを 1 段介する形に変更(挙動不変)
- 新規 1 件: `Controller_HasNoJumpToReference`(grep 検証を CI に組み込む代替=`typeof(GrepController).GetFields(...).All(f => f.FieldType != typeof(Action<GrepHit>))` の反射テスト)

#### C-4. DoD

- ローカルゲート全緑・Release 0 警告
- テスト数: 944 → **945**(+1・新規反射テスト)
- App: 153 → 154
- 挙動不変(既存 20 件が緑のまま=挙動保存の証明)
- grep 検証: `Grep "Action<GrepHit>\|GrepResultsCallbacks" src/yEdit.App/GrepController.cs` → 0 件(GrepOutcome/GrepHit は Populate 経由で通過するため残る=対象外)

### Task D — Stage 7 由来の高価値テスト追加

Stage 7 のミューテーション検証で kill 可能と確認済みだが未書きの 3 領域。実装変更なし・テストのみ。

#### D-1. `Cancel_CancelsCurrentRun_...` の副作用側 assertion 網羅

現行テストは「トークンがキャンセルされること」のみ assert。以下を追加:
- キャンセル後は Summary 発声(`"検索を完了しました"` 等)が **出ない**
- `_resultsView` の Populate/Show が **呼ばれない**(FakeGrepResultsView の PopulateLog/ShowResultsCount == 0)
- `_view.Visible` の状態は変えない(BeginClose 判定と直交)

想定 +2 件。

#### D-2. Progress コールバック内の追い越し guard 3 条件の直接テスト

`ReportProgress` 内(または相当箇所)の 3 条件:
- `!d.IsDisposed`
- `ReferenceEquals(_cts, cts)`
- `!token.IsCancellationRequested`

TaskCompletionSource で「Progress 発火→追い越し状態(_cts 差し替え / Dispose / Cancel)を人工的に作り込んでから→もう 1 度 Progress 発火」の順で決定的タイミングを組み、各条件の false 経路で **UI 更新が抑止される** ことを assert。

想定 +3 件(3 条件 × 1)。

#### D-3. `catch` 内 guard `if (!d.IsDisposed && ReferenceEquals(_cts, cts))` の分岐被覆

現行 20 件では準等価変異(guard を常時 true にする変異)が生存。以下 2 テストで kill:
- `Catch_AfterDispose_DoesNotAnnounceError` — 検索デリゲート内で例外→await 完了までに Dispose→エラー通知が出ない
- `Catch_AfterCtsSwapped_DoesNotAnnounceError` — 検索デリゲート内で例外→await 完了までに `Open` を再呼び出し(=新しい `_cts`)→旧 run のエラー通知が出ない

想定 +2 件。

#### D-4. DoD

- ローカルゲート全緑・Release 0 警告
- テスト数: 945 → **952**(+7)
- App: 154 → 161
- ミューテーション kill: Stage 7 で生存した準等価変異 1 件が kill 化・Progress guard 3 条件も個別 kill

## 3. Task 依存関係

| Task | 依存 | 理由 |
|---|---|---|
| A | なし | 純リファクタ・単独完結 |
| B | A に軽依存 | KinsokuFormatController の owner 引数は不要だが、MainForm 側の `_announcer` readonly 化が先に入ると diff が綺麗 |
| C | A に依存 | GrepController.ctor を書き換える際、`_owner` の型狭化と ctor パラメータ順を同時に確定させる |
| D | C に依存 | GrepController のシグネチャ変更後のテストにするため |

**推奨順**: A → B → C → D(直列)。SDD は Task 単位で並列化可だが、C/D は同じ GrepController を触るため直列が安全。

## 4. 想定コミット・テスト数遷移

| Step | 内容 | テスト数 |
|---|---|---|
| ベース | Stage 7 マージ後 | 938 |
| A 完了 | 型狭化+readonly 化・FindPrev 簡約 | 938(不変) |
| B 完了 | KinsokuFormatController 抽出+6 件 | 944 |
| C 完了 | Grep _jumpTo 外し+反射 1 件 | 945 |
| D 完了 | Grep 高価値 7 件 | **952** |

想定コミット数: **8〜10**(Task 毎に 2〜3 コミット=シーム/実装/テスト/レビュー由来修正)。

## 5. リスクと対策

- **A-2 の owner 型狭化リスク**: `_owner.FindForm()` や `.Handle` 参照があると Form → IWin32Window で壊れる。事前 grep で確認済み(3 Controller 内で `_owner.` 参照は `ShowDialog(_owner)` / ダイアログ生成の第 1 引数のみ)。
- **A-3 の FindPrev 簡約リスク**: 「_lastHit があるとき選択が hit と一致」不変は既存 32 件のうち複数で暗黙に検証されている(step state 系)。緑を保つことが不変の証明。
- **B の抽出リスク**: `_settings` が MainForm でミュータブル(`OpenSettings` で差し替え)なため、Controller に注入すると陳腐化する。→ `Run(AppSettings)` 引数で解決(Stage 6 のパターン)。
- **C の設計選択リスク**: 案 3(factory 側で GrepResultsCallbacks 組立)を採るとテスト側で GrepResultsCallbacks 生成が 1 段深くなる。→ TestHost.cs に `MakeGrepFactory(Action<GrepHit>)` ヘルパを追加して DRY 化。
- **D のタイミング決定性リスク**: Progress の追い越し guard は非同期タイミング依存。→ Stage 7 で確立した TaskCompletionSource パターンを踏襲(実 I/O ゼロ)。

## 6. 申し送り(Stage 8 完了後 → Phase 2 終了宣言)

Stage 8 完了で Phase 2 は終了とする。以下は Phase 2 外の恒久バックログ:

- §1.2〜§1.4 の未回収項目(必要に応じて個別 PR)
- 原案の他コマンド抽出(§1.1)は現時点で見送り。将来 MainForm がさらに肥大化したら再評価。
- Phase 3(SR 性能ゲート・任意)着手条件は上位文書に従い、実機 SR で退行が観測された場合のみ検討。
- ci.yml / release.yml の実機 CI 初回検証は次回 push 時。

## 7. 実施記録(執筆時点=未完)

本文書の commit 後、実装計画 `2026-07-15-test-strategy-phase2-stage8.md` を writing-plans skill で作成し、ブランチ `feature/test-strategy-phase2-stage8` で SDD 実行。各 Task 完了・最終レビュー・マージ後に本節へ追記する。
