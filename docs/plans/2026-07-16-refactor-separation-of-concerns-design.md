# 責務分離リファクタリング 設計書

- 日付: 2026-07-16
- 発端: 本セッションのプロジェクトレビュー(技術スタック/アーキテクチャ堅牢性/責務分離)で挙げた 6 項目の弱点
- 前提: main(`2635346` Phase 3 完了時点)・954 テスト緑・0 警告

## 0. スコープと方針

前ターンのレビューで挙げた「責務分離が弱い」6 項目を、**挙動不変**を前提に段階的に解消する。全項目を対象とし、EditorControl 3396 行の分割を本命に据えつつ、先行 Phase で中小 5 項目を清算して足場を固める。

### 対象 6 項目(レビューからの棚卸し)

| # | 項目 | ファイル/場所 | 深刻度 |
|---|---|---|---|
| a | MainForm の `null!` Controller 6 個 | `MainForm.cs:15-20` | 低(readonly 化で構造改善) |
| b | BackupCoordinator の `catch { }` 4 箇所 silent | `BackupCoordinator.cs:113,117,134,155` | 中(診断困難) |
| c | 旧 `TextFileService.Save(string, string, …)` の三重コピー | `TextFileService.cs:273-313` | 中(大容量で OOM 要因) |
| d | CsvController の `Editor.SnapshotText` 全文字列化 | `Document.cs:38-47` | 中(1GB CSV で OOM リスク) |
| e | DocumentManager の Action/EventHandler 混在 | `DocumentManager.cs:32` | 低(可読性のみ) |
| f | **EditorControl.cs 3396 行の God クラス** | `EditorControl.cs` 全体 | 高(責務分離の主負債) |

### 全体方針

- **挙動不変(shape-preserving)** を全 Phase の絶対条件とする。テスト緑を維持することが成功条件。
- **段階マージ**([[phase-work-git-flow]])と **別エージェント review**([[review-by-separate-agent]])を全サブ Phase で遵守。
- **ローカルゲート** `tools/pre-merge-check.ps1` を main マージ前必須。
- **L5 実機検証(NVDA/PC-Talker)は SR 経路が変わる Phase だけ**(Phase 1b・2d・3a・3d の 4 回想定)。
- **ロールバック粒度はサブ Phase 単位**(独立ブランチで作業)。

## 1. 全体ロードマップ

```
Phase 1: 中小5項目の一括処理(先行足場固め)
  ├─ 1a: MainForm null! Controller → readonly
  ├─ 1b: BackupCoordinator catch{} に診断導線
  ├─ 1c: TextFileService 旧 Save(string,…) 集約
  ├─ 1d: CsvParser.Parse(TextSnapshot) 追加+SnapshotText 撤廃
  └─ 1e: DocumentManager Action/EventHandler 統一 or ドキュメント補強

Phase 2: EditorControl の partial 物理分割(見通し確保)
  ├─ 2a: EditorControl.Ime.cs
  ├─ 2b: EditorControl.Caret.cs
  ├─ 2c: EditorControl.Input.cs
  ├─ 2d: EditorControl.Uia.cs
  └─ 2e: EditorControl.Paint.cs

Phase 3: Controller 委譲(本命の責務移譲)
  ├─ 3a: ImeController 抽出
  ├─ 3b: CaretController 抽出
  ├─ 3c: InputRouter 抽出
  └─ 3d: UiaTextHostAdapter 抽出
```

**サブ Phase 総数 = 14**。想定期間は 3〜5 週間規模(1 サブ Phase = 1〜2 日 + レビュー + マージ)。

## 2. Phase 1 詳細: 中小5項目

各サブ Phase = 独立ブランチ / 独立 PR / 別エージェント review / main へ no-ff マージ。

### 2.1 Task 1a: MainForm の null! Controller → readonly

- **現状**: `_file`/`_search`/`_grep`/`_backup`/`_csv`/`_kinsoku` が全て `= null!` で宣言され、ctor 本体で代入(`MainForm.cs:15-20`)。分岐 ctor(`MainForm(AppSettings)` → `MainForm(AppSettings, string)`)のため readonly を諦めている。
- **対応**: 内側 ctor で全 Controller を初期化リスト形式で組み立てて `readonly` 化。ctor chain は据え置き。
- **変更範囲**: `MainForm.cs` のみ
- **テスト**: 既存の `MainFormSmokeTests` 緑継続
- **L5**: 不要
- **リスク**: 極小(定義済み代入の順序保証のみ)

