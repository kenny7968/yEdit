# 起動時に前回開いていたファイルを開く 設計書

- **作成日**: 2026-07-23
- **対象**: `src/yEdit.Core`(Settings 拡張・LastSessionBuffersStore 新設)+ `src/yEdit.App`(MainForm・FileController・BasicSettingsTab)+ 説明書
- **区分**: 機能追加(設定 ON でのみ有効化=既定オフで既存挙動不変)
- **前提**: 通常終了時のセッション情報を settings.json + 別ファイル(last-session-buffers.json)に保存し、次回起動時に復元する。異常終了時は現行のバックアップ復元が優先される。

## 0. スコープと決定事項サマリ

**採用方針**: 案 B ハイブリッド構成 ―― 有効/無効フラグとタブメタ(パス・無題番号・カーソル位置・アクティブフラグ・BufferKey)は `settings.json` へ、無題タブの本文のみ別ファイル `%APPDATA%\yEdit\last-session-buffers.json` へ分離して保存する。

**主要判断**:
- 復元対象: パスあり タブ + **無題タブ(本文も復元)**。ただし本文欠落時は該当レコード skip(空の無題タブを追加しない)。
- 既定値: **オフ**(既存の起動挙動=空無題タブ 1 個 を維持し、明示 ON で復元)。
- 復元範囲: **パス + タブ順 + アクティブタブ + カーソル位置(行・桁)**。文字コード指定・スクロール位置・ウィンドウ内位置・分割ペインなどは対象外(YAGNI)。
- 異常終了との関係: **バックアップ復元が優先**。`BackupCoordinator.OfferRestoreOnStartup` の戻り値 `restored > 0` なら前回タブ復元はスキップ。
- 無題本文の分離: 1 タブあたり 1 M chars 上限(超過は BufferKey=null に落として枠だけ復元)。破損時は grace degradation。

**非対象(scope out)**:
- 文字コード指定の記憶(前回「開き直す」で明示指定した codepage の記憶)
- スクロール位置・折りたたみ状態・選択範囲の復元
- ウィンドウ内位置・分割ペイン・ドッキング状態の復元
- L4(bench)・L5(実機 SR)は不要判定(§6 参照)

## 1. アーキテクチャ

### 新規コンポーネント(Core 層)

- `yEdit.Core.Session.SessionTabRecord`(record):タブ 1 個の永続表現。
- `yEdit.Core.Session.LastSessionSnapshot`(record):`List<SessionTabRecord> Tabs` を持つ。
- `yEdit.Core.Session.LastSessionBuffersStore`(静的):`%APPDATA%\yEdit\last-session-buffers.json` の Load / Save / Delete。

### AppSettings 拡張

- `bool RestoreOpenFilesOnStartup { get; set; } = false;`
- `LastSessionSnapshot? LastSession { get; set; }`

`SettingsStore.Normalize` で `LastSession` の防御的補正を行う(§2.3)。

### App 層の配線

- `MainForm.OnFormClosing`:未保存確認通過後に snapshot 構築+保存(既存の WindowWidth/Height 反映と同じブロック)。
- `MainForm.OnShown`:`_backup.OfferRestoreOnStartup` の戻り値=0 のときだけ前回タブ復元を発火。
- `FileController.RestoreLastSession(snap, buffers, initialEmpty)`:復元経路の実体。パスあり ファイルは既存 `TryOpenOrActivate` 経由、無題タブは `_docs.CreateNew` 直呼び+本文差し込み。復元タブが 1 個以上できたら `initialEmpty`(ctor で作った空無題タブ)を閉じる。
- `BasicSettingsTab`:「起動時に前回開いていたファイルを開く」CheckBox を追加。

### 分離の理由

- Core.Session に純ロジックを閉じ、UI スレッド依存を持たせない → App.Tests から Dictionary 直渡しで復元経路を検証できる。
- 保存 I/O は既存 `SaveSettingsSafe` と対称の 1 メソッド(`SaveLastSessionBuffersSafe`)を MainForm 側に持ち、既存の握り潰しポリシーと揃える。
- 復元経路を FileController に置くのは、TryOpenOrActivate / NewFile / メタ通知(`_metaChanged`)/ 最近ファイル登録の全 seam がそこに集約されているため。

## 2. データモデル

### 2.1 AppSettings 追加キー

```csharp
public bool RestoreOpenFilesOnStartup { get; set; } = false;
public LastSessionSnapshot? LastSession { get; set; }
```

### 2.2 record 定義

