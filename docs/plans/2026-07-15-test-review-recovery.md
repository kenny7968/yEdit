# テストレビュー回収 実装計画(Phase 1+2 事後レビュー由来)

> **For Claude:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development でタスク毎に実行する。

**Goal:** Phase 1+2 完了後の 3 観点レビュー(網羅性/堅牢性/横断・2026-07-15)で確認された残穴を、5 ブランチに分けて回収する。

**Architecture:** 各ブランチは「フィーチャーブランチ作成 → 実装(Task 毎コミット) → 別エージェントによるコードレビュー+ミューテーション検証 → `tools/pre-merge-check.ps1` 全緑 → main へ no-ff マージ」。PR 運用はしない(ユーザー指示)。

**Tech Stack:** xUnit v2 + 既存テスト基盤(Sta.Run / HostForm(TestHost.cs) / Fakes/ 一式)。新規基盤は原則追加しない。

**共通レビュー標準(全 Task 適用):**
- ミューテーション検証: 実装行を一時変異 → 対象テスト赤 → 復元(レビュアが実施・kill 対応を記録)
- 非既定位置から検証開始(既定値と同値の空振り assert 禁止)
- カウンタ assert は `>=` 許容(Deselecting 二重発火等)
- 文言 assert は `CsvAnnounceFormatter` 等の Core 定数を参照(文言の正は Core に一元化)
- テスト名は実挙動と一致させる(assertion 前提と guard 発火条件の一致)

---

## ブランチ 1: `test/review-recovery` — 網羅性回収(配線系+ロジック系 約 25 テスト)

### Task 1-1: SearchController — MatchCase/WholeWord 配線+ReplaceAll 不正 regex ガード

**Files:** Modify `tests/yEdit.App.Tests/SearchControllerTests.cs`(既存ハーネス流用)

対象実装: `src/yEdit.App/SearchController.cs:62` の `new SearchOptions(d.Pattern, d.MatchCase, d.WholeWord, d.UseRegex)`。

**引数 swap を kill する fixture 設計(重要):** 件数 assert では swap を検出できない(対称になりがち)。**選択位置**で判別する。

1. `FindNext_MatchCaseTrue_SkipsCaseMismatch` — 本文 `"ABC abc"`・Pattern `"abc"`・MatchCase=true → FindNext() 後の `GetSelectionCharRange() == (4, 7)`。
   - swap 変異(`d.WholeWord, d.MatchCase`)だと WholeWord=true/MatchCase=false 扱いになり先頭 `ABC`(0,3) を選択 → 赤。
2. `FindNext_WholeWordTrue_SkipsPartialWord` — 本文 `"abcx abc"`・Pattern `"abc"`・WholeWord=true → 選択 `(5, 8)`。
   - swap 変異だと MatchCase=true 扱い=先頭 `abcx` の部分一致 (0,3) を選択 → 赤。
3. `ReplaceAll_InvalidRegex_AnnouncesAndDoesNotModify` — UseRegex=true・Pattern `"("` → ReplaceAll() → FakeAnnouncer に「正規表現が正しくありません」・本文不変。UpdateCount/Find/ReplaceOne の既存同型テストと対称化。

コミット: `test: SearchController の MatchCase/WholeWord 配線+ReplaceAll 不正regexガード 3 件`

### Task 1-2: KinsokuFormatController — 禁則パラメータ配線

**Files:** Modify `tests/yEdit.App.Tests/KinsokuFormatControllerTests.cs`

対象実装: `src/yEdit.App/KinsokuFormatController.cs:50-53` の `KinsokuFormatter.Format(target, settings.WrapColumn, settings.KinsokuLineStartChars, settings.KinsokuLineEndChars, settings.KinsokuHangChars, eol, settings.TabWidth)`。

**fixture 設計:** LineStartChars と LineEndChars に**異なる文字集合**を与え、swap すると出力が変わる入力を作る(Core.Tests の KinsokuFormatter テストを参照して挙動を把握してから設計する)。期待値はリテラル文字列で固定(直接 Core を呼んで期待値を作る比較は、同じ swap をした場合に同値になるため不可)。