### 2.2 Task 1b: BackupCoordinator の catch{} に診断導線

- **現状**: `BackupCoordinator.cs:113,117,134,155` で SweepTempFiles / LoadAll / restore 例外 3 種が silent。診断困難。
- **対応**:
  - `yEdit.App/Abstractions/IBackupTraceSink.cs` を新設。`Warn(string category, string detail, Exception? ex)` の 1 メソッド。
  - 既定実装 `DebugBackupTraceSink` = `System.Diagnostics.Trace.TraceWarning` 出力。
  - `BackupCoordinator` の ctor に追加引数を1個入れ、既存 ctor は既定を渡す chain。
  - 4 箇所の catch を `catch (Exception ex) { _trace.Warn("...", "...", ex); }` に。
  - `SerialBackupWriter.OnWriteFailed` は UI 動作用の contract なのでそのまま残す。
- **変更範囲**: `BackupCoordinator.cs`, `Abstractions/IBackupTraceSink.cs`(新規), `MainForm.cs`(注入配線)
- **テスト**: `App.Tests/BackupCoordinatorTests.cs` に FakeTraceSink を追加し、4 catch 経路が Trace に流れることを assert。
- **L5**: 必要(バックアップ経路は本番運用の生死に関わる=T診断強化が誤って挙動を変えないことの実機確認)
- **リスク**: 低。Trace 出力は既定で無害。

### 2.3 Task 1c: 旧 `TextFileService.Save(string, string, …)` を Buffer 版に集約

- **現状**: `TextFileService.cs:273-313` の `Save(path, string text, Encoding, hasBom)` は `text → body(byte[]) → payload(byte[])` の 2〜3 倍メモリ。`Save(path, TextBuffer, …)`(`TextFileService.cs:324-386`)は stream 化済。
- **対応**:
  - 旧版の呼び出し元を全 grep で列挙。
  - 全て Buffer 版に移行。
  - 旧版は `internal` に格下げ(Tests 用に残置)、または Buffer 版の共有違反 fallback 内(`TextFileService.cs:380-385`)からのみ呼ばれる限定用途に閉じる。
  - 呼び出し元が既に共有違反 fallback だけなら、旧版は現状のままで良い(判定は 1c 着手時)。
- **変更範囲**: `TextFileService.cs`, 呼び出し元(grep で特定)
- **テスト**: `Core.Tests/Text/TextFileServiceTests` に既存 byte 検証テストが Buffer 版でも通ることを確認。
- **L5**: 不要
- **リスク**: 中(呼び出し元の切替漏れで挙動差異)。着手時に呼び出し元 grep 結果を PR description に明示。

### 2.4 Task 1d: CsvParser.Parse(TextSnapshot) オーバーロード + Document.ParseCsv 全文字列化撤廃

- **現状**: `Document.cs:38-47` が `Editor.SnapshotText`(全文字列化)を呼ぶ。1GB CSV で OOM リスク。
- **対応**:
  - `CsvParser.Parse(TextSnapshot)` オーバーロードを追加。実装は `SnapshotReader` で chunk 読みして既存の状態機械に流す。既存 string 版はそのままテスト互換のため残す。
  - `Document.ParseCsv()` は `Editor.CurrentBuffer.Current`(TextSnapshot)を渡す。
  - 参照同一性判定を `TextSnapshot.Root` の `ReferenceEquals` に変更(PieceTree の immutable ルートで一意性保証)。
- **変更範囲**: `CsvParser.cs`, `Document.cs`, `Core.Tests/Csv/CsvParserTests.cs`
- **テスト**:
  - 既存 string 版テストを TextSnapshot 版でも通す(パラメータ化)。
  - 大容量(数百 MB 相当)の chunk 境界テスト(quoted field が chunk 境界をまたぐケース)。
- **L5**: 不要(CSV パースは SR 経路に触れない・CsvAnnounceFormatter の入力型は不変)
- **リスク**: 中(chunk 境界での quoted field 分断バグ)。テストで根絶する。

### 2.5 Task 1e: DocumentManager Action/EventHandler 混在

- **現状**: `BeforeActiveChange` のみ `Action?`(sender/args なし・`DocumentManager.cs:32`)、他 7 個は `EventHandler[<T>]`。
- **対応**: 2 案から選ぶ(1e 着手時に別途確定):
  - **案 A(YAGNI 寄り)**: `BeforeActiveChange` の xmldoc に「意図的例外」と明記済み。追加ドキュメントのみで完了。実質何もしない。
  - **案 B(統一寄り)**: `BeforeActiveChange` を `EventHandler` に統一(空 EventArgs)。購読側 = `MainForm.cs:93` と `DocumentManager` 内部発火 4 箇所を書き換え。