```csharp
public sealed record LastSessionSnapshot(List<SessionTabRecord> Tabs);

public sealed record SessionTabRecord(
    string? Path,          // 保存済ファイルなら絶対パス・無題なら null
    int UntitledNumber,    // 無題タブの連番(パスありは 0)
    string? BufferKey,     // 無題本文 store の参照キー(Guid.N)。パスありは null
    bool IsActive,         // 終了時にアクティブだったか(高々 1 タブが true)
    int CaretLine,         // 0-based
    int CaretColumn        // 0-based(EditorControl.GetColumn 由来)
);
```

### 2.3 SettingsStore.Normalize の追加補正

- `LastSession` の Deserialize が失敗した場合は `AppSettings` 全体既定へ落ちる(既存 try/catch)=`LastSession=null` になり復元経路に入らない。
- `LastSession is not null` のとき:
  - `Tabs` が null → 空 List に補正
  - 各レコードの `Path` が非 null かつ空白のみ(`Path is not null && IsNullOrWhiteSpace(Path)`)→ その `SessionTabRecord` を skip(`Path is null` の無題タブは保持する)
  - `UntitledNumber<0` → 0 に clamp
  - `CaretLine<0` / `CaretColumn<0` → 0 に clamp(過大値は復元時の実バッファ範囲 clamp で吸収)

### 2.4 無題本文 Store

- パス: `Path.Combine(Environment.GetFolderPath(ApplicationData), "yEdit", "last-session-buffers.json")`(`SettingsStore.DefaultPath` と同ディレクトリ)。
- 形式: `Dictionary<string, string>`(BufferKey → 本文)を JSON でシリアライズ。
- API:

```csharp
public static class LastSessionBuffersStore
{
    public static string DefaultPath { get; }
    public static IReadOnlyDictionary<string, string> Load(string path);  // 例外は握って空 Dict
    public static void Save(string path, IReadOnlyDictionary<string, string> map);
    public static void Delete(string path);                               // 復元成功後・保存無効時に呼ぶ
}
```

- **サイズ上限**: 1 タブあたり 1 M chars(UTF-16 で 2 MB 相当)。超過タブは BufferKey=null に落として枠だけ保存 + `Trace.TraceWarning("last-session-content-skipped", ...)`(バックアップ BK-M-3 と同方針)。
- 破損時: Load が空 Dict を返す → 未見 BufferKey に対応するレコードは skip(§4 E4/E5)。

### 2.5 BufferKey の命名規約

- Guid.N("N" フォーマット=32 桁 hex)。
- `BackupCoordinator._sessionDir` と同じ命名規約=正規表現/検証コードの流用余地あり。

## 3. データフロー

### 3.1 保存(通常終了時)

`MainForm.OnFormClosing` の末尾(未保存確認を全通過し WindowWidth/Height を反映した直後):

```
1. WindowWidth/Height を _settings に反映              [既存]
2. if (_settings.RestoreOpenFilesOnStartup) {
     var (snap, buffers) = BuildLastSessionSnapshot(_docs);
     _settings.LastSession = snap;
     SaveLastSessionBuffersSafe(buffers);           // 失敗は握る
   } else {
     _settings.LastSession = null;
     DeleteLastSessionBuffersSafe();                // 前回状態の残骸削除
   }
3. SaveSettingsSafe();                                [既存]
4. base.OnFormClosing(e);                             [既存]
```

`BuildLastSessionSnapshot`(MainForm 内 private static、`DocumentManager` を引数に取る純関数):
- `_docs.Documents` を順に走査し `SessionTabRecord` を組む。
- `IsActive` はアクティブタブ 1 個だけ true。
- パスあり: `BufferKey=null`。
- パスなし: `BufferKey = Guid.NewGuid().ToString("N")` + `buffers[key] = doc.Editor.SnapshotText`(1 M 超は key=null に落として枠だけ)。
- Caret: `doc.Editor.CurrentLine` と `doc.Editor.GetColumn(doc.Editor.CurrentPosition)`(いずれも 0-based)。

**追記(Task 6 review I-1)**: dirty(`Modified=true`)の**無題タブ**は BuildLastSessionSnapshot で
skip する。OnFormClosing の未保存確認で「No=破棄」を明示選択した本文を、buffers.json 経由で
次回起動時に silent 復活させないため。無題+`Modified=false` の空タブ枠は従来どおり保存する。

**追記(Task 6 review I-2)**: 無題本文の**累積**上限 15 M chars(≈ 30 MB)を書込側に置き、
Load 側 pre-cap(32 MB)を絶対に下回るようにする。超過時は BufferKey=null に落として
枠だけ保存し `Trace.TraceWarning` する(per-tab 上限 1 M chars と組合わせて二段防御)。

### 3.2 復元(起動時)

