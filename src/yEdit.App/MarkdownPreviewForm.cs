using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// マークダウンを整形表示するモーダルプレビュー窓。WebView2 に HTML を流し込み、
/// 相対リソース（画像・ローカルリンク）は元ファイルのフォルダ基準（仮想ホスト）で解決する。
/// baseDir が null（未保存タブ等）の場合は仮想ホストを設定せず、相対リソースは解決できない。
/// 「閉じる」ボタンと Esc の両方でエディタへ戻る。
/// </summary>
public sealed class MarkdownPreviewForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, AccessibleName = "プレビュー" };
    private readonly Button _close = new()
    {
        Text = "閉じる(&C)",
        AccessibleName = "閉じる",
        Left = 6,
        Top = 6,
        Width = 100,
        Height = 26,
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
        CancelButton = _close; // ボタン/フォーム側フォーカス時の Esc を担保

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 38 };
        _close.Click += (_, _) => Close();
        bottom.Controls.Add(_close);

        // Dock 順: Fill を先に Add し、Bottom を後から載せる。
        Controls.Add(_web);
        Controls.Add(bottom);

        Shown += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            string userData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "yEdit",
                "WebView2"
            );
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            if (IsDisposed || Disposing)
                return;
            await _web.EnsureCoreWebView2Async(env);
            if (IsDisposed || Disposing)
                return;

            var core = _web.CoreWebView2;

            // 相対リソース（画像・ローカルリンク）を .md のフォルダから解決する。
            if (!string.IsNullOrEmpty(_baseDir) && System.IO.Directory.Exists(_baseDir))
            {
                core.SetVirtualHostNameToFolderMapping(
                    MarkdownRenderer.PreviewVirtualHost,
                    _baseDir,
                    CoreWebView2HostResourceAccessKind.Allow
                );
            }

            // ローカル閲覧用途のため不要機能を抑止。
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // WebView2 にフォーカスがある時の Esc を JS 経由で拾って閉じる。
            // このスクリプトは CDP 経由で注入されページ CSP の影響を受けずに実行される。
            core.WebMessageReceived += (_, e) =>
            {
                if (e.TryGetWebMessageAsString() == "close")
                    Close();
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.addEventListener('keydown', e => {"
                    + " if (e.key === 'Escape') window.chrome.webview.postMessage('close'); });"
            );
            if (IsDisposed || Disposing)
                return;

            // DOM 準備完了・keydown リスナー装着済みの状態で WebView にフォーカス（本文を先に読ませる）。
            // 一発着火とし、以降のリダイレクト等では発火しない。
            void OnNavCompleted(object? _s, CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= OnNavCompleted;
                if (!IsDisposed && !Disposing && e.IsSuccess)
                    _web.Focus();
            }
            core.NavigationCompleted += OnNavCompleted;

            core.NavigateToString(_html);
        }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing)
                return; // フォーム破棄後（初期化中に閉じられた等）は何もしない
            string head =
                ex is WebView2RuntimeNotFoundException
                    ? "マークダウンプレビューには Microsoft Edge WebView2 ランタイムが必要です。\n"
                        + "インストール後に再度お試しください。"
                    : "マークダウンプレビューを表示できませんでした。";
            MessageBox.Show(
                this,
                $"{head}\n\n詳細: {ex.Message}",
                "プレビューを表示できません",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            if (!IsDisposed)
                Close();
        }
    }
}