- **判断基準**: 案 A で十分な理由(意図の xmldoc あり・sender/args を使わない)が既に整っているため既定は案 A。案 B は「他の event と揃える」以外の益がない。着手時に最終判断。
- **変更範囲**: `DocumentManager.cs`(案 A)、または + `MainForm.cs`(案 B)
- **L5**: 不要
- **リスク**: 極小

## 3. Phase 2 詳細: EditorControl partial 物理分割

各サブ Phase:
- `EditorControl.<Aspect>.cs` に partial として切り出し。
- private field は EditorControl.cs 側に残置(共有継続 = Phase 3 の宿題)。
- 挙動不変・テスト緑を継続。
- 全 `Editor.Tests`(現在 218 テスト)緑を DoD。

| サブ | 抽出対象 | 行数目安 | L5 |
|---|---|---|---|
| 2a `EditorControl.Ime.cs` | `WndProc` の WM_IME_* 分岐, `OnImeStart/Composition/End/SetContext`, `ApplyResult`, `ApplyComposition`, `ReadImeString/Bytes/Int`, `CancelCompositionAndDefault`, `NotifyCandidateWindow`, `NotifyCompositionFont`, `__Test*/__Smoke*`, `DrawImeOverlay`(暫定・2e で最終位置決定) | ~500 | 不要 |
| 2b `EditorControl.Caret.cs` | `SetCaretCharOffset`, `MoveCaretWith*`, `MoveCaretCharOffset`, `SelectCharRange`, `SetSelection*`, `GetSelectionCharRange`, `SnapAndClamp`, `PositionCaret`, `ComputeCaretPoint`, `BringCaretIntoView`, `EnsureVisibleCharRange`, `GoToLine`, `CurrentLine`, `GetColumn` | ~450 | 不要 |
| 2c `EditorControl.Input.cs` | `OnKeyDown`, `OnKeyPress`, `IsInputKey`, `OnMouseWheel/Down/Move/Up/DoubleClick`, `OffsetFromClientPoint`, `PrevWordBoundary`, `NextWordBoundary`, `InsertConfirmedText` | ~600 | 不要 |
| 2d `EditorControl.Uia.cs` | `IUiaTextHost` 全実装(`GetTextRange`/`TextLength`/`GetSelection`/`SetSelection`/`Next/PrevChar`/`LineStart/End`/`Word*`/`GetBoundingRectangles`/`OffsetFromScreenPoint`), `TryFindVisualSegment(Core)`, `_bufferSnapshot`/`_bounds` 関連, `CacheSnapshot`, `UpdateBoundsCache`, WM_GETOBJECT 分岐 | ~350 | 不要(実装は動かず配置のみ) |
| 2e `EditorControl.Paint.cs` | `OnPaint`, `RenderFrame`, `MeasureLineNumberWidth`, `ToColor`, `DefaultStyle`, `BuildStyle`, `BlendRgb`, `FromRgb`, `DrawImeOverlay`(2a と共有 = 2e で最終管理) | ~250 | 不要 |

**分割後の `EditorControl.cs`**: ctor + Buffer 配線 + `SetSource`/`ReplaceSource`/`AfterEdit` + `Undo`/`Redo`/`Copy`/`Cut`/`Paste`/`SelectAll` + `ApplyAppearance` + ハンドル/リサイズ系ライフサイクル + `HighlightCharRange`/`ClearHighlight` + `ConvertEols`。~800 行想定。

## 4. Phase 3 詳細: Controller 委譲

### 4.1 Task 3a: ImeController 抽出

- **切り出し先**: `yEdit.Editor/ImeController.cs`(WinForms 依存を含むため Editor 層)
- **状態機械**: 既存の `ImeCompositionState`(Core・純粋)はそのまま活用。
- **API 案**:
  ```csharp
  internal sealed class ImeController {
      public bool IsActive { get; }
      public void OnStartComposition(int caretPos);
      public void OnComposition(long gcsFlags, IImeContext ctx);
      public void OnEndComposition();
      public void OnSetContext(ref Message m);
      public void Cancel();
      public void Draw(Graphics g, IImeOverlayHost host);
  }
  ```