`MainForm.OnShown` の既存 `OfferRestoreOnStartup` 呼び出し直後で分岐:

```
restored = _backup.OfferRestoreOnStartup(...);            [既存]
if (restored > 0)                                        [既存]
    _announcer.Say(...);                                 [既存]
else if (_settings.RestoreOpenFilesOnStartup
         && _settings.LastSession is { Tabs.Count: > 0 } snap)
{
    try {
        var buffers = LastSessionBuffersStore.Load(...);
        var failedPaths = _file.RestoreLastSession(snap, buffers, _startupEmptyDoc);
        LastSessionBuffersStore.Delete(...);              // 復元済みなので消す
        if (failedPaths.Count > 0)
            _prompt.Warn(BuildFailedMessage(failedPaths),
                         "一部のファイルを開けませんでした");
    } catch (Exception) {
        // ロジックバグ等の想定外は通常起動へフォールバック(§4 E8)
    }
}
```

### 3.3 起動時の空無題タブとの共存

- MainForm ctor 末尾の `_file.NewFile()` は残す。この結果を `_startupEmptyDoc` フィールドに保持。
- 復元経路がタブを 1 個以上作れた場合、`_docs.TryClose(_startupEmptyDoc, _ => true)` で閉じる(空無題タブなので無条件破棄)。
- 復元経路がタブを 1 個も作れなかった場合、空無題タブが残る=通常起動と等価。

### 3.4 FileController.RestoreLastSession の骨子

```csharp
public IReadOnlyList<string> RestoreLastSession(
    LastSessionSnapshot snap,
    IReadOnlyDictionary<string, string> buffers,
    Document? initialEmpty)
{
    var failedPaths = new List<string>();
    Document? activeDoc = null;
    int openedCount = 0;

    _suppressLoadErrorPrompt = true;       // §4 E1〜E3: 単発ダイアログを抑止し集約
    try
    {
        foreach (var rec in snap.Tabs)
        {
            if (rec.Path is not null)
            {
                var doc = TryOpenOrActivate(rec.Path, suppressAutoCsv: false);
                if (doc is null) { failedPaths.Add(rec.Path); continue; }
                ApplyCaret(doc, rec.CaretLine, rec.CaretColumn);
                openedCount++;
                if (rec.IsActive) activeDoc = doc;
            }
            else
            {
                // 無題タブ復元(BufferKey 欠落なら skip=§4 E4)
                if (rec.BufferKey is null || !buffers.TryGetValue(rec.BufferKey, out var content))
                    continue;
                var doc = RestoreUntitled(rec, content);
                openedCount++;
                if (rec.IsActive) activeDoc = doc;
            }
        }
    }
    finally { _suppressLoadErrorPrompt = false; }

    if (activeDoc is not null) _docs.Activate(activeDoc);
    if (openedCount > 0 && initialEmpty is not null)
        _docs.TryClose(initialEmpty, _ => true);
    _metaChanged();
    return failedPaths;
}
```

- `RestoreUntitled` は `_docs.CreateNew` → Path/UntitledNumber/Encoding/HasBom/LineEnding を設定 → `SetOrReplaceSource(TextBuffer.FromString(content))` → `ApplyEol` → `EmptyUndoBuffer` → `SetSavePoint`(Modified=false で開始) → `ApplyCaret` → `DocumentManager.UpdateLabel`。
- `ApplyCaret`: `line/column` を実バッファ範囲に clamp してから `EditorControl.GoToLine(line)` + カーソル列の反映。

### 3.5 LoadInto のエラーダイアログ抑止

- FileController に private field `bool _suppressLoadErrorPrompt = false;` を追加。
- `LoadInto` の catch 内 `_prompt.Error(...)` を `if (!_suppressLoadErrorPrompt) _prompt.Error(...);` にガード。
- 復元経路のみで一時 true にする(finally で false へ戻す)。復元以外の全経路(開く/最近/grep ジャンプ/開き直し)は挙動不変。

## 4. エラー処理

### 4.1 復元経路のエラー一覧

