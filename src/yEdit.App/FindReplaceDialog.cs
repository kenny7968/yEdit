using System.Windows.Forms.Automation;

namespace yEdit.App;

/// <summary>
/// モードレスの検索・置換ダイアログ。入力収集とステータス表示に徹し、操作は
/// SearchController 経由。検索モード/置換モードでフィールド表示を切替える。
/// </summary>
public sealed class FindReplaceDialog : Form
{
    private readonly SearchController _controller;

    private readonly TextBox _pattern = new();
    private readonly TextBox _replacement = new();
    private readonly Label _replacementLabel = new() { Text = "置換後の文字列(&R):", AutoSize = true };
    private readonly CheckBox _matchCase = new() { Text = "大文字と小文字を区別(&C)", AutoSize = true };
    private readonly CheckBox _wholeWord = new() { Text = "単語単位(&W)", AutoSize = true };
    private readonly CheckBox _useRegex = new() { Text = "正規表現(&E)", AutoSize = true };
    private readonly CheckBox _inSelection = new() { Text = "選択範囲のみ(&S)", AutoSize = true };
    private readonly Button _next = new() { Text = "次を検索(&N)", AutoSize = true };
    private readonly Button _prev = new() { Text = "前を検索(&P)", AutoSize = true };
    private readonly Button _replaceOne = new() { Text = "置換して次を検索(&L)", AutoSize = true };
    private readonly Button _replaceAll = new() { Text = "すべて置換(&A)", AutoSize = true };
    private readonly Button _close = new() { Text = "閉じる(&X)", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Text = "" };

    public FindReplaceDialog(SearchController controller)
    {
        _controller = controller;
        Text = "検索";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildLayout();

        _next.Click += (_, _) => _controller.FindNext();
        _prev.Click += (_, _) => _controller.FindPrev();
        _replaceOne.Click += (_, _) => _controller.ReplaceOne();
        _replaceAll.Click += (_, _) => _controller.ReplaceAll();
        _close.Click += (_, _) => Hide();
        _pattern.TextChanged += (_, _) => _controller.UpdateCount();
        _matchCase.CheckedChanged += (_, _) => _controller.UpdateCount();
        _wholeWord.CheckedChanged += (_, _) => _controller.UpdateCount();
        _useRegex.CheckedChanged += (_, _) => _controller.UpdateCount();
    }

    public string Pattern => _pattern.Text;
    public string Replacement => _replacement.Text;
    public bool MatchCase => _matchCase.Checked;
    public bool WholeWord => _wholeWord.Checked;
    public bool UseRegex => _useRegex.Checked;
    public bool InSelection => _inSelection.Checked;

    public void FocusPattern() { _pattern.Focus(); _pattern.SelectAll(); }

    public void SetMode(bool replaceMode)
    {
        Text = replaceMode ? "置換" : "検索";
        _replacementLabel.Visible = replaceMode;
        _replacement.Visible = replaceMode;
        _replaceOne.Visible = replaceMode;
        _replaceAll.Visible = replaceMode;
        _inSelection.Visible = replaceMode;
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>ステータス Label の UIA プロバイダから通知を上げて SR に読ませる。</summary>
    public void RaiseNotification(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        SetStatus(message);
        try
        {
            _status.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message);
        }
        catch { /* 通知非対応環境では視覚表示＋選択移動の自動読みにフォールバック */ }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Hide(); return true;
            case Keys.F3: _controller.FindNext(); return true;
            case Keys.Shift | Keys.F3: _controller.FindPrev(); return true;
            case Keys.Enter when _pattern.Focused: _controller.FindNext(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        var patternLabel = new Label { Text = "検索する文字列(&F):", AutoSize = true };
        root.Controls.Add(patternLabel, 0, 0);
        _pattern.Width = 260; root.Controls.Add(_pattern, 1, 0);
        root.Controls.Add(_replacementLabel, 0, 1);
        _replacement.Width = 260; root.Controls.Add(_replacement, 1, 1);

        var opts = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        opts.Controls.AddRange(new Control[] { _matchCase, _wholeWord, _useRegex, _inSelection });
        root.Controls.Add(opts, 0, 2); root.SetColumnSpan(opts, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.AddRange(new Control[] { _next, _prev, _replaceOne, _replaceAll, _close });
        root.Controls.Add(buttons, 0, 3); root.SetColumnSpan(buttons, 2);

        root.Controls.Add(_status, 0, 4); root.SetColumnSpan(_status, 2);
        _status.AccessibleName = "検索結果";

        Controls.Add(root);
        AcceptButton = _next;
    }
}
