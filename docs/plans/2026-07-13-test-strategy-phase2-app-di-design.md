# テスト戦略 Phase 2: App 層 DI リファクタ+App.Tests 設計書

- 日付: 2026-07-13
- 上位文書: `docs/plans/2026-07-13-test-strategy-design.md` §3
- 前提: Phase 1(ローカルゲート+CI)完了後に着手する

## 0. 方針(上位文書で承認済み)

- **コンテナレスの本格分離**: MainForm を composition root 化し、各 Controller はコンストラクタ注入されたインターフェースにのみ依存する。DI コンテナは導入しない。
- **EditorControl はモックしない**: App.Tests でも実物 EditorControl+実 Core を STA 上で使う。偽物にするのは **Form 境界(ダイアログ・MessageBox)と OS 境界(SR 稼働判定・時計・背景書込)** に限定する。
- **ストラングラー方式**: Controller 1 つ=1 フィーチャーブランチ=1 no-ff マージ。各段で「シーム導入 → 失敗テスト → 実装 → 挙動不変確認 → マージ」。

## 1. 現状の依存分析(2026-07-13 コード精読の結果)

| クラス | Form 依存 | ダイアログ直 new | static/OS 依存 | テスト容易性 |
|---|---|---|---|---|
| DocumentManager | なし(TabControl 内包のみ) | なし | なし | **◎ 現状のままテスト可能** |
| CsvController | なし(FindForm() 経由 1 箇所) | CsvGoToCellDialog | なし(IAnnouncer 注入済み) | **○ GoToCell 以外は現状で可能** |
| FileController | `Form _owner`(ダイアログ親) | OpenFileDialog / SaveAsDialog / EncodingPickDialog / MessageBox×5 | TextFileService(実ファイル=温存可) | △ ダイアログ抽象化が必要 |
| SearchController | `Form _owner` | FindReplaceDialog(生成+8 メンバー参照) | なし(IAnnouncer 注入済み) | △ ダイアログ抽象化が必要 |
| BackupCoordinator | IWin32Window(復元ダイアログ親) | RestoreDialog | WinForms Timer / SerialBackupWriter(背景スレッド) / DateTime.UtcNow | △ 時計・ライター・タイマー抽象化が必要 |
| GrepController | `Form _owner` | GrepDialog / GrepResultsWindow | Task.Run+`async void Run()` | △ ビュー抽象化+async 化が必要 |
| MainForm | ―(本体) | GoToLineDialog / SettingsDialog / MarkdownPreviewForm / MessageBox | PcTalkerSpeech.IsRunning() / AnnouncerFactory(static+Lazy) | ✕ composition root 化で解消 |
| Speech 系 | Label 束縛 | なし | PCTKUsr.dll P/Invoke(遅延束縛) | △ 経路判定の注入化が必要 |

好材料: BackupCoordinator は `directory` が注入可能済み、判定中核は Core の純粋関数 `BackupPlanner`(テスト済み)。SearchController/CsvController は `IAnnouncer` 注入済み。Core 側(TextFileService/SnapshotSearcher/GrepService/CsvParser)は L1 でテスト済みなので、App.Tests の責務は**配線・状態遷移・通知文言**に絞る。

## 2. 導入するシーム(インターフェース設計)

命名・所属はすべて `yEdit.App`(必要なら `yEdit.App.Abstractions` サブフォルダ)。実装クラスは既存 UI をラップするだけの薄い Adapter とし、ロジックを持たせない。

### 2.1 OS 境界

```csharp
/// <summary>起動時に一度だけ確定する SR 経路(PC-Talker か否か)。プロセス寿命で不変。</summary>
public interface ISrRoute { bool IsPcTalker { get; } }
// 実装: PcTalkerSrRoute(PcTalkerSpeech.IsRunning() を ctor で 1 回だけ評価)
// AnnouncerFactory は static/Lazy をやめ、ISrRoute を受けるインスタンス化 or
// Program.Main で判定→MainForm に bool/IAnnouncer を注入する形へ。
// MainForm._isPcTalker(空行・単語ナビ分岐)も同じ ISrRoute から取る=判定源を 1 つに統一。
```

```csharp
/// <summary>ユーザーへの確認・警告(MessageBox のラップ)。</summary>
public interface IUserPrompt
{
    void Info(string text, string caption);
    void Warn(string text, string caption);
    void Error(string text, string caption);
    bool OkCancel(string text, string caption);            // 文字コード劣化警告など
    DialogResult YesNoCancel(string text, string caption); // 未保存確認
}
```

- 時計: .NET 標準の `TimeProvider` を注入(BackupRecord.TimestampUtc 用)。
- 背景書込: `IBackupWriter { void Enqueue(Action job); void Dispose(); }` を切り、実装は既存 SerialBackupWriter。テストは同期実行フェイク。

### 2.2 Form 境界(ダイアログ)

```csharp
/// <summary>ファイル系ダイアログの結果だけを返す抽象。UI 実装は既存ダイアログをラップ。</summary>
public interface IFileDialogService
{
    string? PickOpenPath(IWin32Window owner);
    SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current); // パス/コードページ/BOM/改行
    int? PickEncoding(IWin32Window owner, int currentCodePage);          // 開き直し
}
```