| ケース | 現象 | ハンドリング |
|---|---|---|
| E1 パスあり ファイルが存在しない | LoadInto 内 `FileNotFoundException` | 単発ダイアログを抑止し `failedPaths` に加える |
| E2 パスあり ファイルが読めない(権限/破損等) | `UnauthorizedAccessException` 等 | 同上 |
| E3 UNC / ネットワークパスが到達不能 | `TryProbeReachability` が 5 秒プローブ後 false | 同上 |
| E4 無題タブ本文が buffers store から欠落 | rec.BufferKey が buffers に無い | **その `SessionTabRecord` を skip**(タブを作らない) |
| E5 buffers.json 全体が破損 / 空 | Load が空 Dict | **全無題タブレコードを skip**・パスあり ファイルのみ復元 |
| E6 settings.json の `LastSession` が壊れた形状 | System.Text.Json 例外 | 既存 try/catch で `AppSettings` 全体既定へ落ちる=`LastSession=null` で復元経路に入らない=空無題タブで起動(結果として空タブは 1 個のみ) |
| E7 `SessionTabRecord` の値異常 | Path 空白 / UntitledNumber<0 / CaretLine<0 等 | Normalize で skip / clamp(§2.3) |
| E8 復元中の想定外例外 | ロジックバグ・OOM 等 | 復元処理全体を try/catch で包み通常起動へフォールバック(挙動不変を最優先) |

### 4.2 エラーメッセージ(E1〜E3 集約)

```
以下のファイルを開けませんでした:

  {sanitized path 1}
  {sanitized path 2}
  ...

これらは復元対象からはずしました。
```

- パスは `SanitizeForDisplay.OneLine(path, 200)` で無害化(既存の Restore 経路と同ポリシー)。
- 表示上限は先頭 10 件(`RecentFilesList.MaxItems` と揃える)、超過は `... 他 N 件` を末尾に付ける。

### 4.3 保存経路のエラー

| ケース | ハンドリング |
|---|---|
| `SaveSettingsSafe` 失敗 | 既存挙動と同じ(握って続行) |
| `LastSessionBuffersStore.Save` 失敗 | try/catch で握り + `Trace.TraceWarning`(致命でない) |
| 無題本文が 1 M chars 超過 | `BufferKey=null` に落として枠だけ保存 + `Trace.TraceWarning("last-session-content-skipped", ...)` |
| 通常終了時の buffers.json 削除失敗(設定 OFF 時) | 握って続行(次回起動でも Load が空 Dict になるだけで無害) |

### 4.4 起動時分岐の確定順序

1. `_backup.OfferRestoreOnStartup(...)` を呼ぶ → `restored` を得る。
2. `restored > 0` ならバックアップ復元完了・**前回タブ復元はスキップ**(異常終了経路が実復元済み)。
3. `restored == 0` かつ設定 ON かつ `LastSession` が非空 → 前回タブ復元経路へ。
4. どちらも該当しなければ既存挙動(空無題タブ 1 個で起動)。

## 5. UI / 設定 / 説明書

### 5.1 BasicSettingsTab

**追加コントロール**:

```csharp
private readonly CheckBox _restoreOnStartup = new()
{
    Text = "起動時に前回開いていたファイルを開く(&R)",
    AutoSize = true,
};
```

- BuildPage への追加: `_csvAutoMode` の次の row(row=3)・ColumnSpan=2・TabIndex=5。
- LoadFrom: `_restoreOnStartup.Checked = s.RestoreOpenFilesOnStartup;`
- SaveTo: `r.RestoreOpenFilesOnStartup = _restoreOnStartup.Checked;`
- Dispose: `_restoreOnStartup.Dispose();` を追加。
- アクセラレータキー `&R` の理由: 既存 `&E`(文字コード)・`&L`(改行)・`&V`(CSV) と非衝突。R=Restore。

### 5.2 説明書 ―― 基本タブのテーブルへ 1 行追加

```markdown
| 起動時に前回開いていたファイルを開く | 前回終了時に開いていたタブを再度開く | オフ |
```

### 5.3 説明書 ―― バックアップ節に補足を追加

現行「設定で『起動時に復元するか確認する』をオフにすると、確認なしで自動的に全件復元されます。」の直後に:

```markdown
「起動時に前回開いていたファイルを開く」を有効にしている場合でも、異常終了によるバックアップが残っているときは、バックアップからの復元が優先されます(前回開いていたファイルは自動では開かれません)。
```

**なぜこの位置に書くか**: 両者の相互作用を一箇所に集約し、設定タブと復元の節を行き来しなくても分かるようにする。ユーザー編集版が正の原則(CLAUDE.md §8)により、この文面はレビュー時に微調整いただく前提の「たたき台」として提示する。

### 5.4 説明書 ―― 書かない内容

- 「無題タブが復元される」は基本タブ表の説明で包含(明示しない)。
- 無題本文 1 M 上限などの内部詳細は開発者向けであり載せない。
- カーソル位置復元は独立の設定にせず、この機能の副次的な挙動として扱う。

## 6. テスト計画

### 6.1 L1(Core.Tests)

