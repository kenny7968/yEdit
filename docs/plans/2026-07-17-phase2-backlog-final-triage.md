# Phase 2 残バックログ 最終トリアージ計画

> **For Claude:** REQUIRED SUB-SKILL: 実行時は `superpowers:executing-plans` を使用。判断項目(§B)は着手前に「本当に条件が発火したか」を再検証してから実装可否を決めること。

**Goal:** テスト戦略 Phase 2 由来の恒久バックログのうち **CI 系以外の全項目** を最終決着(実装・恒久非対応記録・条件監視化・メモ正規化)まで進める。

**Architecture:** 実装作業は 1 タスクのみ(Task A1)。他は「なぜ実装しないか」の判断記録・条件監視化・古い申し送りリストの正規化。src 変更ゼロ・テスト追加 1 件のみ(F 系 App.Tests +1)。

**Tech Stack:** .NET 9 / WinForms / xUnit / 既存 FakeGrepView・FakeGrepResultsView Fake 基盤

**上位ゲート:** Task A1 完了後に `tools/pre-merge-check.ps1` を通す(0 警告 + テスト全緑)。§C のメモ更新のみでも新規ブランチ→main no-ff マージが望ましい。

**参照:**
- [[test-strategy]] — 5 層ピラミッド。§ 申し送りリストが古いので Task C1 で正規化
- [[test-review-cleanup-complete]] — 直前 13 タスク(実装 11+キャンセル 1+判断 1)の全回収記録=**残バックログのグラウンドトゥルース**
- `docs/plans/2026-07-15-test-review-cleanup-backlog.md` — Task 5 副次発見の出典
- `tests/README.md` — テストレビュー標準

---

## トリアージ結果サマリ(重要=先に読むこと)

`[[test-strategy]]` § 「申し送り(Phase 2 完了後の恒久バックログ)」のリストは **[[test-review-cleanup-complete]] の 13 タスクを反映しないまま残存**している。実際の状態を再判定した結果:

| # | 元の項目 | 元の Stage | 真の状態 |
|---|---------|-----------|---------|
| 1 | `_writer ??=` Lazy 二重呼び非再生成テスト | 5 | **✅ 実施済**(cleanup Task 3) |
| 2 | `HasBackup=false` Delete ガード被覆 | 5 | **✅ 実施済**(cleanup Task 4) |
| 3 | `SerialBackupWriter.Write` catch→`OnWriteFailed` 実 I/O 統合テスト | 5 | **✅ 実施済**(cleanup Task 2) |
| 4 | `BackupStore.LoadAll/SweepTempFiles` 抽象化再評価 | 5 | **✅ 判断済=見送り**(cleanup Task 13・§B1 で条件監視化) |
| 5 | `WinFormsRestorePrompt` テストなし | 5 | **恒久非対応**(§B2・単純 enum mapping のみで L5 領分) |
| 6 | CsvCellEditor.Commit/CancelEdit internal 化+F2 経路テスト | 6 | **✅ 実施済**(cleanup Task 7) |
| 7 | 列側クランプ追加テスト | 6 | **✅ 実施済**(cleanup Task 8・pivot: OutOfRange 通知に変更) |
| 8 | `ed.FindForm()!` NRT 契約クリーンアップ | 6 | **✅ 実施済**(cleanup Task 11・`OwnerFormOf` ヘルパ) |
| 9 | `default: throw` 経路カバレッジ完全化 | 6 | **✅ 実施済**(cleanup Task 9) |
| 10 | CsvGoToCellDialog UI プレゼンター抽出 | 6 | **恒久非対応**(§B3・L5 手動領分) |
| 11 | `_view.Visible` BeginClose/Cancel 経路の不変固定 | 7 | **✅ 判断済=キャンセル**(cleanup Task 5 YAGNI・BeginClose は view に触れない) |
| 12 | `ShowResults` 内 `_resultsView.IsDisposed` 分岐被覆 | 7 | **✅ 実施済**(cleanup Task 6) |
| 13 | GrepDialog `UiaAnnouncer` 直生成の外出し | 7 | **✅ 実施済**(cleanup Task 12・IAnnouncer 注入) |
| 14 | `ed.Focus()` offscreen host 検証不能 | 8 | **§B4 判断=YAGNI 推奨**(seam 導入コスト>利得) |
| 15 | GrepController 反射テスト ctor param 型スキャン | 8 | **✅ 実施済**(cleanup Task 10) |
| 16 | TestHost.cs 命名不一致 | 4 | **意図的=対応不要**(§B5・cleanup 側で結論) |
| 17 | WriteToPath 失敗時 EOL 変換ロールバック対象外 | 2/3 | **✅ 実施済**(cleanup Task 1 で根治=B1/B2 修正確認テスト転用) |
| 18 | Sta.cs 共有抽出(3 プロジェクト目条件) | 横断 | **条件監視**(§B6・現状 STA 必要は 2 プロジェクト) |
| ★ | Open() が `_closing` をリセットしない implicit contract | 7(副次) | **§A1 で実施**(defensive test 1 件) |

