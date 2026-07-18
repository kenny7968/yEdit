# リリース承認ゲート導入 実装プラン

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** GitHub Environments の Required reviewers 機能で `release.yml` を承認ゲート化し、意図せぬリリース発行を防ぐ。

**Architecture:** `jobs.release` に `environment: release` を 1 行追加。GitHub 側で同名 environment を作成し reviewer=kenny7968 単独・deployment refs=`refs/tags/v*` を設定。承認は Actions UI から都度手動。

**Tech Stack:** GitHub Actions・GitHub Environments・`gh` CLI(検証用)

**設計書**: `docs/plans/2026-07-18-release-env-approval-gate-design.md`

---

## Option B pivot (2026-07-18 追記)

実装着手時に判明: yEdit は **private リポジトリ + GitHub Free plan**。この組み合わせでは Environment の **protection rules (Required reviewers 等) が UI に出ず設定不可**(public repo または Pro/Team/Enterprise 以上のみ)。

**判断**: 数日中に OSS 公開予定のため、**Option B** を採用:
- **今**: release.yml に `environment: release` を宣言するだけ(protection rules 未設定の environment は no-op)
- **public 化後**: GitHub UI で protection rules を追加 → 即座に承認ゲート発動

このため、以下タスクの扱いを変更:
- **Task 1**(Environment 作成): 実施タイミングを **public 化直後**に延期
- **Task 4/5**(ドライラン + テストタグ後始末): protection rules がないと pending にならないため**今回スキップ**。public 化後の初回 activate 検証として実施
- **Task 2/3/6**(release.yml 編集・commit・merge): 今回実施

現行フロー(タグ push → 即 release 公開)は変わらない(no-op のため)。

## 前提と全体順序 (原計画・参考)

**重要**: Environment 作成 → release.yml 変更マージ の順序を厳守。逆にすると次のタグ push で `Environment not found` 失敗する。

タスク順序:
1. Environment 作成(GitHub UI 手動)
2. release.yml 編集
3. コミット
4. フィーチャーブランチ push + テストタグでドライラン
5. テストタグ後始末
6. ローカルゲート → main へ no-ff マージ
7. 次回本番タグでの動作確認(follow-up)

## Task 1: GitHub UI で `release` environment を作成

**種別**: 手動作業(GUI)

**手順**:

1. GitHub リポジトリの Web UI で `Settings` → 左メニュー `Environments` を開く
2. `New environment` ボタンをクリック
3. 名前入力欄に `release` を入力し `Configure environment`
4. 表示された設定画面で以下を設定:
   - **Deployment protection rules** → **Required reviewers** にチェック
     - 右のユーザ検索欄で `kenny7968` を追加
     - `Prevent self-review` は **オフのまま**(オンにすると単独メンテナは承認不可)
   - **Wait timer** は `0` のまま(触らない)
   - **Deployment branches and tags** → `Selected branches and tags` を選択
     - `Add deployment branch or tag rule` → Ref type `Tag` → Name pattern `v*` を追加
5. 画面下部 `Save protection rules` をクリック

**検証** (Bash tool):

```bash
gh api "repos/$(gh repo view --json nameWithOwner -q .nameWithOwner)/environments/release"
```

Expected: JSON が返り `"name": "release"` を含む(HTTP 200)。エラーなら environment 未作成。

さらに reviewer 設定を確認:

```bash
gh api "repos/$(gh repo view --json nameWithOwner -q .nameWithOwner)/environments/release" | grep -E '"login"|"prevent_self_review"'
```

Expected: `"login": "kenny7968"` と `"prevent_self_review": false` が見える。

**コミット**: なし(GitHub UI 設定のみ)

## Task 2: `release.yml` に `environment: release` を追加

**Files:**
- Modify: `.github/workflows/release.yml`(`jobs.release` 直下)

**Step 1: 現在の該当箇所を確認**

Read: `.github/workflows/release.yml` の 15-20 行目付近(`jobs.release` の宣言)

現在:
```yaml
jobs:
  release:
    runs-on: windows-latest
    timeout-minutes: 30
    steps:
```

**Step 2: `environment: release` を追加**

Edit: `.github/workflows/release.yml`

```yaml
jobs:
  release:
    runs-on: windows-latest
    timeout-minutes: 30
    environment: release
    steps:
```

`environment: release` は `timeout-minutes` の直下、`steps:` の直上。字下げは 4 スペース(`runs-on` と同じレベル)。

**Step 3: yaml 構文チェック**

Run (Bash):
```bash
python -c "import yaml; yaml.safe_load(open('.github/workflows/release.yml'))" && echo OK
```

Expected: `OK` のみ出力(構文エラーなし)。

Python 未インストールなら powershell で:
```powershell
$content = Get-Content .github/workflows/release.yml -Raw
$content -match "environment: release" | Out-Null
if ($matches) { "found" } else { "missing" }
```
Expected: `found`

**Step 4: diff 確認**

Run (Bash):
```bash
git diff .github/workflows/release.yml
```

Expected: `environment: release` の 1 行追加のみ(他の変更が入っていないこと)。

## Task 3: 変更をコミット

**Step 1: ステージング**

Run (Bash):
```bash
git add .github/workflows/release.yml
git status --short
```

Expected: `M  .github/workflows/release.yml` のみ表示。他ファイルが入っていたら Task 2 で意図しない変更が起きている。

**Step 2: コミット**