**LastSessionBuffersStore**:
- Save & Load ラウンドトリップ(空 Dict / 1 件 / 多件)。
- Load 未存在ファイル → 空 Dict。
- Load 破損 JSON → 空 Dict(例外握り)。
- Load 空ファイル → 空 Dict。
- Save で親ディレクトリ未作成 → 作成される。
- Delete 未存在ファイル → no-op。

**SettingsStore.Normalize の LastSession 拡張**:
- 未知形の LastSession → null。
- Tabs 内 UntitledNumber<0 → 0 に clamp。
- Tabs 内 CaretLine/Column<0 → 0 に clamp。
- Tabs 内 Path が空白 → その `SessionTabRecord` を skip。

**AppSettings**:
- 既定値: `RestoreOpenFilesOnStartup=false`, `LastSession=null`。
- JSON round-trip: 設定 → JSON → 設定 で不変。

### 6.2 L3(App.Tests)

**FileController.RestoreLastSession**:
- パスあり 3 件 → タブ 3 個・`initialEmpty` がクローズされる。
- 存在しないパス 1 + 実在パス 1 → `failedPaths=[存在しないパス]`・タブは 1 個(既存 FakePrompt が `Warn` を受信することは検証しない=集約メッセージは MainForm 側)。
- 無題タブ 3 件(BufferKey あり・buffers あり) → 復元 3 個・本文一致・Modified=false。
- 無題タブ 1 件・buffers から欠落 → **skip=タブ 0**(E4)。
- 無題タブ 3 件・buffers 全体が空 → 全 skip(E5)。
- パスあり 0 件かつ無題 全 skip → 復元 0=`initialEmpty` は残る=通常起動と等価。
- アクティブタブ指定 → 復元後にその doc が `_docs.Active`。
- カーソル位置復元: 通常値・負値 clamp・過大値 clamp。

**MainForm 起動分岐**(既存 internal ctor+Fake で駆動):
- restored=0 && 設定 ON && LastSession あり → 復元発火。
- **restored>0 → 復元スキップ**(バックアップ優先)。
- 設定 OFF → LastSession があっても復元スキップ。
- LastSession null → 復元スキップ。
- LastSession.Tabs 空 → 復元スキップ。

**OnFormClosing 保存経路**:
- 設定 ON: snapshot が組まれ `LastSession` と buffers.json に書かれる。
- 設定 OFF: `LastSession=null` に落ち buffers.json が削除される(前回残骸掃除)。
- 無題本文 1 M chars 超 → `BufferKey=null` に落ちる(枠だけ保存)。
- パスありレコードは `BufferKey=null`。
- アクティブフラグは 1 個だけ true。

### 6.3 L4(Bench)

**なし**。復元は起動時 1 回・保存は終了時 1 回のイベント経路で、性能クリティカルではない。

### 6.4 L5(実機 SR 検証)

**不要と判定**。SR 経路(`yEdit.Accessibility` / `EditorControl.Uia*` / App の Speech 系)に触れない:
- 復元後のカレット反映は既存の `GoToLine` / `SelectCharRange` 経路と同一。
- 復元されたタブ列は通常起動と同一の DocumentManager 経路。
- SR から見て通常の「複数タブ起動」と区別できない。

CLAUDE.md §5 の判定基準「SR 経路に触れる変更は必須」に対し変更ゼロ。ただし迷いを残して倒すなら軽い動作確認(NVDA で復元後にタブ切替・カレット移動が期待どおり読まれる)を推奨に留める(必須ではない)。

### 6.5 ミューテーション検証の重点

- **「復元スキップ(restored>0)」テストのピボット**: restored=1 と restored=0 の境界(assertion 前提と guard 発火条件を一致=Stage 8 D-1 の教訓)。
- **設定 ON/OFF ピボット**: OFF テストで ON へ変異すると failed になる位置に assertion を置く。
- **partial(存在しないパス 1 + 実在パス 1)** の fixture 順序は「存在しないパスを中央」に置き、prefix / suffix 除外テストとして機能させる(Stage 8 教訓)。
- **no-change テスト**: BufferKey なしで store save 呼び出しが起きないことを、既定状態と区別するため **非既定な初期状態(prior store あり)** から検証を始める(Stage 6 教訓)。

### 6.6 ゲート

- `tools/pre-merge-check.ps1` **EXIT 0**。
- **0 warnings** 維持(`-warnaserror` 稼働)。
- 文書にテスト数を書かない(=テストプロジェクトの実行結果が正)。

## 7. 申し送り(follow-up 候補)

