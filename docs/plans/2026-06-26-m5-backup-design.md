# M5 自動バックアップ＆クラッシュ復元 — 設計ドキュメント

最終更新: 2026-06-26 / 作業ディレクトリ: `<repo>` / .NET 9 / Windows 11・日本語環境

前提資料: `docs/plans/2026-06-26-yedit-production-architecture-design.md`（§3 M5・§4.1 鉄則・§4.5 エラー処理）。

---

## 0. ゴール

クラッシュ／電源断で未保存変更を失わない。定期的に編集内容をサイドカーへ退避し、次回起動時に
**孤児バックアップ＝前回の異常終了**を検出して復元提案する。バックアップは完全に無音（SR を妨げない）。

**DoD（今回）**: 実装＋Core 自動テスト＋別エージェントのコードレビュー通過＋ビルド 0 警告。実機 SR 検証はユーザーが後日。

---

## 1. アーキテクチャ（鉄則順守）

```
[UIスレッド] System.Windows.Forms.Timer（既定30秒）tick ＋ DocumentManager.ActiveDocumentChanged
   → Reconcile(): 文書を走査。dirty かつ前回バックアップ以降に内容変化のある文書だけ、
     本文スナップショット(UIスレッドで取得)＋メタから BackupRecord を作りジョブを投入。
     クリーン化(保存)した文書はバックアップ削除ジョブを投入。閉じた文書も削除。
[背景1スレッド] SerialBackupWriter: BlockingCollection を直列処理 → BackupStore.Write/Delete（Core純I/O・原子的）
[起動時] BackupStore.LoadAll → 孤児あり → RestoreDialog（CheckedListBox・SR対応）
   → 選択を新タブへ復元（元IDを引き継ぎ既存バックアップを継続使用）／非選択は削除／すべて破棄／あとで
[クリーン終了] OnFormClosing(全タブ確認後): timer停止 → writer ドレイン → BackupStore.DeleteAll（=孤児なし）
```

**鉄則順守**: スナップショット取得（`SCI_*` 由来）は UI スレッドの Reconcile で行い、背景スレッドは Core の
ファイル I/O のみ（`SCI_*` に触れない・§4.1）。Core(`BackupStore`)は SR/スレッド非依存の純ロジック。

---

## 2. Core 新規（`yEdit.Core.Backup`・テスト可能）

```csharp
public sealed record BackupRecord(
    string Id,             // GUID。バックアップファイル名 <Id>.json
    string? OriginalPath,  // 元ファイル（無題は null）
    int UntitledNumber,    // 無題の連番（OriginalPath==null のとき表示に使う）
    int CodePage,
    bool HasBom,
    int LineEndingId,      // (int)LineEnding
    string Content,        // 本文（UTF-16・JSON内に格納）
    DateTime TimestampUtc);

public static class BackupStore
{
    public static string DefaultDirectory;                 // %APPDATA%\yEdit\backups
    public static void Write(string dir, BackupRecord r);  // 1文書1 JSON・原子的(temp→Replace/Move)
    public static IReadOnlyList<BackupRecord> LoadAll(string dir); // 列挙＋破損スキップ
    public static void Delete(string dir, string id);
    public static void DeleteAll(string dir);
}
```

**保存形式**: 1 文書＝1 JSON（`System.Text.Json`・`SerializeToUtf8Bytes`）。単一ファイルなので原子的書き込み
（同ディレクトリの temp に書いてから `File.Replace`／新規は `Move`）で完結。破損ファイルは `LoadAll` でスキップ。

---

## 3. App 新規

### BackupCoordinator
- `System.Windows.Forms.Timer`（既定 `BackupIntervalSeconds`）＋ `DocumentManager.ActiveDocumentChanged` で `Reconcile()`。
- 文書毎の状態 `Dictionary<Document, DocBackup{ string Id; int LastSig; bool HasBackup }>`。
  - `Reconcile()`（UIスレッド）:
    - map にあるが `_docs.Documents` に無い文書＝閉じた → `HasBackup` なら削除ジョブ、map から除去。
    - 新規文書 → 新 Id・`LastSig=Sig(snapshot)`・`HasBackup=false` で登録（変化があるまで書かない）。
    - 既存文書: `Editor.Modified` かつ `Sig(snapshot)!=LastSig` → 書込ジョブ＋`LastSig`更新・`HasBackup=true`。
      非 `Modified` かつ `HasBackup` → 削除ジョブ・`HasBackup=false`（保存でクリーン化＝バックアップ不要）。
  - `Sig` は内容長＋ハッシュの簡易署名（プロセス内比較のみ・tick 抑止＝無変化なら書かない）。
- **背景直列ライター** `SerialBackupWriter`: `BlockingCollection<Action>`＋ LongRunning Task。各ジョブを try/catch
  （バックアップ失敗は致命でない・無音）。Dispose で `CompleteAdding`→ドレイン待ち。
- **無音**: バックアップは一切 SR 通知・フォーカス移動をしない。
- `OfferRestoreOnStartup(owner, restoreFunc)`: `LoadAll`→空なら何もしない。孤児あれば RestoreDialog。
- `Shutdown()`: timer 停止 → writer ドレイン → `DeleteAll`（クリーン終了の印）。