1. `Run_WiresLineStartAndLineEndChars_Distinctly` — swap 変異で赤になることをレビュアが確認できる fixture。テスト内に `// swap すると <差分> になる` を注記。
2. `Run_WiresTabWidth` — TabWidth=8 と 4 で折返し位置が変わる本文(行頭タブ+長文)。settings.TabWidth=8 の期待値で固定。
3. `Run_WiresHangChars` — HangChars 有無で出力が変わる fixture(句読点ぶら下げ)。

コミット: `test: KinsokuFormatController の禁則パラメータ配線 3 件(swap/TabWidth/HangChars kill)`

### Task 1-3: CsvController — 端ジャンプ 4 API+ByKey 表+EdgeMessage 残 3 分岐+クランプ

**Files:** Modify `tests/yEdit.App.Tests/CsvControllerTests.cs`

**共通 fixture:** 5x5 グリッド(全セル値ユニーク・例 `r0c0..r4c4`)、開始位置 (2,2)(非既定位置・全方向に 2 以上の余地=隣接移動と端ジャンプの到達先が必ず異なる)。

1. `MoveRowStart/MoveRowEnd/MoveColumnTop/MoveColumnBottom` 4 テスト — (2,2) から各 API → State.CsvRow/CsvCol と FakeAnnouncer の Cell 文言((2,0)/(2,4)/(0,2)/(4,2))。
2. `ByKey_MapsAllEntriesToExpectedCommands` — `CsvCommands.ByKey` の 16 エントリを Theory で全件実行。各キーの delegate を (2,2) 起点の独立セットアップで invoke し、期待効果を assert:
   - Up/Down/Left/Right → 隣接 4 方向のセル文言
   - Home/End/PageUp/PageDown → 端 4 方向のセル文言(隣接と異なる=取り違え kill)
   - Ctrl+Home/Ctrl+End → (0,0)/(4,4)
   - Tab/Shift+Tab → 現在セル (2,2) の Cell 文言(移動なし)
   - C → (0,2) の Header 文言 / R → (2,0) の Header 文言(Cell と書式が異なる=Tab と判別可)
   - G/Ctrl+G → FakeCellPicker に Ok(1 始まり座標)を登録し、指定セルへ移動(Canceled 無音だと他キーと判別しづらいため Ok を使う)
   - F2 → `IsEditing == true`
   - ※ `CsvCommands` は internal(InternalsVisibleTo 済)。
3. `Move_AtRightEdge/TopEdge/BottomEdge_AnnouncesEdge` 3 テスト — (2,4) Right→RightEdge、(0,2) Up→TopEdge、(4,2) Down→BottomEdge(LeftEdge は既存)。
4. `ReadCurrent_ClampsColHigh/ClampsColLow/ClampsRowLow` — ragged CSV(`a,b,c` + `d`)で State.CsvCol=99→(0,2)、CsvCol=-1→(0,0)、CsvRow=-5→行 0。クランプ後の State 書き戻しも assert。

コミット(2 分割可): `test: CsvController の端ジャンプ4種+EdgeMessage残3分岐+クランプ` / `test: CsvCommands.ByKey 16 エントリの対応固定`

### Task 1-4: GrepController — CancelClose

**Files:** Modify `tests/yEdit.App.Tests/GrepControllerTests.cs`

1. `CancelClose_AfterBeginClose_RestoresResultDisplay` — Open → BeginClose() → CancelClose() → RunAsync(即時完了 FakeGrepSearchFn・ヒットあり) → ResultsFactoryCalls == 1・Populate 呼び出しあり(BeginClose 抑止テストの対称形。`_closing=false` 復帰が消えると赤)。

コミット: `test: GrepController.CancelClose の復帰配線 1 件`

### Task 1-5: DocumentManager — BeforeActiveChange 発火+Host フィクスチャ遡及

