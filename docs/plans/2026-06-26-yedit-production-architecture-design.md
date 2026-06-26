# yEdit 本番エディタ — アーキテクチャ＆ロードマップ設計

最終更新: 2026-06-26 / 作業ディレクトリ: `<repo>` / .NET 9 SDK / Windows 11・日本語環境

前提資料: `docs/HANDOFF-scintilla-uia.md`（特に §13 最終アーキテクチャ）、メモリ `scintilla-uia-architecture`。

---

## 0. このドキュメントの位置づけ

前セッションまでで **Scintilla × スクリーンリーダー（PC-Talker / NVDA）対応の「編集部分」実装方法を実機で実証**した（probe 完了）。本ドキュメントは、そこから **秀丸エディタ級の機能を持つ SR 対応エディタ本番版**を作るための、アーキテクチャと段階的ロードマップの設計合意。

### 確定した前提（ブレインストーミングで決定）
- **作り方**: 前回 RichEdit 版 yEdit の**ソースは追わない**。設計知見・ノウハウだけ流用し、**Scintilla 前提で新規に作り直す**。
- **主目的**: まず自分が PC-Talker/NVDA で日常使えること。**最終的に他の SR ユーザーにも配布**（→ 安定性・設定UI・ドキュメント・インストーラも将来必要）。
- **機能優先度**: 基本機能（編集・タブ・開閉・文字コード・読み上げ）の次に **①検索・置換（正規表現・インクリメンタル） → ②複数ファイル横断 grep** を最優先。マクロ・キーバインド完全カスタマイズは後回し。
- **進め方**: **案A＝ウォーキングスケルトン漸進型**。薄い本番 v0.1 を端から端まで動かし実機SR検証 → 機能を1マイルストーンずつ追加。各マイルストーンは独立ブランチ→別エージェントレビュー→no-ff マージ。

---

## 1. 本番アーキテクチャ（プロジェクト構成）

probe（検証ハーネス）から本番アプリへ昇格。SR非依存でテストできる純ロジックを UI から分離する。

```
yEdit.App ──→ yEdit.Editor ──→ yEdit.Accessibility
   │
   └────────────────────────→ yEdit.Core
```
（注: `yEdit.Editor` は `Core` を参照しない＝`Accessibility` と `Scintilla5.NET` のみ。文字コード I/O 等の Core 連携は App が仲介する。）

| プロジェクト | 種別 / TFM | 役割 |
|---|---|---|
| **yEdit.Core** | classlib / `net9.0`（UI非依存・Nullable有効） | 純ロジック。文字コード判定＆I/O（`EncodingDetector`＋`UTF.Unknown`、原子的保存、`CodePagesEncodingProvider`登録）、設定モデル＆永続化（settings.json）、検索エンジン（正規表現/リテラル＝単一検索・grep 共通）、バックアップ/復元ロジック。**xUnit で単体テスト可** |
| **yEdit.Accessibility** | 既存 classlib / `net9.0-windows`+UseWPF | PC-Talker 用 UIA プロバイダ層。**既存コードを無改変流用**（鉄則どおり） |
| **yEdit.Editor** | classlib / `net9.0-windows` | エディタコントロール。probe の `ScintillaHost` を昇格＝Scintilla 継承＋WM_GETOBJECT 横取り＋SR適応（`ScreenReaders` 判定で UIA/MSAA 切替）＋`ScintillaTextHost` アダプタ＋システムキャレット＋イベント＋読み上げ。`Scintilla5.NET` 依存 |
| **yEdit.App** | WinForms WinExe / `net9.0-windows` | シェル。MainForm・メニュー・ステータスバー・タブ（ドキュメント管理）・各ダイアログ・コマンド経路・キー割り当て。Core＋Editor 依存 |
| **tests/yEdit.Core.Tests** | xUnit / `net9.0` | Core の単体テスト（文字コード・検索・設定） |

**probe 類の扱い**: `src/yEdit.UiaProbe` / `src/yEdit.ScintillaProbe` は参照実装として残置（製品に含めない）。`ScintillaHost` / `ScreenReaders` / `Sci.cs` / `NativeMethods.cs` を ScintillaProbe から `yEdit.Editor` へ移して本番化。

