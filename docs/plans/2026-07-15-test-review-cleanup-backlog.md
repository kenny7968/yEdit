# テストレビュー回収 残バックログ 対応計画

> **For Claude:** REQUIRED SUB-SKILL: 実行時は `superpowers:executing-plans` を使用し、タスク単位に着手↔レビューを回す。

**Goal:** テスト戦略 Phase 2 事後レビュー([[phase2-test-review-findings]])および各 Stage 由来の残バックログ(CI/CD 非関連)を、データ安全性優先の順に全件回収する。

**Architecture:** バグ修正 2 件(EOL 非ロールバック 2 種)+ テスト補強 8 件 + ミニリファクタ 3 件の計 13 タスク。src 変更を伴うのは Task 1(EOL 修正)/ Task 7(CsvCellEditor internal 化)/ Task 11(NRT クリーンアップ)/ Task 12(UiaAnnouncer 外出し)の 4 件のみ、それ以外はテスト追加または観測 seam 導入。**各タスクは独立して commit/main マージ可**(前提依存なし・順序は費用対効果とリスクの優先度)。

**Tech Stack:** .NET 9 / WinForms / xUnit / Sta.cs(同一 STA)/ TestHost / FakeGrepSearchFn 系 Fake 基盤(既存)

**上位ゲート:** 各タスク完了時に `tools/pre-merge-check.ps1` を通す(0 警告 + テスト全緑)。main マージ前必須。

**参照:**
- [[test-strategy]] — 5 層ピラミッド・レビュー標準
- [[test-review-recovery-complete]] — 直前の 5 ブランチ回収記録
- [[phase2-test-review-findings]] — レビュー元指摘
- `tests/README.md` — コピペ元の正典 + レビュー標準

---

## 対応順序と根拠

| # | タスク | 分類 | 影響 | 見積 | 由来 |
|---|--------|------|------|------|------|
| **1** | **EOL 非ロールバック修正(バグ 1+2)** | src 修正 | データ安全性 ★★★ | 中 | B3 pin ・Stage 2/3 |
| **2** | SerialBackupWriter catch→OnWriteFailed 実 I/O 統合テスト | テスト | データ安全性 ★★ | 中 | Stage 5 |
| **3** | `_writer ??=` Lazy 二重呼び非再生成テスト | テスト | 挙動固定 ★★ | 小 | Stage 5 |
| **4** | `HasBackup=false` で閉じた doc の Delete ガード被覆 | テスト | 挙動固定 ★★ | 小 | Stage 5 |
| **5** | `_view.Visible` BeginClose 経路の不変固定 | テスト | Grep 恒久沈黙予防 ★★ | 小 | Stage 7 |
| **6** | `ShowResults` 内 `_resultsView.IsDisposed` 分岐被覆 | テスト | 挙動固定 ★★ | 小 | Stage 7 |
| **7** | CsvCellEditor.Commit/CancelEdit internal 化 + F2 経路テスト | src+テスト | CSV F2 UX 保護 ★ | 中 | Stage 6 |
| **8** | CsvController 列側クランプ追加テスト | テスト | 挙動固定 ★ | 小 | Stage 6 |
| **9** | CsvController `default: throw` 経路カバレッジ完全化 | テスト | 挙動固定 ★ | 小 | Stage 6 |
| **10** | GrepController 反射テスト ctor param 型スキャン強化 | テスト | 回帰予防 ★ | 小 | Stage 8 |
| **11** | `ed.FindForm()!` NRT 契約クリーンアップ | src(注釈のみ) | 保守性 | 小 | Stage 6 |
| **12** | GrepDialog `UiaAnnouncer` 直生成の外出し | src+テスト | 保守性 | 中 | Stage 2/7 |
| **13** | `BackupStore.LoadAll/SweepTempFiles` 抽象化再評価 | 判断(≠実装) | 保守性 | 小 | Stage 5 |