```csharp
/// <summary>FindReplaceDialog の Controller 向け表面。</summary>
public interface IFindReplaceView
{
    string Pattern { get; }
    string Replacement { get; }
    bool MatchCase { get; } bool WholeWord { get; } bool UseRegex { get; } bool InSelection { get; }
    bool Visible { get; }
    void SetMode(bool replaceMode);
    void SetStatus(string text);
    void ShowAndFocus(IWin32Window owner); // Show+Activate+FocusPattern を 1 メソッドに集約
}
// SearchController はビューを Func<IFindReplaceView> ファクトリで受け、生成タイミングは現状維持。
```

- GrepController: 同様に `IGrepView` / `IGrepResultsView` を切る。`async void Run()` は `internal Task RunAsync()` に改め(メニューは async void ラッパ)、テストで await 可能にする。
- BackupCoordinator: RestoreDialog 相当を `IRestorePrompt`(records → 復元/全破棄/後で+チェック済み一覧)に抽象化。
- CsvController: CsvGoToCellDialog を `Func<(int row,int col), (int,int)?>` 相当の `ICellPicker` に抽象化(GoToCell のみが対象。他の操作は現状で可)。
- GoToLineDialog / SettingsDialog / MarkdownPreviewForm は MainForm 側の残置とし、コマンドロジック抽出(§4 Stage 8)時に必要なら抽象化する(YAGNI)。

### 2.3 タイマー(BackupCoordinator)

WinForms Timer は抽象化せず、**`Reconcile()` を internal にして App.Tests から直接叩く**(タイマーは「Reconcile を定期起動するだけ」の 1 行配線であり、間隔クランプは ctor 単体で検証可能)。テストが時間待ちしないための最小手段。

## 3. テスト観点(App.Tests の責務)

真実源: 配線・状態遷移・通知文言・ロールバック。Core が検証済みの照合・I/O 正しさは再検証しない。

| 対象 | 主要観点(抜粋) |
|---|---|
| DocumentManager | CreateNew の配線(SavePoint→ラベル`*`/イベント転送がアクティブ限定)・FindByPath(PathKey 同一視)・TryClose(confirm 拒否で閉じない/Dispose される)・SelectNext 巡回/SelectAt 範囲外 no-op・KeyBasedSwitch は実切替時のみ発火 |
| FileController | NewFile 既定(設定のコードページ/EOL・無題連番)・TryOpenOrActivate(既存タブ再利用/失敗時の作りかけタブ破棄と復帰)・SaveAs ロールバック(WriteToPath 失敗で Encoding/EOL/BOM が元に戻る=データ破損防止の要)・CanEncodeBuffer(非 UTF-8 で表せない文字→OkCancel 経由)・ConfirmDiscardIfDirty(Yes=保存成否/No=true/Cancel=false)・RestoreFromBackup(dirty のまま・無題連番の引継ぎ)・RegisterRecent(上限 10・先頭繰上げ) |
| SearchController | 歩進(_lastHit 一致時は次から/ゼロ幅で前進)・文書切替で _lastHit/_selectionScope リセット・ReplaceOne の VSCode 準拠(未選択→検索して即置換)・ReplaceAll の InSelection スコープ(未捕捉は「選択範囲がありません」)・通知文言(「N 件中 M 件目」「これ以上見つかりません」)・CSV モード中の置換抑止 |
| BackupCoordinator | Reconcile 直接呼び(dirty→Write/クリーン化→Delete/変化なし→None は BackupPlanner 済みなので配線のみ)・閉じた文書の削除投入・背景失敗→ForceWrite 再書込・UpdateSettings(有効化で即 Reconcile/無効化で Stop)・Shutdown(管理分削除+ドレイン・冪等)・OfferRestoreOnStartup(confirm=false 全復元と件数/不正レコード 1 件で巻き添えにしない) |
| CsvController | TryEnterMode(解析不可→通知して通常のまま/ReadOnly・RaiseUiaSelectionEvents の設定)・ExitMode(キャレット復帰→UIA 再有効化の順序)・Move 端で境界文言・Clamp(本文編集で行列が減った後の補正)・F2 確定(EscapeField 経由の直列化と再ハイライト)・GoToCell 範囲外 |
| GrepController | RunAsync(入力検証の文言/連打で先行実行の結果が UI を上書きしない=_cts 追い越し判定/BeginClose 中は結果窓を出さない/エラー件数ありは必ず発声) |
| Speech | ISrRoute 固定化(プロセス内で判定が揺れない)・PcTalker 経路で空行/単語ナビの能動発声が出る・非 PcTalker では出ない(MainForm 配線を関数抽出してテスト) |

共通テストユーティリティ: `FakeAnnouncer`(Say 履歴を記録)・`FakePrompt`(応答を事前登録)・`FakeFileDialogService`・同期 `FakeBackupWriter`・STA ヘルパ(Editor.Tests の `Sta.cs` パターンを流用)。

## 4. 段階分解(1 Stage=1 ブランチ=1 no-ff マージ)

| Stage | 内容 | リファクタ量 |
|---|---|---|
| 1 | `tests/yEdit.App.Tests` 新設(csproj・STA ヘルパ・Fake 群)+**DocumentManager のテスト**(リファクタ不要で書ける=基盤の実証) | なし |
| 2 | Speech: ISrRoute 導入・AnnouncerFactory 非 static 化・MainForm の判定源統一+テスト | 小 |
| 3 | FileController: IUserPrompt/IFileDialogService 導入+テスト(SaveAs ロールバックを最優先) | 中 |
| 4 | SearchController: IFindReplaceView 導入+テスト | 中 |
| 5 | BackupCoordinator: TimeProvider/IBackupWriter/IRestorePrompt 導入・Reconcile internal 化+テスト | 中 |
| 6 | CsvController: ICellPicker 導入+テスト | 小 |
| 7 | GrepController: IGrepView/IGrepResultsView・RunAsync 化+テスト | 中 |
| 8 | (任意)MainForm 痩身: コマンドロジック(禁則整形・位置読み・行ジャンプ等)の抽出+テスト。ここまでで MainForm は配線と ProcessCmdKey のみ | 中 |