**Files:** Modify `tests/yEdit.App.Tests/DocumentManagerTests.cs`

1. まず既存 14 箇所の `Make()` タプル+`using (form)` を Stage 3 以降標準の `using var host` 型フィクスチャへ機械的に置換(挙動不変・別コミット)。
2. `BeforeActiveChange` 発火テスト(counter は `>=` 許容・Deselecting 二重発火があり得る):
   - `CreateNew_SecondTab_FiresBeforeActiveChange`(2 枚目作成で ≥1)
   - `Activate_DifferentTab_Fires` / `Activate_SameTab_DoesNotFire`(line 94 guard の固定)
   - `SelectNext_Fires` / `SelectAt_Fires`
   - 発火**タイミング**の固定: ハンドラ内で「その時点の Active」を記録し、切替**前**の文書であることを assert(「直前フック」契約の本体)。

コミット: `refactor: DocumentManagerTests を Host フィクスチャへ遡及統一` / `test: BeforeActiveChange の発火 5 経路+直前性 6 件`

### Task 1-6: BackupCoordinator — confirm=true 巻き添え防止+クランプ実 assert 化

**Files:** Modify `src/yEdit.App/BackupCoordinator.cs`(seam 1 行), `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`

1. seam: `internal int TimerIntervalMs => _timer.Interval;`(観測専用・挙動不変)。
2. 既存 `UpdateSettings_IntervalClamp_TooSmall_And_TooLarge`(assertion ゼロ)を実 assert 化+改名: ctor(1s)→5000 / ctor(99999s)→3_600_000 / UpdateSettings 同様。旧コメント(「例外にならないこと自体が保証」)は不正確なので削除。
3. `OfferRestore_ConfirmTrue_OneBadRecord_DoesNotAbortOthers` — FakeRestorePrompt に Restore+Checked=[rec1, rec2] を登録、restore デリゲートは rec1 で throw・rec2 で成功 → 例外が伝播しない・restore が 2 回呼ばれる・rec2 の文書が生成される(confirm=false 側の既存テストと対称)。
4. `Dispose_WithoutShutdown_...` の手動 `host.Form.Dispose()` を `using var host` へ統一(Dispose 冪等確認済み)。

コミット: `test: BackupCoordinator の confirm=true 巻き添え防止+クランプ実assert化(seam TimerIntervalMs)`

### Task 1-7: FileController — HadReplacementChar/ReadOnly 復元/Save() 入口

**Files:** Modify `tests/yEdit.App.Tests/FileControllerTests.cs`

1. `Reopen_WithReplacementChar_WarnsToReopen` — ASCII `abc` のファイルを開く(警告なし)→ ファイルへ不正バイト(`abc` + 0xFF)を書き戻し → FakeFileDialogService.EncodingCodePage=65001 で ReopenWithEncoding() → 0xFF は UTF-8 不正=U+FFFD → FakePrompt.Log に ("Warn", 文言に「置換文字」を含む) が積まれる。
2. `Save_ReadOnlyDocument_RestoresReadOnlyAfterSave` — Path 確定済み文書で `Editor.ReadOnly = true`(CSV モード相当)→ Save() true → 保存後も `ReadOnly == true`・ディスク内容更新。
3. `Save_ReadOnlyDocument_WriteFailure_StillRestoresReadOnly` — 保存先を読み取り専用属性にして失敗させ(finally 経路)、`ReadOnly == true` を assert(後始末で属性解除)。
4. `Save_ExistingPath_WritesAndClearsModified` — 開く→編集→Save() → true・ディスク一致・`Modified == false`(Ctrl+S 導線の公開入口)。

コミット: `test: FileController の置換文字警告/ReadOnly復元/Save入口 4 件`

### Task 1-8: MainForm — コンポジションルート・スモーク

**Files:** Modify `src/yEdit.App/MainForm.cs`(seam 2 点・挙動不変), Create `tests/yEdit.App.Tests/MainFormSmokeTests.cs`

