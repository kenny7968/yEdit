# テスト戦略 Phase 2 Stage 5: BackupCoordinator シーム導入+テスト 設計書

- 日付: 2026-07-14
- 上位文書: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` §2.1・§2.2・§2.3・§3 BackupCoordinator 行・§4 Stage 5・§5
- ベースライン: main `8f25882`(Stage 4 マージ済み)・テスト数 865(Core 573+Editor 218+App 74)
- 前提: 復元 dirty 化バグ修正(`59ad8b5`)は既に main マージ済み=復元タブ Modified=true が期待挙動

## 0. 方針(要旨)

- **BackupCoordinator の 3 境界(時計・背景書込・復元ダイアログ)をシーム化**し、App.Tests から Reconcile を直叩きで駆動する。
- **Reconcile の Timer 抽象化はしない**(§2.3 の指示どおり)。テストは `internal Reconcile()` を直呼びして状態機械を観測する。
- **BackupStore(Core 静的 I/O)は抽象化しない**。Coordinator が直参照していた 4 箇所は IBackupWriter を通す=Adapter(SerialBackupWriter)側に集約=Coordinator は BackupStore への参照ゼロ。
- Document/Editor はモックしない(§0)。実 DocumentManager+実 EditorControl を STA 上で使う。

## 1. 上位文書からの逸脱(3 点・いずれも精密化)

| 上位文書 §2.1/§2.2/§2.3 | 本 Stage | 理由 |
|---|---|---|
| `IBackupWriter { void Enqueue(Action job); void Dispose(); }` | `IBackupWriter : IDisposable { Write(BackupRecord), Delete(id), DeleteAll() }` の型付き 3 メソッドに精密化 | Stage 3 の IUserPrompt/IFileDialogService・Stage 4 の IFindReplaceView 精密化と同型の判断。Action 束の Fake は Action を同期実行=**BackupStore の実 I/O が走ってしまう**穴があり、Coordinator も静的 BackupStore への参照を持ち続ける。型付きにすると Fake が完全純化され、Coordinator は BackupStore への参照ゼロに落ちる |
| `TimeProvider を注入` | .NET 9 標準の `System.TimeProvider` を採用(自前 IClock を切らない)。既定引数 `TimeProvider.System` | 標準の再発明を避ける。テストは `FakeTimeProvider`(自作 or Microsoft.Extensions.TimeProvider.Testing)を差す |
| `Reconcile() を internal に` | 追加作業は「private → internal」のみ | `InternalsVisibleTo yEdit.App.Tests` は既に csproj 側で導入済み(`src\yEdit.App\yEdit.App.csproj:26`) |

そのほか上位文書どおり: `IRestorePrompt` へ抽象化・タイマー抽象化しない・BackupStore は抽象化しない(Core テスト済み)。

## 2. 現状の結合分析(2026-07-14 コード精読)

- BackupCoordinator の外部 6 境界(冒頭表): 時計=`DateTime.UtcNow`(BuildRecord 1 箇所)/背景書込=`SerialBackupWriter` 直 new(ctor+UpdateSettings で遅延生成)/復元ダイアログ=`RestoreDialog` 直 new(OfferRestoreOnStartup 1 箇所)/Timer=直 new(抽象化しない)/BackupStore=`Write` 1・`Delete` 3・`DeleteAll` 1・`LoadAll` 1・`SweepTempFiles` 1(**LoadAll/SweepTempFiles の 2 箇所は Coordinator が UI スレッド同期で呼ぶ=非抽象化・現状維持**)
- MainForm 側の触点: `MainForm.cs:60`(生成)・`MainForm.cs:107`(OfferRestoreOnStartup)・`MainForm.cs:160`(Shutdown)・`MainForm.cs:381`(UpdateSettings)= **触面はこの 4 行のみ**
- 復元 dirty 化バグ修正(`59ad8b5`)は既にマージ済み=**復元タブ=Modified=true・SetSavePoint 打たない・タブに「\*」付く** が現行挙動

## 3. 導入するシーム

### 3.1 `src/yEdit.App/Abstractions/IBackupWriter.cs`(新規)

```csharp
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// バックアップの背景書込ジョブ受け(Phase 2 Stage 5・上位文書 §2.1 の精密化)。
/// Coordinator が BackupStore への静的参照を持たないよう、Action 束ではなく型付きの
/// 3 メソッドで表面を切る。SerialBackupWriter が既存の BlockingCollection 直列実行で
/// 実装し、Fake は in-memory Dictionary で完全に I/O から独立する。
/// </summary>
public interface IBackupWriter : IDisposable
{
    void Write(BackupRecord record);
    void Delete(string id);
    void DeleteAll();
}
```

### 3.2 `src/yEdit.App/Abstractions/IRestorePrompt.cs`(新規)

```csharp
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>復元ダイアログのユーザー選択(Phase 2 Stage 5・上位文書 §2.2)。</summary>
public enum RestoreAction { Later, Restore, DiscardAll }

