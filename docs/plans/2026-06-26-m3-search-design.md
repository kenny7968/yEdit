# yEdit M3 — 検索・置換（単一ファイル）設計

最終更新: 2026-06-26 / 作業ディレクトリ: `<repo>` / .NET 9 / Windows 11・日本語環境

前提資料: `docs/plans/2026-06-26-yedit-production-architecture-design.md`（§1 アーキテクチャ、§3 ロードマップ M3、§4.1 アクセシビリティの鉄則、§4.3 テスト戦略）、`docs/plans/2026-06-26-m2-tabs-design.md`、M2 実装（`MainForm` / `DocumentManager` / `Document` / `ScintillaHost`）。

---

## 0. 位置づけ

M2（タブ / 複数ドキュメント）まで完了。本ドキュメントは M3「検索・置換（単一ファイル）」の確定設計。ロードマップ §3 で grep（M4）と並ぶ ★最優先 機能。

照合ロジックは `yEdit.Core` の純ロジックとして新設し（§1・§4.3 の方針、grep でも再利用）、エディタ操作（選択・置換・スクロール・アンドゥ）は Scintilla を活用する（M3 行「Scintilla の検索を活用」）。アクセシビリティ層（`yEdit.Accessibility`）と SR 適応（`ScintillaHost` の UIA/MSAA 切替・フォーカスイベント）は M1/M2 で実機検証済みの機構をそのまま使い、`ScintillaHost` への追加は文字オフセット指定の選択/置換ヘルパに限る。

### 確定した要件（ブレインストーミングで決定）

| # | 決定 | 内容 |
|---|---|---|
| Q1 | UI 形態 | **モードレスダイアログ**。`Ctrl+F`=検索 / `Ctrl+H`=置換。開いたままエディタへ移って結果確認でき、`F3`/`Shift+F3` で次/前。入力欄とオプションのチェックボックスに `Tab` でアクセス、フォーカス位置が明確。 |
| Q2 | 増分検索 | **静かな増分**。入力中はエディタを動かさず**件数だけ更新**。`Enter`/`F3` で初めてヒットへジャンプしその行を SR が読む（SR が賑やかになりすぎないため）。 |
| Q3 | 結果通知 | **ダイアログ内ステータス＋ライブ通知**。件数「N件中M件目」・不在「見つかりません」・置換件数を、ダイアログ内ステータス＋UIA 通知で読み上げ。ヒット行自体は選択移動で両 SR が自動で読む。汎用 Announcer は導入しない（M6 のまま）。 |
| Q4 | 置換操作 | **「置換して次を検索」「すべて置換」「選択範囲のみ置換」の3つ**を含める。 |
| Q5 | 折り返し | **折り返ししない**。端に達したら停止し「これ以上見つかりません」を通知。 |

### スコープ

**IN（M3 で実装）**
- 検索ダイアログ（`Ctrl+F`）: 検索文字列 / オプション（大文字小文字・単語単位・正規表現）/ 次を検索・前を検索 / 件数ステータス。
- 置換ダイアログ（`Ctrl+H`）: 上記＋置換後文字列 / 「置換して次を検索」「すべて置換」「選択範囲のみ置換」。
- 静かな増分カウント、折り返しなし、`F3`/`Shift+F3`（ダイアログを閉じていても最後の条件で動く）。
- 結果通知（件数 / 不在 / 置換件数 / 正規表現エラー）。
- 正規表現は **.NET `Regex`**（`$1` 置換展開を含む）。

**OUT（後続マイルストーン送り）**
- 複数ファイル横断 grep（M4）。
- 検索履歴の永続化・ドロップダウン、全マッチの一括ハイライト（indicator）表示、ブックマーク。
- 汎用読み上げ Announcer（M6）。大容量ファイル向けの増分カウント最適化（§4.1 で後続 M）。
- 折り返し検索、検索方向の永続設定。

---

## 1. アーキテクチャ方針

責務を3層に分離する。**照合は Core（テスト可能・grep 再利用）／エディタ操作は Scintilla（選択・置換・スクロール・アンドゥ）／統括と UI は App**。これにより設計合意の「Core 検索エンジン」と M3 行「Scintilla を活用」を両立する。

```
MainForm ──(Ctrl+F/H, F3)──▶ SearchController ──▶ FindReplaceDialog (modeless)
                                   │
                                   ├─▶ yEdit.Core.Search.TextSearcher（純ロジック・照合）
                                   └─▶ DocumentManager.Active.Editor（ScintillaHost：選択/置換）
```

**位置空間の統一**: Core は **UTF-16 文字オフセット**（= `editor.Text` の .NET string index、ScintillaNET の文字位置 API と同一空間）で扱う。`ScintillaHost` の UIA スナップショット層が内部で使うバイト⇔UTF-16 変換とは独立で、検索系は文字位置 API に閉じるため新規のバイトプラミングは不要。

---

## 2. 新規・変更コンポーネント