**依存の原則**: `Core` は UI 非依存（→ SR 無しで高速テスト）。`Accessibility` は UIA のため `net9.0-windows`+UseWPF。`Editor`→`Accessibility`、`App`→`Editor`+`Core`。文字コード I/O は「App が Core でバイト→文字列に復号→Editor にセット」「保存は Editor 本文→Core で符号化→原子的保存」の流れ。

---

## 2. v0.1 ウォーキングスケルトンの範囲

**ゴール**: 「1ファイルを開く・編集する・文字コードを保ったまま保存でき、PC-Talker/NVDA が読む」を**実機で確認できる最小の本番アプリ**。

**含むもの**
1. **yEdit.Editor 本番化** — probe の `ScintillaHost` を移植・昇格。UTF-8 内部固定。SR適応（NVDA検出→ネイティブ譲り／それ以外→UIA提供）。カーソル/選択/文字の読み上げ。フォーカス獲得時に `TextSelectionChanged` 発火。
2. **App シェル（単一ドキュメント）** — MainForm、メニューバー（ファイル/編集/ヘルプ最小）、ステータスバー（行・桁・文字コード・改行コード）。
3. **ファイル操作** — 新規／開く／上書き保存／名前を付けて保存。文字コード判定（UTF-8 BOM無し既定＋Shift_JIS(932)/EUC-JP(51932)/UTF-16）。**指定文字コードで開き直し**。改行コード（CRLF/LF/CR）判定・保持。原子的保存（temp→`File.Replace`、ロック時 in-place フォールバック）。
4. **編集** — Scintilla 標準（入力・Undo/Redo・コピペ・選択）がそのまま機能。
5. **設定の骨格** — settings.json の読み書き（ウィンドウ位置・フォント・既定文字コード等の最小キー）。**設定ダイアログUIは後回し**。
6. **基盤** — `git init`＋初回コミット。Core 単体テスト（文字コード判定・往復・改行判定）。build 0警告。実機SR検証（PC-Talker/NVDA で開く・編集・保存・カーソル読み）。

**判断（合意済）**: タブは v0.1 に**含めない**。単一ドキュメントで薄く保ち、SR検証を素早く回す。ただしドキュメント管理層は「将来タブが差し込める」形（`DocumentManager` 抽象を最初から置く）。

**v0.1 では作らない**: タブ、検索・置換、grep、バックアップ/復元、設定ダイアログ、シンタックスハイライト、最近のファイル、キーカスタマイズ、マクロ、座標API。

---

## 3. ロードマップ

優先度（検索・置換 → grep が最優先、マクロ・キーカスタマイズは後）に沿ったマイルストーン列。各 M は独立ブランチ→別エージェントレビュー→no-ff マージ。各 M 着手時に専用ブレインストーミング/計画を行う。M4 以降は順序を柔軟に入れ替え可。

| M | 内容 | 主な範囲 |
|---|---|---|
| **M1 (v0.1)** | ウォーキングスケルトン | セクション2の通り（単一ドキュメント） |
| **M2** | タブ / 複数ドキュメント | `DocumentManager`、SRで操作できるタブUI、タブ毎の状態（パス・文字コード・改行・変更フラグ） |
| **M3** ★最優先 | 検索・置換（単一ファイル） | 正規表現/リテラル、インクリメンタル検索、ヒット内容・件数の SR 読み上げ、置換/全置換、オプション（大小・単語・正規表現）。Scintilla の検索を活用 |
| **M4** ★最優先 | 複数ファイル横断 grep | フォルダ再帰検索、ファイルフィルタ、アクセシブルな結果一覧（行移動・該当へジャンプ）、文字コード混在対応 |
| **M5** | 自動バックアップ＆クラッシュ復元 | 定期バックアップ（サイドカー形式・背景直列書き込み）、起動時の復元提案。旧 yEdit 復元設計の知見を流用 |
| **M6** | SR利便機能＆PC-Talker精緻化 | 現在位置/文字情報の照会ホットキー、行ジャンプ、全角/半角空白判別、文字数、挿入/上書きモード読み上げ、空行の PC-Talker 読み決着、座標API（`GetBoundingRectangles`/`RangeFromPoint`） |
| **M7** | 設定ダイアログ／外観 | フォント・配色・ハイコントラスト・弱視対応。最近のファイルUI露出 |
| **M8** | シンタックスハイライト＆折りたたみ | Scintilla でほぼタダ。SR価値は低いが視覚ユーザー・配布向け |
| **M9+** | 拡張 | ブックマーク、キーバインドカスタマイズ、マクロ、ファイル比較、補完、複数インスタンス backup 競合対策、インストーラ/配布整備 |