seam:
- `_settingsPath` の初期化子を ctor 代入へ移し、`public MainForm(AppSettings) : this(settings, SettingsStore.DefaultPath)` + `internal MainForm(AppSettings, string settingsPath)` にチェーン(テストが実設定ファイルを汚さないため・必須)。
- `internal FileController FileForTest => _file;`(スモークの導線。命名はレビュアと調整可)。

テスト(全て `Sta.Run`・TempDir に settings.json/.csv/.txt を用意・`StartPosition=Manual`+`Location=(-32000,-32000)` を Show 前に設定):
1. `AutoCsv_On_OpensCsvIntoCsvMode` — CsvAutoModeOnOpen=true・BackupEnabled=false で MainForm 生成+Show → `FileForTest.TryOpenOrActivate(data.csv)` → `doc.State.CsvMode == true`。**拡張子は大文字 `DATA.CSV` を使い OrdinalIgnoreCase 落ち変異を kill**。
2. `AutoCsv_SettingOff_StaysNormalMode` — CsvAutoModeOnOpen=false → CsvMode false(ガード 1)。
3. `AutoCsv_NonCsvExtension_StaysNormalMode` — auto ON で .txt → CsvMode false(ガード 2)。
4. `OpenAndSelect_OpensSelectsAndSuppressesAutoCsv` — auto ON のまま `form.OpenAndSelect(data.csv, 2, 3)` → 選択 (2,5)・**CsvMode == false**(suppressAutoCsv 配線の固定)・アクティブ文書の Path 一致。

注意: MainForm は sealed で ShowWithoutActivation を注入できない=Show() は一時的にアクティブ化する。並列無効なので実害なし(コメントで注記)。OnShown の復元提案は BackupEnabled=false で素通り(OfferRestoreOnStartup 先頭ガード)。

コミット: `feat: MainForm に settingsPath/FileForTest の内部 seam` / `test: コンポジションルート・スモーク 4 件(AutoEnterCsvMode+OpenAndSelect)`

### ブランチ 1 仕上げ

1. 別エージェントレビュー(コード品質+全 Task のミューテーション kill 検証)→ 指摘対応
2. `powershell -File tools/pre-merge-check.ps1` 全緑
3. `git merge --no-ff` で main へ(メッセージ例: `テストレビュー回収: 配線系+ロジック系テスト 25 件をマージ`)

---

## ブランチ 2: `test/serial-backup-writer` — 実書込パイプライン統合テスト

**Files:** Create `tests/yEdit.App.Tests/SerialBackupWriterTests.cs`

**決定化の原則: 待ちは一切入れない。**「ジョブ投入 → `Dispose()`(=CompleteAdding+Join でドレイン完了が同期確定)→ ディスク/コールバックを assert」の形に統一する(Sleep/リトライ禁止)。ディレクトリはテスト毎に `Directory.CreateTempSubdirectory`。

1. `Write_ThenDispose_DrainsToDisk` — Write(rec) → Dispose() → `BackupStore.LoadAll(dir)` に rec が居る(ドレイン契約)。
2. `WriteThenDelete_SameId_EndsAbsent` — Write→Delete(同 Id)→Dispose → LoadAll に居ない(投入順実行)。
3. `DeleteAll_RemovesEverything`。
4. `WriteFailure_InvokesOnWriteFailed_AndWorkerSurvives` — dir に**ファイルのパス**を渡す等で BackupStore.Write を失敗させ、OnWriteFailed に Id が届く(記録は List+Dispose 後に assert)。その後 dir を正しい場所に差した 2 個目の writer で正常 Write が通る…は別インスタンスになるため、**同一 writer で「失敗 Write → 成功 Delete(no-op でも例外なく完走)→ Dispose が 15 秒内に戻る」でワーカー生存を証明**する形にする(外側 catch=ワーカー死の検出)。
5. `Enqueue_AfterDispose_IsIgnored` — Dispose 後の Write/Delete が無例外・無効果。
6. `Dispose_IsIdempotent` — 二重 Dispose 無例外。

