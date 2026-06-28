# マークダウンプレビュー機能 実装プラン

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `.md` タブの編集中内容を Markdig で HTML 化し、WebView2 のモーダル窓で「Webブラウザ閲覧のように」整形表示する（閉じるボタン／Esc でエディタへ復帰）。

**Architecture:** Core 層に UI 非依存の `MarkdownRenderer`（Markdig）と `MarkdownFile.IsMarkdownPath` を置きユニットテストする。App 層に WebView2 を載せた `MarkdownPreviewForm` を新設し、`.md` のフォルダを仮想ホストにマッピングして相対リソースを解決する。`MainForm` にトップメニュー「マークダウン」を追加し、アクティブが `.md` の時だけ活性化する。

**Tech Stack:** .NET 9 / WinForms、Markdig（CommonMark+GFM）、Microsoft.Web.WebView2、xUnit。

**設計ドキュメント:** `docs/plans/2026-06-28-markdown-preview-design.md`

**前提:** フィーチャーブランチ `feature/markdown-preview` で作業中。各タスクごとにコミットする。

---

## Task 1: `MarkdownFile.IsMarkdownPath`（Core・TDD）

`.md` 判定をメニュー活性化とテストで共用する小ユーティリティ。Markdig 不要なので先に片付ける。

**Files:**
- Create: `src/yEdit.Core/Text/MarkdownFile.cs`
- Test: `tests/yEdit.Core.Tests/Text/MarkdownFileTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Text/MarkdownFileTests.cs`:

```csharp
using yEdit.Core.Text;

namespace yEdit.Core.Tests.Text;

public class MarkdownFileTests
{
    [Theory]
    [InlineData("a.md", true)]
    [InlineData("a.MD", true)]
    [InlineData(@"C:\dir\readme.Md", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.markdown", false)] // 要件は .md のみ。.markdown は対象外
    [InlineData("noext", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsMarkdownPath_detects_md(string? path, bool expected)
        => Assert.Equal(expected, MarkdownFile.IsMarkdownPath(path));
}
```

**Step 2: テストが失敗することを確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter MarkdownFileTests`
Expected: コンパイルエラー（`MarkdownFile` 未定義）で FAIL。

**Step 3: 最小実装**

`src/yEdit.Core/Text/MarkdownFile.cs`:

```csharp
namespace yEdit.Core.Text;

/// <summary>マークダウンファイルの判定。メニュー活性化とテストで共用する。</summary>
public static class MarkdownFile
{
    /// <summary>パスの拡張子が .md（大文字小文字無視）なら true。null・拡張子なし・他拡張子は false。</summary>
    public static bool IsMarkdownPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return string.Equals(
            System.IO.Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase);
    }
}
```

**Step 4: テストが通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter MarkdownFileTests`
Expected: PASS（8 ケース緑）。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Text/MarkdownFile.cs tests/yEdit.Core.Tests/Text/MarkdownFileTests.cs
git commit -m "マークダウン判定 MarkdownFile.IsMarkdownPath を追加"
```

---

## Task 2: Markdig パッケージを Core へ追加

**Files:**
- Modify: `src/yEdit.Core/yEdit.Core.csproj`

**Step 1: パッケージ追加**

Run: `dotnet add src/yEdit.Core package Markdig`
Expected: `yEdit.Core.csproj` に `<PackageReference Include="Markdig" Version="..." />` が追記され、restore 成功。

**Step 2: ビルドで復元を確認**

Run: `dotnet build src/yEdit.Core`
Expected: ビルド成功・0 警告。

**Step 3: コミット**

```bash
git add src/yEdit.Core/yEdit.Core.csproj
git commit -m "Core に Markdig パッケージを追加"
```

---

## Task 3: `MarkdownRenderer.Render`（Core・TDD）

マークダウン本文を、`<base>`・charset・読みやすい CSS を備えた完結 HTML 文書へ変換する。

**Files:**
- Create: `src/yEdit.Core/Text/MarkdownRenderer.cs`
- Test: `tests/yEdit.Core.Tests/Text/MarkdownRendererTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Text/MarkdownRendererTests.cs`:

```csharp
using yEdit.Core.Text;

namespace yEdit.Core.Tests.Text;

public class MarkdownRendererTests
{
    private const string Base = "https://yedit.preview/";

    [Fact]
    public void Heading_becomes_h1()
        => Assert.Contains("<h1", MarkdownRenderer.Render("# 見出し", Base));