各 Stage の DoD: `tools/pre-merge-check.ps1` 緑(App.Tests を含むよう Stage 1 でスクリプトへ追加)・テスト数純増・**挙動不変**(公開挙動・SR 発声文言・フォーカス遷移を変えない)。

## 5. リスクと対策

- **SR 挙動の退行**: Stage 2(Speech)と Stage 6(CSV)は SR 経路に触れるため、マージ前に L5 スポット確認(p6-manual-checklist の該当項目: 空行発声・単語ナビ・CSV セル読み)を必須とする。他 Stage はダイアログ抽象化のみで SR 経路不変。
- **FindReplaceDialog 側の結合**: FindReplaceDialog は `SearchController` を直接参照している(相互参照)。Stage 4 でビュー→Controller 方向はイベント/コールバックに置き換え、循環を断つ。
- **挙動同一性の担保**: 各 Stage は「シーム導入(Adapter が既存 UI を呼ぶだけ)」に徹し、条件分岐・文言・順序を一切変えない。diff レビューで機械的に確認できる粒度を保つ。

## 6. 申し送り

- ダイアログ内部(FindReplaceDialog の UI 配線・SaveAsDialog のコントロール構成など)は本 Phase の対象外(L5 手動)。プレゼンター抽出は必要が生じた Stage で個別判断。
- Stage 8 は任意。Stage 7 完了時点で費用対効果を再評価する。

### Stage 1 実施記録(2026-07-13)

- **完了**: `tests/yEdit.App.Tests` 新設(csproj・Sta.cs・GlobalUsings)+DocumentManager テスト 14 件+pre-merge-check.ps1 へ App.Tests 追加。ゲート全通過(Release 0 警告・Core 588+Editor 229+App 14=831 緑)。
- **逸脱 1**: §4 Stage 1 の「Fake 群」は `FakeAnnouncer` のみ作成。IUserPrompt/IFileDialogService/IBackupWriter 等のシームは未導入のため、対応する Fake は各導入 Stage(3/5 ほか)で追加する。
- **逸脱 2(小拡張)**: Stage 1 の DoD は pre-merge-check への追加のみ言及だが、上位戦略書 §1 の L3「毎マージ(ローカルゲート+CI)」と揃えるため ci.yml / release.yml にも App.Tests(`Category!=LocalOnly` フィルタ付き)を追加した。
- **技術知見**: TabControl の Selected/Deselecting/SelectedIndexChanged は、ハンドル生成だけではプログラム切替で発火せず、**ウィンドウ可視のとき同期発火**する(プローブ実測)。App.Tests のテストホスト Form は ShowWithoutActivation+画面外+ShowInTaskbar=false で Show() する。実運用 MainForm は常に可視のため、これが挙動の忠実な再現。
- **CI 初回観察ポイント**: 可視 Form 方式は windows-latest 実機未検証(Phase 1 からの既知の申し送りと同じ穴)。初回 push で App.Tests が落ちた場合、14 件の機械的 LocalOnly 隔離では CI の App ゲートが空になるため、「Selected 転送系のみ隔離+残りは不可視 Form で回す」等の分割を検討する。
- **Stage 2 への追加観点**: ActiveCaretEnteredEmptyLine / ActiveWordNavigated の転送テスト(配線は UpdateUI と同型)。空行発声の未解決バグ(PC-Talker)の切り分け材料として DocumentManager 区間の配線を緑と証明できる。テストユーティリティの共通化(Make ヘルパの IDisposable フィクスチャ化・Sta.cs の共有抽出は 3 プロジェクト目が現れたら)も Stage 2 以降で判断。

### Stage 2 再スコープ(2026-07-13)

PC-Talker サポート廃止(`docs/plans/2026-07-13-pctalker-removal-design.md`)により本 Stage を縮小する。

- §2.1 の `ISrRoute` シームは**導入しない**(判定対象の PC-Talker 経路が消滅)。
- 残作業: AnnouncerFactory の構造整理(static 解消 or MainForm での直接生成)+`FakeAnnouncer` による通知配線テスト。
- 排除ブランチのレビュー指摘の持ち込み: `UiaAnnouncer.Raise` は退避呼び出し元(PcTalkerAnnouncer)の消滅で同クラス内からしか呼ばれない=private 化 or Speak へのインライン化を検討。Smoke の `UseUiaAnnouncer` は実態(タイトルに `[UIA]` を付けるだけ)に合わせた改名(例: MarkUiaTitle)を検討。
- Stage 1 実施記録の追加観点「ActiveCaretEnteredEmptyLine / ActiveWordNavigated の転送テスト」は対象消滅により不要。
- §3 テスト観点表の「Speech」行・§5 の「Stage 2 は SR 経路に触れるため L5 スポット確認必須」は上記縮小後の内容に読み替える。

### Stage 2(縮小版)実施記録(2026-07-13)

