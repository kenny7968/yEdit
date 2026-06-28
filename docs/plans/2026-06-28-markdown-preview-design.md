# マークダウンプレビュー機能 設計

日付: 2026-06-28
ステータス: 承認済み（実装プランへ）

## 目的

`.md` 形式のファイルを「Webブラウザで閲覧するように」整形表示するプレビュー機能を追加する。
編集中の内容を素早く確認できるようにし、SR（NVDA / PC-Talker）利用者も整形後の見出し・
リンク・表などを読み上げで辿れることを重視する。

## 要件（ユーザー指定）

1. プルダウンに「マークダウン」トップメニューを追加し、子項目「マークダウンプレビュー」を置く。
2. 「マークダウンプレビュー」は、アクティブタブが `.md` ファイルのときのみ活性化する。
3. 実行すると、開いている `.md` をWebブラウザ閲覧時のように整形表示する。
4. プレビュー画面の最上部に「閉じる」ボタンを置き、押下でエディタへ戻る。Esc 押下でも戻る。

## 決定事項（ブレインストーミング結果）

- 表示エンジン: **WebView2**（Edge/Chromium）。HTML/CSS を正確に描画し、Chromium の
  a11y ツリー経由で NVDA・PC-Talker 双方が読める。WebView2 ランタイムは Windows 11 標準搭載。
- 変換: **Markdig**（CommonMark 準拠＋GFM 拡張＝表・チェックリスト・自動リンク等）。
- プレビュー対象: **編集中バッファ**（`SnapshotText`）。未保存の編集も反映する。
- 相対リソース: 画像 `![](pic.png)` やローカルリンクを、`.md` のあるフォルダ基準で
  **解決して表示する**（WebView2 の仮想ホストマッピング）。

## アーキテクチャ

### 1. yEdit.Core 層（UI 非依存・ユニットテスト可能）

- NuGet `Markdig` を追加（Core は `net9.0`、Markdig はプラットフォーム非依存）。
- `MarkdownRenderer.Render(string markdown, string baseHref)` → 完結した HTML 文書文字列。
  - Markdig パイプライン（`UseAdvancedExtensions()` 相当）で本文を HTML 化。
  - `<head>` に `<base href="{baseHref}">` と読みやすい CSS（GitHub 風の素直なスタイル、
    `<meta charset="utf-8">`、`lang` 属性、適切な見出し構造）を含める。
  - 用途はローカルファイル閲覧であり信頼できない外部入力ではないため、生 HTML は素のまま
    描画する（Markdig 既定）。
- `MarkdownFile.IsMarkdownPath(string? path)` → 拡張子 `.md`（大文字小文字無視）判定。
  メニュー活性化とテストで共用。`null`・拡張子なし・他拡張子は `false`。

### 2. yEdit.App 層（UI）

- NuGet `Microsoft.Web.WebView2` を追加。
- `MarkdownPreviewForm`（新規 WinForms `Form`）
  - レイアウト:
    - 上部 `ToolStrip`/`Panel`（`Dock=Top`）: 「閉じる」ボタン＋ファイル名ラベル。
    - 中央 `WebView2`（`Dock=Fill`）。
  - 初期化: `CoreWebView2Environment.CreateAsync(userDataFolder)` で
    ユーザーデータフォルダを `%LOCALAPPDATA%\yEdit\WebView2` に固定（Program Files 直下への
    書込み回避）→ `await EnsureCoreWebView2Async(env)`。
  - 相対リソース解決: `CoreWebView2.SetVirtualHostNameToFolderMapping("yedit.preview",
    <md のフォルダ>, Allow)` ＋ HTML の `<base href="https://yedit.preview/">`
    → `NavigateToString(html)`。
  - 閉じる手段（二経路で確実に）:
    - 「閉じる」ボタン → `Close()`。
    - Esc: ボタンにフォーカス時は Form の `KeyPreview`/`ProcessCmdKey`、WebView2 に
      フォーカス時は `CoreWebView2.AcceleratorKeyPressed` で `Escape` を捕捉 → `Close()`。
  - 表示は `ShowDialog`（モーダル）。閉じると呼び出し元がアクティブエディタへフォーカスを戻す。
  - 表示直後の初期フォーカスは「閉じる」ボタン（SR の着地点が予測可能）。