    [Fact]
    public void Bold_becomes_strong()
        => Assert.Contains("<strong>太字</strong>", MarkdownRenderer.Render("**太字**", Base));

    [Fact]
    public void Inline_code_becomes_code()
        => Assert.Contains("<code>x</code>", MarkdownRenderer.Render("`x`", Base));

    [Fact]
    public void Fenced_code_becomes_pre()
        => Assert.Contains("<pre><code", MarkdownRenderer.Render("```\ncode\n```", Base));

    [Fact]
    public void Pipe_table_becomes_table()
    {
        string md = "| A | B |\n|---|---|\n| 1 | 2 |";
        Assert.Contains("<table", MarkdownRenderer.Render(md, Base));
    }

    [Fact]
    public void Text_special_chars_are_escaped()
        => Assert.Contains("1 &lt; 2 &amp; 3", MarkdownRenderer.Render("1 < 2 & 3", Base));

    [Fact]
    public void Base_href_is_injected()
        => Assert.Contains("<base href=\"https://yedit.preview/\">", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Document_declares_utf8_charset()
        => Assert.Contains("charset=\"utf-8\"", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Null_markdown_does_not_throw()
        => Assert.Contains("<html", MarkdownRenderer.Render(null!, Base));
}
```

**Step 2: テストが失敗することを確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter MarkdownRendererTests`
Expected: コンパイルエラー（`MarkdownRenderer` 未定義）で FAIL。

**Step 3: 最小実装**

`src/yEdit.Core/Text/MarkdownRenderer.cs`:

```csharp
using Markdig;

namespace yEdit.Core.Text;

/// <summary>マークダウン本文を、プレビュー表示用の完結した HTML 文書へ変換する。</summary>
public static class MarkdownRenderer
{
    // CommonMark + GFM 拡張（表・チェックリスト・自動リンク等）。スレッドセーフなので使い回す。
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>
    /// markdown を HTML 化し、&lt;base href&gt;・charset・読みやすい CSS を備えた
    /// 完結した HTML 文書文字列を返す。baseHref は相対リソース解決の基準 URL。
    /// </summary>
    public static string Render(string markdown, string baseHref)
    {
        string body = Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
        string baseTag = string.IsNullOrEmpty(baseHref)
            ? string.Empty
            : $"<base href=\"{HtmlAttr(baseHref)}\">";
        return $$"""
            <!DOCTYPE html>
            <html lang="ja">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            {{baseTag}}
            <style>{{Css}}</style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }

    private static string HtmlAttr(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    private const string Css = """
        body { font-family: "Segoe UI", "Meiryo", sans-serif; line-height: 1.6;
               max-width: 900px; margin: 0 auto; padding: 24px; color: #1f2328; }
        h1, h2 { border-bottom: 1px solid #d0d7de; padding-bottom: .3em; }
        code { background: #afb8c133; padding: .2em .4em; border-radius: 6px;
               font-family: "Consolas", monospace; }
        pre { background: #f6f8fa; padding: 16px; border-radius: 6px; overflow: auto; }
        pre code { background: none; padding: 0; }
        table { border-collapse: collapse; }
        th, td { border: 1px solid #d0d7de; padding: 6px 13px; }
        blockquote { color: #57606a; border-left: .25em solid #d0d7de;
                     padding: 0 1em; margin: 0; }
        img { max-width: 100%; }
        a { color: #0969da; }
        """;
}
```

**Step 4: テストが通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter MarkdownRendererTests`
Expected: PASS（9 ケース緑）。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Text/MarkdownRenderer.cs tests/yEdit.Core.Tests/Text/MarkdownRendererTests.cs
git commit -m "Markdig による MarkdownRenderer.Render を追加"
```

---

## Task 4: WebView2 パッケージを App へ追加

**Files:**
- Modify: `src/yEdit.App/yEdit.App.csproj`

**Step 1: パッケージ追加**

Run: `dotnet add src/yEdit.App package Microsoft.Web.WebView2`
Expected: `yEdit.App.csproj` に `<PackageReference Include="Microsoft.Web.WebView2" Version="..." />` が追記され、restore 成功。

**Step 2: ビルドで復元を確認**

Run: `dotnet build src/yEdit.App`
Expected: ビルド成功・0 警告。

**Step 3: コミット**

```bash
git add src/yEdit.App/yEdit.App.csproj
git commit -m "App に Microsoft.Web.WebView2 パッケージを追加"
```

---

## Task 5: `MarkdownPreviewForm`（App・UI）

WebView2 を載せたモーダルプレビュー窓。UI 層のため自動テストは行わず、ビルド成功＋手動確認で担保する。

**Files:**
- Create: `src/yEdit.App/MarkdownPreviewForm.cs`

**設計上の要点（実装時に守ること）:**
- 初期化は `Shown` 後（フォーム可視後）に行う。`Load` 中の `Close()` 失敗を避けるため。
- Esc は二経路で捕捉する:
  - ボタン等にフォーカス時 → `CancelButton = close` で Esc がボタン押下相当になる。
  - WebView2 にフォーカス時 → JS で keydown(Escape) を `window.chrome.webview.postMessage('close')` し、
    `CoreWebView2.WebMessageReceived` で受けて `Close()`。スクリプトは `NavigateToString` 前に
    `AddScriptToExecuteOnDocumentCreatedAsync` で登録する。
- WebView2 ランタイム未導入時は `EnsureCoreWebView2Async` 例外を捕捉し、案内 MessageBox 後に `Close()`。
- ユーザーデータフォルダは `%LOCALAPPDATA%\yEdit\WebView2` に固定（Program Files 直下への書込み回避）。
- 相対リソース解決: `baseDir` が実在すれば `SetVirtualHostNameToFolderMapping("yedit.preview", baseDir, Allow)`。

**Step 1: フォームを実装**

`src/yEdit.App/MarkdownPreviewForm.cs`:

```csharp
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace yEdit.App;

/// <summary>
/// マークダウンを整形表示するモーダルプレビュー窓。WebView2 に HTML を流し込み、
/// 相対リソースは元の .md フォルダ基準（仮想ホスト）で解決する。
/// 「閉じる」ボタンと Esc の両方でエディタへ戻る。
/// </summary>
public sealed class MarkdownPreviewForm : Form
{
    private const string VirtualHost = "yedit.preview";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Button _close = new()
    {
        Text = "閉じる(&C)", AccessibleName = "閉じる", Left = 6, Top = 6, Width = 100, Height = 26,
    };
    private readonly string _html;
    private readonly string? _baseDir;

    public MarkdownPreviewForm(string html, string? baseDir, string fileName)
    {
        _html = html;
        _baseDir = baseDir;

        Text = $"プレビュー: {fileName} - yEdit";
        Width = 900;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        CancelButton = _close; // ボタン/フォーム側フォーカス時の Esc を担保

        var top = new Panel { Dock = DockStyle.Top, Height = 38 };
        _close.Click += (_, _) => Close();
        top.Controls.Add(_close);

        // Dock 順: Fill を先に Add し、Top を後から載せる。
        Controls.Add(_web);
        Controls.Add(top);

        Shown += async (_, _) =>
        {
            _close.Focus();         // 初期フォーカスは「閉じる」へ（SR の着地点を固定）
            await InitAsync();
        };
    }

    private async Task InitAsync()
    {
        try
        {
            string userData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "yEdit", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;

            // 相対リソース（画像・ローカルリンク）を .md のフォルダから解決する。
            if (!string.IsNullOrEmpty(_baseDir) && System.IO.Directory.Exists(_baseDir))
            {
                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, _baseDir, CoreWebView2HostResourceAccessKind.Allow);
            }

            // ローカル閲覧用途のため不要機能を抑止。
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // WebView2 にフォーカスがある時の Esc を JS 経由で拾って閉じる。
            core.WebMessageReceived += (_, _) => Close();
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.addEventListener('keydown', e => {" +
                " if (e.key === 'Escape') window.chrome.webview.postMessage('close'); });");

            core.NavigateToString(_html);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "マークダウンプレビューには Microsoft Edge WebView2 ランタイムが必要です。\n" +
                "インストール後に再度お試しください。\n\n" +
                $"詳細: {ex.Message}",
                "プレビューを表示できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }
}
```

**Step 2: ビルドで型・参照を確認**

Run: `dotnet build src/yEdit.App`
Expected: ビルド成功・0 警告。

**Step 3: コミット**

```bash
git add src/yEdit.App/MarkdownPreviewForm.cs
git commit -m "WebView2 のマークダウンプレビュー窓 MarkdownPreviewForm を追加"
```

---

## Task 6: MainForm へメニューと配線を追加

トップメニュー「マークダウン」＋「マークダウンプレビュー」を追加し、`.md` の時だけ活性化する。

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`（`BuildMenu` 内のメニュー組立／ハンドラ追加）

**Step 1: メニュー項目を追加**

`MainForm.cs` の `BuildMenu()` 内、`var help = ...` の直前に以下を追加する:

```csharp
        var md = new ToolStripMenuItem("マークダウン(&M)");
        var mdPreview = new ToolStripMenuItem(
            "マークダウンプレビュー(&P)", null, (_, _) => ShowMarkdownPreview());
        md.DropDownItems.Add(mdPreview);
        // 開く度に活性状態を更新（アクティブが .md の時だけ有効）。
        md.DropDownOpening += (_, _) =>
            mdPreview.Enabled = MarkdownFile.IsMarkdownPath(_docs.Active?.State.Path);
```

そして同メソッド末尾の `menu.Items.AddRange(...)` を、`md` を `read` と `help` の間へ挟むよう差し替える:

```csharp
        menu.Items.AddRange(new ToolStripItem[] { file, edit, read, md, help });
```

**Step 2: ハンドラを追加**

`MainForm.cs` の「読み上げ照会」付近（例: `ToggleOvertype` の後）に以下を追加する:

```csharp
    /// <summary>アクティブな .md タブの編集中内容を WebView2 プレビューで表示する。</summary>
    private void ShowMarkdownPreview()
    {
        var doc = _docs.Active;
        // メニュー無効化で基本到達しないが、保険として再ガードする。
        if (doc is null || !MarkdownFile.IsMarkdownPath(doc.State.Path)) return;

        string markdown = doc.Editor.SnapshotText;            // 編集中バッファ（未保存も反映）
        string? dir = System.IO.Path.GetDirectoryName(doc.State.Path);
        string html = MarkdownRenderer.Render(markdown, "https://yedit.preview/");

        using var f = new MarkdownPreviewForm(html, dir, doc.State.DisplayName);
        f.ShowDialog(this);
        _docs.Active?.Editor.Focus();                          // 戻り後はエディタへフォーカス
    }
```

注: `MainForm.cs` は冒頭で `using yEdit.Core.Text;` 済みのため `MarkdownFile` / `MarkdownRenderer` は追加 using 不要。

**Step 3: ビルドで確認**

Run: `dotnet build src/yEdit.App`
Expected: ビルド成功・0 警告。

**Step 4: コミット**

```bash
git add src/yEdit.App/MainForm.cs
git commit -m "マークダウンメニューとプレビュー起動を MainForm に配線"
```

---

## Task 7: 全体ビルド・テスト・手動スモーク

**Step 1: ソリューション全体ビルド**

Run: `dotnet build yEdit.sln`
Expected: 成功・**0 警告**（プロジェクト方針）。

**Step 2: Core テスト全件**

Run: `dotnet test tests/yEdit.Core.Tests`
Expected: 既存 181 件＋本機能の新規分が全緑。

**Step 3: 手動スモーク（要 WebView2 ランタイム）**

1. `dotnet run --project src/yEdit.App` で起動。
2. 任意の `.md` ファイル（相対パス画像を含むものが望ましい）を開く。
3. メニュー「マークダウン」→「マークダウンプレビュー」が **活性**であることを確認。
   無題タブや `.txt` を開いた状態では **非活性**であることも確認。
4. 実行 → 整形表示・相対画像の表示・「閉じる」ボタンでエディタ復帰を確認。
5. 再度開き、WebView2 にフォーカスした状態で **Esc** → エディタ復帰を確認。
6. 「閉じる」ボタンにフォーカスした状態で **Esc** → エディタ復帰を確認。

**Step 4: コードレビュー依頼（マージ前）**

別エージェントへコードレビューを依頼する（プロジェクト方針 [[review-by-separate-agent]]）。
REQUIRED SUB-SKILL: superpowers:requesting-code-review。

**Step 5: 実機 SR 検証の申し送り**

NVDA / PC-Talker でプレビュー内の見出し・リンク・表が読み上げられるか、WebView2 ランタイム
未導入環境の挙動を申し送り（メモリへ記録）。

---

## 完了の定義（DoD）

- [ ] 「マークダウン」トップメニュー＋「マークダウンプレビュー」項目がある。
- [ ] アクティブが `.md` の時だけ項目が活性化する。
- [ ] 実行で編集中内容が Web ブラウザ閲覧のように整形表示される（相対画像も表示）。
- [ ] プレビュー上部に「閉じる」ボタンがあり、押下／Esc でエディタへ戻る。
- [ ] `dotnet build yEdit.sln` が 0 警告、`dotnet test` が全緑。
- [ ] コードレビュー実施 → main へ no-ff マージ（[[phase-work-git-flow]]）。