Run (Bash):
```bash
git commit -m "$(cat <<'EOF'
ci(release): Environment 承認ゲートを追加

jobs.release に environment: release を追加。GitHub Environments の
Required reviewers 機能で意図せぬリリース発行を防ぐ。

Environment 設定(GitHub UI 側で別途構成):
- Required reviewers: kenny7968 単独
- Deployment refs: refs/tags/v*
- Prevent self-review: off(単独メンテナのため)

設計書: docs/plans/2026-07-18-release-env-approval-gate-design.md
EOF
)"
```

Expected: `[feature/release-env-approval-gate <sha>] ci(release): ...` が表示され pre-commit hook(Husky)が通る。

## Task 4: フィーチャーブランチ push + テストタグでドライラン

**Step 1: フィーチャーブランチを push**

Run (Bash):
```bash
git push -u origin feature/release-env-approval-gate
```

Expected: リモートに新規ブランチとして push 成功。ci.yml が push に反応して起動(ci 通過を後で確認)。

**Step 2: テストタグを打つ**

Run (Bash):
```bash
git tag v0.0.0-test-approval
git push origin v0.0.0-test-approval
```

Expected: タグ push 成功。release.yml が `on.push.tags: 'v*'` にマッチして起動。

**Step 3: 承認 UI が出るのを確認**

ブラウザで GitHub の `Actions` タブを開き、`release` ワークフローの最新ラン(タグ `v0.0.0-test-approval`)を確認:

Expected:
- ワークフローが「**Waiting for review**」状態(黄色/オレンジのアイコン)
- `release` job に「Review deployments」ボタンが表示
- 承認しない限り `checkout` すら走っていない(steps セクションが空)

**Step 4: 承認せずに Cancel**

GitHub Actions UI 右上の `Cancel workflow` ボタンをクリック。

Expected:
- ラン全体が Cancelled 状態(グレー)
- release は作成されていない(`Releases` タブで確認)

CLI で確認:
```bash
gh release list --limit 5
```

Expected: `v0.0.0-test-approval` が **含まれない**(既存の release のみ)。

## Task 5: テストタグの後始末

**Step 1: リモートタグを削除**

Run (Bash):
```bash
git push origin :refs/tags/v0.0.0-test-approval
```

Expected: `- [deleted] v0.0.0-test-approval` が表示。

**Step 2: ローカルタグを削除**

Run (Bash):
```bash
git tag -d v0.0.0-test-approval
```

Expected: `Deleted tag 'v0.0.0-test-approval'`

**Step 3: 確認**

Run (Bash):
```bash
git tag -l | grep test-approval
```

Expected: 何も出力されない(exit 1 でも可)。

## Task 6: ローカルゲート → main へ no-ff マージ

**Step 1: ローカルゲート実行**

Run (PowerShell):
```powershell
pwsh tools/pre-merge-check.ps1
```

Expected: 全緑(csharpier check OK・build 0 warnings・tests 全通過)。今回の変更は yaml のみだが workflow 全体の健全性確認として実施。

**Step 2: main へ切り替え+マージ**

Run (Bash):
```bash
git switch main
git merge --no-ff feature/release-env-approval-gate -m "$(cat <<'EOF'
Merge branch 'feature/release-env-approval-gate' (リリース承認ゲート導入)

release.yml に Environment 承認ゲートを追加し、意図せぬリリース発行を
防止。GitHub Environments の Required reviewers で承認 → publish の
フローに変更。

- 動作要件表記を Windows 11 統一(chore)
- 設計書追加(docs/plans/2026-07-18-release-env-approval-gate-design.md)
- release.yml に environment: release を追加
EOF
)"
```

Expected: マージコミット作成成功。

**Step 3: log 確認**

Run (Bash):
```bash
git log --oneline -5
```

Expected: 一番上に `Merge branch 'feature/release-env-approval-gate' ...`、その下に `docs(plans)` `chore(release.yml)` `ci(release)` の feature 側 3 commits が並ぶ。

**Step 4: push (任意)**

現運用は「main 未 push で先行」パターン([[lint-format-adoption-progress]] 参照)。今回も他 backlog と合わせて後日まとめて push で良い。急ぐ場合のみ:

```bash
git push origin main
```

**Step 5: feature ブランチ削除**

Run (Bash):
```bash
git branch -d feature/release-env-approval-gate
git push origin :feature/release-env-approval-gate
```

Expected: ローカル削除+リモート削除成功。

## Task 7: 次回本番タグでの動作確認 (follow-up)

**種別**: 実運用時の確認(このプラン中には実行しない)

次のバージョン(例 vN.N.N)を実際にリリースするタイミングで:

1. `git tag vN.N.N && git push origin vN.N.N`
2. GitHub Actions で release ワークフローが「Waiting for review」で pending することを確認
3. `Review deployments` → `Approve and deploy` をクリック
4. build/test/publish が走り、`Releases` タブに新 release が公開されることを確認

**失敗した場合の切り戻し**:
- release.yml から `environment: release` を revert
- 従来通り即 publish に戻る
- Environment 設定は残しておいて後日再挑戦

## 完了条件

- [ ] Task 1-6 全ステップ完了
- [ ] main に承認ゲート付き release.yml がマージ済み
- [ ] テストタグ・テスト release が残っていない
- [ ] feature ブランチ削除済み(local/remote)
- [ ] pre-merge-check.ps1 が最終状態で緑

## 関連情報

- 設計書: `docs/plans/2026-07-18-release-env-approval-gate-design.md`
- 変更ファイル: `.github/workflows/release.yml`
- GitHub docs: https://docs.github.com/en/actions/deployment/targeting-different-environments/using-environments-for-deployment
- 参考 memory: `phase-work-git-flow`(no-ff マージ運用)、`production-build-started`(リリース CI の歴史)
