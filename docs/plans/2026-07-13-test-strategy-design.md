# yEdit テスト戦略 設計書

- 日付: 2026-07-13
- ステータス: 承認済み(ユーザーレビュー・全セクション承認)
- 対象: プロジェクト全体のテスト戦略(テストピラミッド/CI・ローカルゲート/App 層テスト基盤/実機 SR 検証の位置づけ)

## 0. 背景と現状

### 現状の資産(強い所)

| 資産 | 内容 |
|---|---|
| `tests/yEdit.Core.Tests` | 約 600 ケース。Buffer/Layout/Search/Csv/Text/Settings/Backup/Editing/IO の純ロジック単体テスト+UIA プロバイダ(yEdit.Accessibility)の契約テスト 3 ファイル |
| `tests/yEdit.Editor.Tests` | 約 226 ケース。EditorControl 実生成(STA)での編集・キャレット・選択・UIA イベント・IME・クリップボード検証 |
| `tests/yEdit.Core.Bench` | 性能ゲート EXE(1GB 合成文書/レイアウト/タイピング応答 5µs 目標)。目標未達で EXIT 1 |
| `tests/yEdit.Editor.Smoke` | 手動起動器 EXE(目視確認/`--bench` GDI 描画/`--ime` ATOK/`--uia` SR 実機/`--gen-1gb`・`--bench-save` 大容量 I/O) |
| `docs/plans/2026-07-06-p6-manual-checklist.md` ほか | 実機手動チェックリスト(A〜P・90+項目、NVDA/PC-Talker/ナレーター/SR なしのマトリクス) |
| `tools/verify-uia-editor.ps1` / `walk-test-editor.ps1` / `word-sim.ps1` | UIA クライアント側から SR の呼び出しパターンを再現する検証スクリプト |

### 穴(本戦略で解決する課題)

1. **App 層(`src/yEdit.App`)の自動テストがゼロ。** MainForm が全 Controller を ctor で直接 new し、static+P/Invoke(`PcTalkerSpeech.IsRunning()`/`AnnouncerFactory`)が絡むためテスト困難。`tests/yEdit.App.Tests` 新設は従来から申し送り事項。
2. **CI ゲートが弱い。** ワークフローは `release.yml` 1 本(v* タグ push 時のみ)で、テスト実行は Core.Tests のみ。Editor.Tests(約 226 ケース)すら CI ゲート外。通常の push/PR ではテストが一切走らない。
3. **SR 実機検証が完全手動**(チェックリスト+個別 ps1 スクリプト)で、回帰スクリーニングの自動化がない。
4. **性能ゲート(Bench)・大容量 I/O(Smoke)が手動運用**で、実行タイミングの規約がない。

### 運用の前提

- リポジトリはローカル中心運用(local main は origin より大幅に先行・未 push)。フェーズ作業は「フィーチャーブランチ→main へ no-ff マージ」。
- したがって GitHub Actions だけでは普段のマージを守れない。**ローカルゲートを正とし、GitHub CI は push 時・リリース時の防衛線として拡充する**(両方やる)。

## 1. テストピラミッドと各層の責務

テストは「可能な限り下層(高速・安定な層)に置く」を原則とし、5 層で構成する。