/// <summary>復元ダイアログの結果 record。RestoreAction が Restore 以外なら Checked は空配列。</summary>
public sealed record RestoreOutcome(RestoreAction Action, IReadOnlyList<BackupRecord> Checked)
{
    public static readonly RestoreOutcome LaterEmpty = new(RestoreAction.Later, Array.Empty<BackupRecord>());
}

/// <summary>
/// 起動時の復元ダイアログの Controller 向け表面。ダイアログを ShowDialog し、
/// ユーザー選択(Later/Restore/DiscardAll)+チェック済み records をまとめて返す。
/// </summary>
public interface IRestorePrompt
{
    RestoreOutcome Prompt(IWin32Window owner, IReadOnlyList<BackupRecord> records);
}
```

### 3.3 Adapter(既存 UI をラップ)

**`src/yEdit.App/SerialBackupWriter.cs` を IBackupWriter 化**:
- 既存の public `Enqueue(Action)` を **private ヘルパに降格**(BlockingCollection 直列実行を担う実装詳細)
- ctor 引数に `string directory` を追加(BackupStore.Write の第 1 引数を Adapter 側で保持)
- 追加メソッド: `Write(BackupRecord rec) => Enqueue(() => BackupStore.Write(_dir, rec));`・`Delete(string id) => Enqueue(() => BackupStore.Delete(_dir, id));`・`DeleteAll() => Enqueue(() => BackupStore.DeleteAll(_dir));`
- 失敗時のリカバリ(`_failed` へ積む)は Coordinator 側の関心だったため、**Adapter は失敗を握り潰したまま**(catch は無音・現行踏襲)。「書込失敗を Coordinator が知る」経路は次項 §4 の writer コールバックで残す

**`src/yEdit.App/WinFormsRestorePrompt.cs`(新規)**:
```csharp
namespace yEdit.App;

public sealed class WinFormsRestorePrompt : IRestorePrompt
{
    public RestoreOutcome Prompt(IWin32Window owner, IReadOnlyList<BackupRecord> records)
    {
        using var dlg = new RestoreDialog(records);
        dlg.ShowDialog(owner);
        return dlg.Action switch
        {
            RestoreDialog.RestoreAction.Restore    => new(RestoreAction.Restore, dlg.Checked),
            RestoreDialog.RestoreAction.DiscardAll => new(RestoreAction.DiscardAll, Array.Empty<BackupRecord>()),
            _                                      => RestoreOutcome.LaterEmpty,
        };
    }
}
```
- `RestoreDialog.RestoreAction`(内部 enum)は既存維持。Adapter で App 層公開 enum(`RestoreAction`)にマップ

### 3.4 失敗回復経路の保存(重要)

現行の `EnqueueWrite` は `catch { _failed.Enqueue(id); }` を **Coordinator が定義した Action の中**で行っており、次 Reconcile で `_failed` を drain して `ForceWrite` を立てる。型付き IBackupWriter に置換すると Adapter 側で catch する形になり、Coordinator への通知経路が切れる。

**解決**: IBackupWriter に「書込失敗時のコールバック」を渡す方針は表面を汚す(=IBackupWriter.Write に Action<string>? onFailed を足す等)。代わりに **`IBackupWriter.Write` は例外を投げない前提とし、失敗の通知は Adapter が保持する `Action<string>? OnWriteFailed`(Coordinator が ctor で登録するイベント風フック)** で伝える。

```csharp
public interface IBackupWriter : IDisposable
{
    /// <summary>書込失敗を UI スレッド側に通知するためのフック。null なら失敗は握り潰す(=Fake のデフォルト)。</summary>
    Action<string>? OnWriteFailed { get; set; }
    void Write(BackupRecord record);
    void Delete(string id);
    void DeleteAll();
}
```

Coordinator ctor でセット:
```csharp
_writer.OnWriteFailed = id => _failed.Enqueue(id);
```

Adapter(SerialBackupWriter):
```csharp
public void Write(BackupRecord record) => Enqueue(() =>
{
    try { BackupStore.Write(_dir, record); }
    catch { OnWriteFailed?.Invoke(record.Id); }
});
```

これで「失敗回復のフロー」を挙動不変で保存できる(現行と 1:1)。

## 4. BackupCoordinator の変更(挙動不変)

### 4.1 ctor 変更

```csharp
public BackupCoordinator(
    DocumentManager docs,
    bool enabled,
    int intervalSeconds,
    TimeProvider clock,
    Func<IBackupWriter> writerFactory,
    IRestorePrompt restorePrompt,
    string? directory = null)
