# PC-Talker サポート廃止(専用実装の排除) 設計書

- 日付: 2026-07-13
- ステータス: 承認済み(ユーザー承認: ①排除をテスト戦略 Phase 2 Stage 2 より先に実施 ②イベント土台も全削除)
- 関連: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`(Stage 2 の再スコープ元)

## 0. 判断と理由

PC-Talker への対応(専用実装による能動発声の保証)をやめる。

- **理由**: ①PC-Talker 対応のための特殊実装・実機検証・バグ調査の負担が継続的に発生する(空行未発声バグは未解決のまま)。②PC-Talker に対応した優秀な既存エディタが存在する。③標準的な UIA に対応した SR(主に NVDA)で読み上げできれば yEdit の意義は十分果たせる。
- **「廃止」の意味**: PC-Talker を能動的に壊すのではなく「保証しない」。全 SR 共有の汎用 UIA プロバイダ経路(受動読み)は残るため、PC-Talker でも文字/行の受動読みは動く範囲で動くが、専用の能動発声(空行・単語ナビ・状態通知の PCTKPReadW 直叩き)は失われる。説明書に対応 SR を明記する。
- **実施タイミング**: テスト戦略 Phase 2 Stage 2(Speech: ISrRoute 導入)の**前**に行う。Stage 2 の内容(`ISrRoute { bool IsPcTalker }` シーム・「PcTalker 経路で能動発声が出る」テスト・PC-Talker 実機 L5 スポット確認)は削除予定コードへの投資そのものであり、先に整備すると二重の無駄になるため。
- **「リファクタ前にテスト整備」原則との整合**: あの原則が守るのは「残す挙動」。残す挙動(NVDA/汎用 UIA の読み上げ)は UIA プロバイダ契約+UIA イベント系 約35ケース+Editor.Tests 全体で既に担保済み。削除対象の PC-Talker 分岐には既存の自動テストがゼロなので、先に書くべき特徴付けテストが存在しない。

## 1. 現状分析(2026-07-13 コード調査)

- 旧二系統機構(SrRoute/SrRouteSelector/SrContext/優先SR設定タブ)は **P7 で撤去済み**(`docs/plans/2026-07-12-p7-verify-remove-release.md` Task 8)。現在は全 SR が単一の v2 UIA プロバイダで受動読みする構成。
- 現行の PC-Talker 依存は「起動時1回の稼働判定+bool ガード内の能動発声上乗せ」に集約されており、約130行+死にコード:

| 対象 | 場所(2026-07-13 時点) | 規模 |
|---|---|---|
| PCTKUsr.dll P/Invoke(検出 PCTKStatus+発声 PCTKPReadW) | `src/yEdit.App/Speech/PcTalkerSpeech.cs` | 79行 |
| PC-Talker 用 Announcer | `src/yEdit.App/Speech/PcTalkerAnnouncer.cs` | 17行 |
| Announcer 生成分岐(static Lazy) | `src/yEdit.App/Speech/AnnouncerFactory.cs:15,18-20` | 数行 |
| 判定フィールド+空行/単語ナビ能動発声 | `src/yEdit.App/MainForm.cs:33,56-75` | 約21行 |
| 空行判定の孤立コード(**本番未使用**・grep で参照はテストのみ) | `src/yEdit.Core/Reading/EmptyLineDetector.cs`+テスト19件 | 23行 |

- `PcTalkerSpeech.IsRunning()` の呼び出し源は `AnnouncerFactory.cs:15` と `MainForm.cs:33` の2箇所のみ。
- イベント土台(`EditorControl.CaretEnteredEmptyLine`/`WordNavigated`+`DocumentManager` 転送)の購読者は「MainForm の `_isPcTalker` ガード内2箇所」「Smoke の `UiaSmokeAnnouncer`」「テスト15件」のみ。NVDA・ナレーター等の UIA 系 SR は UIA プロバイダ経由でキャレットを自力追跡するため、このイベントを使わない=**用途は PC-Talker 専用**(全削除の根拠)。

## 2. 削除スコープ(コミット分割)

diff レビューを機械的に保つため 3 コミットに分ける。

### コミット① App 層の PC-Talker 摘出

- `src/yEdit.App/Speech/PcTalkerSpeech.cs` 全削除
- `src/yEdit.App/Speech/PcTalkerAnnouncer.cs` 全削除
- `AnnouncerFactory`: 分岐を削除し常に `UiaAnnouncer` を返す。**このブランチでは分岐除去のみ**とし、構造整理(static Lazy 解消/Factory 解体)は縮小版 Stage 2 で判断
- `MainForm`: `_isPcTalker` フィールド+空行能動発声+単語ナビ能動発声の購読ブロックを削除
- `IAnnouncer.cs` 等の「PC-Talker 稼働判定に応じて生成」系コメントを現状に合わせて修正

### コミット② イベント土台の全削除

- `EditorControl.CaretEnteredEmptyLine` イベント+`RaiseCaretEnteredEmptyLineIfNeeded`+行遷移検出用の内部状態(`EditorControl.cs:76-78` コメントの対象フィールド)+呼び出し2箇所(`:2046,:2508`)+関連コメント(`:955,:2280` ほか)
- `EditorControl.WordNavigated` イベント+`RaiseWordNavigated`+発火判定(`:2297,:2509-2511`)+`WordNavigatedEventArgs.cs`
- `DocumentManager.ActiveCaretEnteredEmptyLine`/`ActiveWordNavigated`+転送配線(`DocumentManager.cs:41-42,70-77`)
- Smoke: `tests/yEdit.Editor.Smoke/UiaSmokeAnnouncer.cs` 削除+`Program.cs`/`MainForm.cs` の購読配線除去(**`--uia` モード自体は NVDA 実機検証用に温存**)
- テスト削除: `EmptyLineNavigationTests.cs` のうち `CaretEnteredEmptyLine` 系10件+`EditorControlWordNavEventTests.cs`(3件)。**同ファイル内の `RaiseUiaSelectionEvents` プロパティ契約テスト2件は温存機能のカバレッジのため新ファイル `RaiseUiaSelectionEventsTests.cs` へ移設**(実装計画作成時のコード精査で判明)
- 死にコード削除: `EmptyLineDetector.cs`+`EmptyLineDetectorTests.cs`(19件)

### コミット③ ドキュメント・ツール整理

- **説明書**(`説明書/yEdit説明書.md`): 対応 SR を「NVDA 等 UIA 対応スクリーンリーダー」とし PC-Talker がサポート対象外である旨を明記。あわせて「優先するスクリーンリーダー」設定の記載(235行)を削除(**P7 で撤去済み機能の記載残り=既存の陳腐化**・実装計画作成時に発見)。**文面はユーザー編集版が正のため、変更案を提示してユーザー確認を経る**
- `docs/plans/2026-07-06-p6-manual-checklist.md`: 冒頭に「PC-Talker 列は 2026-07-13 のサポート廃止によりスキップ対象」の注記を追記(過去の実施記録は改変しない)
- `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`: Stage 2 再スコープを追記(本書 §5)
- `tools/verify-msaa-client.ps1`(untracked): 削除。調査の結果 Scintilla 前提(クラス名探索)の陳腐化残骸で、自作 EditorControl 化後の現行アプリでは動作不能

## 3. 温存するもの(触らない)

- `src/yEdit.Accessibility/` 一式(v2 UIA プロバイダ) — 全 SR 共有の唯一の受動読み経路
- `UiaAnnouncer`/`AnnouncerBase`/`IAnnouncer` — 汎用能動通知(検索結果件数等)
- MSAA 抑制(`EditorControl` の WM_GETTEXT 非応答=ネイティブ表面原則) — 全 SR を UIA 一本に寄せる汎用設計
- CSV モードの `RaiseUiaSelectionEvents` 制御 — 抑止効果は UIA 経路全体に及ぶため挙動は温存。「PC-Talker 向け」コメントのみ「UIA 系 SR 向け」に修正

## 4. DoD

1. `tools/pre-merge-check.ps1` 全緑(Release 0 警告+Core+Editor+App)
2. テスト数 **831 → 799**(Core 588→569・Editor 229→216・App 14)。Editor は −15+移設2(RaiseUiaSelectionEvents 契約)。減少は「削除機能に付随するテストの削除」であり、テスト数純増規約の例外として本書を典拠とする
3. **NVDA 実機スポット確認(L5・ユーザー実施)**: 文字/行の通常読み・空行(NVDA ネイティブ読み)・単語ナビ(Ctrl+←→)・状態通知(検索結果件数など UiaAnnouncer 経由)・CSV セル読み
4. 別エージェントによるコードレビュー(マージ前・いつもの運用)
5. main へ no-ff マージ。**マージコミットのハッシュを本書 §6 に追記**(復活用参照)

## 5. テスト戦略 Phase 2 Stage 2 の再スコープ

- `ISrRoute { bool IsPcTalker }` は不要化(判定対象の消滅)。Phase 2 設計書 §2.1 の該当シームは削除
- Stage 2 の残作業(縮小版): `AnnouncerFactory` の構造整理(static Lazy 解消 or MainForm での直接生成)+`FakeAnnouncer` による通知配線テスト
- Stage 1 実施記録の追加観点「`ActiveCaretEnteredEmptyLine`/`ActiveWordNavigated` 転送テスト」は対象消滅により不要
- Stage 3 以降(FileController 等)は PC-Talker 非依存のため影響なし

## 6. 申し送り

- **復活用参照**: PC-Talker 対応を将来復活させる場合は本節に追記されるマージコミットの直前状態を参照(installer 廃止時の `b82d084` 参照方式と同じ)。マージコミット: (マージ後に追記)
- 未解決バグ「PC-Talker 時に空行が読まれない」(仮説A/B/C 切り分け中)は、サポート廃止により**調査打ち切り**とする
- テスト戦略 Phase 3 の SR 回帰スイート統合対象から `verify-msaa-client.ps1` を除外(本件で削除)。`walk-test-editor.ps1`(PC-Talker の1文字走査再現)は汎用 UIA クライアント堅牢性テストとして残すか、Phase 3 着手時に採否を判断
