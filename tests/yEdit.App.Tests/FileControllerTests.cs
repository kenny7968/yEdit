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
    /// <summary>
    /// FileController を Fake 境界で配線したテストホスト。共通の HostForm.CreateWithDocs で
    /// 「可視・画面外・非アクティブ」の土台を作る(実運用 MainForm は常に可視のため)。
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
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            File = new FileController(
                docs: Docs,
                owner: Form,
                settings: () => Settings,
                saveSettings: () => SaveSettingsCount++,
                recentChanged: () => RecentChangedCount++,
                metaChanged: () => MetaChangedCount++,
                openedFresh: d => OpenedFresh.Add(d),
                prompt: Prompt,
                fileDialogs: Dialogs);
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

    // ===== Save 公開入口(active 経由 Ctrl+S) / ReadOnly 復元(WriteToPath finally) =====

    [Fact]
    public void Save_NoActive_ReturnsFalse() => Sta.Run(() =>
    {
        // タブ 0 枚(Host 生成直後は docs.CreateNew を呼ばないため Active=null)。
        // Save() の `docs.Active is not null` ガードを `true` に変える NRE 変異を kill する
        // (ガードが外れれば SaveDocument(null) で NullReferenceException が伝播する)。
        using var host = new Host();
        Assert.Equal(0, host.Docs.Count);
        Assert.Null(host.Docs.Active);

        Assert.False(host.File.Save());
        Assert.Empty(host.Prompt.Log); // ダイアログにも一切進まない
    });

    [Fact]
    public void Save_ExistingPath_WritesAndClearsModified() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "orig"); // ASCII=UTF-8 として妥当(初回オープンで警告なし)
        var doc = host.File.TryOpenOrActivate(path)!;
        // 開いた直後は Modified=false=SetSavePoint 済み。編集して Save で再度 SetSavePoint されるかを観測する。
        doc.Editor.ReplaceCharRange(0, doc.Editor.CurrentBuffer.Current.CharLength, "changed");
        Assert.True(doc.Editor.Modified);

        // Ctrl+S 導線: FileController.Save() は docs.Active を SaveDocument に流す公開入口。
        // 既存 SaveDocument 直呼び系(ConfirmDiscardIfDirty_Yes_...)と異なり、Active 経由のエントリを固定する。
        Assert.True(host.File.Save());

        Assert.Equal("changed", File2.ReadAllText(path)); // ディスクへ書き出し=バッファと一致
        Assert.False(doc.Editor.Modified);                // SetSavePoint 済み
    });

    [Fact]
    public void Save_ReadOnlyDocument_RestoresReadOnlyAfterSave() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        // 既存 Save 系テストの流儀(CreateNew + Text + State.Path)。既定 State=UTF-8/BOM なし/CRLF。
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        doc.State.Path = path;
        doc.Editor.ReadOnly = true; // CSV モード相当(閲覧専用に落として保存する経路)

        Assert.True(host.File.Save());

        Assert.True(doc.Editor.ReadOnly);              // WriteToPath の try/finally で復元される契約
        Assert.Equal("abc", File2.ReadAllText(path)); // ディスクは更新されている(=Save 経路が抜けている)
    });

    [Fact]
    public void Save_ReadOnlyDocument_WriteFailure_StillRestoresReadOnly() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "orig"); // ReadOnly 属性を付けるため一旦実在させる
        File2.SetAttributes(path, System.IO.FileAttributes.ReadOnly);
        try
        {
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "changed";
            doc.State.Path = path;
            doc.Editor.ReadOnly = true; // CSV モード相当

            // 保存先ファイルの ReadOnly 属性で AtomicFile.Write の File.Replace が UnauthorizedAccessException
            // (WriteToPath の catch フィルタで false 返却+prompt.Error 通知)。
            // (inner finally は TextFileService.Save が例外を投げる前に完走・ReadOnly=true 復元済み)
            Assert.False(host.File.Save());
            Assert.True(doc.Editor.ReadOnly); // 失敗経路でも finally で復元される(=CSV 復帰不能を防止)
            Assert.Equal("orig", File2.ReadAllText(path)); // 原本は不変(AtomicFile の契約)
            Assert.Contains(host.Prompt.Log, e => e.Kind == "Error" && e.Text.StartsWith("保存できませんでした"));
        }
        finally
        {
            // TempDir の再帰削除が ReadOnly 属性で失敗するのを避け、テスト成否に関わらず属性を戻す。
            File2.SetAttributes(path, System.IO.FileAttributes.Normal);
        }
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

    // ===== 開く系(TryOpenOrActivate は path を開く唯一の経路) =====

    [Fact]
    public void TryOpenOrActivate_NewFile_LoadsMetaContent_AndFiresOpenedFresh() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        // 本文は LF 改行: 既定(Crlf)と同値だと改行検出配線のアサートが空振りする(レビュー I-2)
        File2.WriteAllBytes(path,
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("あい\nう")).ToArray());

        var doc = host.File.TryOpenOrActivate(path);

        Assert.NotNull(doc);
        Assert.Equal(path, doc!.State.Path);
        Assert.Equal(65001, doc.State.Encoding.CodePage);
        Assert.True(doc.State.HasBom);                       // BOM 検出の配線(既定 false に対し非デフォルト)
        Assert.Equal(LineEnding.Lf, doc.State.LineEnding);   // 改行検出の配線(既定 Crlf に対し非デフォルト)
        Assert.Equal("あい\nう", doc.Editor.Text);
        Assert.False(doc.Editor.Modified);                   // SetSavePoint 済み
        Assert.Same(doc, Assert.Single(host.OpenedFresh));   // .csv 自動モード判定への通知
        Assert.Equal(path, host.Settings.RecentFiles[0]);
    });

    [Fact]
    public void TryOpenOrActivate_AlreadyOpen_ActivatesExistingTab_WithoutReload() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "abc");
        var first = host.File.TryOpenOrActivate(path);
        _ = host.Docs.CreateNew(); // 別タブをアクティブにしてから再オープン

        var second = host.File.TryOpenOrActivate(path);

        Assert.Same(first, second);              // 既存タブ再利用(二重編集の上書き事故防止)
        Assert.Same(first, host.Docs.Active);    // アクティブ化
        Assert.Equal(2, host.Docs.Count);        // タブは増えない
        Assert.Single(host.OpenedFresh);         // 再ロードなし=openedFresh は初回のみ
    });

    [Fact]
    public void TryOpenOrActivate_LoadFailure_DiscardsScratchTab_AndRestoresPrevious() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        // タブ 3 枚構成にして prev を先頭以外に置く: TabControl は選択中タブの除去後に
        // 先頭(index 0)を自動選択するため、prev が先頭だと自動選択と明示復帰(Activate)を
        // 判別できない(レビュー I-1・ミューテーションで実証)。prev=2 枚目なら自動選択(先頭)と区別できる
        _ = host.Docs.CreateNew();        // 1 枚目(自動選択の着地先)
        var prev = host.Docs.CreateNew(); // 2 枚目(作成時点でアクティブ=直前のアクティブ)

        // Task 4 と同じ方式: 実在し得る絶対パス直書きを避け、一時フォルダ配下の
        // 存在しないサブフォルダを使う(レビュー申し送り)。
        var doc = host.File.TryOpenOrActivate(tmp.File(@"no-such-dir\no-such-file.txt"));

        Assert.Null(doc);
        Assert.Equal(2, host.Docs.Count);      // 作りかけタブは破棄
        // 作りかけ(末尾)除去後の TabControl 自動選択は先頭=明示復帰がないと落ちる
        Assert.Same(prev, host.Docs.Active);   // 直前のアクティブへ復帰
        Assert.Contains(host.Prompt.Log, e => e.Kind == "Error" && e.Text.StartsWith("開けませんでした"));
    });

    [Fact]
    public void TryOpenOrActivate_SuppressAutoCsv_DoesNotFireOpenedFresh() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.csv");
        File2.WriteAllText(path, "a,b");

        var doc = host.File.TryOpenOrActivate(path, suppressAutoCsv: true); // grep ジャンプ経路

        Assert.NotNull(doc);
        Assert.Empty(host.OpenedFresh); // 選択+エディタフォーカスを機能させるため自動 CSV を抑止
    });

    [Fact]
    public void OpenFileWithDialog_UsesPickedPath_AndCancelDoesNothing() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        host.Dialogs.OpenPath = null; // キャンセル
        host.File.OpenFileWithDialog();
        Assert.Equal(0, host.Docs.Count);

        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "abc");
        host.Dialogs.OpenPath = path;
        host.File.OpenFileWithDialog();
        Assert.Equal(path, host.Docs.Active!.State.Path); // 選択パスが唯一の開く経路へ流れる
    });

    // ===== 文字コード指定の開き直し =====

    [Fact]
    public void ReopenWithEncoding_WithoutPath_InformsAndSkipsDialog() => Sta.Run(() =>
    {
        using var host = new Host();
        _ = host.Docs.CreateNew(); // Path=null の無題

        host.File.ReopenWithEncoding();

        Assert.Contains(host.Prompt.Log, e => e.Kind == "Info" && e.Text == "ファイルを開いてから実行してください。");
        Assert.Equal(0, host.Dialogs.PickEncodingCount); // ダイアログまで進まない
    });

    [Fact]
    public void ReopenWithEncoding_ForcedCodePage_Reloads_AndReenablesUiaSelectionEvents() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "abc"); // ASCII=どのコードページでも同一内容(判定を決定的にする)
        var doc = host.File.TryOpenOrActivate(path)!;
        // PC-Talker 廃止後も温存の UIA 配線: LoadInto が RaiseUiaSelectionEvents を確実に戻すことを固定
        doc.Editor.RaiseUiaSelectionEvents = false;
        host.Dialogs.EncodingCodePage = 932;

        host.File.ReopenWithEncoding();

        Assert.Equal(932, doc.State.Encoding.CodePage);
        Assert.True(doc.Editor.RaiseUiaSelectionEvents);
        Assert.Equal(2, host.OpenedFresh.Count); // 開き直しも .csv 自動モードの対象
    });

    [Fact]
    public void ReopenWithEncoding_DirtyCancelled_AbortsBeforeDialog() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "abc");
        var doc = host.File.TryOpenOrActivate(path)!;
        doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty
        host.Prompt.YesNoCancelResult = DialogResult.Cancel;

        host.File.ReopenWithEncoding();

        Assert.Equal(0, host.Dialogs.PickEncodingCount); // 未保存確認で中止=ダイアログまで進まない
        Assert.True(doc.Editor.Modified);
        Assert.Equal(65001, doc.State.Encoding.CodePage);
    });

    [Fact]
    public void Reopen_WithReplacementChar_WarnsToReopen() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "abc"); // ASCII=UTF-8 として妥当=初回オープンで置換文字は発生しない
        Assert.NotNull(host.File.TryOpenOrActivate(path)); // 初回オープン成功
        Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Warn"); // 初回オープンは警告なしを固定

        // 本体を UTF-8 で不正なバイト(0xFF)に差し替える。TextBufferBuilder の Utf8Sanitizer が
        // U+FFFD へ置換し HadReplacementChar=true を返す=文字コード取り違えの示唆経路を発火させる。
        // (forcedCodePage=65001 で UTF-8 として強制デコード=0xFF は不正バイト→U+FFFD 置換)
        File2.WriteAllBytes(path, new byte[] { 0xFF });
        host.Dialogs.EncodingCodePage = 65001;

        host.File.ReopenWithEncoding();

        Assert.Contains(host.Prompt.Log,
            e => e.Kind == "Warn" && e.Text.Contains("置換文字") && e.Caption == "文字コードの警告");
    });

    // ===== 未保存確認(Yes=保存成否/No=true/Cancel=false) =====

    [Fact]
    public void ConfirmDiscardIfDirty_CleanDocument_TrueWithoutPrompt() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc"; // Text セッター=新規バッファで Modified=false

        Assert.True(host.File.ConfirmDiscardIfDirty(doc));
        Assert.Empty(host.Prompt.Log); // クリーンなら問わない
    });

    [Fact]
    public void ConfirmDiscardIfDirty_No_ReturnsTrueWithoutSaving() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        doc.Editor.ReplaceCharRange(0, 0, "x");
        doc.State.Path = tmp.File("a.txt"); // まだ存在しないファイル
        host.Prompt.YesNoCancelResult = DialogResult.No;

        Assert.True(host.File.ConfirmDiscardIfDirty(doc)); // 破棄=続行してよい

        Assert.False(File2.Exists(doc.State.Path)); // 保存はしない
        Assert.True(doc.Editor.Modified);
    });

    [Fact]
    public void ConfirmDiscardIfDirty_Yes_SavesDocument_AndReturnsSaveResult() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        doc.Editor.ReplaceCharRange(0, 0, "x");
        doc.State.Path = tmp.File("a.txt");
        host.Prompt.YesNoCancelResult = DialogResult.Yes;

        Assert.True(host.File.ConfirmDiscardIfDirty(doc));

        Assert.True(File2.Exists(doc.State.Path)); // Yes=保存してから続行
        Assert.False(doc.Editor.Modified);
        Assert.Contains(host.Prompt.Log, e => e.Kind == "YesNoCancel" && e.Text.Contains("の変更を保存しますか"));
    });

    [Fact]
    public void ConfirmDiscardIfDirty_Yes_WithoutPath_FallsBackToSaveAs_CancelMeansFalse() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty な無題(Path=null)
        host.Prompt.YesNoCancelResult = DialogResult.Yes;
        host.Dialogs.SaveAs = null; // SaveAs ダイアログでキャンセル

        Assert.False(host.File.ConfirmDiscardIfDirty(doc)); // Yes→SaveAs 失敗=続行しない(閉じない)
    });

    // ===== NewFile 既定+無題連番 / バックアップ復元 =====

    [Fact]
    public void NewFile_AppliesSettingsDefaults_AndNumbersUntitledTabs() => Sta.Run(() =>
    {
        using var host = new Host();
        host.Settings.DefaultCodePage = 932;
        host.Settings.DefaultLineEnding = 1; // LineEnding.Lf

        host.File.NewFile();
        var doc1 = host.Docs.Active!;
        host.File.NewFile();
        var doc2 = host.Docs.Active!;

        Assert.Equal(932, doc1.State.Encoding.CodePage);   // 設定の既定コードページ
        Assert.Equal(LineEnding.Lf, doc1.State.LineEnding); // 設定の既定改行
        Assert.False(doc1.State.HasBom);                   // 既定と同値=契約の文書化(NewFile は BOM なし固定)
        Assert.Equal(1, doc1.State.UntitledNumber);
        Assert.Equal(2, doc2.State.UntitledNumber);        // セッション内で再利用しない連番
        Assert.Equal("無題 1", doc1.Page.Text);
        Assert.False(doc2.Editor.Modified);
        Assert.True(host.MetaChangedCount >= 2);           // タイトル・ステータス更新の配線
    });

    [Fact]
    public void RestoreFromBackup_UntitledRecord_KeepsNumber_StaysDirty_AndAdvancesSeq() => Sta.Run(() =>
    {
        using var host = new Host();
        var rec = new BackupRecord("id-1", OriginalPath: null, UntitledNumber: 5,
            CodePage: 932, HasBom: false, LineEndingId: 1, Content: "abc", TimestampUtc: DateTime.UtcNow);

        var doc = host.File.RestoreFromBackup(rec);

        Assert.Equal(5, doc.State.UntitledNumber);         // ダイアログ表示と復元後タブの番号一致
        Assert.Equal(932, doc.State.Encoding.CodePage);
        Assert.Equal(LineEnding.Lf, doc.State.LineEnding);
        Assert.Equal("abc", doc.Editor.Text);
        Assert.True(doc.Editor.Modified);                  // 保存点を打たない=ユーザーが保存できる(復元 dirty 化バグの修正で本来意図へ)
        Assert.Equal("* 無題 5", doc.Page.Text);

        host.File.NewFile();                               // 連番カウンタは既存最大値の先へ進む
        Assert.Equal(6, host.Docs.Active!.State.UntitledNumber);
    });

    [Fact]
    public void RestoreFromBackup_PathRecord_SetsMetaFromRecord_AndToleratesNullContent() => Sta.Run(() =>
    {
        using var host = new Host();
        // UntitledNumber: 7 は「path レコードでは旧無題番号を無視して 0 化する」契約を実効検証するため
        // (0 のままだとコピー実装でも 0 化実装でも通ってしまう=レビュー I-1 と同型の空振り)
        var rec = new BackupRecord("id-2", OriginalPath: @"C:\backup-origin\b.txt", UntitledNumber: 7,
            CodePage: 65001, HasBom: true, LineEndingId: 0, Content: null!, TimestampUtc: DateTime.UtcNow);

        var doc = host.File.RestoreFromBackup(rec); // 復元はディスクを読まない=実在しないパスでよい

        Assert.Equal(@"C:\backup-origin\b.txt", doc.State.Path);
        Assert.Equal(0, doc.State.UntitledNumber);         // path レコードは旧無題番号(7)を無視して 0 化
        Assert.True(doc.State.HasBom);
        Assert.Equal("", doc.Editor.Text);                 // JSON 破損(null)でも空タブ復元で継続(レビュー M-5 の防御)
        Assert.True(doc.Editor.Modified);                  // 復元 dirty 化バグの修正で本来意図へ
        Assert.Equal("* b.txt", doc.Page.Text);
    });
}