**順序の根拠(Why this order):**
- **1〜2 (データ安全性)**: 静音書換とバックアップ無言停止の 2 大リスク。B3 pin は既に赤化 assertion があるので**修正コストが最も安い**。統合テストは B2 の SerialBackupWriter 基盤を再利用できる。
- **3〜6 (残 catch/guard の分岐固定)**: 既存の Fake 基盤(FakeBackupWriter/FakeGrepView)で全て書ける。個別コストは 15〜30 分程度。
- **7〜9 (CSV 保護と分岐固定)**: CSV は L5(実機 SR)で回帰検知が難しい領域なので L3 で厚めに固める。
- **10 (回帰予防)**: 反射テストの強化。単発の小改修。
- **11〜12 (保守性)**: 挙動不変・低リスク。バックグラウンドで消化。
- **13 (判断のみ)**: 実装コード変更を伴うかを一度立ち止まって判断する項目。単独では**判断 + docs 追記のみ**で終わる可能性が高い(YAGNI 判定)。

---

## Task 1: EOL 非ロールバック修正(バグ 1+2)

**Files:**
- Modify: `src/yEdit.App/FileController.cs:268-290` (WriteToPath)
- Modify: `src/yEdit.Editor/EditorControl.cs` (ConvertEols の非 fast-path 差替)または `src/yEdit.Core/Buffers/TextBuffer.cs` (`_savedRoot` 継承)
- Test: `tests/yEdit.App.Tests/FileControllerTests.cs:615/660/670`(既存 pin が赤化=そのまま「修正確認テスト」に転用)

**Step 1: 現状確認(pin 3 assertion が赤化する状態を意図的に作る前の観察)**

- `FileControllerTests.cs:615` = SaveAs 失敗時 `SnapshotText == "a\nb"`(ConvertEols(Lf)後 CRLF に戻っていない)
- `:663` = Save 失敗時 `SnapshotText == "x\r\ny"`(元の LF に戻っていない)
- `:674` = Save 失敗時 `Modified == false`(ReplaceSource で保存点破壊)

これら 3 件がバグ 1(EOL 変換の非ロールバック)/ バグ 2(TextBuffer 差替で保存点消失)の pin。

**Step 2: バグ 1(EOL 非ロールバック)修正案**

`WriteToPath` の ConvertEols を try/catch で囲み、失敗時に本文スナップショットを復元する。既存の try/catch 構造:

```csharp
private bool WriteToPath(Document doc, string path)
{
    ApplyEol(doc);
    // ★追加: ConvertEols 前スナップショット(バグ 1 対策)
    var snapshotBefore = doc.Editor.CurrentBuffer;
    try
    {
        bool wasReadOnly = doc.Editor.ReadOnly;
        if (wasReadOnly) doc.Editor.ReadOnly = false;
        try { doc.Editor.ConvertEols(doc.Editor.EolMode); }
        finally { if (wasReadOnly) doc.Editor.ReadOnly = true; }
        TextFileService.Save(path, doc.Editor.CurrentBuffer, doc.State.Encoding, doc.State.HasBom);
        doc.Editor.SetSavePoint();
        _docs.UpdateLabel(doc);
        _metaChanged();
        return true;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
    {
        // ★追加: WriteToPath 失敗時に ConvertEols 済み本文をロールバック
        doc.Editor.SetOrReplaceSource(snapshotBefore); // 差替経由は Task 1-3 で保存点継承が必要
        _prompt.Error($"保存できませんでした: {ex.Message}", "エラー");
        return false;
    }
}
```

**Step 3: バグ 2(TextBuffer 差替で保存点破壊)修正案**

現状 `SetOrReplaceSource` は新規 TextBuffer で `_savedRoot=root=Modified=false` にリセットする。修正案 A/B のいずれか:

- **案 A**: `SetOrReplaceSource(TextBuffer buffer, bool preserveSavePoint = false)` オーバーロード追加。`preserveSavePoint=true` の時のみ `_savedRoot` を旧値(元の buffer の `_savedRoot`)から継承。ロールバック経路と ConvertEols 非 fast-path の両方で `preserveSavePoint=true` を使う。
- **案 B**: `ConvertEols` を fast-path 化(EOL 変換が in-place になる場合は差替不要)。実装難度が案 A より高いので優先度は下。