```

- `writerFactory` は **Lazy 生成の意味論を保存**するための Func 型(現行 `_writer ??= new SerialBackupWriter()` と同型)。ctor 時点で無効な場合はまだ生成しない=現行「無効時はスレッド生成なし」を維持
- `directory` は `BackupStore.LoadAll`/`SweepTempFiles`(UI スレッド同期で Coordinator が直接呼び続ける 2 箇所)用に残す

内部フィールド:
```csharp
private readonly TimeProvider _clock;
private readonly Func<IBackupWriter> _writerFactory;
private readonly IRestorePrompt _restorePrompt;
private IBackupWriter? _writer;                // Lazy: 有効化まで null
```

初期化:
```csharp
_clock = clock;
_writerFactory = writerFactory;
_restorePrompt = restorePrompt;
if (_enabled) { _writer = _writerFactory(); _writer.OnWriteFailed = OnBackgroundWriteFailed; _timer.Start(); }
```

`OnBackgroundWriteFailed(string id)` は `_failed.Enqueue(id)` を呼ぶ private メソッド(現行の失敗経路と同義)。

### 4.2 UpdateSettings 変更

`_writer ??= new SerialBackupWriter();` → `_writer ??= CreateWriter();`(内部で `_writerFactory()` を呼び `OnWriteFailed` を配線する)。

### 4.3 呼び出し置換(全 4 箇所)

| 現行 | 変更後 |
|---|---|
| `_writer?.Enqueue(() => BackupStore.Write(_dir, rec))` (EnqueueWrite・catch は Action 内) | `_writer?.Write(rec)` |
| `_writer?.Enqueue(() => BackupStore.Delete(_dir, id))` (2 箇所: Reconcile の閉じた doc・Reconcile の clean→Delete・Shutdown の管理分削除=**計 3 箇所**) | `_writer?.Delete(id)` |
| `_writer?.Enqueue(() => BackupStore.DeleteAll(_dir))` (DiscardAll) | `_writer?.DeleteAll()` |
| `TimestampUtc: DateTime.UtcNow` (BuildRecord) | `TimestampUtc: _clock.GetUtcNow().UtcDateTime` |

`BackupStore.LoadAll` / `SweepTempFiles`(OfferRestoreOnStartup 冒頭の 2 箇所)は Coordinator が UI スレッド同期で直呼び継続。

### 4.4 OfferRestoreOnStartup 変更

```csharp
// 現行の RestoreDialog 生成+ShowDialog+switch 3 分岐を、以下に置換
var outcome = _restorePrompt.Prompt(owner, ordered);
switch (outcome.Action)
{
    case RestoreAction.Restore:
        foreach (var rec in outcome.Checked) { /* 既存 try/catch と _map[doc] 登録は不変 */ }
        break;
    case RestoreAction.DiscardAll:
        _writer?.DeleteAll();
        break;
    case RestoreAction.Later:
        break;
}
```

`confirm=false` の全件復元経路は無変更(UI 非経由)。

### 4.5 Reconcile を internal 化

```csharp
- private void Reconcile()
+ internal void Reconcile()
```

### 4.6 MainForm 配線更新(`MainForm.cs:60`)

```csharp
_backup = new BackupCoordinator(
    _docs, _settings.BackupEnabled, _settings.BackupIntervalSeconds,
    TimeProvider.System,
    () => new SerialBackupWriter(BackupStore.DefaultDirectory),
    new WinFormsRestorePrompt());