不安定さが観測された場合のみ `[Trait("Category", "LocalOnly")]`(ブランチ 4 の方針に従う)。レビュー→pre-merge-check→no-ff マージ。

---

## ブランチ 3: `test/eol-characterization` — EOL 非ロールバックの特徴付け

**Files:** Modify `tests/yEdit.App.Tests/FileControllerTests.cs`

Stage 3 方式(バグ注記付き特徴付け)。**修正はしない**。各テスト冒頭に「既知挙動: WriteToPath 失敗時、State はロールバックされるが ConvertEols 済み本文はロールバックされない(修正要否は別途判断)」を注記。

1. `SaveAs_WriteFailure_LeavesEolConverted_KnownBehavior` — 本文 `"a\r\nb"`(CRLF)・State=CRLF。FakeFileDialogService.SaveAs に 実在しないサブディレクトリ配下のパス+LineEnding=LF+cp65001 を登録 → SaveAs() false → State.LineEnding は CRLF に戻る(既存テストの担保)が **SnapshotText は `"a\nb"`(LF)のまま**を assert。
2. `Save_WriteFailure_LeavesEolNormalized_KnownBehavior` — 混在 EOL 本文(`"x\ny"`)+State=CRLF+保存先を読み取り専用属性で失敗 → 本文が `"x\r\ny"` に正規化済みのまま・Modified true。後始末で属性解除。

マージ後、**修正要否の判断をユーザーへ提起**(本計画の成果報告に含める)。

---

## ブランチ 4: `chore/ci-prep` — CI 初回 push 前の準備

1. **LocalOnly 方針**: 素の `[Trait("Category", "LocalOnly")]` を正とする(カスタム属性の xUnit v2 プラミングは YAGNI)。`tests/yEdit.Editor.Tests/ClipboardTests.cs` のクラスへ先行付与(実クリップボード+リトライ=CI 共有資源競合の筆頭。pre-merge-check はフィルタ無しなのでローカル網羅は維持)。
2. **Editor.Tests のフォーム生成標準化**(慎重に・ファイル単位で dotnet test 確認):
   - `tests/yEdit.Editor.Tests/TestHost.cs`(新設)に App.Tests 同型の HostForm(ShowWithoutActivation+ShowInTaskbar=false+画面外)を用意。
   - **適用対象**: レイアウト確定のためだけに Show している系(TextInsertion/TextEditing/AnchorSelection/ContractTests/CacheTests/NativeSurface/UiaGetObject/BoundingRects/OffsetFromPoint 等)。1 ファイル変換ごとに該当テストを実行し、赤になったら差し戻して理由コメントを残す。
   - **適用除外(現状維持)**: Win32 フォーカス/IME/UIA フォーカスイベント依存系(CaretScrollTests のフォーカス部・ImeTests・UiaFocusEventTests・UiaHostTests・ClipboardTests)=アクティブ化が本質的に必要。
3. **Sta.cs(App.Tests)の remarks 更新**: 「同期的な API 呼び出しのみ」の記述を実態(TCS 駆動 async テストあり)へ追随させ、**TCS 規律**を明記: 「TCS は同一 STA スレッドで完了させる・`RunContinuationsAsynchronously` 禁止・searchFn に Task.Run を挟まない。破ると継続がポンプされない WinForms SC へ Post され GetResult() がハングする」。
4. **release.yml**: テスト前に `dotnet build yEdit.sln -c Release -warnaserror` ステップを追加し、テストを `--no-build` 化(タグ直 push 時の 0 警告ゲート素通りを閉じる+二重ビルド解消)。
5. **ゲート単一情報源化の検証**: `dotnet test yEdit.sln -c Release --no-build --filter "Category!=LocalOnly"` をローカル実行し、(a) 3 テストプロジェクトのみ実行されるか(Bench/Smoke スキップ)、(b) UI テスト 2 アセンブリの実行が直列か、を確認。**両方 Yes なら** ci.yml/release.yml/pre-merge-check.ps1 を sln 一括へ寄せる(pre-merge はフィルタ無し)。**No なら現状維持**とし、3 ファイルへ「テストプロジェクト追加時は 3 箇所同期」の相互参照コメントを追記。検証結果を実施記録に残す。

