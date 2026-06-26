using System.IO;
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// 起動時のクラッシュ復元ダイアログ（モーダル・CheckedListBox）。前回の異常終了で残った
/// バックアップを一覧し、復元するものを選ばせる。標準 Win32 コントロールなので PC-Talker/NVDA が
/// ネイティブに各項目を読む。
/// </summary>
public sealed class RestoreDialog : Form
{
    public enum RestoreAction { Later, Restore, DiscardAll }

    private readonly CheckedListBox _list = new()
    {
        Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false,
        AccessibleName = "復元する未保存ファイル",
    };
    private readonly IReadOnlyList<BackupRecord> _records;

    public RestoreAction Action { get; private set; } = RestoreAction.Later;
    public IReadOnlyList<BackupRecord> Checked { get; private set; } = Array.Empty<BackupRecord>();

    public RestoreDialog(IReadOnlyList<BackupRecord> records)
    {
        _records = records;
        Text = "前回の未保存ファイルの復元";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 560;
        Height = 360;
        KeyPreview = true;

        BuildLayout();

        foreach (var r in records)
        {
            int idx = _list.Items.Add(Describe(r));
            _list.SetItemChecked(idx, true); // 既定で全チェック
        }
    }

    private static string Describe(BackupRecord r)
    {
        string name = r.OriginalPath is not null
            ? Path.GetFileName(r.OriginalPath)
            : (r.UntitledNumber > 0 ? $"無題 {r.UntitledNumber}" : "無題");
        return $"{name}（{r.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm}）";
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var info = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = "前回 yEdit が正常に終了しなかったため、未保存の変更が残っています。復元するファイルを選んでください。",
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var restore = new Button { Text = "選択を復元(&R)", AutoSize = true };
        var discard = new Button { Text = "すべて破棄(&D)", AutoSize = true };
        var later = new Button { Text = "あとで(&L)", AutoSize = true };
        restore.Click += (_, _) => { Action = RestoreAction.Restore; Checked = CheckedRecords(); DialogResult = DialogResult.OK; };
        discard.Click += (_, _) => { Action = RestoreAction.DiscardAll; DialogResult = DialogResult.OK; };
        later.Click += (_, _) => { Action = RestoreAction.Later; DialogResult = DialogResult.Cancel; };
        buttons.Controls.AddRange(new Control[] { restore, discard, later });

        root.Controls.Add(info, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = restore;
    }

    private IReadOnlyList<BackupRecord> CheckedRecords()
    {
        var list = new List<BackupRecord>();
        foreach (int i in _list.CheckedIndices) list.Add(_records[i]);
        return list;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Action = RestoreAction.Later; DialogResult = DialogResult.Cancel; return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
