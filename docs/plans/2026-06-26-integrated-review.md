# プロジェクト統合レビュー（M4〜M7 完了時点）— 結果と申し送り

最終更新: 2026-06-26 / 対象: yEdit 本番ビルド（M1〜M7 main マージ済）

M4〜M7 を一括実装し各 M を 5 レンズレビュー＋敵対的検証で堅牢化した後、**プロジェクト全体を横断する統合レビュー**を
6 レンズ（アーキ整合 / アクセシビリティ鉄則 / 並行性 / 機能間相互作用 / ライフサイクル / 一貫性・回帰）で実施した。

## 総括

- **確認 24 件。全て MINOR / NIT。BLOCKER / MAJOR はゼロ。**
- アーキテクチャの層分離（Core=UI/スレッド非依存・純ロジック / Editor=UIスレッド契約 / App=シェル）は M4〜M7 を通じて
  維持されている。背景スレッド（grep の `Task.Run`・backup の直列ライター）はいずれも Core の純 I/O とスナップショット面
  （`SnapshotText`/`CaretCharOffset`/`GetSelectionCharRange`）のみを使い、**`SCI_*` を別スレッドから呼ぶ鉄則違反は無い**。
- ホットキー・メニューニーモニックの全体衝突は検出されず。複数モードレス窓（検索/grep）の共存も問題なし。

## マージ前（本統合フォローアップ）で修正済み

- **grep の終了協調**: `OnFormClosing` で `GrepController.BeginClose()`（実行中 grep を中止・終了確認中の結果窓ポップ抑止）、
  終了取りやめ時は `CancelClose()` で通常運用へ復帰。古い実行の進捗が新状態を上書きしないガードも追加。
- **UIA 系 BeginInvoke のハンドル破棄ガード**: `SelectCharRange`/`ReplaceCharRange`/`SetSelection`/`SetFocus` に
  `!IsHandleCreated || IsDisposed` ガード（タブ閉じ等で破棄後に BeginInvoke しない）。
- **設定の数値健全化**: `SettingsStore.Normalize` で `DefaultCodePage`（選択肢外→既定）・`DefaultLineEnding`（0..2）・
  `FontSize`(>0)・ウィンドウ寸法・`BackupIntervalSeconds` をクランプ（手編集で壊れた設定の起動時クラッシュ/不可視を防止）＋テスト。
- **`BackupCoordinator.Dispose` の冪等化**＋ `MainForm.Dispose(bool)` から呼び出し（異常系で Timer/背景スレッドを確実解放）。
- **SR 能動通知の共通化**: `SrNotify.Raise(Label, message)` へ集約（Announcer / FindReplaceDialog / GrepDialog が共有・実機 SR 調整を 1 箇所に）。
- **「名前を付けて保存」で最近のファイルに登録**。設計図の依存辺（`Editor→Core` は実在しない）を修正。

## 申し送り（M8 以降・実機 SR 検証で要否判断）

1. **セッション中の SR 起動/終了に非追従**（MINOR）: `ConfigureForCurrentScreenReader()` がエディタ生成時の 1 回のみ。
   PC-Talker で開いた後に NVDA を起動する等で既存タブの UIA 提供方針が古いまま固定され得る。`WndProc` はフラグを動的に読むため、
   `OnGotFocus` で再評価すればウィンドウ再生成なしに動的化可能。**ただし SR 適応の中核変更のため実機 SR 検証とセットで行う**。当面は
   「SR は yEdit 起動前に立ち上げる」を制約として運用。
2. **ScintillaHost が `Scintilla` を public 継承**（MINOR）: 基底 `SCI_*` 由来 API の UIスレッド契約が型でなく App 規律に依存。
   現状違反なし。将来は薄いファサード化 / `internal` 化 / Debug ビルドの UIスレッド assert を検討。
3. **App 層の検索歩進/置換ロジックに自動テストが無い**（MINOR）: `SearchController` の歩進開始位置・置換後前進・選択スコープを
   Core 純関数へ抽出して xUnit 固定すると回帰耐性が上がる（`BackupPlanner` と同様の方針）。
4. `OpenExistingPath` と `OpenAndSelect` の「開く/再利用/失敗ロールバック」ロジック重複の集約（最近のファイル登録方針の一本化含む）。
5. 終了時バックアップドレインが UI スレッドを最大 15 秒ブロックし得る（ハングディスク時のみ）。応答性とデータ保全のトレードオフ。
6. `ScintillaHost`: ハンドル再生成時のイベント二重購読、破棄時の UIA 切断（現状タブはハンドル再生成しないため実害低）。
7. タブ切替時に上書き/挿入モードがステータス非表示（現在位置照会 `Ctrl+Alt+P` で照会は可能）。
8. probe 2 プロジェクトが既定ソリューションビルドに残存（製品外と構造で示すなら `tools/` 等へ）。
9. ステータス Label の AccessibleName がダイアログ間で表現不統一（検索結果/状態）。
10. 複数インスタンス同時起動時のバックアップ競合（既知・M9+）。

## 結論

M4〜M7 はアーキテクチャ・アクセシビリティ鉄則・並行性・ライフサイクルの観点で**統合的に健全**。残課題はいずれも
非ブロッカーで、最大の DoD 項目は**全マイルストーンの実機 SR 検証（PC-Talker / NVDA）＝ユーザー実施**である。