- 文字コード指定の記憶(前回「開き直す」で明示指定した codepage の保持)は将来検討。現状は自動判定に任せる。
- 復元時に「バックアップから復元しました」と同型で「前回のタブを N 件復元しました」の Announcer 通知を出すかは実装時に判断(小声で提示・SR 経路に影響なし)。
- buffers.json の暗号化・パーミッション制限は将来のセキュリティ強化課題(現状は既存の settings.json と同水準の平文保存)。
- 復元対象が 100 件級になった場合の UI(タブ列の横スクロール)は既存の制限を継承(本設計で新たな制約は導入しない)。
- `LastSession.Tabs` の要素数上限(現状無制限=攻撃 JSON で 10M 要素の展開が可能)。バックアップ復元と同種のキャップを Normalize / Load 側に加えるかは別途検討(BackupCoordinator の BK-M-3 と対称の防御)。
- **(Task 7 review Minor-1)** `MainForm.ShowFailedRestoreDialog` の本文組立(Cap=10・「他 N 件」・SanitizeForDisplay.OneLine)を純関数 `BuildFailedRestoreBody(IReadOnlyList<string>)` に切り出し、境界(count=1/10/11/100)+RLO/LF injection 入力でユニットテストする。現状 Task 4 のダイアログ抑止 seam で表示自体は避けているため body の mutation 検知経路がない(fix はスタイル+防御性の double-check)。
- **(Task 7 review Minor-3)** 前回タブ復元時の SR 通知(バックアップ復元と同型の「前回のタブを N 件復元しました」)。yEdit は SR 対応が第一級のため、復元後の状況把握を助けるためのアナウンスを検討する(Minor-3 と既存の同旨申し送りを統合)。
- **(Task 7 review Minor-5)** `TryRestoreLastSession` の post-loop 経路(`_docs.Activate` / `_docs.TryClose(initialEmpty)` / `_metaChanged`)で例外が出た場合、`LastSessionBuffersStore.Delete` が実行されず buffers.json + settings.json.LastSession が両方残留=次回起動でも同じ復元を試行する。design §4 E8 の "best-effort 通常起動フォールバック" 範囲内だが、deterministic bug の場合の infinite retry を避けるため `Delete` を `finally` へ移すか、catch 側でも呼ぶ改修を検討。
- **(Task 13 review Important)** `_confirmDiscardOverrideForTest` は `_file.ConfirmDiscardIfDirty` の 1 callsite 用 per-site seam。同種の per-site 追加が Task 14 以降で累積するリスクあり。長期的には `MainForm` に `internal MainForm(AppSettings, string, IUserPrompt)` の overload を追加して `FileController` へ inject する形へリファクタするのが scalable(現状 `MessageBoxUserPrompt` は `MainForm.cs:158` で inline `new`)。本 §8 補遺後に別 PR で対応検討。
- **(Task 14 review Minor-2)** E9 demote 時の `Trace.TraceWarning` および同種 BackupRecord 系 Trace の observability seam(テスト観測 hook)は未実装。設計書 §8.5 は「Trace 検証(observability seam)」を明記しているが、既存 BackupRecord 系にも観測 seam は無く、既存レベル据え置き。将来対応時は BackupRecord + Session の Trace を一括で TraceListener hook 化する。
- **(Task 14 review Minor-4)** dirty パス経路の RecentFiles 汚染テストが無い。既存 `RestoreLastSession_DoesNotPolluteRecentFiles` は non-dirty パス record のみ検証。dirty パス経路は `WithLoadErrorPromptSuppressed` 内で `RestorePathDirty` が RegisterRecent を一切呼ばないので構造上汚染し得ないが、明示テストで「うっかり `_recent.Push(rec.Path)` を追加する」変異を kill できるため回帰防止テストとして追加を検討。
- **(§8 final review Minor-3)** E9 demote(dirty パスあり record で buffers 欠落)は現状 `Trace.TraceWarning` のみでユーザーへの通知がない。設計 §8.4 は「silent lost + Trace」を明示的に受容しているが、`failedPaths` の集約 Warn(「開けなかった」)と demote(「開けたが編集を失った」)は意味論が異なるため、将来「N 個のファイルは編集途中の内容を復元できず、保存時点の内容を開きました」型の通知を追加する余地あり。

## 8. 補遺(2026-07-23 実機検証 後): dirty タブの silent 復元

### 8.1 動機

実機検証で以下が判明:
- 設定 ON でも close 時に dirty タブは Yes/No/Cancel 確認が出る。No 選択で新規タブが silent 消失。
- 現行 §3.1 追記(I-1)は「明示 No した本文の silent 復活」を避ける保守的方針だったが、ユーザーが期待する動作は「設定 ON = 未保存確認を出さず・次回起動時に同じ状態を silent 復元」(=IDE の "restore workspace on exit" 型)。