### 2.1 `yEdit.Core.Search`（新規・純ロジック・xUnit 対象）

検索オプションと照合エンジン。UI 非依存・SR 非依存で高速にテストできる（§4.3）。

- `SearchOptions`（record）: `Pattern` / `Replacement` / `MatchCase` / `WholeWord` / `UseRegex`。
- `MatchSpan`（readonly record struct）: `Start` / `Length`（UTF-16 文字オフセット）。
- `TextSearcher`:
  - 生成時に内部を **.NET `Regex` に統一**して構築する。
    - リテラル: `Regex.Escape(Pattern)`。
    - 単語単位: 前後に `\b` 境界を付与（CJK では `\b` の意味が限定的＝既知の制約として許容）。
    - 大小無視: `RegexOptions.IgnoreCase`（必要に応じ `CultureInvariant`）。
  - 正規表現のコンパイル失敗は**例外でなく結果状態**で返す（`IsValid` / `Error`）。
  - メソッド（すべて文字オフセット・**折り返しなし**）:
    - `int Count(string text)` … 総ヒット数（増分カウント用）。
    - `MatchSpan? FindNext(string text, int from)` … `Start >= from` の最初のヒット。
    - `MatchSpan? FindPrev(string text, int before)` … `End <= before`（または caret より前）の最後のヒット。
    - `(int index, int total)? Locate(string text, int caret)` … 「M件中N件目」用。
    - `(string text, int count) ReplaceAll(string text)` … 全文置換。
    - `(string text, int count) ReplaceInRange(string text, int start, int length)` … 選択範囲内のみ。
    - 現在マッチの置換: 指定位置で再マッチして置換文字列を生成（正規表現 `$1` 等を `Match.Result` で展開）。リテラル置換は素の文字列を挿入。
- grep（M4）ではファイルから読んだ文字列に同じ `TextSearcher` を適用する。

### 2.2 `yEdit.App.FindReplaceDialog`（新規・モードレス Form）

- MainForm を親（`Owner`）に持つ**非 TopMost** のモードレスフォーム。1インスタンスを生成して再利用（再 `Show`/`Activate`）。`Esc` で閉じる（破棄せず隠す）。
- 検索モード / 置換モードでフィールドの表示を切替（`Ctrl+F`=検索欄のみ、`Ctrl+H`=置換欄も表示）。
- コントロール（すべてラベル付与・タブ順整備）:
  - 「検索する文字列」TextBox、「置換後の文字列」TextBox（置換モード時）。
  - チェックボックス: 「大文字と小文字を区別」「単語単位」「正規表現」、置換モードに「選択範囲のみ」。
  - ボタン: 「次を検索」「前を検索」「置換して次を検索」「すべて置換」「閉じる」。
  - **読み取り専用ステータス**（UIA ライブ通知元・§3.4）。
- 対象文書は `DocumentManager.Active` を**毎操作ごとに遅延解決**（開いたままタブ切替しても現在のアクティブに作用）。
- ロジックは持たず、入力収集とステータス表示に徹し、操作は `SearchController` 経由。

### 2.3 `yEdit.App.SearchController`（新規・統括）

- 状態: 最後の `SearchOptions`（パターン・オプション）と `FindReplaceDialog` インスタンス。
- 入口: `OpenFind()` / `OpenReplace()`（ダイアログ表示）、`FindNext()` / `FindPrev()`（条件があれば即移動・なければ何もしない）、各置換操作、`UpdateCount()`（増分）。
- 仲介: `TextSearcher` で照合 → `ScintillaHost` の選択/置換ヘルパを駆動 → ステータス＆ライブ通知を更新。
- アクティブ文書解決は `DocumentManager` を介する。

### 2.4 `yEdit.Editor.ScintillaHost`（最小追加）

文字オフセット指定の操作を **ScintillaNET の文字位置 API** で実装（MainForm が既に使う `CurrentPosition`/`GetColumn` 等と同じ文字位置空間）。

- `void SelectCharRange(int start, int length)` … `SelectionStart/End` 設定＋`ScrollCaret`。選択移動で既存 `OnUpdateUI`→`TextSelectionChanged` が発火し SR が一致行を読む。
- 置換系 … `ReplaceSelection` または `TargetStart/End`＋`ReplaceTarget`。「すべて置換」「選択範囲のみ」は `BeginUndoAction`/`EndUndoAction` で囲み **1アンドゥ単位**にする。
- いずれも UI スレッドで実行（`InvokeRequired` 配慮）。鉄則（§4.1：RPC スレッドから `SCI_*` を呼ばない／スナップショットは UI スレッドで更新）を順守。スナップショットと UIA イベントは既存の `OnUpdateUI`/`OnTextChangedEvt` 経路で自動更新される。

### 2.5 `MainForm`（変更）

