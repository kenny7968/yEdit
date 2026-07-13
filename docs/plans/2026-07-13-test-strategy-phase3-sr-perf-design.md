# テスト戦略 Phase 3: SR 検証半自動化+性能ゲート CI 組み込み 設計書

- 日付: 2026-07-13
- 上位文書: `docs/plans/2026-07-13-test-strategy-design.md` §4
- 位置づけ: **任意・将来**。Phase 2 完了後、または a11y 回帰が頻発した時点で着手する。

> **2026-07-13 追記**: PC-Talker サポート廃止(`docs/plans/2026-07-13-pctalker-removal-design.md`)により、`verify-msaa-client.ps1` は削除済み=統合対象から除外。`walk-test-editor.ps1`(PC-Talker の 1 文字走査再現)は汎用 UIA クライアント堅牢性テストとして残すか本 Phase 着手時に採否を判断。実機検証(L5)のマトリクスから PC-Talker は除外する。

## 1. SR 呼び出しパターン回帰スイート `tools/sr-regression.ps1`(新設)

### 目的

NVDA/PC-Talker が UIA プロバイダに対して行う呼び出しパターンを UIA クライアントとして再現し、a11y 関連変更の**一次スクリーニング**を自動化する。実発声の人手確認(L5)は置き換えない(上位文書の原則 2)。

### 統合対象(既存資産)

| スクリプト | 再現している SR 挙動 |
|---|---|
| `tools/verify-uia-editor.ps1` | TextPattern/DocumentRange/GetSelection/RangeFromPoint の基本疎通 |
| `tools/walk-test-editor.ps1` | PC-Talker の 1 文字ずつの行走査(Expand(Char)→Move(Char,1) 反復) |
| `tools/word-sim.ps1` | NVDA の TextUnit.Word 挙動(Expand/Move スパン保持/MoveEndpointByUnit・6 ケース) |
| `tools/verify-msaa-client.ps1`(未コミット) | MSAA クライアント経路(統合時に採否を確定) |

### 設計

- ランナーが `yEdit.Editor.Smoke --uia` をビルド・起動し、上記スクリプトを順に実行して PASS/FAIL を集計、総合 EXIT コードを返す。
- 各スクリプトの既定 `$Exe` が worktree 時代のパス(`.worktrees\custom-editcontrol-design\...`)を指している問題をこの統合で修正し、リポジトリ相対に統一する。
- 実行タイミング: a11y 関連変更(yEdit.Accessibility / EditorControl の UIA 部 / Speech 系)のマージ前+リリース前。`pre-merge-check.ps1` には**組み込まない**(UIA クライアントはフォアグラウンドのデスクトップセッションが必要で、常時実行に向かない)。

### 判定の限界(明記)

このスイートが検証するのは「UIA プロバイダが SR の呼び出しに正しく応答するか」まで。「SR が実際に発声するか」(PC-Talker の空行問題のような発声側の事象)は検出できない。PASS でも L5 の人手確認は省略しない。

## 2. 性能ゲートの CI 組み込み `bench.yml`(新設)

- トリガ: `workflow_dispatch`(手動)のみ。push/PR には組み込まない(実行時間とランナー性能揺れのため)。
- 内容: `dotnet run --project tests/yEdit.Core.Bench -c Release -- --typing` と `--layout` を実行し、結果を**レポートとして表示**する。
- **しきい値ゲート(EXIT 判定)はローカル実行を正とする**(上位文書 §4)。ホステッドランナーの EXIT 1 は参考情報とし、リリースブロッカーにしない(ジョブは `continue-on-error: true`)。
- ローカル運用規約: Buffer/Layout/IO に大きな変更を入れたマージの前に、ローカルで `--typing`(応答性)を実行し EXIT 0 を確認する。1GB 級(`--mb 1024`・Smoke `--bench-save`)はリリース前のみ。

## 3. スコープ外

- SR の実発声の自動判定(音声キャプチャ等)は行わない。費用対効果が合わず、L5 チェックリスト運用を継続する。
- NVDA のスクリプト駆動(nvda --portable 自動操縦)は将来検討の余地があるが本 Phase では扱わない。

## 4. 着手条件(いずれかを満たしたら)

1. Phase 2 完了後、a11y 関連の回帰が実際に発生した(スクリーニングの価値が実証された)
2. リリース頻度が上がり、L5 チェックリストの全走が負担になった
3. UIA プロバイダ(yEdit.Accessibility)に大きな変更を計画している