**真に実装が残るのは §A1 のみ・1 テスト追加**。他はすべて実施済み・判断済み・条件監視 or 恒久非対応の記録作業。

---

## §A 実装タスク(1 件・小)

### Task A1: GrepController.Open() が `_closing` フラグを勝手にリセットしない contract を pin

**由来:** [[test-review-cleanup-complete]] § 「次(継続バックログ)」の副次発見(Task 5 キャンセル時の観察)。**優先度低・回帰観測時に検討**とされていたが、コストが極小(テスト 1 件・15 分)で、且つ「終了確認カスケード中の Open() で `_closing` が黙って解除される」回帰は **アプリ終了経路の不可視故障** に直結するため、この機会に予防的に固定する。

**Files:**
- Test: `tests/yEdit.App.Tests/GrepControllerTests.cs`(既存ファイルに +1 テスト)

**Background(実装挙動の観察):**
- `GrepController.BeginClose()` (src/yEdit.App/GrepController.cs:132): `_closing=true; _cts?.Cancel();`
- `GrepController.CancelClose()` (:135): `_closing=false;` (終了取消時のみ復帰)
- `GrepController.Open()` (:40-46): view の生成/再表示のみ。`_closing` に触れない(=正しい)
- `RunAsync()` の 3 ヶ所(:85, :102, :114 の guard 群)は `_closing` を尊重して結果反映を抑止

**回帰リスク:** 誰かが Open() に「以前の `_closing` を掃除しておこう」という善意で `_closing=false;` を紛れ込ませると、終了キャンセル中に grep ダイアログを開いた瞬間に `_closing` が解除され、走査中のレース経路で結果反映が復活する可能性がある(=終了処理と競合)。

**Step 1: 既存 FakeGrepView の状態確認**

- `tests/yEdit.App.Tests/FakeGrepView.cs` に `IsDisposed` プロパティがあることを確認
- 既存 `GrepControllerTests` の Open 系テストを 1 件開き、view 生成の Assert パターン(`Assert.Equal(1, factoryCalls)` 等)を把握

**Step 2: 失敗するテストを書く**

```csharp
[Fact]
public void Open_AfterBeginClose_DoesNotResetClosingFlag()
{
    // Arrange: 通常の Grep Controller と Fake view/results/searchFn
    var (ctrl, view, _, _) = BuildController();

    ctrl.Open();                   // 初回オープン(_closing=false のはず)
    ctrl.BeginClose();             // 終了フラグ = true

    // Act: 終了フラグ立ち中に Open() を再呼び(終了確認ダイアログ中に間違って開かれた等)
    ctrl.Open();

    // Assert: _closing はまだ true のまま(Open() は解除しない)
    //   → private field を reflection で観察(GrepController._closing)
    var closingField = typeof(GrepController).GetField("_closing",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    Assert.NotNull(closingField);
    Assert.True((bool)closingField!.GetValue(ctrl)!,
        "Open() は _closing を勝手にリセットしてはならない。復帰は CancelClose() 経由のみ。");
}
```

**Step 3: テスト実行 → 実装が正しければ green**

```powershell
dotnet test tests/yEdit.App.Tests --filter FullyQualifiedName~Open_AfterBeginClose_DoesNotResetClosingFlag
```
期待: PASS(現在の実装は `_closing` に触れていないため)

**Step 4: mutation kill 検証(自主)**

- `GrepController.Open()` の先頭に `_closing=false;` を仕込む → テストが FAIL することを目視確認 → 復元
- これで「回帰予防」として機能することの証拠(commit message に mutation 結果を残す)

**Step 5: commit**

```bash
git add tests/yEdit.App.Tests/GrepControllerTests.cs
git commit -m "test(app): pin GrepController.Open() does not reset _closing flag

Task 5 副次発見(2026-07-15 cleanup 由来)の defensive test。
Open() 内に _closing=false; を紛れ込ませる回帰(終了確認カスケード中の
grep 開放で結果反映が復活する経路)を reflection で機械検出する。

mutation: Open() 先頭に _closing=false; 追加で赤化を目視確認。"
```

