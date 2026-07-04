# 設定ダイアログ新項目（6 タブ構成）— 設計書

- 日付: 2026-07-04
- ブランチ: `feature/settings-new-items`
- 前提: 設定ダイアログのタブ化（`docs/plans/2026-07-04-settings-tabbed-dialog-design.md`）が main マージ済み
- 対象: `src/yEdit.Core/Settings/`（AppSettings/SettingsStore）、`src/yEdit.App/Settings/`（タブ群）、
  `src/yEdit.Core/Speech/`・`src/yEdit.App/Speech/`（SR 経路）、`src/yEdit.App/`（MainForm/FileController/CsvController/BackupCoordinator/EditorAppearance/Program）、`src/yEdit.Editor/ScintillaHost.cs`

## 目的

タブ化で受け皿ができた設定ダイアログに、コードベース精査で洗い出した「ユーザーが変えたくなる固定値・挙動」のうち採用が確定した 9 項目を追加する。あわせて既存キー `BackupEnabled` / `BackupIntervalSeconds` を初めて UI に露出する。

## スコープ

**採用（本設計の対象）:**

| タブ | 項目 | 既定 |
|---|---|---|
| 基本 | .csv ファイルを開いたとき自動的に CSV モードにする | 無効 |
| 編集 | タブ幅 | 4 |
| 編集 | タブをスペースに変換 | 無効 |
| 表示 | 行番号を表示する | 無効 |
| 表示 | 現在行を強調表示する | 無効 |
| 表示 | キャレットの太さ（px） | 1 |
| 表示 | 空白・改行文字を表示する | 無効 |
| バックアップ（新設） | 文書のバックアップを有効にする | 有効 |
| バックアップ | バックアップ間隔（秒） | **300**（既定値を 30 から変更） |
| バックアップ | 起動時にバックアップを復元するか確認する | 有効 |
| 読み上げ（新設） | 優先するスクリーンリーダー | NVDA |

**スコープ外（ユーザー指示で明示的に除外）:**

- 新規 UTF-8 ファイルの BOM 付与設定（別途検討）
- 上記以外の候補（最近のファイル件数、検索既定オプション、CSV 区切り文字、Markdown プレビュー系、折り返し単位、判定失敗時フォールバック文字コード等）

## AppSettings 新キーと健全化

```csharp
public bool CsvAutoModeOnOpen { get; set; } = false;
public int TabWidth { get; set; } = 4;              // 範囲 1〜16
public bool TabsToSpaces { get; set; } = false;
public bool ShowLineNumbers { get; set; } = false;
public bool HighlightCurrentLine { get; set; } = false;
public int CaretWidth { get; set; } = 1;            // 範囲 1〜5（px）
public bool ShowWhitespace { get; set; } = false;
public bool ConfirmRestoreOnStartup { get; set; } = true;
public string PreferredScreenReader { get; set; } = "nvda";  // "nvda" | "pctalker"
```

- `BackupIntervalSeconds` の既定値を 30 → **300** に変更。settings.json に明示値を持つ既存ユーザーは影響なし（値が保持される）。`SettingsStore.Normalize` の「5 未満は既定へ」補正はそのまま（補正先が 300 になる）。
- `SettingsStore.Normalize` に追加:
  - `TabWidth` が 1〜16 の範囲外 → 4
  - `CaretWidth` が 1〜5 の範囲外 → 1
  - `PreferredScreenReader` が `"nvda"` / `"pctalker"` 以外（明示 null・空文字・未知値）→ `"nvda"`
- `AppSettings.Clone()` は `MemberwiseClone` ベースのため新キーは自動で複製される（値型・string のみ）。テストで固定する。

## タブ構成（6 タブ）

`SettingsDialog._tabs` の配列順 = タブ順:

```
基本 / 編集 / 禁則処理 / 表示 / バックアップ / 読み上げ
```

新規クラス: `BackupSettingsTab` / `SpeechSettingsTab`（`src/yEdit.App/Settings/Tabs/`）。既存タブ 3 つ（基本・編集・表示）に項目追加。禁則処理タブは無変更。

## 基本タブ: CSV 自動モード

チェックボックス「.csv ファイルを開いたとき自動的に CSV モードにする(&V)」（既定 OFF）。

