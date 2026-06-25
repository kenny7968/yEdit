using System.IO;
using System.Text;
using System.Windows.Automation;
using yEdit.Accessibility;
using yEdit.Editor;

namespace yEdit.ScintillaProbe;

/// <summary>
/// Scintilla + 自作 UIA プロバイダの実機検証ハーネス。
/// 診断メニューで UIA 選択/テキストイベントの ON/OFF と ControlType(Document/Edit) を切り替え、
/// PC-Talker / NVDA がどの機構で反応するかを切り分ける。ログは %TEMP%\yedit-scintilla-probe.log。
/// </summary>
public sealed class MainForm : Form
{
    private readonly ScintillaHost _editor;
    private readonly ToolStripStatusLabel _status;
    private readonly string _logPath;
    private readonly object _logLock = new();
    private bool _startEdit;            // --edit: 起動時から ControlType=Edit
    private string _srMode = "pctalker";
    private bool _nvdaDetected;
    private bool _pcTalkerDetected;

    private ToolStripMenuItem _miServeUia = null!;
    private ToolStripMenuItem _miSuppressMsaa = null!;
    private ToolStripMenuItem _miUiaSel = null!;
    private ToolStripMenuItem _miUiaText = null!;
    private ToolStripMenuItem _miCtDocument = null!;
    private ToolStripMenuItem _miCtEdit = null!;

    private const string InitialText =
        "これは Scintilla + UIA テキストプロバイダの実験です。\n" +
        "次の行は空行（改行だけの行）です。\n" +
        "\n" +
        "ABC abc 123 半角と　全角　スペースの混在。\n" +
        "二行目の日本語テキスト。Hello, world!\n" +
        "末尾行（改行なし）。";

    public MainForm()
    {
        Text = "yEdit Scintilla UIA Probe (PC-Talker / NVDA 実機検証)";
        Width = 960;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;

        _logPath = Path.Combine(Path.GetTempPath(), "yedit-scintilla-probe.log");
        try { File.WriteAllText(_logPath, $"=== yEdit Scintilla UIA Probe log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n"); } catch { }
        UiaDiag.Sink = TraceAppend; // UIA プロバイダ呼び出しをログへ（RPC スレッド）

        _editor = new ScintillaHost { Dock = DockStyle.Fill };
        _editor.Log = AppendLog;

        // 起動フラグ: --no-uia で WM_GETOBJECT のプロバイダ提供を最初から無効化
        // （NVDA がネイティブ Scintilla 対応で読むか／我々の UIA を使うかの切り分け用）。
        var argv = Environment.GetCommandLineArgs();
        bool Has(string flag) => argv.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        // ---- SR 判定で構成を決定（確定アーキテクチャ）----
        // NVDA 起動中 → ネイティブ Scintilla に任せる（UIA も MSAA も出さない）。
        // それ以外（PC-Talker 等）→ 我々の UIA プロバイダを適用。
        _nvdaDetected = ScreenReaders.IsNvdaRunning();
        _pcTalkerDetected = ScreenReaders.IsPcTalkerRunning();
        if (Has("--pctalker")) _srMode = "pctalker";       // 手動上書き
        else if (Has("--nvda")) _srMode = "nvda";          // 手動上書き
        else _srMode = _nvdaDetected ? "nvda" : "pctalker"; // 自動

        if (_srMode == "nvda")
        {
            _editor.ServeUiaProvider = false;   // NVDA は我々の UIA を被せると競合 → 出さない
            _editor.SuppressClientMsaa = true;  // 念のためネイティブ MSAA も出さない
        }
        else
        {
            _editor.ServeUiaProvider = true;    // PC-Talker は我々の UIA を読む
            _editor.SuppressClientMsaa = false;
        }

        // ---- 低レベルの明示上書き（デバッグ用。SR モードより優先）----
        if (Has("--no-uia")) _editor.ServeUiaProvider = false;
        if (Has("--no-msaa")) _editor.SuppressClientMsaa = true;
        _editor.UseRenamedClass = Has("--rename-class"); // 既定は元の "Scintilla"（NVDA-UIA 路線は破棄）
        _startEdit = Has("--edit");

        var menu = BuildMenu();

        var statusStrip = new StatusStrip();
        _status = new ToolStripStatusLabel("準備完了。エディタにフォーカスがあります。") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(_status);

        Controls.Add(_editor);
        Controls.Add(statusStrip);
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var diag = new ToolStripMenuItem("診断(&D)");

        _miServeUia = new ToolStripMenuItem("UIA プロバイダを返す(&U)", null, OnToggleServeUia)
        { CheckOnClick = true, Checked = _editor.ServeUiaProvider };

        _miSuppressMsaa = new ToolStripMenuItem("ネイティブ MSAA を抑制(&M)", null, OnToggleSuppressMsaa)
        { CheckOnClick = true, Checked = _editor.SuppressClientMsaa };

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
            _miServeUia, _miSuppressMsaa,
            new ToolStripSeparator(),
            _miUiaSel, _miUiaText,
            new ToolStripSeparator(),
            _miCtDocument, _miCtEdit,
            new ToolStripSeparator(),
            miRefocus, miLogClear, miLogPath,
        });

        menu.Items.Add(diag);
        return menu;
    }

    private void OnToggleServeUia(object? sender, EventArgs e)
    {
        _editor.ServeUiaProvider = _miServeUia.Checked;
        SetStatus($"UIA プロバイダ提供: {(_miServeUia.Checked ? "ON" : "OFF")}（SR は再起動推奨。UIA はキャッシュされるため）");
        _editor.Focus();
    }

    private void OnToggleSuppressMsaa(object? sender, EventArgs e)
    {
        _editor.SuppressClientMsaa = _miSuppressMsaa.Checked;
        SetStatus($"ネイティブ MSAA 抑制: {(_miSuppressMsaa.Checked ? "ON" : "OFF")}（SR は再起動推奨）");
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
        // ハンドル生成後に初期テキストを投入（Scintilla はネイティブ窓が必要）。
        _editor.Text = InitialText;
        // NVDA の最初のフォーカス前に ControlType を確定させる（途中切替は UIA キャッシュで効きにくい）。
        if (_startEdit)
        {
            _editor.SetReportedControlType(ControlType.Edit.Id);
            _miCtEdit.Checked = true;
            _miCtDocument.Checked = false;
        }
        _editor.Focus();
        string cfg = $"srMode={_srMode} (nvda={_nvdaDetected} pctalker={_pcTalkerDetected}) " +
                     $"class={(_editor.UseRenamedClass ? "renamed" : "Scintilla")} " +
                     $"uia={(_editor.ServeUiaProvider ? "on" : "off")} " +
                     $"msaa={(_editor.SuppressClientMsaa ? "suppressed" : "on")} " +
                     $"ctlType={(_startEdit ? "Edit" : "Document")}";
        AppendLog($"[config] {cfg}");
        SetStatus($"起動構成: {cfg}");
    }

    private void SetStatus(string text) => _status.Text = text;

    // コントロール（UI スレッド）からのイベントログ。ステータス表示＋ファイル。
    private void AppendLog(string line)
    {
        SetStatus(line);
        WriteLogLine($"{DateTime.Now:HH:mm:ss.fff}  EVT  {line}");
    }

    // UIA プロバイダ（RPC スレッド）からのトレース。UI に触れず、ファイルのみへ。
    private void TraceAppend(string line)
    {
        WriteLogLine($"{DateTime.Now:HH:mm:ss.fff}  UIA  {line}");
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
