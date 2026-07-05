using System.IO;
using System.Text;
using System.Windows.Automation;
using yEdit.Accessibility;

namespace yEdit.UiaProbe;

public sealed class MainForm : Form
{
    private readonly UiaTextControl _editor;
    private readonly ToolStripStatusLabel _status;
    private readonly string _logPath;
    private readonly object _logLock = new();

    private ToolStripMenuItem _miSysCaret = null!;
    private ToolStripMenuItem _miUiaSel = null!;
    private ToolStripMenuItem _miUiaText = null!;
    private ToolStripMenuItem _miCtDocument = null!;
    private ToolStripMenuItem _miCtEdit = null!;

    public MainForm()
    {
        Text = "yEdit UIA Probe (PC-Talker / NVDA 実機検証)";
        Width = 960;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;

        _logPath = Path.Combine(Path.GetTempPath(), "yedit-uia-probe.log");
        try { File.WriteAllText(_logPath, $"=== yEdit UIA Probe log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n"); } catch { }

        _editor = new UiaTextControl { Dock = DockStyle.Fill };
        _editor.Log = AppendLog;
        _editor.SetInitialText(string.Join("\n", new[]
        {
            "これは UIA テキストプロバイダの実験です。",
            "次の行は空行（改行だけの行）です。",
            "",
            "ABC abc 123 半角と　全角　スペースの混在。",
            "二行目の日本語テキスト。Hello, world!",
            "末尾行（改行なし）。",
        }));

        var menu = BuildMenu();

        var statusStrip = new StatusStrip();
        _status = new ToolStripStatusLabel("準備完了。エディタにフォーカスがあります。") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(_status);

        // 追加順に注意: Fill を先に Add し、Dock 計算で menu/status が端に来るよう最後に menu。
        Controls.Add(_editor);
        Controls.Add(statusStrip);
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var diag = new ToolStripMenuItem("診断(&D)");

        _miSysCaret = new ToolStripMenuItem("システムキャレット(&C)", null, OnToggleSysCaret)
        { CheckOnClick = true, Checked = true };
        _miUiaSel = new ToolStripMenuItem("UIA 選択イベント(&S)", null, OnToggleUiaSel)
        { CheckOnClick = true, Checked = true };
        _miUiaText = new ToolStripMenuItem("UIA テキストイベント(&T)", null, OnToggleUiaText)
        { CheckOnClick = true, Checked = true };

        _miCtDocument = new ToolStripMenuItem("ControlType = Document(&1)", null, OnPickDocument)
        { Checked = true };
        _miCtEdit = new ToolStripMenuItem("ControlType = Edit(&2)", null, OnPickEdit)
        { Checked = false };

        var miLogClear = new ToolStripMenuItem("ログをクリア(&L)", null, (_, _) =>
        {
            try { File.WriteAllText(_logPath, ""); } catch { }
            SetStatus("ログをクリアしました。");
        });
        var miLogPath = new ToolStripMenuItem("ログの場所をステータスに表示(&P)", null, (_, _) => SetStatus(_logPath));
        var miRefocus = new ToolStripMenuItem("エディタにフォーカス(&F)", null, (_, _) => _editor.Focus());

        diag.DropDownItems.AddRange(new ToolStripItem[]
        {
            _miSysCaret, _miUiaSel, _miUiaText,
            new ToolStripSeparator(),
            _miCtDocument, _miCtEdit,
            new ToolStripSeparator(),
            miRefocus, miLogClear, miLogPath,
        });

        menu.Items.Add(diag);
        return menu;
    }

    private void OnToggleSysCaret(object? sender, EventArgs e)
    {
        _editor.UseSystemCaret = _miSysCaret.Checked;
        _editor.ApplySystemCaretToggle();
        SetStatus($"システムキャレット: {(_miSysCaret.Checked ? "ON" : "OFF")}");
        _editor.Focus();
    }

    private void OnToggleUiaSel(object? sender, EventArgs e)
    {
        _editor.RaiseUiaSelectionEvents = _miUiaSel.Checked;
        SetStatus($"UIA 選択イベント: {(_miUiaSel.Checked ? "ON" : "OFF")}");
        _editor.Focus();
    }

    private void OnToggleUiaText(object? sender, EventArgs e)
    {
        _editor.RaiseUiaTextEvents = _miUiaText.Checked;
        SetStatus($"UIA テキストイベント: {(_miUiaText.Checked ? "ON" : "OFF")}");
        _editor.Focus();
    }

    private void OnPickDocument(object? sender, EventArgs e)
    {
        _miCtDocument.Checked = true;
        _miCtEdit.Checked = false;
        _editor.SetReportedControlType(ControlType.Document.Id);
        SetStatus("ControlType = Document に切替（SR によっては再フォーカス/再起動が必要）。");
        _editor.Focus();
    }

    private void OnPickEdit(object? sender, EventArgs e)
    {
        _miCtDocument.Checked = false;
        _miCtEdit.Checked = true;
        _editor.SetReportedControlType(ControlType.Edit.Id);
        SetStatus("ControlType = Edit に切替（SR によっては再フォーカス/再起動が必要）。");
        _editor.Focus();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _editor.Focus();
    }

    private void SetStatus(string text) => _status.Text = text;

    // コントロール（UI スレッド）からのイベントログ。ステータス表示＋ファイル。
    private void AppendLog(string line)
    {
        SetStatus(line);
        WriteLogLine($"{DateTime.Now:HH:mm:ss.fff}  EVT  {line}");
    }

    private void WriteLogLine(string text)
    {
        lock (_logLock)
        {
            try { File.AppendAllText(_logPath, text + "\r\n", Encoding.UTF8); }
            catch { /* ログ失敗は無視 */ }
        }
    }
}