- **配線**: EditorControl は ctor で `_ime = new ImeController(this)`。`WndProc` から `_ime.OnComposition(m.LParam.ToInt64(), new WinImeContext(Handle))` などディスパッチのみ。
- **`IImeContext` seam**: `ImmGetContext`/`ImmGetCompositionString*`/`ImmReleaseContext`/`ImmSetCandidateWindow` などをラップした interface。Fake で pure テスト可能に。
- **テスト**: `Editor.Tests/ImeControllerTests.cs`(Fake `IImeContext` で状態遷移・overlay 描画順序・キャンセル・GCS_RESULTSTR/GCS_COMPSTR 分岐を網羅)。既存 `EditorControlImeTests.cs` の一部を移植。
- **L5**: 必要(NVDA 実機で IME 変換読み・確定読み・overlay 描画が不変であること)

### 4.2 Task 3b: CaretController 抽出

- **切り出し先**: `yEdit.Editor/CaretController.cs`
- **所有権**: `_caret`/`_anchor`/`_desiredXpx` を CaretController が保持。
- **API 案**:
  ```csharp
  internal sealed class CaretController {
      public int Caret { get; }
      public int Anchor { get; }
      public int DesiredXpx { get; set; }
      public (int Start, int End) Selection { get; }
      public void MoveTo(int newPos, bool extend, TextSnapshot snap, ICharMetrics metrics);
      public void SetTo(int pos, TextSnapshot snap);
      public void SetSelection(int anchor, int caret, TextSnapshot snap);
      public int SnapAndClamp(int offset, TextSnapshot snap);
  }
  ```
- **配線**: EditorControl は `_caret.Caret` を参照して描画/UIA へ流す。
- **テスト**: `Editor.Tests/CaretControllerTests.cs`。既存 `AnchorSelectionTests`/`KeyboardNavigationTests` の pure ロジック部分を移植。
- **L5**: 不要(SR 経路不変・キャレット位置計算は EditorControl から Controller に写るだけ)

### 4.3 Task 3c: InputRouter 抽出

- **切り出し先**: `yEdit.Editor/InputRouter.cs`
- **手法**: `CsvCommands.ByKey` パターン踏襲 = `Dictionary<Keys, Action<InputContext>>` でキーマッピングを外部化。
- **API 案**:
  ```csharp
  internal sealed class InputRouter {
      public bool Route(KeyEventArgs e);
      public bool Route(MouseEventArgs e, MouseEventKind kind);
      public void RegisterKey(Keys keys, Action handler);
  }
  ```
- **配線**: EditorControl の `OnKeyDown/OnMouseDown` などは `if (_input.Route(e)) return;` のみ、default ハンドラ群は `InputRouter` 内へ移動。
- **テスト**: `Editor.Tests/InputRouterTests.cs`。既存 `KeyboardNavigationTests`/`MouseInputTests` の一部を移植。
- **L5**: 不要

### 4.4 Task 3d: UiaTextHostAdapter 抽出

- **切り出し先**: `yEdit.Editor/UiaTextHostAdapter.cs`
- **設計判断**: EditorControl は `IUiaTextHost` 実装を止め、`_uiaAdapter` に委譲。`TextControlProviderV2` は EditorControl の代わりに `_uiaAdapter` を host として受け取る(`EditorControl.cs:1432` の `new TextControlProviderV2(this)` を `new TextControlProviderV2(_uiaAdapter)` に変更)。
- **所有権**: `_bufferSnapshot`/`_bounds`/`_boundsSync`/`_lastLineSegs` を Adapter に移す。EditorControl から snapshot 更新を通知するイベント経路のみ残す:
  - `AfterEdit`/`SetSource`/`ReplaceSource` から `_uiaAdapter.OnSnapshotChanged(newSnap)` を呼ぶ。
  - `OnHandleCreated`/`OnHandleDestroyed`/`OnSizeChanged`/`OnLocationChanged` から `_uiaAdapter.OnBoundsChanged(...)` を呼ぶ。
- **`SnapshotText` プロパティの扱い**: EditorControl 側で使う `SnapshotText`(`EditorControl.cs:284`)は `_buffer.Current` から直接取り、Adapter 経由にしない(内部消費なので設計を分ける)。
- **テスト**: `Editor.Tests/UiaTextHostAdapterTests.cs`。既存 `EditorControlUiaHostTests`(305 行) の大部分を移植。
- **L5**: 必要(NVDA/PC-Talker 実機で読み・カーソル移動読み・UIA 通知の 3 経路が不変であること)

## 5. 分割後のクラス構成

Phase 3 完了時の Editor 層クラス構成(想定):