| 層 | 資産 | 責務 | 実行タイミング |
|---|---|---|---|
| L1 単体 | Core.Tests(約 600) | 純ロジック+UIA プロバイダ契約テスト | 毎マージ(ローカルゲート+CI) |
| L2 コンポーネント | Editor.Tests(約 226) | EditorControl 実生成での編集・キャレット・UIA イベント・IME・クリップボード | 毎マージ(ローカルゲート+CI) |
| L3 App 統合 | **App.Tests(Phase 2 で新設)** | Controller 群・Document 管理・Speech 経路選択のロジック | 毎マージ(Phase 2 以降) |
| L4 性能ゲート | Core.Bench / Smoke `--bench` `--bench-save` | 1GB 級・タイピング応答 5µs 等の EXIT コード判定 | 手動(Buffer/Layout/IO に大きな変更が入ったとき) |
| L5 実機手動 | SR チェックリスト+tools/*.ps1 | NVDA/PC-Talker/ナレーターの実発声確認 | リリース前・a11y 関連変更時 |

### 原則

1. **下層優先**: 新機能はまず L1/L2 で TDD する。上層のテストは下層で表現できない結合部分に限る。
2. **L5 が a11y の最終ゲート**: 「実際に SR が発声するか」は自動化不可能。UIA 呼び出しパターンの自動再現(Phase 3)はあくまで前段フィルタであり、実機の人手確認を置き換えない。
3. **Accessibility の契約テストは現行配置を維持**: yEdit.Accessibility 専用のテストプロジェクトは作らず、Core.Tests(プロバイダ契約)と Editor.Tests(UIA 統合)に置き続ける。
4. **DoD 規約の継続**: 「テスト数は純増のみ・退行なし」を各フェーズの DoD に含める。

## 2. Phase 1: ローカルゲート+GitHub CI(小・1 セッション)

### 2.1 ローカルゲート `tools/pre-merge-check.ps1`(新設)

- 内容: ①Release ビルド 0 警告 → ②Core.Tests → ③Editor.Tests を順に実行し、いずれか失敗で EXIT 1。
- 運用ルール: **main への no-ff マージ前に必ず実行する**(本書がその規約の典拠)。
- Phase 2 以降、App.Tests もこのスクリプトに追加する。

### 2.2 GitHub Actions `ci.yml`(新設)

- トリガ: 全ブランチの push+main への PR。ランナー: windows-latest。
- ステップ: setup-dotnet(9.x)→ Release ビルド(0 警告チェック)→ Core.Tests → Editor.Tests。
- **既知リスク**: Editor.Tests のクリップボード/UIA イベント系はホステッドランナーで不安定な可能性がある。
  - 対策: 初回 CI 実行で不安定テストを洗い出し、`[Trait("Category", "LocalOnly")]` で隔離。CI ではフィルタ除外し、ローカルゲートでは全件実行する。隔離したテストは一覧を本書 §6 の申し送りとして管理する。

### 2.3 `release.yml` の拡張

- 既存の「テスト(リリース前ゲート)」ステップに Editor.Tests を追加する(現状 Core.Tests のみ)。

## 3. Phase 2: App 層 DI リファクタ+App.Tests 新設(大・複数セッション)

### 3.1 方針: コンテナレスの本格分離

- MainForm を「組み立てだけを行う composition root」に変え、各 Controller はコンストラクタ注入されたインターフェースにのみ依存する形へリファクタする。
- **DI コンテナ(Microsoft.Extensions.DependencyInjection 等)は導入しない**。この規模では手動組み立てで十分であり、起動経路を単純に保つ(承認済みの設計判断)。

### 3.2 鍵となる分離ポイント

1. **MainForm(`this`)への強結合を解消**: 各 Controller が Form 全体ではなく、必要な操作だけの狭いインターフェース(例: `IStatusPresenter`、`ITabHost`)を受け取る。
2. **static+P/Invoke の壁を注入可能に**: `PcTalkerSpeech.IsRunning()` / `AnnouncerFactory` を `ISpeechRouteDetector` 等のインターフェースでラップし、テストでは偽実装を注入する。
3. **EditorControl はモックしない**(承認済みの設計判断): Editor.Tests で実証済みのとおり STA 上で実生成できるため、App.Tests でも実物 EditorControl+実 Core を使う。偽物にするのは Form 境界と OS 境界(SR 稼働判定・ファイルダイアログ・時計)に限定する。
4. **BackupCoordinator は時計を抽象化**: `TimeProvider` を注入してタイマーロジックを決定的にテスト可能にする。

### 3.3 進め方(ストラングラー方式)

- **Controller 1 つ=1 フィーチャーブランチ=1 no-ff マージ**。各ブランチで「シーム導入 → App.Tests に失敗テスト → 挙動不変を確認してマージ」。
- 優先順: DocumentManager → FileController → SearchController → BackupCoordinator → CsvController / GrepController → Speech 系。
- `tests/yEdit.App.Tests` 新設: xUnit・net9.0-windows・STA ヘルパは Editor.Tests のパターン(`Sta.cs`)を踏襲。必要に応じ yEdit.App に `InternalsVisibleTo` を追加。
- ダイアログ類はロジックをプレゼンター層に抽出できるものだけテスト対象とし、純 UI 部分は L5 手動検証に残す。

## 4. Phase 3(任意・将来): SR 半自動化+性能ゲートの CI 組み込み

- `tools/verify-uia-editor.ps1` / `walk-test-editor.ps1` / `word-sim.ps1`(+`verify-msaa-client.ps1`)を「SR 呼び出しパターン回帰スイート」として 1 本のランナーに統合し、a11y 関連変更時の一次スクリーニングにする。実発声の人手確認(L5)は置き換えない。
- Core.Bench を `workflow_dispatch`(手動トリガ)で CI から実行可能にする。ただしホステッドランナーは性能が揺れるため、EXIT 判定のしきい値ゲートは**ローカル実行を正**とする。

## 5. ロードマップ(承認済み: 案 A「CI 先行→DI リファクタ」)

| Phase | 内容 | 規模 |
|---|---|---|
| Phase 1 | ローカルゲートスクリプト+CI ワークフロー新設+release.yml 拡張 | 小・1 セッション |
| Phase 2 | App 層 DI リファクタ+yEdit.App.Tests 新設(Controller 単位で段階マージ) | 大・複数セッション |
| Phase 3 | SR 検証半自動化・性能ゲートの CI 組み込み | 任意・将来 |

リスクの高い Phase 2 の大手術を、Phase 1 で常時回る安全網(既存 約 827 テスト)を張ってから行う、が案 A の趣旨。

## 6. 申し送り・未決事項

- CI 初回実行で判明した `LocalOnly` 隔離テストの一覧は、判明次第この節に追記する。
- Phase 2 の Controller ごとの詳細設計(インターフェース名・分割単位)は、各ブランチ着手時に個別の実装計画で確定する。本書の §3 は方針レベルの合意。
- `tools/verify-msaa-client.ps1` は現在未コミット(untracked)。Phase 3 でスイート統合する際に扱いを確定する。
- ci.yml / release.yml 拡張の実機検証は未実施(未公開履歴の公開を避けるため push しない方針)。ユーザーが次回 origin へ push した際に初回 CI 実行を確認し、不安定テストがあれば LocalOnly 隔離を行うこと。あわせて初回実行の所要時間を確認し、timeout-minutes(ci.yml=20/release.yml=30)が窮屈なら調整すること。