- **完了**: 実装計画=`docs/plans/2026-07-13-test-strategy-phase2-stage2.md`。①Announcer 契約テスト 5 件(AnnouncerBase の視覚無条件/発声は非空のみ・UiaAnnouncer の握りつぶし契約)+`InternalsVisibleTo Include="yEdit.App.Tests"`(`68dbf8f`+レビュー対応 `2abc6e5`) ②AnnouncerFactory 廃止(MainForm/GrepDialog で UiaAnnouncer 直接生成・`36f7c2b`) ③UiaAnnouncer.Raise の Speak へのインライン化(`4220c02`) ④Smoke `UseUiaAnnouncer`→`MarkUiaTitle` 改名+`--uia` コメントの実態追随(`e1a65b3`+`0389a40`)。
- **テスト数**: 800 → 805(App 14→19)。ゲート全通過(Release 0 警告)。
- **読み替えの明確化**: 再スコープ文の「FakeAnnouncer による通知配線テスト」は、Stage 2 時点で注入可能な IAnnouncer 消費者が SearchController/CsvController(=Stage 4/6 の責務)のみのため「残存 Speech サブシステムの契約テスト」として実施した。FakeAnnouncer の実使用(通知文言検証)は Stage 4 以降。
- **レビュー由来の申し送り**: 実装計画の「申し送り」節を参照(空白のみメッセージの特徴付け=Stage 4/`_announcer` の readonly 化=Stage 8/GrepDialog の IAnnouncer 注入化=Stage 7 設計時判断)。
- **マージ**: main へ no-ff マージ=**`59ef10f`**(2026-07-13・マージ直前の main=`01bea88`)。NVDA 実機スポット 2 項目(検索「N 件中 M 件目」読み・Ctrl+Tab タブ名読み)OK・マージ後ゲート全緑 805。

### Stage 3 実施記録(2026-07-14)

- **完了**: 実装計画=`docs/plans/2026-07-13-test-strategy-phase2-stage3.md`(Subagent-Driven Development・各 Task 2 段レビュー)。①IUserPrompt/IFileDialogService シーム+薄い Adapter(MessageBoxUserPrompt/WinFormsFileDialogService)=`e82d5bb` ②FileController 注入化(MessageBox 7 箇所+ダイアログ直 new 3 箇所の機械的置換・挙動不変)=`a40a7b9`+XML doc 追随 `e30ada4` ③FileControllerTests 22 件(SaveAs ロールバック最優先・FakePrompt/FakeFileDialogService 導入)=`74eced3`/`561d71c`/`2525982`+レビュー対応 `d518729`/`3e3467d`/`d0f2580`。
- **PC-Talker 廃止の反映**: FileController は Speech 非依存(参照ゼロをコード精査で確認)=再スコープ不要。温存対象の RaiseUiaSelectionEvents 復帰配線(LoadInto)を開き直しテストで固定。L5 スポット確認は不要(§5 のとおりダイアログ抽象化のみで SR 経路不変)。
- **テスト数**: 805 → **827**(App 19→41・純増 +22)。ゲート全通過(Release 0 警告)。
- **★Task 6 のテストが実バグを発見(復元 dirty 化バグ・ユーザー判断=別ブランチで修正)**: RestoreFromBackup で復元したタブが Modified=false(実装コメントの意図「SetSavePoint しない → Modified=true」と乖離。TextBuffer は生成時に保存点を持つ)。影響: ①「*」なし ②次の Reconcile でバックアップ本体が削除 ③終了時の保存確認スキップ=復元内容のサイレント恒久喪失。Stage 3 では現行挙動の特徴付け(バグ注記付き)とし、マージ後の修正ブランチで復元 dirty 化+テスト期待反転を行う(詳細=実装計画の申し送り)。
- **レビューの学び(ミューテーションテストの常用)**: Task 4〜6 の品質レビューで「既定値と同値のため空振りする assert」を 3 件検出(SaveAs ロールバックの Encoding・LoadFailure の直前タブ復帰・path レコードの無題番号 0 化)、いずれも実装行の一時変異→赤→復元で実効性を証明のうえ修正。TabControl は選択中タブ除去後に**先頭(index 0)を自動選択**する(直前 index ではない)ことも実測で確定。Stage 4 以降のテストレビューでもミューテーション検証を標準とする。
- **マージ**: main へ no-ff マージ=**`e50c4e6`**(2026-07-14・マージ直前の main=`6fc1626`)。マージ後ゲート全緑 827(Release 0 警告)。フィーチャーブランチ削除済み。

### Stage 4 実施記録(2026-07-14)