**推奨=案 A(小さく安全)**。実装:
- `EditorControl.SetOrReplaceSource` のシグネチャに `bool preserveSavePoint = false` 追加(既存呼び出し側は挙動不変)。
- `TextBuffer` に `_savedRoot` を継承する内部 API(または EditorControl から `_savedRoot` を書き戻せる internal setter)を追加。

**Step 4: pin 3 件を「修正確認テスト」に転用**

- xmldoc の「★修正時に赤化」を「★修正で緑化(バグ修正後の担保)」へ言い換え。
- 期待値を反転:
  - `:615` `Assert.Equal("a\r\nb", doc.Editor.SnapshotText)`(元 CRLF に戻る)
  - `:663` `Assert.Equal("x\ny", doc.Editor.SnapshotText)`(元 LF に戻る)
  - `:674` `Assert.True(doc.Editor.Modified)`(Save 前 dirty のまま)
- テスト名末尾から `_KnownBehavior` を外し `_RollsBackContentEol` / `_KeepsModifiedFlag` に変更。

**Step 5: pre-merge-check.ps1 通過確認 → commit → main マージ**

```
git commit -m "fix(app): EOL 非ロールバック(バグ 1+2)を修正=WriteToPath 失敗時に ConvertEols 済み本文と保存点をロールバック"
```

---

## Task 2: SerialBackupWriter catch→OnWriteFailed 実 I/O 統合テスト

**Files:**
- Test: `tests/yEdit.App.Tests/SerialBackupWriterTests.cs`(B2 で新設済み・末尾に追加)

**Background:** B2 で `Enqueue` の Dispose ガードは統合テスト化済み。しかし `Write` の内部 catch(`SerialBackupWriter.cs:34` → `OnWriteFailed?.Invoke(record.Id)`)は未検証。

**Step 1: 失敗を意図的に起こす方法の確定**

`BackupStore.Write` が失敗する条件=書込先ディレクトリが存在しない/read-only。TempDir を作った上で書込先ディレクトリを削除して失敗させる。

**Step 2: テスト追加**

```csharp
[Fact]
public void Write_Failure_Invokes_OnWriteFailed_WithRecordId()
{
    // ディレクトリを削除して I/O 失敗を起こす
    using var tmp = new TempDir();
    string dir = tmp.Path;
    using var writer = new SerialBackupWriter(dir);
    Directory.Delete(dir, recursive: true); // 存在しない dir に対する書込は失敗する

    string? failedId = null;
    var doneEvent = new ManualResetEventSlim(false);
    writer.OnWriteFailed = id => { failedId = id; doneEvent.Set(); };

    var record = new BackupRecord { Id = "test-id-1", /* ... 最小フィールド ... */ };
    writer.Write(record);

    Assert.True(doneEvent.Wait(TimeSpan.FromSeconds(5)), "OnWriteFailed が呼ばれない");
    Assert.Equal("test-id-1", failedId);
}
```

**Step 3: 決定性の担保**

- `ManualResetEventSlim` で背景スレッドからの通知を待つ(sleep でリトライしない)。
- B2 と同じく 50〜55ms 目安のタイムアウト観察はしない=イベント駆動で完結。
- LocalOnly 化は不要(実 I/O は失敗させるだけで書き込みしない)。

**Step 4: pre-merge-check.ps1 → commit**

```
git commit -m "test(app): SerialBackupWriter.Write の catch→OnWriteFailed 経路を実 I/O 統合テストで固定"
```

---

## Task 3: `_writer ??=` Lazy 二重呼び非再生成テスト

**Files:**
- Test: `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`(既存に追加)

**Background:** `BackupCoordinator.cs:93` の `_writer ??= CreateWriter()`。書込タイミング 2 回目以降に `writerFactory` が呼ばれないことをテストで機械固定する(Stage 5 で `HasBackup` 側は固めたが Lazy 生成側は未検証)。

**Step 1: FakeBackupWriter の呼び出し回数観測**

`writerFactory` が Fake だが生成回数を数える wrapper に置き換え:

```csharp
int factoryCalls = 0;
Func<IBackupWriter> factory = () => { factoryCalls++; return new FakeBackupWriter(); };
```

**Step 2: テスト追加**

```csharp
[Fact]
public void Writer_IsCreated_LazilyOnce_AcrossMultipleReconciles()
{
    // dirty サイクル 3 回で writerFactory は 1 回だけ呼ばれる
    // (Reconcile 内の CreateWriter=_writer ??= のカバレッジ)
    // ...
    Assert.Equal(1, factoryCalls);
}
```

**Step 3: commit**

---

## Task 4: `HasBackup=false` で閉じた doc の Delete ガード被覆

**Files:**
- Test: `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`

**Background:** `BackupCoordinator.cs:189` `if (gone.HasBackup) _writer?.Delete(gone.Id);` と `:270` `if (info.HasBackup) _writer?.Delete(info.Id);` の 2 箇所。HasBackup=false のドキュメントを閉じたときに Delete が呼ばれないことを固定。

**Step 1: シナリオ設計**

- doc を作成 → 一度も書き込まない(Reconcile なしで close) → `_writer.DeleteCalls` が空。
- 既存 FakeBackupWriter に `List<string> DeleteCalls` があるはず(なければ追加)。

**Step 2: テスト追加**

```csharp
[Fact]
public void Close_Without_Backup_DoesNotCall_Delete()
{
    // doc 作成 → 即クローズ(HasBackup=false のまま)
    // FakeBackupWriter.DeleteCalls.Count == 0
}
```

**Step 3: commit**

---

## Task 5: `_view.Visible` BeginClose 経路の不変固定 【キャンセル】