レビュー→pre-merge-check→no-ff マージ。

---

## ブランチ 5: `docs/test-suite-guide` — 文書整備

1. Create `tests/README.md`:
   - 層の責務(L1 Core/L2 Editor/L3 App/L4 Bench 手動/L5 実機 SR)と「どこに書くか」の判断基準
   - **コピペ元の正典**: 新 Controller テスト=GrepControllerTests+Host フィクスチャ+FakeGrepSearchFn(TCS 駆動の決定化)が現行完成形。Fake は「応答事前登録+順序保持の記録」型
   - **レビュー標準チェックリスト**(集約): ミューテーション検証/非既定位置/partial-selection の prefix・suffix fixture/guard 発火条件と assertion 前提の一致/カウンタ `>=` 許容/文言は Core 定数参照
   - LocalOnly 方針(素 Trait・付与判断は CI 実績ベース・pre-merge はフィルタ無し)
2. Modify `docs/plans/2026-07-13-test-strategy-design.md`: §2 近辺に tests/README.md への参照+LocalOnly 節(プレースホルダでなく上記方針の要約)を追記。
3. 本計画書に全ブランチの実施記録(マージハッシュ・テスト数・逸脱)を追記。

レビュー(文書のみ=軽量レビュー可)→pre-merge-check→no-ff マージ。

---

## 実施記録

### ブランチ 1: `test/review-recovery` — 網羅性回収(配線系+ロジック系)

- **main マージ**: `c73c226`(2026-07-15)
- **規模**: 8 Task・15 コミット
- **テスト数変動**: App **163 → 218**(+55)
- **概要**: Task 1-1 SearchController の MatchCase/WholeWord 配線+ReplaceAll 不正 regex ガード 3 件 / Task 1-2 KinsokuFormatController の禁則パラメータ配線 3 件(swap/TabWidth/HangChars kill) / Task 1-3 CsvController の端ジャンプ 4 種+EdgeMessage 残 3 分岐+クランプ+ByKey 17 エントリの対応固定 / Task 1-4 GrepController.CancelClose の復帰配線 1 件 / Task 1-5 DocumentManager の BeforeActiveChange 発火 5 経路+直前性 6 件+Host フィクスチャ遡及統一 / Task 1-6 BackupCoordinator の confirm=true 巻き添え防止+クランプ実 assert 化(seam TimerIntervalMs) / Task 1-7 FileController の置換文字警告/ReadOnly 復元/Save 入口 4 件 / Task 1-8 MainForm に settingsPath/FileForTest の内部 seam+コンポジションルート・スモーク 4 件(AutoEnterCsvMode+OpenAndSelect)。
- **逸脱・特記**: 実装 seam は最小限(MainForm=2 点/BackupCoordinator=1 点)。挙動不変。CsvCommands.ByKey は 17 エントリ(計画本文の「16 エントリ」はカウントミス=実装が正)。

### ブランチ 2: `test/serial-backup-writer` — 実書込パイプライン統合テスト

- **main マージ**: `def941b`(2026-07-15)
- **規模**: 6 テスト + src バグ修正 1 件
- **テスト数変動**: App **218 → 224**(+6)
- **概要**: `SerialBackupWriterTests` を新設し、決定化原則(Sleep/リトライ禁止・`Dispose()` によるドレイン契約で観測)で 6 件を追加(Write→Dispose ドレイン / WriteThenDelete 同 Id 消失 / DeleteAll / WriteFailure ワーカー生存 / Enqueue_AfterDispose 無効化 / Dispose 冪等)。
- **src 修正**: `SerialBackupWriter.Enqueue` の Dispose 後ガードを xmldoc の意図(Dispose 後は無例外・無効果)に一致させる修正(`e8aa3a8`)。テストが実装のバグを検出した実例。