- **完了**: 実装計画=`docs/plans/2026-07-14-test-strategy-phase2-stage4.md`(Subagent-Driven Development・各 Task 2 段レビュー)。①IFindReplaceView+FindReplaceCallbacks シーム(§2.2 からの精密化 2 点=ファクトリの `Func<FindReplaceCallbacks, IFindReplaceView>` 化・IsDisposed 追加。根拠は実装計画 §0)=`28a71a8` ②FindReplaceDialog のコールバック化で SearchController への型参照を除去(相互参照の切断=§5)+ShowAndFocus 集約(挙動不変)=`89572d9` ③SearchControllerTests 32 件(Open ライフサイクル/歩進(ゼロ幅前進)/文書切替リセット/置換系(VSCode 準拠・空置換前進・選択スコープ)/CSV 抑止/コールバック対応固定)+AnnouncerTests 1 件(空白のみメッセージ=IsNullOrEmpty ガードの特徴付け・Stage 2 申し送り回収)=`d14ceec`/`1213de1`/`5de45f8`。
- **レビュー由来の増分**: Task 3 品質レビュー指摘(同型 delegate の位置取り違えがコンパイル・テストとも検出不能)対応で、FindReplaceCallbacks 構築の名前付き引数化(`0ba0ad1`)+対応固定テスト 1 件を追加(計画 864→実績 865)。
- **テストユーティリティ共通化**: 可視 HostForm パターンを `TestHost.cs` へ抽出(「3 copy 目が現れたら」ルール発動=Stage 3 申し送りの判断)。DocumentManagerTests/FileControllerTests も追随=`64f0fbb`。Sta.cs の共有抽出(3 プロジェクト目条件)は未成立のため見送り継続。
- **テスト数**: 832 → **865**(App 41→74・純増 +33)。ゲート全通過(Release 0 警告)。
- **ミューテーション検証(Stage 3 由来の標準)**: Task 5〜7 の品質レビューで計 18 変異を実施し 17 kill(いずれも想定テストのみが赤=1:1 対応)。生存 1 は Find 未ヒット分岐の `_lastHit = null`(準等価変異)=実装計画の申し送りに記録。空振りリスク 1 件(ResetsStepState の文言 assert)は発声カウント assert で堅牢化済み。全テストが現行実装のまま初回 green=置換系に実装バグの兆候なし。
- **L5 スポット確認**: 不要(§5 のとおりダイアログ抽象化のみで SR 経路不変。Announce は同一 UiaAnnouncer・ダイアログ表示手順同順)。
- **マージ**: main へ no-ff マージ=**`c3a8415`**(2026-07-14・マージ直前の main=`eaa520c`・ブランチ 8 コミット)。マージ後ゲート全緑 865(Release 0 警告)。フィーチャーブランチ削除済み。

### Stage 5 実施記録(2026-07-14)

- **完了**: 実装計画=`docs/plans/2026-07-14-test-strategy-phase2-stage5.md`・設計書=`docs/plans/2026-07-14-test-strategy-phase2-stage5-design.md`(上位文書 §2.1/§2.2/§2.3 からの精密化 3 点=①型付き `IBackupWriter`(Action 束→Write/Delete/DeleteAll+`OnWriteFailed` フック) ②.NET 9 標準 `TimeProvider` を採用(自前 IClock 不採用・既定 `TimeProvider.System`) ③`Func<IBackupWriter> writerFactory` で Lazy 生成の意味論を保存)。①IBackupWriter+IRestorePrompt+RestoreOutcome シーム追加=`6ada960` ②SerialBackupWriter を IBackupWriter 化(既存 Enqueue は private 化・ctor に dir 追加)+WinFormsRestorePrompt 追加+BackupCoordinator 注入化(BackupStore/DateTime.UtcNow/RestoreDialog への参照ゼロ・Reconcile を internal 化)+MainForm 配線更新(1 コミット・挙動不変)=`ccf7aa9` ③FakeBackupWriter/FakeRestorePrompt/FakeTimeProvider 追加=`08b8a82` ④BackupCoordinatorTests 第 1 弾 10 件(ctor+UpdateSettings+Reconcile 登録+dirty サイクル)=`477f3cc` ⑤BackupCoordinatorTests 第 2 弾 16 件(失敗回復+復元 4 分岐+Shutdown 冪等+TimeProvider+対応固定)=`7705d45`。
- **テスト数**: 865 → **891**(App 74→100・純増 +26)。ゲート全通過(Release 0 警告)。
- **計画からの逸脱(いずれも Task 6/7 テスト側のみ・実装は挙動不変)**: ①`NewDoc(text, dirty=true)` の意味論反転(計画 `if (!dirty) SetSavePoint()` → 実装 `if (dirty) ClearSavePoint()`)。理由: `EditorControl.Text` セッターは `TextBuffer.FromString` で Modified=false 起点のフレッシュバッファに差し替わる(restore-dirty バグ修正 `59ad8b5`+`FileController.cs:342` と同型)。 ②`TryClose` シグネチャ(`Func<bool>` → `Func<Document, bool>`)。 ③`XunitException()` に文字列引数追加(xUnit v2 API 制約)。 ④`OfferRestore_ConfirmTrue_Restore_UsesCheckedRecords_AndInheritsId` の最終アサーション前に `Text 変更+ClearSavePoint+Reconcile` 追加。理由: OfferRestoreOnStartup が復元時に `_map[doc] = { LastSig = sig(SnapshotText), HasBackup=true }` を打ち、同 sig の Reconcile は BackupPlanner.Decide→None を返す(=陳腐化バックアップの上書き抑制=正しい挙動)。Id 引き継ぎ実証には sig 変化が必要=最小補正で本来の意図を保存。 ⑤計画本文の「15 件」表記はカウントミス(code samples 実数 16 件)=実績 +26。
- **ミューテーション検証(Stage 3/4 標準)**: Task 6 で 6/6 kill・Task 7 で 10/10 kill(計 16 kill・spec-critical mutation は 100% kill)。生存(いずれも既知の申し送り済み・本 Stage 対象外):`_writer ??= CreateWriter()` の二重呼び非再生成(=Stage 8 の Lazy ライフサイクル残置)/`HasBackup=false` で閉じた doc の Delete ガード(HasBackup=true でしか閉じるテストがない=Stage 8 でテスト追加検討)。
- **L5 スポット確認**: 不要(§5 のとおりダイアログ抽象化のみで SR 経路不変。Announce/バックアップ通知経路は無変更)。
- **申し送り(Stage 6 以降)**: 上位文書 §6 と合わせて実装計画の §9 に集約(Stage 8 候補=Lazy ライフサイクル/HasBackup=false ケースのテスト追加/`BackupStore.LoadAll/SweepTempFiles` の抽象化再評価/最終レビュー由来の追加=`SerialBackupWriter.Write` catch→`OnWriteFailed` 経路の実 I/O 統合テスト)。
- **マージ後クリーンアップ**: 最終ブランチレビュー M1(`FakeBackupWriter._failNextWriteId`+`FailNextWriteWith` は dead code)を削除=`48f1f4e`(実装計画外・レビュー由来の小コミット)。
- **マージ**: main へ no-ff マージ=**`f22a15a`**(2026-07-14・マージ直前の main=`8f25882`・ブランチ 7 コミット)。マージ後ゲート全緑 891(Release 0 警告・Core 573+Editor 218+App 100)。フィーチャーブランチは削除予定。手動スモークはユーザー判断でスキップ(SR 経路不変・§5 のとおり)。

