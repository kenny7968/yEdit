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
| `tools/word-sim.ps1` | NVDA の TextUnit.Word 挙動(Expand/Move スパン保持/MoveEndpointByUnit・6 ケース) |

> 2026-07-16 実装時決定:
> - `tools/walk-test-editor.ps1` は削除(PC-Talker サポート廃止=[[pctalker-removal]] に伴い維持動機喪失。Move スパン保持は word-sim.ps1 Case 3 で網羅)
> - `tools/verify-msaa-client.ps1`(未コミット)は PC-Talker 廃止で除外確定
> - **アグリゲータ実装時に判明した既知の環境制約**: `word-sim.ps1` は BOMless UTF-8 の日本語コメント(L4)が WinPS 5.1 の日本語ロケールで Shift-JIS 誤解釈でパーサエラーになる pre-existing 問題を持つ。`tools/sr-regression.ps1`(Phase 3 で追加した集約ランナー)は `Get-Command pwsh` で PowerShell 7+ を優先検出して子スクリプトを起動することで回避する。pwsh 未インストール環境ではアグリゲータが警告バナーを出したうえで `powershell`(WinPS 5.1)にフォールバックする=word-sim.ps1 は失敗するが verify-uia-editor.ps1 は動作する。恒久解決には運用機で `winget install Microsoft.PowerShell` が推奨。
> - **`sr-regression.ps1` 自体は UTF-8 BOM 付きで作成**(`tools/pre-merge-check.ps1` と同じ流儀の docstring-heavy ユーザー起動アグリゲータ)

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

---

**2026-07-16 実装完了**: 上記 3 条件のいずれも該当していなかったが、害が小さいためユーザー判断で能動的に着手し実装完了(実装計画=`docs/plans/2026-07-16-test-strategy-phase3.md`)。`tools/sr-regression.ps1`(BOM 付き UTF-8・pwsh 優先起動)と `.github/workflows/bench.yml`(手動 workflow_dispatch)を追加。`tools/walk-test-editor.ps1` は同時に削除。

**bench.yml の運用注意**: ジョブレベル+ステップレベルの `continue-on-error: true` により、ホステッドランナーの EXIT 1 はワークフロー全体の badge を緑にする。**ビルド失敗や `--typing`/`--layout` の性能未達を検知するにはワークフロー結果ページ(ジョブ画面)を目視確認する必要がある**(緑バッジだけを見る運用は最初から想定外)。初回 dispatch 時にこの挙動を実測確認しておくと運用者が誤解しにくい。