**L5 影響:** なし(SR 経路不変・reflection のみ)

---

## §B 判断記録タスク(実装なし・docs のみ)

各項目は「実装しない」ことを明示的に決着し、**将来の再検討条件** を計画書側に残す。commit は §C1 と抱き合わせで 1 コミット可(判断だけの独立コミットは Overhead)。

### Task B1: BackupStore 抽象化 → 見送り継続(条件監視化)

**由来:** cleanup Task 13。[[test-review-cleanup-complete]] Batch E で既に「見送り」と結論済み。本タスクは**見送りを再確認**するだけ。

**再検討トリガ**(この 3 条件のいずれかが発火した時のみ着手検討):
1. `BackupCoordinator.OfferRestoreOnStartup` に新規分岐が追加され、L4/L5 で回帰観測
2. `BackupStore` に副作用のある新規 API(OneNote 連携等・[[cloud-notes-backend-pending]] 由来)追加時
3. 防波堤 catch(`SweepTempFiles`/`LoadAll`)を実際に踏む回帰観測時

**判断根拠(再確認):** src 内呼び出し元は 2 箇所のみ、L2(Core 6 件)+L3(App 8 件・`PlantBackup` パターン)で契約側+配線側を実 I/O で完全 cover 済み。抽象化で cover 可能になるのは `SweepTempFiles/LoadAll` の防波堤 catch 2 経路のみ=100〜150 行のコストに見合わない。

**アクション:** なし(このドキュメントの記録で条件監視完了)

---

### Task B2: `WinFormsRestorePrompt` テストなし → 恒久非対応

**Files reviewed:** `src/yEdit.App/WinFormsRestorePrompt.cs`(23 行・class 全体を確認)

**内容:** `RestoreDialog` を `ShowDialog` し、内部 enum → App 層公開 enum の 3 way マッピングを行うだけの薄い Adapter。ロジックは switch 式 1 個のみ。

**判断根拠:**
- `ShowDialog` は L5(実機 SR)領分・xUnit STA でも走査困難
- 3 way switch は「Restore」「DiscardAll」「その他=LaterEmpty」で **default 経路が LaterEmpty に潰される設計** = 分岐追加時のみ壊れる
- カバレッジ導入コスト >>> 得られる保護

**再検討トリガ:** `RestoreDialog.RestoreAction` に enum 値が追加された時(現在 3 値=Restore/DiscardAll/その他)。追加時は必ず switch 式 default の潰し方を再確認する。

**アクション:** なし(コメント追記も過剰・L5 領分の明示記録で完了)

---

### Task B3: `CsvGoToCellDialog` UI プレゼンター抽出 → 恒久非対応(L5 手動領分)

**Files reviewed:** `src/yEdit.App/CsvGoToCellDialog.cs`(54 行・class 全体を確認)

**内容:** WinForms `Form` 派生。`TryGetCell(out row, out col)` の parse ロジックは既に **cleanup Task 8 が `ICellPicker` 経由でカバー済み**(GoToCell 経路の 3 分岐通知=Canceled/InvalidFormat/Ok)。

**判断根拠:**
- ダイアログ本体のレイアウト・IME 無効化・アクセシビリティ名は **視覚 UX + キーボード操作** の領分 = L5 実機確認でのみ回帰検出可能
- プレゼンター抽出(MVP パターン)しても得られるのは `TryGetCell` の直接テスト = **既に ICellPicker 経由で pin 済みなので二重投資**
- Form 派生から presenter を切り出すコスト = 50〜80 行のリファクタ

**再検討トリガ:** `CsvGoToCellDialog` に **新規入力欄** が追加され、`TryGetCell` の返り値が拡張された時(現在は row/col の 2 値のみ)。

**アクション:** なし(このドキュメントの記録で L5 領分明示完了)

---

### Task B4: Stage 8 `ed.Focus()` offscreen host 検証不能 → YAGNI 推奨(seam 導入は保留)

**現状(grep 結果):**
- `src/yEdit.App/KinsokuFormatController.cs:60` — `ed.Focus();`(整形後にエディタへ復帰)
- `src/yEdit.App/MainForm.cs:465` — `ed.Focus();`(要調査:タブ切替 or ダイアログ復帰系)

