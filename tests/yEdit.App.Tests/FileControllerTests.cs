using System.Text;
using yEdit.App.Tests.Fakes;
using yEdit.Core.Backup;
using yEdit.Core.Settings;
using yEdit.Core.Text;
using Directory = System.IO.Directory;
using File2 = System.IO.File;
using IOException = System.IO.IOException;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 3: FileController の配線・状態遷移・ロールバックのテスト(設計書 §3)。
/// 実 DocumentManager+実 EditorControl+実ファイル I/O(TextFileService=温存対象)を使い、
/// Form/OS 境界(FakePrompt/FakeFileDialogService)だけを偽物にする。
/// Core が検証済みの照合・I/O 正しさ(TextFileService/RecentFilesList/EncodingCatalog)は再検証しない。
/// </summary>
public class FileControllerTests
{
    private sealed class HostForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    /// <summary>
    /// FileController を Fake 境界で配線したテストホスト。DocumentManagerTests と同じ
    /// 「可視・画面外・非アクティブ」の HostForm パターン(実運用 MainForm は常に可視のため)。
    /// </summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FileController File { get; }
        public AppSettings Settings = new();
        public FakePrompt Prompt { get; } = new();
        public FakeFileDialogService Dialogs { get; } = new();
        public int SaveSettingsCount;
        public int RecentChangedCount;
        public int MetaChangedCount;
        public List<Document> OpenedFresh { get; } = new();

        public Host()
        {
            Form = new HostForm
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
            };
            Docs = new DocumentManager(() => new EditorControl());
            Form.Controls.Add(Docs.TabHost);
            Form.Show();
            File = new FileController(Docs, Form, () => Settings,
                () => SaveSettingsCount++, () => RecentChangedCount++, () => MetaChangedCount++,
                d => OpenedFresh.Add(d), Prompt, Dialogs);
        }

        public void Dispose() => Form.Dispose();
    }

    /// <summary>テスト毎に使い捨ての一時フォルダ(実ファイル I/O 用)。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("yEditAppTests_").FullName;
        public string File(string name) => System.IO.Path.Combine(Root, name);
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* 掃除失敗はテスト失敗にしない(読み取り専用属性等は UnauthorizedAccessException) */ }
        }
    }

    // ===== SaveAs ロールバック(データ破損防止の要=最優先) =====

    [Fact]
    public void SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc"; // 既定 State=UTF-8/BOM なし/CRLF
        // 存在しないフォルダ配下を保存先にして TextFileService.Save を確実に失敗させる
        // (DirectoryNotFoundException は IOException 派生=想定内エラー経路)。
        // CodePage は 932 を選ぶ: 既定(65001)と同値だと Encoding ロールバックの assert が
        // 空振りする(レビュー I-1)。"abc" は ASCII なので 932 でも劣化警告は出ない。
        host.Dialogs.SaveAs = new SaveAsResult(tmp.File(@"no-such-dir\a.txt"), 932, HasBom: true, LineEnding.Lf);

        Assert.False(host.File.SaveAs());

        Assert.Null(doc.State.Path); // Path は旧のまま(後続 Ctrl+S の別エンコード上書き事故防止)
        Assert.Equal(65001, doc.State.Encoding.CodePage);   // ロールバック(932→65001)
        Assert.False(doc.State.HasBom);                    // ロールバック
        Assert.Equal(LineEnding.Crlf, doc.State.LineEnding); // ロールバック
        Assert.Contains(host.Prompt.Log, e => e.Kind == "Error" && e.Text.StartsWith("保存できませんでした"));
    });

    [Fact]
    public void SaveAs_Success_UpdatesMeta_SetsSavePoint_AndRegistersRecent() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty にして SetSavePoint の効果を観測する
        string path = tmp.File("a.txt");
        host.Dialogs.SaveAs = new SaveAsResult(path, 65001, HasBom: true, LineEnding.Lf);

        Assert.True(host.File.SaveAs());

        Assert.Equal(path, doc.State.Path);
        Assert.True(doc.State.HasBom);
        Assert.Equal(LineEnding.Lf, doc.State.LineEnding);
        Assert.False(doc.Editor.Modified); // SetSavePoint 済み
        var bytes = File2.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray()); // HasBom が Save まで配線される
        Assert.Equal(path, host.Settings.RecentFiles[0]); // RegisterRecent の配線
        Assert.True(host.SaveSettingsCount >= 1);
        Assert.True(host.RecentChangedCount >= 1);
        // ダイアログへ現在値が初期値として渡る
        Assert.Equal(new SaveAsRequest(null, 65001, false, LineEnding.Crlf), Assert.Single(host.Dialogs.SaveAsRequests));
    });

    [Fact]
    public void SaveAs_Cancelled_ReturnsFalse_AndChangesNothing() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        host.Dialogs.SaveAs = null; // キャンセル

        Assert.False(host.File.SaveAs());

        Assert.Null(doc.State.Path);
        Assert.Empty(host.Prompt.Log);
        Assert.Empty(host.Settings.RecentFiles);
    });

    [Fact]
    public void SaveAs_WhitespacePath_WarnsAndAborts() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        host.Dialogs.SaveAs = new SaveAsResult("   ", 65001, HasBom: false, LineEnding.Crlf);

        Assert.False(host.File.SaveAs());

        Assert.Null(doc.State.Path);
        Assert.Contains(host.Prompt.Log, e => e.Kind == "Warn" && e.Text == "ファイル名を指定してください。");
    });

    // ===== 符号化劣化警告(CanEncodeBuffer 経由) =====

    [Fact]
    public void SaveAs_LossyEncoding_CancelKeepsStateAndWritesNothing() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "こんにちは😀"; // 😀 は Shift_JIS(932) で表せない
        string path = tmp.File("a.txt");
        host.Dialogs.SaveAs = new SaveAsResult(path, 932, HasBom: false, LineEnding.Crlf);
        host.Prompt.OkCancelResult = false; // 中止

        Assert.False(host.File.SaveAs());

        Assert.False(File2.Exists(path));
        Assert.Equal(65001, doc.State.Encoding.CodePage); // 警告は State 反映前=変化なし
        Assert.Contains(host.Prompt.Log, e => e.Kind == "OkCancel" && e.Caption == "文字コードの警告");
    });

    [Fact]
    public void SaveAs_LossyEncoding_OkProceedsAndWrites() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "こんにちは😀";
        string path = tmp.File("a.txt");
        host.Dialogs.SaveAs = new SaveAsResult(path, 932, HasBom: false, LineEnding.Crlf);
        host.Prompt.OkCancelResult = true; // 続行

        Assert.True(host.File.SaveAs());

        Assert.True(File2.Exists(path));
        Assert.Equal(932, doc.State.Encoding.CodePage);
    });

    [Fact]
    public void SaveAs_Utf8_SkipsLossyWarning() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "😀"; // astral でも UTF-8 は全表現可
        host.Dialogs.SaveAs = new SaveAsResult(tmp.File("a.txt"), 65001, HasBom: false, LineEnding.Crlf);

        Assert.True(host.File.SaveAs());

        Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "OkCancel");
    });
}
