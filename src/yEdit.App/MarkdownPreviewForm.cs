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
            // このスクリプトは CDP 経由で注入されページ CSP の影響を受けずに実行される。
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