**test-infra 拡張案の検討:**
- **案 A(seam 導入):** `IEditorFocusTarget` 抽出+Fake 差し替え → 2 呼び出しに対して interface 1 個・Adapter 1 個・Fake 1 個 = **50 行超のリファクタ**
- **案 B(reflection 反射):** `ed.Focus()` 呼び出しは EditorControl の実装 public method 呼び出し=reflection で観測不可(呼び出し履歴がフレームワーク側)
- **案 C(offscreen host で `Control.CanFocus`+`ContainsFocus` 観察):** offscreen HostForm の Visibility 制約で常に false = 検証不能(Stage 8 Task B レビューで既に判明)
- **案 D(受け入れ):** L5 実機で「整形直後にエディタへ戻る」「タブ切替後にエディタへ戻る」を目視確認する

**判断:** **案 D(現状維持)を推奨**。Focus() は WinForms の副作用 API で、Fake seam を挟むと今度は seam 側の実 Focus 呼び出しがテストできなくなる(**再帰的な YAGNI**)。KinsokuFormatController は cleanup Task B で 9 件テスト付き抽出済み・Focus() 呼び出し自体は 1 行の副作用で回帰リスクは低い。

**再検討トリガ:** `ed.Focus()` 呼び出しが 5 箇所以上に増えた時(=見落としリスクが線形に増える閾値)、または「Focus が奪われて戻らない」種のバグが 1 度でも観測された時。

**アクション:** なし(このドキュメントで判断記録完了)

---

### Task B5: Stage 4 `TestHost.cs` ファイル名とクラス名(HostForm)不一致 → 対応不要

**由来:** [[test-strategy]] メモ Stage 4。「意図的(集積地)」と結論済み。

**判断根拠:** `TestHost.cs` は複数の可視 Form ヘルパを集約したファイル名で、内部の `HostForm` クラスはそのうちの 1 つ。**ファイル=集積地、クラス=個別ヘルパ**の意図的分離。リネームすると集積地の意図が失われる。

**アクション:** なし(コメント追記も過剰)

---

### Task B6: 横断 `Sta.cs` 共有抽出 → 条件監視(3 プロジェクト目条件)

**現状(glob 結果):**
- `tests/yEdit.Editor.Tests/Sta.cs`
- `tests/yEdit.App.Tests/Sta.cs`
- **未該当**(現状 STA 必要は 2 プロジェクト・Core.Tests は STA 不要)

**再検討トリガ:** 3 番目の STA 必要な xUnit プロジェクトが発生した時(=DRY 3 コピー則の発火)。現時点で該当プロジェクトの計画なし。

**アクション:** なし(条件監視化完了)

---

## §D 再監査で拾った追加発見(元バックログ外・参考)

Explore で src/tests を精査した結果、[[test-strategy]] § 申し送りには載っていない **追加の観察** が 5 件見つかった。いずれも**現状で回帰は発生していない**ので本計画では実装しないが、判断根拠を残して将来の議論に備える。

### D1: `GrepResultsWindow` の ctor param 型スキャン反射テスト未実施

- **現状:** `GrepController` の 2 本(field+ctor param)は cleanup Task 10 で pin 済み。`GrepResultsWindow` は未 pin。
- **リスク:** `GrepResultsWindow(GrepResultsCallbacks callbacks)` を `Action<GrepHit>` 直渡しに戻す差戻しは、既存の Controller 反射テストでは検出できない。
- **判断:** 現状 `MainForm.cs` の唯一の生成箇所が `GrepResultsCallbacks` 経由で明示的なので、回帰確率は低い。**cleanup Task 10 の反射スタイルを踏襲した 1 テスト追加で足りる**(コスト極小)ため、Task A1 と併せて着手しても良い(判断はユーザー)。

### D2: `IGrepView.Visible` が dead property 化している

- **現状:** interface で公開されているが `GrepController` は読み書きせず、`GrepDialog` は自クラスの `Form.Visible` を直接使う。`FakeGrepView.Visible` は書換可能だが利用テストなし。cleanup Task 5 で YAGNI と結論した本人発。
- **リスク:** interface 汚染(意図と実態の乖離)。挙動リスクはゼロ。
- **判断:** **削らない**。`IFindReplaceView.Visible` との対称性が保守性を支える([[test-strategy]] Stage 4 由来の集約ポリシー)。削るなら Fake 側の Show/Hide 明示 API 化まで含む二次リファクタになり YAGNI 濃度が高い。

