using yEdit.App.Settings;
using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App.Settings.Tabs;

/// <summary>「基本」タブ。既定の文字コードと既定の改行を扱う。</summary>
public sealed class BasicSettingsTab : ISettingsTab
{
    public string Title => "基本";

    private static readonly IReadOnlyList<EncodingCatalog.EncodingOption> Encodings =
        EncodingCatalog.SelectableEncodings;
    private static readonly (string Name, int Id)[] Eols =
    {
        ("CRLF（Windows）", 0),
        ("LF（Unix）", 1),
        ("CR（旧 Mac）", 2),
    };

    // MD-L-5: 拡張子入力の区切り文字 (半角空白 / 全角空白 / タブ)。CA1861 対策で
    // 静的キャッシュ化 (Split の第一引数に毎回配列 literal を渡すのを避ける)。
    private static readonly char[] MarkdownExtensionSeparators = { ' ', '　', '\t' };

    private readonly ComboBox _encoding = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "既定の文字コード",
    };
    private readonly ComboBox _eol = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "既定の改行",
    };
    private readonly CheckBox _csvAutoMode = new()
    {
        Text = ".csvファイルを開いたとき自動的にCSVモードにする(&V)",
        AutoSize = true,
    };

    // MD-L-5: マークダウンプレビュー allow list 拡張子の編集フィールド。
    // 単一行 TextBox + 空白区切り (KinsokuSettingsTab と同じ pattern)。
    // SaveTo で先頭 `.` を自動付与し、lowercase 化して寛容に扱う。
    private readonly TextBox _markdownExtensions = new()
    {
        Width = 240,
        AccessibleName = "マークダウンプレビューを許可する拡張子(空白区切り・小文字ドット付き)",
    };

    public Control BuildPage()
    {
        foreach (var e in Encodings)
            _encoding.Items.Add(e.DisplayName);
        foreach (var (name, _) in Eols)
            _eol.Items.Add(name);

        var root = SettingsTabLayoutHelper.NewRoot();
        SettingsTabLayoutHelper.AddRow(root, 0, "既定の文字コード(&E):", _encoding, tabBase: 0);
        SettingsTabLayoutHelper.AddRow(root, 1, "既定の改行(&L):", _eol, tabBase: 2);
        SettingsTabLayoutHelper.AddRow(
            root,
            2,
            "マークダウン拡張子(&M):",
            _markdownExtensions,
            tabBase: 4
        );

        _csvAutoMode.TabIndex = 6;
        root.Controls.Add(_csvAutoMode, 0, 3);
        root.SetColumnSpan(_csvAutoMode, 2);
        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        int encSel = 0;
        for (int i = 0; i < Encodings.Count; i++)
            if (Encodings[i].CodePage == s.DefaultCodePage)
            {
                encSel = i;
                break;
            }
        _encoding.SelectedIndex = encSel;

        int eolSel = 0;
        for (int i = 0; i < Eols.Length; i++)
            if (Eols[i].Id == s.DefaultLineEnding)
            {
                eolSel = i;
                break;
            }
        _eol.SelectedIndex = eolSel;

        _csvAutoMode.Checked = s.CsvAutoModeOnOpen;
        _markdownExtensions.Text = string.Join(' ', s.MarkdownExtensions);
    }

    public void SaveTo(AppSettings r)
    {
        r.DefaultCodePage = Encodings[_encoding.SelectedIndex].CodePage;
        r.DefaultLineEnding = Eols[_eol.SelectedIndex].Id;
        r.CsvAutoModeOnOpen = _csvAutoMode.Checked;
        // MD-L-5: 空白/全角空白/タブ区切り。先頭 `.` の自動付与で `md` → `.md` を許容
        // (ユーザ入力の寛容な扱い)。SettingsStore.Normalize 側でも lowercase/trim される
        // が、ここでも同じ整形を適用して JSON 書き込み前に確定させる。
        r.MarkdownExtensions = _markdownExtensions
            .Text.Split(MarkdownExtensionSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
            .ToList();
    }

    // CA1001 対応(Sub 3.4-B): BuildPage() 経由で Form の Controls ツリーに接続された
    // 場合は Form.Dispose 経由で二重に呼ばれるが、Control.Dispose は冪等なので安全。
    // BuildPage 未呼び出しで破棄された場合(異常系/テスト)のリーク防止が本 Dispose の主目的。
    public void Dispose()
    {
        _encoding.Dispose();
        _eol.Dispose();
        _csvAutoMode.Dispose();
        _markdownExtensions.Dispose();
    }
}
