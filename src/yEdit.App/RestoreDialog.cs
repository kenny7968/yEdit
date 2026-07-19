using System.IO;
using yEdit.Core.Backup;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// 起動時のクラッシュ復元ダイアログ（モーダル・CheckedListBox）。前回の異常終了で残った
/// バックアップを一覧し、復元するものを選ばせる。標準 Win32 コントロールなので PC-Talker/NVDA が
/// ネイティブに各項目を読む。
/// </summary>
public sealed class RestoreDialog : Form
{
    public enum RestoreAction
    {
        Later,
        Restore,
        DiscardAll,
    }

    private readonly CheckedListBox _list = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false,
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

    // internal: BackupRecord → 表示文字列変換を単体テスト可能にする(実 UI を経由しない
    // App.Tests の RestoreDialogTests 用)。csproj の InternalsVisibleTo yEdit.App.Tests で公開。
    internal static string Describe(BackupRecord r)
    {
        string timestamp = $"（{r.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm}）";
        if (r.OriginalPath is null)
        {
            string untitled = r.UntitledNumber > 0 ? $"無題 {r.UntitledNumber}" : "無題";
            return $"{untitled}{timestamp}";
        }
        // BK-L-4: OriginalPath は攻撃者 JSON 経由で U+202E RLO 等の BiDi 制御文字や CR/LF を含む
        // 可能性があるため、表示前に SanitizeForDisplay.OneLine で無害化する
        // (ファイル名スプーフィング + 改行注入によるダイアログ表示崩し対策)。
        // Path.GetFileName / GetDirectoryName の path traversal 側面は HIGH-2 で塞ぎ済み。
        var fileName = SanitizeForDisplay.OneLine(Path.GetFileName(r.OriginalPath));
        var dir = SanitizeForDisplay.OneLine(Path.GetDirectoryName(r.OriginalPath) ?? string.Empty);
        // HIGH-2 視認性強化: フルパスを 1 行に併記し、復元先ディレクトリを利用者が識別できるようにする。
        // SR 互換のため OwnerDraw で 2 段化はせず、" — " 区切りの 1 行に留める(header 冒頭コメント準拠)。
        return $"{fileName} — {ElideMiddle(dir, maxLen: 60)}{timestamp}";
    }

    /// <summary>長すぎるディレクトリパスを「先頭…末尾」に省略する。maxLen 以下ならそのまま返す。</summary>
    private static string ElideMiddle(string s, int maxLen)
    {
        if (s.Length <= maxLen)
            return s;
        int keep = (maxLen - 3) / 2;
        return s[..keep] + "..." + s[^keep..];
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var info = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text =
                "前回 yEdit が正常に終了しなかったため、未保存の変更が残っています。復元するファイルを選んでください。",
        };
        // ダイアログ自体の説明にも載せ、SR が開いた直後に文脈を読めるようにする。
        AccessibleName = Text;
        AccessibleDescription = info.Text;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var restore = new Button { Text = "選択を復元(&R)", AutoSize = true };
        var discard = new Button { Text = "すべて破棄(&D)", AutoSize = true };
        var later = new Button { Text = "あとで(&L)", AutoSize = true };
        restore.Click += (_, _) =>
        {
            Action = RestoreAction.Restore;
            Checked = CheckedRecords();
            DialogResult = DialogResult.OK;
        };
        discard.Click += (_, _) =>
        {
            Action = RestoreAction.DiscardAll;
            DialogResult = DialogResult.OK;
        };
        later.Click += (_, _) =>
        {
            Action = RestoreAction.Later;
            DialogResult = DialogResult.Cancel;
        };
        buttons.Controls.AddRange(restore, discard, later);

        root.Controls.Add(info, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = restore;
    }

    private List<BackupRecord> CheckedRecords()
    {
        var list = new List<BackupRecord>();
        foreach (int i in _list.CheckedIndices)
            list.Add(_records[i]);
        return list;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Action = RestoreAction.Later;
            DialogResult = DialogResult.Cancel;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
