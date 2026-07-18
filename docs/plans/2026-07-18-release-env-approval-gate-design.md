# リリース承認ゲート導入 設計書

- **作成日**: 2026-07-18
- **対象**: `.github/workflows/release.yml`+GitHub リポジトリ設定 (Environments)
- **区分**: 運用強化(挙動不変=既存の tag→release フローは維持)
- **前提**: OSS 公開を見据えた「意図せぬリリース発行の防止」

## 0. スコープと決定事項サマリ

**採用方針**: **GitHub Environments の Required reviewers 機能**でリリース job を承認ゲート化する。

**主要判断**:
- **原案「タグを main 限定にする」は取り下げ**(§1 参照)。意図(未承認リリース防止)に対して「守り過ぎかつ守り足りない」ため。
- Environment 名: **`release`** (repo 内で release と言えば GitHub Release を指すため曖昧性なし)
- ゲート範囲: **job 全体**(build/test/publish を一括で pending)
- Deployment refs 制限: **`refs/tags/v*` に限定**(workflow の `on.push.tags: 'v*'` と 2 段重ね)
- Wait timer: **0 分**(即承認可)
- Prevent self-review: **オフ**(単独メンテナのため。オンにすると自分では承認できず永久停止)

**非対象(scope out)**:
- タグ保護 ruleset(Settings→Rules→Rulesets)は将来検討。ワークフロー側で完結する Environment 承認のみを本設計の範囲とする。
- 承認者複数化(現状 kenny7968 単独)。将来の共同メンテナ加入時に別途対応。
- タグ自動削除(Reject 後もタグは残す。既存の「打ち直しで回す」運用を維持)。
- release.yml の他の見直し(build 分割・キャッシュ・self-contained 化など)。

## 1. 原案の取り下げ理由(判断ログ)

当初「タグは main ブランチ上のみで発火」を目指したが、以下により棄却:

1. **GitHub Actions の `on.push.branches` と `on.push.tags` は OR**。タグ push イベントに `branches:` フィルタは適用されない=構文レベルで「main 上のタグ」は表現不可。
2. workflow 内で判定するなら `github.event.base_ref` か `git merge-base` によるが、いずれも実装が入り込むうえ**意図(未承認リリース防止)を達成しない**。権限あるユーザが main で誤タグを打っても止まらず、逆に legitimate タグが silent skip される事故のほうが痛い。
3. **未承認リリース防止の本質は「押した人・タイミングの承認」**。ブランチ位置は関係ない。GitHub Environments はこの本質を直接扱える仕組み。

したがって「main 上のタグ判定」ではなく「push 後の人間承認」に舵を切る。

## 2. アーキテクチャ

```
git push origin v1.2.3
   ↓
GitHub 受信 → release ワークフロー起動
   ↓
release job = "Waiting for review" 状態で pending (グレー)
   │  (Environment `release` の Required reviewers 待ち)
   ↓
kenny7968 が Actions UI で "Review deployments" → "Approve and deploy"
   ↓
job 実行 (checkout → build → test → publish → zip → notes → gh release create)
   ↓
GitHub Release 作成
```

ゲートは **job の実行前**に効く。checkout すら走らないため、pending 中の消費リソースはゼロ。

## 3. 実装(変更点)

### 3.1 `.github/workflows/release.yml`

`jobs.release` に `environment: release` を追加(1 行)。

```yaml
jobs:
  release:
    runs-on: windows-latest
    timeout-minutes: 30
    environment: release      # ← 追加
    steps:
      ...
```

他のステップは変更なし。既存の build → test → publish → zip → notes → `gh release create` フローは維持。

### 3.2 GitHub リポジトリ設定(手動)

Settings → Environments → **New environment** → 名前 `release`:

- **Required reviewers**: kenny7968 を追加(単独)
- **Wait timer**: 0 分
- **Prevent self-review**: **オフ**(オンだと単独メンテナは承認不可)
- **Deployment branches and tags**: `Selected refs` → `refs/tags/v*` を追加

環境変数/secrets は不要(既存の `github.token` で足りる)。