### Stage 6 実施記録(2026-07-14)

- **完了**: 実装計画=`docs/plans/2026-07-14-test-strategy-phase2-stage6.md`(上位文書 §2.2 からの精密化 3 点=①CellPickResult の 3 相 record 化(Canceled/InvalidFormat/Ok)で通知文言 3 分岐を保存 ②1 始まり座標を Ok に含めて呼び出し側の変換責務を消す ③CsvController.ctor は ICellPicker を単純注入(ShowDialog 短命リソースのため Func 化しない・名前付き引数化=Stage 4 教訓の CSV 版)。①ICellPicker+CellPickResult シーム追加=`93f465b`+レビュー由来 cref 修飾追加=`3bdb432` ②WinFormsCellPicker Adapter+CsvController.GoToCell の 3 分岐 switch 置換+MainForm 配線(1 コミット・挙動不変)=`1a657b8`+レビュー由来 switch `default: throw` 追加=`0e754df` ③FakeCellPicker 追加=`4554bb7` ④CsvControllerTests 第 1 弾 14 件=`9142f7b`+レビュー由来ミューテーション実効性強化(EmptyCsv 副次通知検出+ParseableCsv 完全一致+Grid3x3 コメント修正)=`6f2d04c` ⑤CsvControllerTests 第 2 弾 13 件=`55cd3c0`+レビュー由来ミューテーション実効性強化(GoToCell 対応固定の非対称化+3 分岐の非既定位置化+名称の実挙動整合)=`f82ba61`。
- **テスト数**: 891 → **918**(App 100→127・純増 +27)。ゲート全通過(Release 0 警告)。
- **計画からの逸脱(いずれも実装は挙動不変・数え違い 2 件のみ)**: ①第 2 弾の計画本文プロース「11 件」はカウントミス(code sample 実数 13 件=MoveBottomRight 1+GoToCell 5+読み上げ 2+BeginEdit/AbortEdit 3+クランプ 1+parse-error 1)。実装は code sample に忠実に従い 13 件追加=実績 +13。計画の目標「合計 25 件・App 125・全 916」も同ずれで、実績「合計 27 件・App 127・全 918」に読み替え。②commit message は計画本文の「11 件」を verbatim 保持(Stage 5 と同型の運用=code sample が正・プロースの数はカウントミス)。
- **ミューテーション検証(Stage 3/4/5 標準)**: Task 5 レビューで 5 finding(うち real coverage hole 1・loose predicate 1・comment 誤記 1・doc 2 は Task 6 で自然回収)→修正 3 件を `6f2d04c` で反映。Task 6 レビューで 6 finding(うち CONFIRMED 4=非対称対応固定 1・非既定位置化 3・名称整合 1+plan 数え違い 1・PLAUSIBLE 2=cosmetic のみ)→修正 3 件を `f82ba61` で反映。Task 5 の EmptyCsv `Assert.Single` は実装者がガード削除の手動ミューテーションで kill を自己確認済み。
- **L5 実機 SR スポット確認**: **必須**(§5 のとおり CSV は SR 経路に触れる=`RaiseUiaSelectionEvents` トグルを含む)。マージ前に NVDA で下記を確認する(実装計画 §Task 8 Step 2 の 6 項目):CSV ファイルを開いて自動 CSV モード進入→矢印キー移動→左端/最終行の端メッセージ→G キーでセル指定移動 4 分岐→F2 セル編集(Enter/Esc)→モード OFF で通常編集の SR 挙動に復帰。
- **技術知見(Task 5 レビュー由来)**: 「Test smell — assertion on default values」を Stage 6 の CSV 系で複数検出(GoToCell の 3 no-change テストが `(0,0)` を assert)。「no-change テストは非既定位置(例:(1,1) 移動後)から検証開始する」をレビュー標準に追加。
- **マージ**: main へ no-ff マージ=**`f1f76ee`**(2026-07-14・マージ直前の main=`aef52b3`・ブランチ 10 コミット)。マージ後ゲート全緑 918(Release 0 警告・Core 573+Editor 218+App 127)。フィーチャーブランチ削除済み。L5 実機 SR スポット確認済み(6 項目すべて OK・NVDA)。

### Stage 7 実施記録(2026-07-15)

- **完了**: 実装計画=`docs/plans/2026-07-14-test-strategy-phase2-stage7.md`(上位文書 §2.2 からの精密化 4 点=①GrepCallbacks/GrepResultsCallbacks の 2 束(Stage 4 と同型)で相互参照を切る ②`Run` → `internal Task RunAsync()`(戻り値の Task をテストが await) ③`Func<GrepRequest, IProgress<GrepProgress>?, CancellationToken, Task<GrepOutcome>>` の注入化(実 I/O ゼロ・TaskCompletionSource で追い越し/BeginClose 決定的タイミング) ④GrepDialog の UiaAnnouncer 直生成温存=Stage 2 由来の申し送りへの結論)。①IGrepView+IGrepResultsView シーム追加=`5d7d5a7` ②GrepDialog/GrepResultsWindow のコールバック化で GrepController への型参照を除去+`Run`→`RunAsync`+検索関数注入+MainForm 配線(1 コミット・挙動不変)=`4d7f3d3` ③FakeGrepView/FakeGrepResultsView/FakeGrepSearchFn 追加+IGrepView.Visible docstring 更新(Task 3 レビュー Minor-1 折り込み)=`878f33a` ④GrepControllerTests 第 1 弾 10 件=`675ad39`+レビュー由来修正(TempFolder 導入で DefaultFolder 分岐を非衝突化+RunningLog 対称化)=`1d41f7c` ⑤GrepControllerTests 第 2 弾 10 件=`2b5df58`。
- **テスト数**: 918 → **938**(App 127→147・純増 +20)。ゲート全通過(Release 0 警告)。
- **計画からの逸脱(いずれも実装は挙動不変)**: ①`BeginClose_DuringRun_SuppressesResults` のアサーションを plan 原案 `Assert.Empty(host.Results.PopulateLog) + Assert.Equal(0, host.Results.ShowResultsCount)` から `Assert.Equal(0, host.ResultsFactoryCalls) + Assert.Null(host.Results)` に変更(BeginClose では await 後 guard で早期 return し `ShowResults` にすら到達しない=`_resultsFactory` 未呼出=`Host.Results` は null! のまま=plan 原案は NRE 失敗)。修正後は stronger invariant(ビュー生成そのものが抑止される)を固定。「検索を開始しました」の Notifications assertion は残して発声順序も保存。 ②Task 3 で計画の「従来 GrepController.Open が…」ShowAndFocus XMLコメントを、Step 7 grep 検証(GrepController への言及ゼロ)と両立するため「従来 Open 側で行っていた表示手順…」に微調整。 ③Task 3 で `GrepResultsWindow.ShowResults(Form)` を `ShowResults(IWin32Window)` に緩和(IGrepResultsView 準拠+MainForm は `Form: IWin32Window` なので呼び出し側無変更)。
- **ミューテーション検証(Stage 3/4/5/6 標準)**: Task 5 レビューで Important 1(DefaultFolder の親ディレクトリが MyDocuments=フォールバックと同一で `if (path is not null)` ブロック削除の変異が生存)+Minor 3 検出→`1d41f7c` で修正(TempFolder 定数導入+MissingFolder/InvalidRegex の RunningLog 対称化)。Task 6 レビューは 20 件の spec-critical mutation を 100% kill 可能と確認(BeginClose 修正が stronger invariant として承認・catch 内 guard の分岐被覆なしは Stage 8 申し送り候補として記録)。
- **L5 スポット確認**: 不要(§5 のとおりダイアログ抽象化のみで SR 経路不変=`RaiseNotification` は同一 UiaAnnouncer・結果窓 SR ネイティブ読み不変)。手動スモーク 1〜2 分は任意。
- **申し送り(Stage 8 以降)**: 実装計画 §申し送り+Task 6 レビュー由来=①`catch` 内 guard `if (!d.IsDisposed && ReferenceEquals(_cts, cts))` の分岐被覆(準等価変異)/②`Cancel_CancelsCurrentRun_...` の副作用側(Summary 発声・Populate)assertion 網羅/③`_view.Visible` の BeginClose/Cancel 経路での不変固定/④Progress コールバック内の追い越し guard 3 条件の直接テスト/⑤`ShowResults` 内の `_resultsView.IsDisposed` 分岐被覆/⑥GrepDialog の `UiaAnnouncer` 直生成の外出し(Stage 2 由来・本 Stage 見送り)。
- **マージ**: main へ no-ff マージ=**`01e52ad`**(2026-07-15・マージ直前の main=`f633ab7`・ブランチ 7 コミット)。マージ後ゲート全緑 938(Release 0 警告・Core 573+Editor 218+App 147)。フィーチャーブランチ削除済み。L5 不要のため手動 SR 確認スキップ(§5 のとおりダイアログ抽象化のみで SR 経路不変)。

### Stage 8 実施記録(2026-07-15・Phase 2 完了)

- **完了(薄い Stage 8)**: 設計書=`docs/plans/2026-07-15-test-strategy-phase2-stage8-design.md`・実装計画=`docs/plans/2026-07-15-test-strategy-phase2-stage8.md`。設計書 §4 の原案「MainForm 痩身(全コマンド抽出)」から益薄が過半をスコープアウトし、4 Task 構成に絞る(A=型狭化+readonly+デッドコード除去+名前付き引数化・B=KinsokuFormatController 抽出・C=GrepController `_jumpTo` 除去・D=Stage 7 由来の高価値テスト)。SDD・各 Task 2 段レビュー+最終ブランチレビュー「Ready to merge」。
- **Task A(4 サブコミット・純リファクタ・挙動不変)**: `_owner` を Form → IWin32Window(Stage 4/6/7 申し送り)=`b225956` / `_announcer` を readonly 化(Stage 2 申し送り)=`04df93f` / FileController ctor 名前付き引数化=`d44fe49` / FindPrev `_lastHit` 三項式デッドコード除去(Stage 4 申し送り)=`c2b365d`。テスト数不変。
- **Task B(KinsokuFormatController 抽出)**: `0757622` 抽出+テスト 8 件(5 Fact+3 Theory rows)+`6b3a185` PartialSelection 補強 1 件+WholeText コメント整合(レビュー由来)。**MainForm.FormatWithKinsoku は 1 行 dispatch に痩身**。ミューテーション 7/7 kill(元 5+レビュー由来 2)。
- **Task C(GrepController から `_jumpTo` 除去)**: `522c382` `Action<GrepHit>` フィールド+ctor 引数削除・`resultsFactory` を `Func<IGrepResultsView>` に変更・xmldoc 更新・反射テストで機械固定+`18a7ebc` IGrepResultsView/FakeGrepResultsView の xmldoc 追随(レビュー由来)。**GrepController は code-level で `Action<GrepHit>`/`GrepResultsCallbacks` を持たない**(grep 検証・xmldoc cref のみ許容)。
- **Task D(Stage 7 由来の高価値テスト 6 件・実装無変更)**: `5c025c2` テスト 7 件(D-1 Dispose 副作用・D-2 Progress 追い越し guard 3 条件・D-3 catch guard 準等価変異 kill 2)+FakeGrepSearchFn.Invocations に IProgress<> 追加+SynchronousSyncContext テストヘルパ+`14a106a` Cancel_DoesNotChangeViewVisibility 削除(tautological=YAGNI)+SynchronousSyncContext xmldoc 安全性根拠追記(レビュー由来)。ミューテーション 5/5 kill(**Stage 7 の唯一生存変異=catch 内 guard 準等価が kill 化**)。
- **テスト数**: 938 → **954**(Core 573+Editor 218+App 163=純増 +16)。**Release 0 warnings**。マージ後ゲート全緑。
- **設計不変達成**: ①MainForm.FormatWithKinsoku 1 行 ②GrepController `Action<GrepHit>`/`GrepResultsCallbacks` code 参照ゼロ ③3 Controller `_owner: IWin32Window` ④`MainForm._announcer: readonly` ⑤反射テストで Controller_HasNoJumpToField 機械固定。
- **計画からの主な逸脱(いずれも実装は挙動不変)**: ①Task A.2 ctor 再順序=`_announcer = null!` 廃止で lambda キャプチャの CS8602 対策 ②Task B の PartialSelection 追加=元テストが (0,20) で whole/partial 区別不能=`start = whole ? 0 : 0` 変異生存を新テスト(prefix+CJK+suffix)で kill 化 ③Task D の D-1 テスト名変更(`Cancel_...` → `Dispose_...`)=`Cancel()` は `_cts?.Cancel()` のみで overtake guard 3 条件どれも発火せず=真の未被覆分岐は `d.IsDisposed` ④Task D の Cancel_DoesNotChangeViewVisibility 削除(tautological=IGrepView に Hide なし+Controller が Visible に書き込まない=YAGNI)⑤Task D の SynchronousSyncContext 追加(Progress<T> の SC キャプチャ race を防ぐテストヘルパ・test-only)。
- **レビューの学び(標準)追加**: Stage 8 の 2 段レビューで新たな観点を 2 件確立=①「partial-selection テストは prefix/suffix を除外する fixture を使う(選択境界と本文境界が一致しないよう強制する)」=whole/partial の start/len 計算区別を担保・**Stage 6「no-change テストは非既定位置から検証開始」の姉妹規約** ②「Cancel/Guard 系テストの assertion は guard 発火条件と assertion 前提が一致することを確認する」=D-1 pivot の教訓・レビュー checklist に追加。
- **L5 不要**(§5 のとおり Task B は UiaAnnouncer 単一経路のまま・文言不変・SR 経路不変。Task A/C/D は SR 経路無関与)。手動スモーク不要。
- **申し送り(Phase 2 終了後の恒久バックログ)**:
  - Task B レビュー由来 Minor: `ed.Focus()` 呼び出し順序の検証が offscreen host では不能(Stage-wide test 基盤の申し送り=Controller テストで Focus() 検証を可能にする test infra 拡張が今後の課題)
  - Task C レビュー由来 Minor: 反射テスト `Controller_HasNoJumpToField_NorActionOfGrepHitField` は field のみ検査=ctor param 経由の閉じ込め回帰を検出不能=`GetConstructors().SelectMany(c => c.GetParameters())` によるスキャン強化の余地
  - Stage 8 設計書 §1.2〜§1.4 の未回収項目(Stage 5/6/7 由来の小テスト追加)は個別 PR で必要時に対応
  - MainForm 原案の他コマンド抽出(§1.1)は継続見送り。将来 MainForm がさらに肥大化したら再評価
- **マージ**: main へ no-ff マージ=**`fb9159b`**(2026-07-15・マージ直前の main=`4bd3b09`・ブランチ 10 コミット)。マージ後ゲート全緑 954(Release 0 警告・Core 573+Editor 218+App 163)。フィーチャーブランチ削除済み。**Phase 2 は Stage 8 で終了宣言**。Phase 3(SR 性能ゲート・任意)は実機 SR で退行が観測された場合のみ着手検討。local main は origin より先行(未 push)。