### RestoreDialog（モーダル・CheckedListBox）
- 一覧（既定全チェック）: `{OriginalPath のファイル名 or 無題 N}（{TimestampUtc ローカル時刻}）`。
- ボタン: 「選択を復元(&R)」「すべて破棄(&D)」「あとで(&L)」。標準 Win32 で SR ネイティブ読み。
- 返り値: `Action ∈ {Restore, DiscardAll, Later}` ＋ Restore 時の `Checked` 一覧。
  - Restore: チェックを新タブへ復元（元 Id 引き継ぎ）・非チェックは削除。
  - DiscardAll: 全削除。Later: 何もしない（次回再提案）。

### MainForm 配線
- `BackupCoordinator` 生成（`_settings.BackupEnabled`/`BackupIntervalSeconds`）。`OnShown` で一度だけ `OfferRestoreOnStartup`。
- 復元 `Func<BackupRecord, Document>`: 新タブを作り本文＋メタを載せ **dirty のまま**（保存可能に）。`EmptyUndoBuffer`、`SetSavePoint` しない。
- `OnFormClosing`（全タブ未保存確認を通過した後）で `_backup.Shutdown()`。
- 復元で作った文書は coordinator が `rec.Id` で登録し既存バックアップを継続使用（孤児・無保護窓を作らない）。

### 設定追加（`AppSettings`）
- `BackupEnabled`(既定 true)・`BackupIntervalSeconds`(既定 30)。UI 露出は M7。`BackupEnabled=false` なら無効（バックアップも復元提案もしない）。

---

## 4. テスト（Core）

`BackupStore`: Write→LoadAll 往復（本文の改行/Unicode/特殊文字・全メタ）／原子的上書き（同 Id 2 回）／
Delete・DeleteAll／破損 JSON はスキップし正常分は読める／無題(OriginalPath=null)往復／空・欠落ディレクトリ→空。

---

## 5. 非対象（follow-up）

- 複数インスタンス同時起動時のバックアップ競合（M9+。今回は単一インスタンス前提・要申し送り）。
- 保存後クリーン化のバックアップ削除に最大 1 tick の遅延（クラッシュ窓は実害小：内容はディスクと一致）。
- バックアップの暗号化・世代管理（最新 1 世代のみ）。
- 巨大ファイルのバックアップ（本文を丸ごと JSON 化）。

## 6. マージ前レビュー（M5）の結果と申し送り

5 レンズ（データ保全/スレッド安全/正確性/SR/統合）で並列レビュー＋敵対的検証（確認 35 件）。**BLOCKER 2 件を含む**ため重点的に修正した。

**マージ前に修正済み（BLOCKER/MAJOR）:**
- **[BLOCKER] 登録時に既に dirty な文書を即退避**（起動時無題タブの恒久データ消失窓を解消）。`RegisterNew` で Modified なら即書込。
- **[BLOCKER] クリーン終了で当セッション管理分のみ削除**（「あとで」先送り孤児を `DeleteAll` で消さない）。`Shutdown` は `_map` の Id だけ削除。
- **[MAJOR] 背景ライターの Dispose 競合クラッシュ**を解消（`Run` の列挙を try/catch・Join 完了時のみ `Dispose`・ドレイン 15 秒）。
- **[MAJOR] 書込失敗の永続化**を解消（楽観 LastSig 更新だが、失敗 Id を `_failed` へ積み次 tick で `ForceWrite` 再書込）。
- **[MAJOR] 復元ループの per-record try/catch**（1 件の不正レコードで全復元・起動クラッシュループにしない）。

**マージ前に修正済み（MINOR/NIT）:**
- 変化検出を 64bit `ContentSignature`（FNV-1a＋長さ・プロセス間安定）へ（32bit 衝突での取り逃し低減）。
- バックアップ間隔の上限クランプ（5〜3600 秒）で `Timer.Interval` の int オーバーフロー防止。
- `*.tmp` 残骸の掃除（起動時 `SweepTempFiles`・`DeleteAll` でも削除）。
- 復元の**未チェック項目は削除せず**残す（SR 誤操作での消失回避・明示破棄は「すべて破棄」のみ）。
- `BackupEnabled=false` 時は背景スレッドを生成しない。`Shutdown`/`Dispose` で Timer を解放。
- `Shutdown` を `OnFormClosed`（閉じ確定後）へ移動。復元タブの無題番号を元番号と一致＋連番カウンタ前進。
- RestoreDialog に `AccessibleDescription`。**Reconcile の状態機械を Core の純粋関数 `BackupPlanner` へ抽出し単体テスト**（データ安全性の中核を SR 無しで検証）。

**申し送り（M6 以降で要否判断）:**
- 複数ファイル復元時のタブフォーカス連続移動で SR がやや冗長（最後に 1 回フォーカスへ集約する改善余地）。
- 起動時の初期無題タブが復元タブと併存（空タブが残る・閉じれば済む）。
- `BackupEnabled=false` に切替えると既存孤児が提案も削除もされず残る（再有効化で復旧可能）。
- 複数インスタンス競合（M9+）。