**発動経路（設計判断・確定）:** 「開く」ダイアログ・最近のファイル・文字コード指定の開き直し（ReopenWithEncoding）で発動。**grep ジャンプ（`MainForm.OpenAndSelect`）は除外** — ジャンプは開いた直後に一致範囲の選択とエディタへのフォーカスを行うため、CSV モード化（読取専用＋フォーカスのシンク退避）と正面衝突する。grep ジャンプで開いた .csv は通常モードのまま（手動でモードに入れる）。

- 却下案: 全経路で発動 — grep ジャンプが使い物にならなくなる。grep ジャンプ × CSV は既存申し送りでもスコープ外の難題。

**実装形:**

- `FileController.TryOpenOrActivate(string path, bool suppressAutoCsv = false)` にフラグを追加。`OpenAndSelect` だけ `true` を渡す。
- 新規ロード成功後（既存タブのアクティブ化では発動しない）、MainForm から注入されたコールバック `Action<Document>` を呼ぶ。`ReopenWithEncoding` も成功後に同コールバックを呼ぶ。
- コールバック側（MainForm）: 「設定 ON かつ `Path` の拡張子が `.csv`（OrdinalIgnoreCase）」なら CSV モード進入を試みる。
- CSV モード進入は `CsvController.ToggleMode` の ON 側ロジックをメソッド抽出（`TryEnterMode(Document doc, bool announceParseError)` 等）して共通化。読取専用化・UIA 抑止・シンク退避・初期セル確定・「CSV モード オン＋セル」読み上げは手動時と同一。
- **解析失敗時**: エラーにせず通常モードで開き、「CSV として解析できませんでした」と読み上げ通知（自動 ON を期待した SR ユーザーが無言で取り残されないため）。空データ（0 行）は手動時と同じ「モード ON＋シンク退避」。

## 編集タブ: タブ幅・タブ→スペース

- 「タブ幅(&T)」NumericUpDown 1〜16、既定 4 → `EditorAppearance.Apply` で `ed.TabWidth = settings.TabWidth`（ScintillaNET プロパティ）。
- 「タブをスペースに変換(&S)」CheckBox 既定 OFF → `ed.UseTabs = !settings.TabsToSpaces`。**新規の Tab 入力にのみ効き、既存のタブ文字は変換しない**（Scintilla の標準セマンティクス。一括変換機能は本設計の対象外）。
- **禁則整形の連動（設計判断・確定）**: `MainForm.FormatWithKinsoku` が `KinsokuFormatter.Format` に `settings.TabWidth` を渡す（現状は既定引数の 8 固定）。「画面の見た目どおりに整形される」一貫性を優先。**既定が 8→4 になるため、タブ文字を含む文書の整形結果が従来版と変わる**（承認済み）。
- 表示折り返し（`ApplyWrapColumn`）はピクセル幅ベースで動くため変更不要。

## 表示タブ: 視覚系 4 項目

いずれも `EditorAppearance.Apply` に追加。OK 時に全タブへ即時反映され、テーマ変更にも一括追従する。既定値はすべて「現状の見た目を変えない」側。

- 「行番号を表示する(&N)」: `ScintillaHost` に `ShowLineNumbers` プロパティを新設。ON でマージン 0 を行番号マージンにし、**行数の桁数変化に応じて幅を自動再計算**（既存の TextChanged ハンドラに乗せる。`TextWidth(Style.LineNumber, "9…9")＋余白`）。OFF で幅 0。行番号スタイルの配色は `StyleClearAll` の伝播でテーマに自動追従。
- 「現在行を強調表示する(&H)」: `ed.CaretLineVisible = true`＋`CaretLineBackColor` は**テーマの背景色に前景色を約 12% ブレンドして自動算出**。
  - 設計判断: 色指定 UI は設けない。「カスタム RGB は対象外」の既存合意（`AppearanceTheme.cs` コメント・M7 設計）と整合。ブレンド比は 4 テーマ（標準/白黒/黒白/紺白）すべてで破綻しないよう実装時に微調整可（10〜15% 目安）。
- 「キャレットの太さ(&W)」: NumericUpDown 1〜5（px）、既定 1 → `ed.CaretWidth`。弱視ユーザーのキャレット視認性対策。
- 「空白・改行文字を表示する(&B)」: 1 チェックで両方切替 → `ed.ViewWhitespace = VisibleAlways / Invisible`＋`ed.ViewEol = true / false`。項目を分けない（シンプル優先）。