### 8.2 挙動変更(§3.1 / §3.2 / §3.4 の差分)

**OnFormClosing(§3.1 差替)**:

```
if (_settings.RestoreOpenFilesOnStartup) {
    // Pre-check: 全 dirty タブが cap 内に収まるか
    if (WillDirtyContentFitInCaps()) {
        // silent close: 未保存確認を一切出さない
        var (snap, buffers) = BuildLastSessionSnapshot();
        _settings.LastSession = snap;
        SaveLastSessionBuffersSafe(buffers);
        SaveSettingsSafe();
        base.OnFormClosing(e);
        return;
    }
    // cap 超過 → all-or-nothing で従来経路へ fall through
}
// 従来経路: 全 dirty タブに ConfirmDiscardIfDirty
foreach (var doc in _docs.Documents.ToArray()) {
    if (!doc.Editor.Modified) continue;
    _docs.Activate(doc);
    if (!_file.ConfirmDiscardIfDirty(doc)) {
        e.Cancel = true;
        _grep.CancelClose();
        base.OnFormClosing(e);
        return;
    }
}
// ここに来た時点で dirty はすべて処理済み(保存 or 破棄)
if (_settings.RestoreOpenFilesOnStartup) {
    var (snap, buffers) = BuildLastSessionSnapshot();  // cap OK が保証されている(dirty 消化済)
    _settings.LastSession = snap;
    SaveLastSessionBuffersSafe(buffers);
} else {
    _settings.LastSession = null;
    DeleteLastSessionBuffersSafe();
}
SaveSettingsSafe();
base.OnFormClosing(e);
```

**BuildLastSessionSnapshot(§3.1 追記 I-1 撤回)**:
- 「dirty 無題タブは skip」ロジックを撤去。すべての dirty タブ(パスあり+無題)の本文を `buffers` に含める。
- `WillDirtyContentFitInCaps()` は snapshot 構築の pre-check として同ロジック(per-tab 1M chars + total 15M chars)を dry-run する。

**SessionTabRecord 拡張**:

```csharp
public sealed record SessionTabRecord(
    string? Path,
    int UntitledNumber,
    string? BufferKey,
    bool IsActive,
    int CaretLine,
    int CaretColumn,
    // ↓ §8 追加
    int CodePage,      // 保存時の Encoding.CodePage(0=未指定/既定)。dirty パスありの復元で使用
    bool HasBom,       // 保存時の BOM 有無。dirty パスありの復元で使用
    int LineEnding,    // 保存時の LineEnding enum。dirty パスありの復元で使用
    bool WasModified   // 保存時の Modified フラグ。復元後の Modified 制御に使用
);
```

**RestoreLastSession(§3.4 差替の骨子)**:

```csharp
foreach (var rec in snap.Tabs) {
    if (rec.Path is not null && rec.BufferKey is not null
        && buffers.TryGetValue(rec.BufferKey, out var content))
    {
        // dirty パスあり: TryOpenOrActivate せず、CreateNew で dirty オーバーレイ復元
        var doc = RestorePathDirty(rec, content);
        openedCount++;
        if (rec.IsActive) activeDoc = doc;
    }
    else if (rec.Path is not null) {
        // 非 dirty パスあり: 従来通り TryOpenOrActivate(auto-detect 任せ)
        var doc = TryOpenOrActivate(rec.Path);
        if (doc is null) { failedPaths.Add(rec.Path); continue; }
        doc.Editor.SetCaretByLineColumn(rec.CaretLine, rec.CaretColumn);
        openedCount++;
        if (rec.IsActive) activeDoc = doc;
    }
    else {
        // 無題: 従来通り(BufferKey 欠落なら skip)
        if (rec.BufferKey is null || !buffers.TryGetValue(rec.BufferKey, out var c))
            continue;
        var doc = RestoreUntitledTab(rec, c);  // 内部で WasModified を尊重
        openedCount++;
        if (rec.IsActive) activeDoc = doc;
    }
}
```

**RestorePathDirty(新規ヘルパ)**:
- `_docs.CreateNew()` → `doc.State.Path = rec.Path` → `Encoding=EncodingCatalog.Get(rec.CodePage)` / `HasBom=rec.HasBom` / `LineEnding=(LineEnding)rec.LineEnding`。
- `SetOrReplaceSource(TextBuffer.FromString(content))` → `ApplyEol` → `EmptyUndoBuffer`。
- **`SetSavePoint` を呼ばない** → Modified=true で開始。
- `SetCaretByLineColumn`(clamp) → `DocumentManager.UpdateLabel`。
- RecentFiles 登録: 既存 `TryOpenOrActivate` と対称に `_recent.Push(rec.Path)` を呼ぶ。