**着手時判断=キャンセル(2026-07-15・Stage 8 D-1 Minor #1 と同じ YAGNI 判定)。**

**キャンセル理由(実装者の実装読解による)**:

- `GrepController.BeginClose()` 実装は `_closing=true; _cts?.Cancel();` の 2 行のみで、`_view` に触れない。
- `IGrepView` interface に `Hide()` メソッド無し(`ShowAndFocus` のみ)。
- 設計意図(MainForm.OnFormClosing の実装から): BeginClose は「結果窓抑止フラグ」であって「ダイアログ閉じ」ではない。**終了確認カスケード中も grep ダイアログは意図的に visible のまま**(終了取消なら `CancelClose` で `_closing=false` に戻す)。
- Stage 8 D-1 Minor #1 レビューで同型の `Cancel_DoesNotChangeViewVisibility` が「trivially true=coverage 0」として削除済み。BeginClose 版も同様に coverage 0。

**将来 IGrepView に Hide/BeginClose メソッドが追加された時点で、defensive テストの再検討候補としてバックログに残す。**

**副次発見(別 backlog 候補・未着手)**:
- `Open()` が `_closing` フラグをリセットしない implicit contract(復帰経路は `CancelClose` のみ)。将来 `Open()` に `_closing=false;` を紛れ込ませる回帰を pin するテストは valuable だが、正常フローで踏まれないため優先度低。

**Step 1: FakeGrepView の Visible/Hide 対応確認**

`IGrepView` に `Visible` プロパティと `BeginClose()` があるはず。BeginClose 後に Visible=false になることを FakeGrepView 側で担保。

**Step 2: テスト追加**

```csharp
[Fact]
public async Task BeginClose_HidesView_And_PreventsFurtherOpen()
{
    // Open → BeginClose → View.Visible == false
    // 追加 Open() 呼び出しでも Visible=true にならないことも観察(冪等性)
}
```

**Step 3: commit**

---

## Task 6: `ShowResults` 内 `_resultsView.IsDisposed` 分岐被覆

**Files:**
- Modify: 現状の `GrepController.cs:148` の分岐は `_resultsView is null || _resultsView.IsDisposed`。テストで両方の枝を被覆する。
- Test: `tests/yEdit.App.Tests/GrepControllerTests.cs`

**Background:** Stage 7 由来。`_resultsView is null` 側は既存テストで踏んでいるはず。`_resultsView.IsDisposed` 側は未被覆。

**Step 1: FakeGrepResultsView に `IsDisposed` プロパティ確認**

無ければ追加(get-only + テスト側から setter 経由で true にする)。

**Step 2: テスト追加**

```csharp
[Fact]
public async Task ShowResults_RecreatesView_When_PreviousDisposed()
{
    // 1回目 Grep 実行 → resultsView 生成
    // FakeGrepResultsView.IsDisposed = true にする(手動 dispose 相当)
    // 2回目 Grep 実行 → factory が再度呼ばれる(新規 view 生成)
}
```

**Step 3: commit**

---

## Task 7: CsvCellEditor.Commit/CancelEdit internal 化 + F2 経路テスト

**Files:**
- Modify: `src/yEdit.App/CsvCellEditor.cs`(public → internal + `InternalsVisibleTo` は既存済み)
- Test: `tests/yEdit.App.Tests/CsvCellEditorTests.cs`(新設)または既存 CsvControllerTests に追加

**Background:** Stage 6 由来。F2 編集経路(BeginEdit → onCommit → 反映 / onCancel → 破棄)が L3 で未検証。

**Step 1: シグネチャ変更**

現在 `public void Begin(EditorControl ed, ...)` を `internal void Begin(...)` に(呼び出し元 CsvController も App 内なので影響なし)。

**Step 2: テスト設計**

CsvCellEditor は EditorControl と `Control refocusTarget` を受け取るので、TestHost で HostForm+EditorControl を用意して直接ドライブする。

```csharp
[Fact]
public void Commit_Applies_ValueToCurrentCell_AndRefocuses()
{
    // HostForm + EditorControl + CSV mode 起動
    // BeginEdit → onCommit("new value") → cell に "new value" が入る
    // refocusTarget にフォーカスが戻る
}

[Fact]
public void CancelEdit_DiscardsChanges_AndRefocuses()
{
    // BeginEdit → onCancel → cell は元の値のまま + refocus
}
```

**Step 3: commit**

---

## Task 8: CsvController 列側の範囲外(OutOfRange 通知)テスト

**Files:**
- Test: `tests/yEdit.App.Tests/CsvControllerTests.cs`

**着手時 pivot(2026-07-15)**: タイトル/背景は当初「列側クランプ」を premise としていたが、実装読解(`src/yEdit.Core/Csv/CsvDocument.cs:111-112`)で **GoTo は範囲外で null 返し=クランプせず OutOfRange 通知** であることが判明。テスト名から「Clamp」を外し、実挙動(`GoToCell_ColumnBeyondMax_...` / `GoToCell_NegativeColumn_...`)と一致した pin に変更した。計画本文 Step 1 で「現状の GoToCell の挙動を先に読んで期待値を決める」と自己補正していた通りの流れ。既存 `GoToCell_OutOfRange_AnnouncesOutOfRange_NoChange`(`Ok(99,99)`)は行+列両方 OOR で列単独 mutation を kill できない=真の被覆穴を埋めた。

**Background:** Stage 6 由来。行側クランプは既存 1 件あるが、列側(GoToCell に負の値/巨大値)のテストが不足。

**Step 1: テスト追加**

```csharp
[Fact]
public void GoToCell_ColumnBeyondMax_ClampsToLastColumn()
{
    // col=9999 → 実カラム数-1 にクランプ + 端メッセージ(?)
}

[Fact]
public void GoToCell_ColumnNegative_ClampsToZero()
{
    // col=-1 → 0 にクランプ(または InvalidFormat 通知?)
}
```

現状の GoToCell の挙動を先に読んでから期待値を決める(Ok 3 分岐 record + クランプ規則の再確認)。

**Step 2: commit**

---

## Task 9: CsvController `default: throw` 経路カバレッジ完全化

**Files:**
- Test: `tests/yEdit.App.Tests/CsvControllerTests.cs`

**Background:** Stage 6 由来。`CsvController.cs:143` に `default:`(GoToCell の switch)。カバレッジ完全化=既存の 3 相 record(Canceled/InvalidFormat/Ok)以外を返す **不正な** ICellPicker を作れば default に落とせる。

**Step 1: FakeCellPicker の拡張**

現在の CsvControllerTests で使う FakeCellPicker に「未知の Kind を返す」オプションを追加。または一時的な NullPicker を作る:

```csharp
private sealed class UnknownKindPicker : ICellPicker
{
    public CellPickResult Pick(IWin32Window owner, int row, int col)
        => new CellPickResult { Kind = (CellPickResultKind)99 }; // enum 未定義値
}
```

**Step 2: テスト追加**

```csharp
[Fact]
public void GoToCell_UnknownResultKind_Throws()
{
    // UnknownKindPicker を注入
    // Assert.Throws<InvalidOperationException>(() => host.Csv.GoToCell());
    // (default: throw の例外型に合わせる)
}
```

**Step 3: commit**

---

## Task 10: GrepController 反射テスト ctor param 型スキャン強化

**Files:**
- Test: `tests/yEdit.App.Tests/GrepControllerTests.cs`

**着手時 pivot(2026-07-16)**: 計画本文の例示コードは `Action<GrepMatch>` を pin していたが、実 API は `GrepHit`(Stage 7〜 の実装で `GrepMatch` 型は存在しない)。実装は `Action<GrepHit>` に pivot(Batch A/B/C の教訓「計画の premise を鵜呑みにしない」に整合)。既存 field 反射テスト(Stage 8 由来)と対で ctor 経路の閉じ込め回帰を機械検出する目的は不変。

**Background:** Stage 8 C 由来。現状の反射テスト(`_jumpTo` が field に含まれないこと)は field のみを見ている。ctor 経由の閉じ込め回帰(ctor 引数に `Action<GrepMatch>` 等が復活する)を検出できない。

**Step 1: 反射クエリの追加**

```csharp
[Fact]
public void GrepController_Ctor_DoesNotAccept_LegacyJumpToDelegate()
{
    var ctor = typeof(GrepController).GetConstructors()[0];
    var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();
    Assert.DoesNotContain(paramTypes, t => t == typeof(Action<GrepMatch>));
    // 併せて resultsFactory が Func<IGrepResultsView> のままであることも固定
    Assert.Contains(paramTypes, t => t == typeof(Func<IGrepResultsView>));
}
```

**Step 2: commit**

---

## Task 11: `ed.FindForm()!` NRT 契約クリーンアップ

**Files:**
- Modify: `src/yEdit.App/CsvController.cs:130`

**Background:** Stage 6 由来。`ed.FindForm()!` の `!` は NRT 抑止だが、`FindForm()` が null を返す条件(Editor がまだ Form に載っていない=起動直後や BeginEdit の呼び出し文脈で理論上起こりうる)は実装で保証されている。契約を明示化する:

**Step 1: 選択肢 A=xmldoc/コメントで意図を明記**

```csharp
// ed は BeginEdit 契機で MainForm 上に既に載っているため FindForm() は非 null。
// null になれば CSV モードそのものが未進入=BeginEdit まで到達しない前提。
var owner = ed.FindForm()!;
```

**Step 2: 選択肢 B=ヘルパで契約集中化**

`private static Form OwnerFormOf(EditorControl ed) => ed.FindForm() ?? throw new InvalidOperationException("EditorControl is not hosted in a Form");`

**推奨=B**(挙動不変・null 時に明示例外・テストでも `Assert.Throws` が書ける)。

**Step 3: commit(挙動不変・0 警告確認のみ)**

---

## Task 12: GrepDialog `UiaAnnouncer` 直生成の外出し

**Files:**
- Modify: `src/yEdit.App/GrepDialog.cs:41`
- Modify: `src/yEdit.App/MainForm.cs`(GrepDialog ctor に IAnnouncer を渡す配線)
- Test: `tests/yEdit.App.Tests/GrepControllerTests.cs`(既存 FakeAnnouncer で発声内容を assert 可能に)

**Background:** Stage 2/7 由来。GrepDialog が `new UiaAnnouncer(_status)` を直生成しており、GrepController テストからは発声を観測できない。

**Step 1: GrepDialog に IAnnouncer 注入**

```csharp
// 現状
public GrepDialog(...)
{
    _announcer = new UiaAnnouncer(_status);
}

// After
public GrepDialog(IAnnouncer announcer, ...)
{
    _announcer = announcer;
}
```

**Step 2: MainForm 配線**

MainForm が `_announcer`(既に UiaAnnouncer を生成)を GrepDialog に渡す。既存 SearchController パターンと同型。

**Step 3: テスト追加**

- GrepController の Open テストで FakeAnnouncer が受け取った文言を検証(空検索・0 件検索・キャンセルなどのメッセージ)。

**Step 4: commit**

---

## Task 13: `BackupStore.LoadAll/SweepTempFiles` 抽象化再評価(判断のみ)

**Files:**
- Read: `src/yEdit.Core/Backup/BackupStore.cs`
- Decision doc(必要なら): `docs/plans/2026-07-15-test-review-cleanup-backlog.md`(本ドキュメント)の末尾に結論を追記

**Background:** Stage 5 由来。BackupStore.LoadAll/SweepTempFiles は現状 static。IBackupStore 抽象化してテスト容易にする案があるが、変更コストと得られる価値のトレードオフの再評価が必要。

**Step 1: 現状スキャン**

- LoadAll/SweepTempFiles の呼び出し元(BackupCoordinator/RestoreDialog 等)を洗う。
- 現状 L4/L5 でカバーされているか、L3 でシーム化するとどれだけテストが増えるか。

**Step 2: 判断**

- **抽象化を採用**: IBackupStore + Fake で BackupCoordinator の LoadAll/Sweep 経由の分岐を L3 でテスト。
- **見送り**: 現状 L4/L5 で十分固まっており、抽象化コスト > 追加テスト価値。

**Step 3: 判断結果を本ファイル末尾に追記(採用/見送りとその理由)、必要なら別タスク化して backlog を更新**

**Step 4: 判断のみで src 変更なしなら commit 不要**

### 判断結果(2026-07-16)

**判断**: **見送り**(継続バックログとして条件付きで再検討可能)

**根拠**:

- **現状の呼び出し元数**: **src 内で 2 箇所のみ**(`BackupCoordinator.OfferRestoreOnStartup` の line 113=`SweepTempFiles(_dir)`・line 116=`LoadAll(_dir)`)。`MainForm.cs` から直接呼ぶ経路はゼロ(Stage 5 で既に `BackupCoordinator` 経由に集約済み)。テストからは Core=6 箇所・App=5 箇所で `BackupStore.LoadAll` を直呼び。

- **現状 L2 (Core) で cover 済みの分岐**:
  - `BackupStoreTests`(6 件)が `LoadAll`/`SweepTempFiles` の I/O 契約を実 I/O で網羅:
    - Write→LoadAll round-trip・untitled(null path)・overwrite atomicity(`.tmp` 残骸なし=SweepTempFiles 相当を含意)・Delete/DeleteAll(内部で SweepTempFiles を呼ぶ)・corrupt-file skip・missing-dir empty
  - `LoadAll` の `!Directory.Exists → return []` と loop 内 `catch { }`(破損 JSON スキップ)は L2 で kill 済み。

- **現状 L3 (App.Tests) で cover 済みの分岐**:
  - `BackupCoordinatorTests` の `OfferRestoreOnStartup` 系 8 件が**実 `BackupStore` + 実 tempdir** で 4+2 分岐を網羅:
    - disabled early-return・no-records 早期 return・confirm=false 全復元(件数戻し)・confirm=false で 1 件不正の巻き添えなし・confirm=true Restore(Id 引継ぎ)・confirm=true 1 件不正の巻き添えなし・DiscardAll・Later
  - `PlantBackup` ヘルパで `BackupStore.Write` を直呼びして LoadAll に実ファイルを読ませる=static のままでも配線分岐は完全に固定できている。

- **現状 L3 で cover 不能な分岐(=抽象化して初めて cover 可能)**:
  1. `BackupCoordinator.cs:113` の `try { SweepTempFiles }; catch { /* 無害 */ }` の catch(実 I/O 失敗の防波堤)
  2. `BackupCoordinator.cs:117` の `catch { return 0; }` (LoadAll が例外を throw した場合の防波堤=OfferRestoreOnStartup が 0 で早期 return)

  いずれも **「復元提案がその 1 回スキップされるだけ・データ喪失なし」の低優先度防波堤 catch**。実 I/O では deterministic に例外を発生させにくく、現状 L4/L5 でも踏んでいない。

- **変更コスト見積**:
  - `IBackupStore` interface(5 メソッド: Write/LoadAll/Delete/DeleteAll/SweepTempFiles)追加 ≒ 15 行
  - `BackupStore` static → instance 化(既存 static は互換 shim として残すか全置換)= 呼び出し元の書換 12 箇所以上(Core Tests 6+App Tests 5+src 2+SerialBackupWriter 経由の間接依存)
  - `BackupCoordinator` の DI ワイヤリング更新(ctor 引数追加・`_dir` の扱い変更)= 数行
  - `FakeBackupStore` 新設 ≒ 30 行
  - `MainForm` ctor で `IBackupStore` 生成/注入 ≒ 数行
  - 既存テスト(11 箇所)の書き換え=既存の `PlantBackup` パターンとの互換維持工数
  - **総計 100〜150 行の src+テスト変更**

- **期待テスト増分**: **2 件のみ**(SweepTempFiles 失敗の catch kill + LoadAll 失敗の catch kill)。しかも防波堤 catch=データ安全性への寄与は小。

**結論の理由**: 現状 Core L2(6 件)+App L3(8 件)で LoadAll/SweepTempFiles の**契約側は実 I/O で完全に固定済み**、配線側も実 `PlantBackup` で網羅済み。抽象化して踏めるのは低優先度の防波堤 catch 2 経路のみで、100〜150 行の変更コストに見合わない(Task 5 と同型の YAGNI 判定=Batch D 学び「実装読解で YAGNI 判定が正しいこともある」に該当)。

**継続バックログ**:
- **抽象化を将来検討する条件**(いずれか成立時):
  1. `OfferRestoreOnStartup` に**新規分岐が追加**され、L4/L5 で回帰観測が発生した場合
  2. `BackupStore` に**副作用のある新規 API**(例: 遠隔バックエンド連携=OneNote 連携等)が追加され、単体で mock したい必然性が生まれた場合
  3. `BackupCoordinator.cs:113/117` の catch を実際に踏む回帰が本番で観測された場合(現状は防波堤で無害)
- **再検討時の scope**: `LoadAll`/`SweepTempFiles` の 2 メソッドだけの限定抽象化(`Write`/`Delete`/`DeleteAll` は `IBackupWriter` 経由で既に注入化済み=Stage 5)で十分。5 メソッドまとめて interface 化する必要はない。

---

## 全体スケジュール(推奨)

**Batch A(データ安全性・優先着手)** = Task 1〜2

**Batch B(catch/guard 分岐固定)** = Task 3〜6

**Batch C(CSV 保護)** = Task 7〜9

**Batch D(保守性)** = Task 10〜12

**Batch E(判断)** = Task 13

各 Batch は独立に着手/main マージ可能。1 Batch = 1 ブランチを推奨(B1〜B5 と同型)。

## 完了条件

- 全 13 タスク完了 → `tools/pre-merge-check.ps1` 全緑 → main マージ
- テスト数見込み: 954 + 10〜15 件 = **964〜970 前後**
- src 変更は Task 1(EOL 修正・2 ファイル)/ Task 7(CsvCellEditor internal 化)/ Task 11(NRT ヘルパ)/ Task 12(UiaAnnouncer 注入)の **4 タスクのみ**
- 挙動変更は Task 1(バグ修正)+Task 11(null 時例外化=実質不変)のみ。**L5 は Task 12(GrepDialog 発声経路の配線変更)のみで軽くドライブすれば十分**