### ブランチ 3: `test/eol-characterization` — EOL 非ロールバックの特徴付け

- **main マージ**: `ec7a988`(2026-07-15)
- **規模**: 2 特徴付けテスト(修正はしない)
- **テスト数変動**: App **224 → 226**(+2)
- **概要**: Stage 3 方式(バグ注記付き特徴付け)で、EOL 保存失敗時の非ロールバック挙動 2 件を pin。
- **既知バグ pin**: (a) SaveAs 失敗時に State.LineEnding はロールバックされるが `ConvertEols` 済み本文がロールバックされない / (b) 保存失敗時に混在 EOL 本文が正規化済みのまま残る。**修正要否は本計画完了後にユーザーへ提起**(下記「マージ後判断」参照)。

### ブランチ 4: `chore/ci-prep` — CI 初回 push 前の準備

- **main マージ**: `099a671`(2026-07-15)
- **規模**: 6 コミット(9 ファイル対象+差し戻し 1 件)
- **テスト数変動**: なし(準備作業)
- **概要**: (1) `tests/yEdit.Editor.Tests/ClipboardTests.cs` に `[Trait("Category", "LocalOnly")]` 付与(素 Trait を正・カスタム属性は YAGNI) / (2) `tests/yEdit.App.Tests/Sta.cs` remarks を TCS 規律込みに更新(`RunContinuationsAsynchronously` 禁止・`searchFn` 内 `Task.Run` 禁止の明文化) / (3) `.github/workflows/release.yml` に `-warnaserror` ビルドステップ追加+テスト `--no-build` 化 / (4) ゲート 3 ファイル(pre-merge-check.ps1・ci.yml・release.yml)に同期コメント追記 / (5) Editor.Tests 9 ファイルに HostForm 方式適用+適用外テストへの差し戻しコメント。
- **逸脱**: sln 一括ゲートへの寄せは検証結果に基づき見送り(現状のフィルタなしローカル+CI フィルタ有の二相を維持)。

### ブランチ 5: `docs/test-suite-guide` — 文書整備(本ブランチ)

- **規模**: 3 サブタスク・文書のみ
- **テスト数変動**: なし
- **概要**: (1) `tests/README.md` 新設(5 層ピラミッド / コピペ元の正典=GrepControllerTests+Host フィクスチャ+FakeGrepSearchFn / レビュー標準 6 点 / LocalOnly 方針・148 行) / (2) `docs/plans/2026-07-13-test-strategy-design.md` に README 参照+LocalOnly 現状(ClipboardTests のみ・追加は CI 実測ベース)を追記+§6 に付与済みの 1 行追記 / (3) 本ファイルへ全ブランチ実施記録を追記。

### 総括(pre-merge-check 実測=2026-07-15)

- **テスト数**: Core **573** + Editor **218** + App **226** = **1017 tests**(全緑)
- **ビルド**: Release **0 警告**
- **ゲート**: `tools/pre-merge-check.ps1` フィルタなし全数緑

### マージ後判断(ユーザーへの提起)

本計画のマージ完了後、以下の 3 点について要否をユーザー判断へ委ねる:

1. **EOL 非ロールバック修正の要否**(ブランチ 3 で pin した既知バグ a): SaveAs 失敗時の `ConvertEols` 済み本文の非ロールバック。
2. **ConvertEols 差替による保存点破壊の修正要否**(ブランチ 3 で pin した既知バグ b): 保存失敗時に混在 EOL 本文が正規化済みのまま残る。
3. **追加 LocalOnly 付与の要否**(CI 初回 push 実測結果次第): UiaFocusEventTests / UiaHostTests / BoundingRects / OffsetFromPoint 等の候補について、CI で赤化・不安定化したものだけを付与する運用に従って判断。