補足: いずれも視覚専用の変更で、SR の読み（受動読み・能動発声）には影響しない。CSV モードのセルハイライトと現在行強調は視覚的に重なり得るが実害なし。

## バックアップタブ（新設）

- 「文書のバックアップを有効にする(&B)」CheckBox（既存キー `BackupEnabled` の初 UI 露出）
- 「バックアップ間隔（秒）(&I)」NumericUpDown 5〜3600、既定 300（`BackupCoordinator` の既存クランプ 5〜3600 と一致させる）
- 「起動時にバックアップを復元するか確認する(&C)」CheckBox 既定 ON（新キー `ConfirmRestoreOnStartup`）
  - ON: 現状どおり `RestoreDialog` を表示（復元/すべて破棄/後で）
  - OFF: ダイアログなしで**全レコードを復元**し「バックアップを N 件復元しました」と読み上げ通知。レコード単位の失敗は現行同様に握り潰さず残す（バックアップ保持・次回再挑戦）。`OfferRestoreOnStartup` に確認スキップ経路を追加する形
- **反映（設計判断）**: `BackupCoordinator` に `UpdateSettings(bool enabled, int intervalSeconds)` を追加し、`MainForm.OpenSettings` の OK 時に**即時反映**（再起動不要）。有効→無効はタイマー停止のみで**既存バックアップファイルは削除しない**。無効→有効はタイマー再開＋ Reconcile 再開。`_enabled` は readonly を外す

## 読み上げタブ（新設）: 優先するスクリーンリーダー

ComboBox「優先するスクリーンリーダー(&R)」= `NVDA（既定）` / `PC-Talker`。

### 解決規則（設計判断・確定 = 検出フォールバック付き）

> **優先 SR が稼働している、またはどちらも稼働していない → 優先 SR の経路。もう片方だけが稼働 → 検出された方の経路（救済）。**

受動読み（UIA プロバイダ提供可否 = `ApplySrAdaptation`）と能動発声（Announcer 選択）は常にペアで同じ経路に確定する。

| 起動時の状況 | 優先=NVDA（既定） | 優先=PC-Talker |
|---|---|---|
| NVDA のみ稼働 | NVDA 経路（現行同） | NVDA 経路（救済） |
| PC-Talker のみ稼働 | PC-Talker 経路（救済・現行同） | PC-Talker 経路（現行同） |
| 両方稼働 | NVDA 経路（現行同） | **PC-Talker 経路（設定が勝つ・新規挙動）** |
| どちらも非稼働 | NVDA 経路（後から起動に対応） | PC-Talker 経路（後から起動に対応） |

- 経路の中身: NVDA 経路 = UIA プロバイダを出さない（ネイティブ Scintilla 読み）＋ UIA 通知 Announcer。PC-Talker 経路 = 自前 UIA プロバイダ提供＋ネイティブ MSAA 抑制＋ `PCTKPReadW` 直叩き Announcer（DLL 不在等のハード失敗時は既存実装どおり UIA 通知へ自動退避）。
- 現行からの挙動変更は 2 セルのみ: 「両方稼働×優先 PC-Talker」と「どちらも非稼働」（現行は汎用 UIA 経路 = プロバイダ ON＋UIA 通知だったが、優先 SR の経路を先回りで準備する形に変わる。自前プロバイダはもともと PC-Talker 専用設計のため、ナレーター等への実害は限定的）。
- 却下案:
  - **設定絶対（検出廃止）**: 最も単純だが、既定 NVDA のまま更新した PC-Talker ユーザーの初回起動で編集領域が読めなくなる退行。
  - **動的追従（起動後も監視・切替）**: 「後から起動」に最も忠実だが、UIA プロバイダ可否はハンドル生成前確定が鉄則（起動時確定方針 = 実機検証で確定したアーキテクチャ）。実行中切替は SR 側のプロバイダキャッシュと不整合を起こし得るためコスト・リスク最大。

### 反映タイミング（設計判断・確定 = 再起動後に一括反映）

- 受動読み・能動発声とも次回起動から有効。OK 時に `PreferredScreenReader` が変更されていた場合のみ「読み上げ設定は再起動後に有効になります」と読み上げ通知。
- 却下案: 能動発声のみ即時（両系統が食い違う中間状態が生じる）／可能な限り即時（SR 側プロバイダキャッシュとの不整合リスク・実機再検証が必要）。