### 3. MainForm 配線

- トップメニュー「マークダウン(&M)」を追加（「読み上げ」と「ヘルプ」の間）。
  - 子項目「マークダウンプレビュー(&P)」。
- 活性制御: メニューの `DropDownOpening`（＋必要に応じ `ActiveDocumentChanged`）で、
  `MarkdownFile.IsMarkdownPath(_docs.Active?.State.Path)` のときだけ `Enabled=true`。
  無題タブ（`Path==null`）は常に無効。
- ハンドラ `ShowMarkdownPreview()`:
  1. `doc = _docs.Active`、`.md` 判定で再ガード。
  2. `md = doc.Editor.SnapshotText`。
  3. `dir = Path.GetDirectoryName(doc.State.Path)`。
  4. `html = MarkdownRenderer.Render(md, "https://yedit.preview/")`。
  5. `using var f = new MarkdownPreviewForm(html, dir, fileName); f.ShowDialog(this);`
  6. 戻り後 `_docs.Active?.Editor.Focus()`。

## データフロー

```
[メニュー「マークダウンプレビュー」/ Alt+M→P]
  → MainForm.ShowMarkdownPreview()
  → doc = _docs.Active（IsMarkdownPath でガード）
  → md  = doc.Editor.SnapshotText（未保存編集も反映）
  → dir = Path.GetDirectoryName(doc.State.Path)
  → html = MarkdownRenderer.Render(md, "https://yedit.preview/")
  → new MarkdownPreviewForm(html, dir, fileName).ShowDialog(this)
       ├ CoreWebView2Environment.CreateAsync(localAppData)
       ├ EnsureCoreWebView2Async
       ├ SetVirtualHostNameToFolderMapping("yedit.preview", dir, Allow)
       ├ NavigateToString(html)
       ├ 「閉じる」or Esc → Close()
  → 戻り後 _docs.Active?.Editor.Focus()
```

## エラー処理

- **WebView2 ランタイム未導入**: `EnsureCoreWebView2Async` 例外を捕捉し、
  MessageBox「マークダウンプレビューには WebView2 ランタイムが必要です」＋導入案内を表示し、
  フォームを安全に閉じる。
- **アクティブが .md でない**: メニュー無効化で基本到達しないが、ハンドラ冒頭で再ガード（no-op）。
- **巨大ファイル**: `NavigateToString` の文字数上限を考慮。v1 は通常サイズ前提で素直に使い、
  上限超過時はメッセージ表示に留める（実ファイル経由フォールバックは YAGNI で見送り）。

## アクセシビリティ（SR 重視）

- フォーム表示時、初期フォーカスは「閉じる」ボタン → SR が読む着地点を固定。
  Tab で WebView2 内へ入り、Chromium の a11y ツリー経由で見出し・リンク・表を辿れる。
- Esc は「ボタンにフォーカス」「WebView2 にフォーカス」の両状態で効くよう二経路で捕捉。
- 開閉時に `Announcer` で「プレビューを表示」「エディタに戻りました」を通知（任意・実装簡便なら採用）。

## テスト

- **Core ユニットテスト**
  - `MarkdownRendererTests`: 見出し→`<h1>`、強調→`<strong>`、表→`<table>`、
    コード→`<code>`、リンク生成、`<base href>` 注入、本文の HTML エスケープを検証。
  - `MarkdownFileTests`: `.md`/`.MD`/`.txt`/`null`/拡張子なしの判定。
- **UI（WebView2 フォーム）**: 自動テスト対象外（本プロジェクト方針どおり）。手動・実機 SR 検証へ
  申し送る。

## スコープ外（YAGNI）

- プレビューのライブ更新（編集に追従）。開き直しで更新する方針。
- 印刷・PDF 出力・テーマ切替。
- 巨大ファイルの実ファイル経由フォールバック。

## 申し送り

- 実機 SR 検証（NVDA / PC-Talker でのプレビュー読み上げ）。
- WebView2 ランタイム未導入環境での挙動確認。