**RestoreUntitledTab(既存修正)**:
- `WasModified=true` → `SetSavePoint` を呼ばない(Modified=true)。
- `WasModified=false` → 既存通り `SetSavePoint`(Modified=false)。

**非 dirty パスありのエンコーディング**: 保存はするが復元時は使わない(TryOpenOrActivate の auto-detect に任せる=決定事項)。将来「保存 encoding と auto-detect 結果が異なる場合のみリロード」の最適化余地あり=申し送り。

### 8.3 データフロー修正まとめ

- 保存: 設定 ON なら「全 dirty タブの本文」を buffers.json に集約(§3.1 の per-tab/total cap は継続)。
- close 時ダイアログ: cap 内なら silent close / cap 超過なら従来通り全 dirty タブに Yes/No/Cancel。
- 復元: dirty パスあり分岐が新設され Modified=true + 保存 encoding で復元。非 dirty パスあり+無題は既存経路。
- **設定 OFF の挙動は完全不変**(既存テストは全緑維持)。

### 8.4 エラー処理の追記(§4 差分)

| ケース | 現象 | ハンドリング |
|---|---|---|
| E9 dirty パスあり record・BufferKey は present だが buffers に欠落 | rec.BufferKey が buffers に無い | **非 dirty パスありへ demote**=TryOpenOrActivate 経路(disk から再オープン)。dirty 編集は silent lost + `Trace.TraceWarning` |
| E10 dirty パスあり record・rec.CodePage が EncodingCatalog に存在しない | `EncodingCatalog.Get` 失敗 | settings.Default* に fallback + Trace |

### 8.5 テスト追加

**L1**:
- SessionTabRecord に CodePage/HasBom/LineEnding/WasModified 追加の JSON round-trip。
- SettingsStore.Normalize で `CodePage<0` → 0 に clamp・`LineEnding` 未知値 → 0 に clamp。

**L3(App.Tests)**:
- OnFormClosing 設定 ON + dirty あり + cap 内 → ConfirmDiscardIfDirty 呼ばれず・snapshot に dirty 内容(パスあり+無題)が入る・buffers.json に書かれる。
- OnFormClosing 設定 ON + dirty あり + cap 超過 → 全 dirty タブに ConfirmDiscardIfDirty(=従来経路 fall through)。
- OnFormClosing 設定 OFF + dirty あり → 従来通り(既存テスト維持)。
- RestoreLastSession dirty パスあり record → CreateNew + Path 設定 + content 復元 + Modified=true + Encoding 復元。
- RestoreLastSession dirty 無題 record(WasModified=true)→ Modified=true。
- RestoreLastSession dirty パスあり + BufferKey buffers 欠落 → demote(TryOpenOrActivate)+ Trace 検証(observability seam)。

**ミューテーション検証重点**:
- cap 判定のピボット: 「cap 内」テストで cap 超過側に変異すると failed になる assertion。逆も。
- WasModified の kill: true と false を両方持つ record セットで、SetSavePoint の呼び忘れ変異を検出。

### 8.6 説明書の追記(§5.3 に補足)

現行「起動時に前回開いていたファイルを開く」の説明に:

```markdown
この設定を有効にすると、終了時に未保存の変更があっても保存の確認をせずに終了します。未保存の変更は次回起動時に、編集途中の状態のまま復元されます。ただし変更内容が非常に大きい場合(1 ファイルあたりおよそ 100 万文字を超える場合)は、通常どおり保存の確認が表示されます。
```

### 8.7 マイグレーション

- 既存 settings.json(§8 前の SessionTabRecord スキーマ)は CodePage/HasBom/LineEnding/WasModified が欠落 → System.Text.Json は既定値(0/false/0/false)で deserialize。
- WasModified=false での復元は既存挙動と等価(SetSavePoint 呼ぶ)=挙動不変。
- CodePage=0 でも dirty パスあり record は buffers に content があれば復元経路に入る(fallback で settings.Default* を使う=§8.4 E10)。
- 既存テストの JSON リテラルは変更不要(既定値で埋まる)。

### 8.8 スコープ外(§8 では扱わない)

- **「復元時の確認ダイアログを非表示化」(Part 2)**: 現状の LastSession 復元経路に「確認ダイアログ」は存在しない。ユーザ実機で再現時に別対応(申し送り)。
- 異常終了時のバックアップ復元: 現状維持(`ConfirmRestoreOnStartup` 設定依存)。
- 復元時のエンコーディング再判定(非 dirty パスあり): 保存 encoding を復元時に活用する最適化は将来検討。