- `SearchController` を1つ保持。
- メニュー「編集」に「検索(&F)…」`Ctrl+F`、「次を検索(&N)」`F3`、「前を検索(&P)」`Shift+F3`、「置換(&H)…」`Ctrl+H` を追加。
- `ProcessCmdKey` で `F3`/`Shift+F3` を横取り（ダイアログ非表示でも最後の条件で移動。子 Scintilla に食われないように。`Ctrl+F`/`Ctrl+H` はメニューのショートカットで処理）。

---

## 3. データフロー & SR 読み上げ

### 3.1 増分カウント（静か）

- ダイアログがアクティブ文書の `Text` をキャッシュ（`TextChanged` とアクティブ変更で更新）。
- 打鍵毎に Core `Count` を実行 → ステータス更新のみ（**カーソルは動かさない**）。
- 大ファイルで打鍵毎に全文 Regex を回すコストは M3 では許容（大容量最適化は §4.1 で後続 M）。

### 3.2 移動（`Enter` / `F3` / `Shift+F3`）

- Core `FindNext` / `FindPrev`（caret 起点・折り返しなし）→ `SelectCharRange` で選択＆スクロール。
- **選択移動で NVDA（ネイティブ Scintilla）/ PC-Talker（UIA プロバイダ）とも一致行を自動で読む**。
- 件数「M件中N件目」はライブ通知（§3.4）。端に達したら「これ以上見つかりません」。

### 3.3 置換

- **置換して次を検索**: 現在選択が今のヒットなら置換（正規表現は `$1` 展開）→ `FindNext`。一致していなければ先に `FindNext`。
- **すべて置換**: Core `ReplaceAll` → エディタ本文を1アンドゥ単位で差し替え → 「N件置換しました」を通知。
- **選択範囲のみ置換**: `Ctrl+H` 押下時（操作開始時）のエディタ選択範囲を捕捉し Core `ReplaceInRange`。選択が空なら案内して中止。

### 3.4 SR 通知の肝（要・実機検証）

件数・不在・置換件数・正規表現エラーは、ヒット行の自動読みとは**別チャネル**が要る。

- **ダイアログ内ステータス Label ＋ `UiaRaiseNotificationEvent`（Win10 1709+ の P/Invoke）でライブ読み上げ**を実装する。
- NVDA は UIA 通知イベント対応が厚い。PC-Talker は通知の可否が不確実なため**実機検証で確認**（DoD の核）。
- **フォールバック**（通知が通らない SR でも成立する二重化）:
  1. ステータスは視覚表示として常に残る。
  2. ヒット行自体は選択移動で必ず読まれる（件数が読まれなくても移動先は分かる）。
- 汎用 Announcer は導入しない（M6 のまま）。通知ヘルパは検索ダイアログ内に閉じる。

---

## 4. エラー処理・エッジケース

- **正規表現不正**: ステータス「正規表現が正しくありません」＋通知。エディタは無変更。
- **空パターン**: 移動・置換は no-op、件数は空欄。
- **端到達（折り返しなし）**: 「これ以上見つかりません」。総ヒット0: 「見つかりません」。
- **ダイアログ表示中のタブ切替**: 次操作で新アクティブを対象に再カウント。表示中はアクティブ変更でカウントを更新。
- **「選択範囲のみ」で選択が空**: 案内表示して中止。
- 例外捕捉は既存方針踏襲（想定内の入出力例外のみ握り、ロジックバグは伝播）。検索・置換は基本メモリ内操作だが、想定外は伝播させる。

---

## 5. テスト戦略（§4.3 準拠）

- **Core 単体（xUnit・新規）**: リテラル/正規表現/大小/単語、`Count`、`FindNext`/`FindPrev` の境界（折り返しなし・caret 起点）、`ReplaceAll`/`ReplaceInRange`、正規表現 `$1` 展開、CJK・サロゲートペア、不正正規表現の結果状態。
- **App/ダイアログ**: WinForms/ネイティブ依存のため自動テストは限定。ビルド0警告＋実機 SR 検証で担保。
- **実機 SR 受け入れ（DoD の核 / PC-Talker・NVDA）**:
  1. `Ctrl+F` → 文字列入力 → 件数がライブで読まれる。
  2. `F3` で次へ移動しその行が読まれる。末尾で「これ以上見つかりません」。
  3. `Shift+F3` で前方向。
  4. 大小 / 単語 / 正規表現の各オプションが効く。
  5. `Ctrl+H` → 「置換して次を検索」「すべて置換（件数通知）」「選択範囲のみ」。
  6. 正規表現エラー時に通知される。
  7. ダイアログを開いたままタブ切替しても現在のアクティブに作用。
  8. `Esc` で閉じ、閉じても `F3` が効く。

---

## 6. git 運用

フィーチャーブランチ `feature/m3-search`（同ディレクトリ）→ **別エージェントでコードレビュー** → main へ **no-ff マージ**（メモリ `phase-work-git-flow` / `review-by-separate-agent` 準拠）。ビルド0警告を維持。

---

## 7. 次のステップ

本ドキュメントをコミット後、**writing-plans スキルで M3 の実装計画**を作成する。