### 実装形

- **Core**: `SrRouteSelector.Select(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning) → SrRoute { Nvda, PcTalker }` を新設（純ロジック・8 象限を単体テストで固定）。既存 `SrSpeechSelector` はこれに置き換え（削除）。
- **起動順の変更**（`Program.Main`）: `SettingsStore.Load` → `SrContext.Detect(preferNvda)` → `new MainForm(settings)`。設定読込を Main へ前倒しし、MainForm 側の二重読込は廃止（コンストラクタで `AppSettings` を受け取る）。
- **SrContext**: `Route` を保持し、既存の消費 2 箇所とは導出プロパティで互換維持 — `NvdaRunning`（`ApplySrAdaptation` 用。意味は「ネイティブ読み経路か」に変わるため `UseNativeReading` へ改名）と `Mode`（`Route == PcTalker → SpeechMode.PcTalker`、それ以外 `SpeechMode.Uia`）。
- `SpeechMode` / `AnnouncerFactory` / `PcTalkerAnnouncer` は無変更。

## 反映タイミングまとめ

| 項目 | 反映 |
|---|---|
| タブ幅 / タブ→スペース / 行番号 / 現在行強調 / キャレット幅 / 空白改行表示 | OK 時に全タブへ即時（`EditorAppearance.Apply`） |
| バックアップ有効 / 間隔 | OK 時に即時（`BackupCoordinator.UpdateSettings`） |
| CSV 自動モード / 復元確認 | 次回の該当操作（開く / 起動）から |
| 禁則整形のタブ幅連動 | 次回の整形実行から |
| 優先するスクリーンリーダー | **再起動後**（変更時のみ通知） |

## 検証方針

- **Core 単体テスト**:
  - `SrRouteSelector` 決定表 2×4 = 8 ケース
  - `SettingsStore.Normalize` 新キー健全化（範囲外・不正文字列・明示 null → 既定へ / 正常値は保持）
  - `AppSettings.Clone` が新キーを複製すること
  - `BackupIntervalSeconds` 既定変更（30→300）に伴う既存テストの期待値更新
- **App 側**: `yEdit.App.Tests` は無し。手動検証（本プロジェクトの慣行）:
  - 6 タブの表示・Tab/Ctrl+Tab 巡回・アクセスキー・キャンセルで元設定保持
  - 表示系 4 項目の ON/OFF が全タブ・全テーマで即時反映
  - タブ幅 4/8 でのタブ表示幅と禁則整形結果の一致
  - .csv 自動モード: 開く/最近/開き直しで ON・grep ジャンプで OFF・解析失敗時に通常モード＋通知
  - 復元確認 OFF: 孤児バックアップありで起動 → 無確認で全復元＋件数通知
- **実機 SR 検証**（マージ前の DoD）:
  - 優先 SR × 稼働状況の主要ケース（少なくとも「両方稼働で設定どおりの経路」「片方のみ稼働で救済」「非稼働→後から優先 SR 起動」）
  - 自動 CSV モード進入時の読み上げ（手動時と同一であること）
  - 優先 SR 変更時の「再起動後に有効」通知

## 影響範囲

- 追加: `Tabs/BackupSettingsTab.cs` / `Tabs/SpeechSettingsTab.cs`、`yEdit.Core/Speech/SrRouteSelector.cs`（`SrSpeechSelector.cs` は削除）
- 変更: `AppSettings.cs` / `SettingsStore.cs`、`BasicSettingsTab.cs` / `EditSettingsTab.cs` / `DisplaySettingsTab.cs`、`SettingsDialog.cs`（_tabs 2 行）、`EditorAppearance.cs`、`ScintillaHost.cs`（行番号プロパティ）、`Program.cs` / `MainForm.cs` / `FileController.cs` / `CsvController.cs` / `BackupCoordinator.cs`、`SrContext.cs`
- 無変更: 禁則処理タブ、`KinsokuFormatter`（呼び出し側が引数を渡すだけ）、`AnnouncerFactory` / `PcTalkerAnnouncer` / `UiaAnnouncer`、CSV パーサ/グリッド系、Markdown プレビュー系