```
yEdit.Editor/
├── EditorControl.cs          (~600 行) Control ライフサイクル + Buffer 配線
├── EditorControl.Ime.cs      (~50 行)  WndProc IME 分岐のみ (ImeController に委譲)
├── EditorControl.Caret.cs    (~30 行)  外部公開キャレット API(Controller 委譲)
├── EditorControl.Input.cs    (~30 行)  OnKey*/OnMouse* オーバーライドのみ (Router 委譲)
├── EditorControl.Uia.cs      (~50 行)  WM_GETOBJECT 分岐 + Adapter 生成
├── EditorControl.Paint.cs    (~250 行) OnPaint + RenderFrame(残存)
├── ImeController.cs          (~500 行) IME 状態機械 + Message pump アダプタ
├── CaretController.cs        (~400 行) Caret/Selection 状態と操作
├── InputRouter.cs            (~500 行) キーマップ + マウスディスパッチ
├── UiaTextHostAdapter.cs     (~350 行) IUiaTextHost 実装 + RPC スレッド安全 cache
├── GdiCharMetrics.cs         (現状維持)
└── NativeMethods.cs          (現状維持)
```

EditorControl 総行数は 3396 → ~1000(3.4x 短縮)。Controller 4 個で ~1750。テスト可能な小クラスに分割された結果として合計行数はやや増える(重複コードの分離コスト)。

## 6. 全体運用

| 項目 | 方針 |
|---|---|
| ブランチ | サブ Phase ごとに `feature/refactor-<phase>-<sub>` |
| PR | サブ Phase ごとに 1 PR(GitHub 上)、または no-ff マージのみ |
| レビュー | 別エージェント(`superpowers:code-reviewer`)にマージ前レビュー依頼 |
| ローカルゲート | `tools/pre-merge-check.ps1` main マージ前必須 |
| L5 実機検証 | 1b・2d・3a・3d の 4 回想定 |
| ロールバック粒度 | サブ Phase 単位(独立ブランチ) |
| メモリ更新 | Phase 完了時に MEMORY.md をリファクタ状況へ更新 |

## 7. リスクと緩和策

| リスク | 影響 | 緩和策 |
|---|---|---|
| Phase 3 委譲で public API が壊れる | Editor.Tests 破壊 | Phase 2 の partial 化で先に境界を可視化し、Phase 3 の変更を小さく保つ |
| IME 状態機械の Message pump 分離失敗 | 3a で NVDA 読み不整合 | `IImeContext` の Fake テストで十分被覆した上で L5 実機検証 |
| UIA cache 移譲でスレッド安全崩壊 | 3d で SR 経路 race | `_boundsSync` のロック粒度を Adapter へそのまま移し、変更を最小化 |
| リファクタ中に別要件(バグ修正)が発生 | 段階マージのコンフリクト | サブ Phase を短命(1〜2 日)に保ち、main と同期しやすくする |

## 8. 非目標(スコープ外)

以下は本リファクタリングに含めない。個別 PR または将来セッションで扱う。

- **新機能追加**(能動発声の拡張・新エンコーディング対応など)
- **性能最適化**(Phase 3 完了後にプロファイル駆動で別 Phase を判断)
- **テスト戦略 Phase 3(SR 性能ゲート)**の追加拡張
- **App 層のさらなる Controller 分割**(Stage 8 で判断済み・[[test-strategy]])
- **設定ダイアログの再設計**
- **Accessibility 層(UIA v2)の内部リファクタ**(Adapter に委譲する Phase 3d までは触らない)

## 9. 完了基準(Definition of Done)

Phase ごと:
- 全テスト緑(現状 954 + 追加分)
- 0 警告(Release ビルド)
- 別エージェント review「マージ可」
- `tools/pre-merge-check.ps1` 緑
- L5 該当 Phase のみ NVDA/PC-Talker 実機再確認 OK

全体:
- EditorControl.cs 3396 → ~600 行
- Editor 層に Controller 4 個抽出済み(Ime/Caret/Input/UiaAdapter)
- 中小 5 項目全て解消
- MEMORY.md に完了記録

## 10. 次アクション

本設計書承認後、`writing-plans` skill を起動して段階別実装計画に落とし込む。各サブ Phase の具体的な Task 分解・レビュー観点・テスト追加項目は実装計画側に持たせる。

**本設計書はコード変更を含まない**。承認 → 設計書コミット → writing-plans skill 起動 → 実装計画作成、までを本セッションで行う。実装は別セッション。