```

## 5. テスト観点(App.Tests の責務)

真実源: 配線・状態遷移・失敗回復。BackupPlanner の判定正しさ・BackupStore の I/O 正しさは Core 検証済み=再検証しない。

| グループ | 観点(件数目安) |
|---|---|
| ctor+UpdateSettings(4 件) | 間隔クランプ(5 未満→5・3600 超→3600)・enabled→disabled で Timer 停止・disabled→enabled で writer 生成+Timer 開始+即 Reconcile |
| Reconcile 登録(3 件) | 新規 dirty=即 Write・新規 clean=書込なし(Write 未呼び)・閉じた doc=Delete 投入+_map から除去 |
| Reconcile dirty サイクル(4 件) | 同 sig 連続=None(Write は 1 回のみ)・sig 変化=再 Write・dirty→clean=Delete+HasBackup=false・clean 連続=None(HasBackup=false のまま) |
| 失敗回復(2 件) | `OnBackgroundWriteFailed` 直呼びで `_failed` へ積む→次 Reconcile で対象 doc に ForceWrite=true=同 sig でも再 Write が走る・成功後 ForceWrite=false |
| OfferRestoreOnStartup(6 件) | confirm=false・全件復元(件数戻し=records 数)・1 件不正で他は生存(FakeFile で 1 件 restore 失敗を仕込む)・Restore Checked のみ復元(元 Id 引継ぎ=`_map[doc].Id == rec.Id`)・DiscardAll で writer.DeleteAll 投入・Later は何もしない(map/writer とも無変化)・records 0 件で 0 戻し(dialog も出さない) |
| Shutdown(3 件) | 管理分の HasBackup=true を全 Delete 投入+writer.Dispose・冪等(2 回呼び無害)・タイマー停止 |
| Dispose 異常系(2 件) | Shutdown 未経由での timer/writer 解放・writer 生成前(無効のまま)なら writer=null で無害 |
| TimeProvider 経由(1 件) | FakeTimeProvider を差し込み BackupRecord.TimestampUtc が固定時刻になることを 1 件で固定 |

**合計 25 件**。テスト数 865 → **890**(App 74→99)を見込む。

### 5.1 Fake 群

- **FakeBackupWriter**: `Dictionary<string, BackupRecord> Store` + `List<string> Deleted` + `bool DisposedAll` + `bool Disposed` + `Action<string>? OnWriteFailed { get; set; }` + `Action<BackupRecord>? BeforeWrite`(テストで失敗注入する場合の切り欠き=省略可)。Dispose は `Disposed=true` のみ
- **FakeRestorePrompt**: `RestoreOutcome NextOutcome { get; set; } = RestoreOutcome.LaterEmpty;` + `int PromptCount;` + `IReadOnlyList<BackupRecord>? LastRecords;`。`Prompt` は `LastRecords=records; PromptCount++; return NextOutcome;`
- **FakeTimeProvider**: `.NET 9` の `TimeProvider` を継承した最小実装(GetUtcNow を固定時刻に)。Microsoft.Extensions.TimeProvider.Testing NuGet を採用する場合はそちら(**採用は Task 2 で判断・自作優先=依存増を避ける**)

### 5.2 テストハーネス

`SearchControllerTests` の `Host` パターンを踏襲: `TestHost.CreateWithDocs()` で HostForm+DocumentManager を起こし、`FakeBackupWriter`/`FakeRestorePrompt`/`FakeTimeProvider` を注入して BackupCoordinator を生成。`_writerFactory` は Fake を返すラムダ。バックアップディレクトリは無害な `Path.GetTempPath()` サブディレクトリ(Reconcile では `_writer.Write` 経由になるので実 I/O は起きない・OfferRestoreOnStartup の `LoadAll`/`SweepTempFiles` のみ**空ディレクトリの Enumerate**が走る=無害)。

## 6. Task 分解(1 Stage=1 ブランチ=1 no-ff マージ)

| Task | 内容 |
|---|---|
| 1 | ブランチ作成 `feature/test-strategy-phase2-stage5`(main=`8f25882` から) |
| 2 | シーム定義: `Abstractions/IBackupWriter.cs`+`Abstractions/IRestorePrompt.cs`(未配線) |
| 3 | `SerialBackupWriter` を IBackupWriter 化(既存 Enqueue は private 化・ctor に dir 引数追加・OnWriteFailed 追加)+`WinFormsRestorePrompt` 追加 |
| 4 | `BackupCoordinator` 注入化(ctor+writerFactory+clock+restorePrompt・4 箇所置換・BuildRecord clock 化・Reconcile internal 化)+MainForm 配線更新 |
| 5 | 挙動不変確認: ビルド 0 警告+既存全テスト緑(Core 573+Editor 218+App 74) |
| 6 | Fake 群追加(FakeBackupWriter/FakeRestorePrompt/FakeTimeProvider) |
| 7 | BackupCoordinatorTests 25 件(観点表どおり)+特徴付け green を確認 |
| 8 | ローカルゲート+設計書へ実施記録追記 |
| 9 | 別エージェントによるコードレビュー(ミューテーション検証標準)→L5 不要(SR 経路不変)→no-ff マージ→設計書へマージコミット追記 |

各 Task の DoD: `dotnet build yEdit.sln -c Release -warnaserror` 0 警告・テスト数純増・挙動不変(通知文言/表示手順/失敗回復の順序を変えない)。

## 7. リスクと対策

- **`_writer` 常時生成 vs Lazy**: writerFactory 化で Lazy 生成の意味論を保存する(§4.1)。無効時のリソース増加なし。
- **失敗回復経路の切断**: IBackupWriter.OnWriteFailed フックで Coordinator に通知を戻す(§3.4)。Adapter 側で catch し、Coordinator が受け取る=挙動 1:1。
- **BackupStore への参照残存**: Coordinator の diff で `BackupStore` grep が 0 件になることを Task 4 で機械確認。
- **RestoreDialog.RestoreAction 内部 enum**: Adapter(WinFormsRestorePrompt)で App 層公開 `RestoreAction` へマップ=UI 側は無変更。
- **TimeProvider 依存**: .NET 9 標準=追加パッケージ不要。テスト用 FakeTimeProvider は 6 行の自作(GetUtcNow の固定戻し)で足りる=NuGet 追加は避ける。
- **特徴付けが赤の場合**: 原則テスト側を現行挙動へ合わせる。**ただしバックアップ書込/削除経路の赤はデータ喪失リスク=修正せずユーザーへ報告**(Stage 4 の置換系と同型の規約)。

## 8. DoD(Stage 5)

1. `tools/pre-merge-check.ps1` 全緑(Release ビルド 0 警告)
2. テスト数 865 → **890+**(App 74→99+・実測で確定)
3. **挙動不変**: OfferRestoreOnStartup の 4 分岐(confirm=false/Restore/DiscardAll/Later)・Shutdown 冪等・Reconcile の Write/Delete/None 判定・失敗回復のフロー(diff レビューで機械的に確認)
4. `BackupCoordinator.cs` から `BackupStore` への参照ゼロ・`DateTime.UtcNow` ゼロ・`RestoreDialog` への参照ゼロ(grep 3 本で確認)
5. 別エージェントによるコードレビュー(マージ前・ミューテーション検証標準)
6. L5 実機 SR スポット確認は**不要**(根拠: 上位文書 §5「他 Stage はダイアログ抽象化のみで SR 経路不変」)。手動スモーク 1 分は任意(復元ダイアログを 1 度出す)
7. main へ no-ff マージ+設計書へ実施記録・マージハッシュ追記

## 9. 申し送り(Stage 6 以降へ)

- **次 Stage**: CsvController(ICellPicker 導入)=上位文書 §4 Stage 6。マージ前に L5 実機 SR スポット必須(CSV セル読み)。
- **Stage 8 候補**: `BackupCoordinator._writer` の Lazy ライフサイクル(writerFactory 化)は残置。`SerialBackupWriter` の外部 API(private 化した Enqueue の要否)は本 Stage で「MainForm から使わない」ことを確認済み=次 Stage 以降で観察対象。
- **Sta.cs の共有抽出**: 3 プロジェクト目条件は継続監視(本 Stage では発動しない)。
- **BackupStore の LoadAll/SweepTempFiles**: 抽象化しない YAGNI 判断。将来「起動時経路も Fake で駆動したい」需要が生じた場合の候補=Stage 8 で判断。