### D3: `ed.Focus()` 直呼び 2 箇所が `FocusTarget.Focus()` を経由しない

- **場所:** `MainForm.cs:465`(GoToLine 後)/`KinsokuFormatController.cs:60`(整形後)
- **現状:** どちらも CSV モードは先頭 guard で return しているため実害なし。ただし残 15 箇所の `FocusTarget.Focus()` パターンと非対称。
- **判断:** **統一しない**。CSV モードは実質「エディタ or シンク」の 2 択で、guard-first 方式で既に矛盾を排除している。統一のためだけに `doc.FocusTarget.Focus()` 差し替えを行うと、KinsokuFormatController は `_docs` 依存が増えて test surface が広がる(現状は `Document` パラメタ 1 個の pure controller)。§B4 と同型 YAGNI。

### D4: `WriteToPath` の catch フィルタは 4 例外種のみ(OOM 等は素通り)

- **場所:** `FileController.cs:296` の `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)`
- **現状:** cleanup Task 1(Batch A)で本文 EOL+Modified ロールバックは実装済み。ただし `OutOfMemoryException`・`ThreadAbortException`・`AccessViolationException` 等は catch されず、`snapshotBefore` へのロールバック未実行。
- **判断:** **広げない**。OOM は「捕まえて続行できる状態ではない」ため catch フィルタから除外するのが .NET 慣習。**現状の 4 例外種は「予想される I/O 失敗経路」の Fowler 準拠の絞り込み**で正しい。ロールバック網羅性の観点で拡張議論は不要。

### D5: `BeginClose()` が結果窓を明示 Close しない

- **現状:** `GrepController.BeginClose()` は `_closing=true; _cts?.Cancel();` のみ。結果窓のクローズは Owner (MainForm) の Close 連鎖に依存。
- **リスク:** MainForm 以外の Owner で `GrepController` を使うケースが将来増えたら、結果窓が孤立する可能性。
- **判断:** **手を入れない**。現行アーキテクチャで Owner は MainForm 一択・Owner 連鎖で確実に閉じる。「将来 Owner が増えたら」の仮定で API を追加するのは YAGNI。逆に explicit Close を入れると「Owner Close 中の重複 Close で ObjectDisposedException」を新たに対処する必要が出る(=負債の付け替え)。

---

## §C メモ正規化タスク(実装なし・memory 更新のみ)

### Task C1: `[[test-strategy]]` § 申し送りリストを cleanup 反映版に更新

**Files:**
- Modify: `%USERPROFILE%\.claude\projects\D--src-yEdit\memory\test-strategy.md`(§ 申し送り部分・行 38〜47 前後)

**Background:** § サマリの表(本ドキュメント冒頭)の通り、cleanup で 12 項目が実施済み・キャンセル・判断済み。test-strategy の申し送りリストがそれを反映していない状態で放置されると、次回セッションで再び「残バックログを確認」→「cleanup メモを掘る」→「本ドキュメントを掘る」の 3 段照合が必要になり、判断コストが線形に増える。

**Step 1: 現状 § 申し送り部分を確認**

```powershell
# test-strategy メモの申し送り部分(行 38 前後〜末尾)を確認
# 直接 Read で開いて該当行を特定
```

**Step 2: 申し送り部分を「実施済(cleanup で回収)」「恒久非対応(L5 領分)」「条件監視」の 3 分類に書き換え**

書き換え後の想定内容(要点):

```markdown
**申し送り(Phase 2 完了後・[[test-review-cleanup-complete]] で 12 項目回収済み・[[phase2-backlog-final-triage]] で最終トリアージ):**

- **回収済** (cleanup 12 項目): 詳細=[[test-review-cleanup-complete]]。列挙は割愛(重複防止)
- **副次発見の実施**: GrepController.Open() が `_closing` を勝手にリセットしない contract を pin(defensive・Task A1)
- **恒久非対応**(L5 領分 or YAGNI 判断): WinFormsRestorePrompt(§B2)/ CsvGoToCellDialog プレゼンター抽出(§B3)/ ed.Focus() offscreen host 検証(§B4)/ TestHost.cs 命名(§B5)
- **条件監視**(発火時のみ着手検討): BackupStore 抽象化 3 条件(§B1)/ Sta.cs 3 プロジェクト目条件(§B6)
- **CI 系(本トリアージの対象外)**: ci.yml/release.yml の初回 push 時確認・App.Tests LocalOnly 判断(継続)
```