**補足**: M6 の一部（空行読み・座標API）は handoff §4.1/§13.5 の PC-Talker 経路の宿題（アクセシビリティ正しさ項目）。日常使用に効く軽い照会ホットキーを早めに前倒しすることも可。

---

## 4. 横断的関心事

### 4.1 アクセシビリティの鉄則（全マイルストーン共通の不変条件）
- ウィンドウクラスは常に **"Scintilla"**（改名厳禁）。
- **NVDA起動中は UIA も MSAA も出さない**（ネイティブ譲り）。それ以外は **UIA 提供（PC-Talker 専用）**。判定は `ScreenReaders.IsNvdaRunning()`。
- **スレッド安全**: RPC スレッドから `SCI_*`(DirectMessage) を呼ばない。UIスレッドで UTF-16 スナップショット保持、`GetText/TextLength/GetSelection` はキャッシュ即答、`SetSelection/SetFocus` のみ `BeginInvoke`、座標APIも UIスレッドのキャッシュから応答。
- **位置空間**: provider は UTF-16 のまま無改変。UTF-8⇔UTF-16 変換は `ScintillaHost` が UIスレッドで。
- **イベント**: 選択→`TextSelectionChanged`、内容→`TextChanged`＋snapshot更新、**フォーカス獲得時にも `TextSelectionChanged`**。
- **PC-Talker 流儀**: `Move` は単位スパン保持、空行は長さ0公開、システムキャレットは Scintilla 任せ。
- **巨大ファイル**: 全文 UTF-16 snapshot を毎回作らない（本番は snapshot 窓化）。v0.1 は全文 snapshot で可、大容量最適化は後続 M。

### 4.2 文字コード
Scintilla 内部 UTF-8 固定、ディスク変換は Core I/O 層。UTF-8(BOM無し)既定＋Shift_JIS(932)/EUC-JP(51932)/UTF-16。CP932/EUC-JP 往復不整合（波ダッシュ U+301C ⇔ 全角チルダ U+FF5E 等）の置換文字は**読み上げで明示**。.NET9 は `CodePagesEncodingProvider` 登録要。

### 4.3 テスト戦略
- **Core**: xUnit 単体テスト（文字コード判定・往復・原子的保存・検索・設定）。SR非依存・高速。純ロジックは TDD。
- **アクセシビリティ**: SR非依存の自動検証ツール（`tools/*.ps1`、別プロセス UIAutomationClient 駆動）。各 M で該当を回す。
- **実機SR**: 各 M の受け入れ時に PC-Talker/NVDA 手動確認（DoD の中核）。

### 4.4 git 運用
最初に `git init`＋初回コミット（`.gitignore` で bin/obj 除外）。main ブランチ。各 M＝フィーチャーブランチ→**別エージェントレビュー**→no-ff マージ。

### 4.5 エラー処理
ファイル I/O 失敗をユーザー＋SR に明示（握り潰さない）。エンコード失敗はフォールバック＋置換文字明示。保存は原子的（temp→`File.Replace`、ロック時 in-place フォールバック、ACL/属性保持）。クラッシュ復元は M5、v0.1 は最低限「保存失敗を伝える」。UI/RPC スレッド境界厳守。

---

## 5. 次のステップ

本ドキュメントをコミット後、**writing-plans スキルで M1（v0.1 ウォーキングスケルトン）の実装計画**を作成する（計画はまず最初のマイルストーンに絞る）。