### 3.3 ドキュメント

README・docs 更新は不要。リリース手順は「タグ push → Actions で Approve」で自明。将来共同メンテナが増える段階で CONTRIBUTING.md 相当が要ればそこで書く。

## 4. データフロー・状態遷移

| 状態 | 契機 | 次状態 |
|---|---|---|
| ワークフロー未起動 | `git push origin v1.2.3` | Pending review |
| Pending review | Approve | Running |
| Pending review | Reject | Cancelled(release 未作成・タグ残) |
| Pending review | 放置 | Actions UI から手動 Cancel 可能 |
| Running | build/test/publish 成功 | Success(release 公開) |
| Running | 途中失敗 | Failed(release 未作成・タグ残) |

## 5. 失敗モードと復旧

| 状況 | 挙動 | 復旧 |
|---|---|---|
| 承認 Reject | job Cancel、release 未作成、タグは残る | タグを消して打ち直す、または次バージョンで再度 push |
| 承認せず放置 | 永久 pending(タイムアウトなし) | Actions UI から手動 Cancel |
| 承認後 build 失敗 | 通常の workflow 失敗、release 未作成、タグは残る | 修正 commit → main → タグ打ち直し |
| 承認後 publish 失敗(zip 済み) | release 未作成、artifact も残らない | 再実行 or タグ打ち直し |
| Deployment refs ルール違反(v* 以外) | job が起動せず即失敗 | 該当タグを消す |
| Environment 未作成のまま release.yml だけマージ | job 起動時に "Environment not found" で失敗 | Environment を作成して再実行 |

**マージ順序の注意**: **Environment を先に作成 → release.yml 変更をマージ**の順が安全。逆にすると次のタグで release 失敗する。

## 6. 検証プラン

現行運用に影響なく試すため、feature branch 上で以下:

1. Environment `release` を GitHub UI で作成(reviewer=kenny7968・deployment refs=`refs/tags/v*`・self-review オフ)
2. feature branch に release.yml の変更を commit・push
3. **ドライラン用テストタグ** `v0.0.0-test-approval` を feature branch の HEAD に打って push
   - Environment の deployment refs が `v*` にマッチ(`v0.0.0-test-approval` は該当)
   - Actions UI に "Waiting for review" が出ることを確認
4. Approve せず **Cancel** → release 未作成を確認
5. テストタグを削除: `git push origin :refs/tags/v0.0.0-test-approval`
6. 通常ゲート(`tools/pre-merge-check.ps1`)通し → main へ no-ff マージ
7. **初回本番タグ**(次バージョン)で Approve ボタンが出ることを最終確認

**Deployment refs 制限のネガティブテスト**(任意):
- `v` 以外の適当なタグ(例 `test-approval`)を push → workflow が `on.push.tags: 'v*'` にマッチせず起動しないことを確認(Environment 側の refs 制限は workflow が起動した後の deployment 段階で効くため、そもそも workflow 起動しないケースの方が先)。

## 7. 開かれた問い/将来課題

- **共同メンテナ加入時の承認者運用**: 現状単独。将来 2 名以上になったら Prevent self-review を有効化し、リリースは相互承認とする(セキュリティ上望ましい)。
- **タグ保護 ruleset との併用**: 「そもそも v* タグを作れるユーザを限定」までやるかは OSS 化直前に別途判断。今回は Environment のみで足りる。
- **リリース通知/告知の自動化**: 現状は GitHub Release ページのみ。SNS 等への告知が要るなら別 workflow で。
- **署名済みインストーラ復活時**: 過去 revert 済み(memory 参照)。将来復活時は artifact 増加+署名 secret 管理で Environment を活用できる(secrets を environment 紐付けにできる)。

## 8. リンク

- 関連 memory: `production-build-started.md`(リリース CI の歴史)、`phase-work-git-flow.md`(feature→main 運用)
- 関連 workflow: `.github/workflows/release.yml`、`.github/workflows/ci.yml`
- GitHub docs: [Using environments for deployment](https://docs.github.com/en/actions/deployment/targeting-different-environments/using-environments-for-deployment)