**Step 3: `[[phase2-backlog-final-triage]]` メモを新規作成 & MEMORY.md に追記**

```markdown
---
name: phase2-backlog-final-triage
description: Phase 2 由来の恒久バックログの最終トリアージ(2026-07-17)。実装 1 件+判断記録 6 件+メモ正規化 1 件で決着。
metadata:
  type: project
---

**★決着**(2026-07-17)。詳細は `docs/plans/2026-07-17-phase2-backlog-final-triage.md`。

- **実装 1 件**: Task A1=GrepController.Open() が _closing を勝手にリセットしない contract の defensive test(cleanup Task 5 副次発見の予防的固定)
- **恒久非対応 4 件**: WinFormsRestorePrompt(単純 enum mapping・L5 領分)/ CsvGoToCellDialog プレゼンター抽出(既に ICellPicker 経由で pin 済)/ ed.Focus() 検証(seam 導入コスト>利得の YAGNI)/ TestHost.cs 命名(意図的集積地)
- **条件監視 2 件**: BackupStore 抽象化(3 条件)/ Sta.cs 共有抽出(3 プロジェクト目)
- **CI 系(対象外・継続)**: ci.yml/release.yml 初回 CI 確認・App.Tests LocalOnly 判断
```

MEMORY.md に 1 行追加:

```markdown
- [Phase 2 バックログ最終トリアージ](phase2-backlog-final-triage.md) — **★決着(2026-07-17)**。実装 1 件+恒久非対応 4+条件監視 2+メモ正規化 1。詳細=リンク先計画書
```

**Step 4: `[[test-review-cleanup-complete]]` § 「次(継続バックログ)」部分にリンクを追加**

「BackupStore 抽象化 の再検討条件」の直前 or 直後に:

```markdown
- **[[phase2-backlog-final-triage]]** = Phase 2 バックログ最終トリアージ(2026-07-17)で残項目を決着(実装 1・恒久非対応 4・条件監視 2)
```

**Step 5: commit(メモは git 管理外なので commit なし・そのまま保存)**

なし。memory ファイルは`.claude/projects/` 配下でユーザー個人領域。ただし本計画書(この docs/plans/...md)は git 管理下なので、Task A1 と抱き合わせで commit する:

```bash
git add docs/plans/2026-07-17-phase2-backlog-final-triage.md tests/yEdit.App.Tests/GrepControllerTests.cs
git commit -m "test(app): finalize Phase 2 backlog triage + Open()/_closing pin

- Task A1: GrepController.Open() が _closing を勝手にリセットしない
  contract を defensive test で pin(reflection・+1 test)
- docs/plans に最終トリアージ計画を保存(実装 1・恒久非対応 4・条件監視 2)
- ed.Focus() seam 導入/CsvGoToCellDialog プレゼンター抽出/
  WinFormsRestorePrompt テスト化 はすべて YAGNI or L5 領分で見送り
- BackupStore 抽象化と Sta.cs 共有抽出は条件監視継続

memory 側(test-strategy/test-review-cleanup-complete/新規 triage)は
別途更新済み(git 管理外)"
```

---

## 実行順序

1. **Task A1**(15 分・test +1)→ pre-merge-check → commit(§C1 と抱き合わせ)
2. **(任意)§D1 の GrepResultsWindow 反射テスト**を Task A1 と同一 commit で追加も可(コスト極小・cleanup Task 10 の反射スタイル踏襲・+1 テスト)。着手判断はユーザー
3. **Task C1**(10 分・memory 3 ファイル更新)→ 保存のみ
4. **git commit**(§A1 [+ §D1 optional] の実装 + §C1 の計画書を 1 コミット)
5. **main へ no-ff マージ**(単一ブランチ・単一コミットのため簡素)

**総所要:** 30 分〜1 時間(mutation 検証・レビュー含む・§D1 追加時 +15 分)

---

## 注意事項

- 本計画は **cleanup(2026-07-15〜16)以降に memory の申し送りリストが更新されていなかった** 事実の発見に基づく。もし将来「Phase 2 のこの項目は?」と再度問われた時は、**まず本トリアージドキュメント → [[test-review-cleanup-complete]] → [[test-strategy]]** の順で参照すれば 3 段照合の無駄が省ける
- §B の判断記録は **「今は実装しない」の記録** であり、条件発火時は独立 PR で着手可能
- L5 実機 SR 影響なし(§A1 は reflection のみ)= pre-merge-check.ps1 のみで OK
