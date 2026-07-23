using System.Collections.Generic;
using System.IO;
using yEdit.Core.Session;
using yEdit.Core.Settings;
using Directory = System.IO.Directory;
using File2 = System.IO.File;
using IOException = System.IO.IOException;

namespace yEdit.App.Tests;

/// <summary>
/// Task 1-8: コンポジションルート(MainForm)結線のスモーク。
/// 中間区間=AutoEnterCsvMode の 2 ガード(設定 ON/拡張子)+OpenAndSelect の
/// suppressAutoCsv 配線を、実 MainForm を可視状態まで作って観測する。
/// FileController 個別の挙動(ロールバック等)は FileControllerTests で担保済み=再検証しない。
/// 責務: MainForm↔FileController の配線が生きているか(=AutoEnterCsvMode を通す/通さない)
/// の 4 分岐だけを固定する。
/// 前提: public <see cref="MainForm(AppSettings)"/> 経路は internal <see cref="MainForm(AppSettings, string)"/>
/// へチェーンする=このスモークでは internal ctor 経路のみ検証(public ctor 空化変異は
/// Release=warnaserror の CS8618 か Program.cs 起動時クラッシュが拾うため対象外)。
/// </summary>
public class MainFormSmokeTests
{
    /// <summary>テスト毎に使い捨てる一時フォルダ(settings.json とテスト対象ファイルの隔離先)。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("yEditAppSmoke_").FullName;

        public string File(string name) => Path.Combine(Root, name);

        /// <summary>SaveSettingsSafe の書込先(実 %APPDATA% を汚さないための隔離パス)。実際に書かれても構わない。</summary>
        public string SettingsPath => Path.Combine(Root, "settings.json");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* 掃除失敗はテスト失敗にしない(バックアップ Timer 等の掴みが残る可能性) */
            }
        }
    }

    /// <summary>
    /// スモーク用の既定設定。BackupEnabled=false により OnShown 内の
    /// <see cref="BackupCoordinator.OfferRestoreOnStartup"/> が先頭の !_enabled ガードで no-op となり、
    /// 実バックアップ処理(SweepTempFiles/LoadAll)が走らないため、テストが実 backup ディレクトリを触らない。
    /// </summary>
    private static AppSettings NewSettings(bool csvAutoModeOnOpen) =>
        new() { BackupEnabled = false, CsvAutoModeOnOpen = csvAutoModeOnOpen };

    /// <summary>
    /// MainForm を可視状態(=OnShown 発火)まで作る。MainForm は sealed のため
    /// <see cref="Form.ShowWithoutActivation"/> を注入できない=Show() が一時的にアクティブ化するが、
    /// xUnit の並列実行は <see cref="Xunit.CollectionBehaviorAttribute"/> で無効化済(GlobalUsings.cs)
    /// のため実害なし。StartPosition/Location/ShowInTaskbar は Show() 前に上書きして
    /// 画面外(-32000,-32000)配置=デスクトップ上のチラつきを最小化する
    /// (ctor 内で StartPosition=CenterScreen が指定されているが、Show() 時に評価されるため上書きが効く)。
    /// </summary>
    private static MainForm ShowMainForm(AppSettings settings, string settingsPath)
    {
        var form = new MainForm(settings, settingsPath);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        return form;
    }

    /// <summary>
    /// OnShown は <see cref="Control.BeginInvoke(Delegate)"/> でキューされるため、
    /// <see cref="Sta"/> の non-pumping STA スレッドでは Show() 単独では走らない。
    /// Task 7 テストは OnShown を明示的に動かす必要があるため
    /// <see cref="Application.DoEvents"/> でメッセージを 1 サイクルだけ処理する。
    /// </summary>
    private static void PumpUntilShown()
    {
        // Application.DoEvents を数回回して、CallShownEvent(BeginInvoke)+続く再入 (OnActivated 内の
        // BeginInvoke など) をすべて処理する。回数は安全側の 4 サイクル(実測 1〜2 で足りる)。
        for (int i = 0; i < 4; i++)
        {
            Application.DoEvents();
        }
    }

    /// <summary>
    /// Task 7: OnShown で <see cref="BackupCoordinator.OfferRestoreOnStartup"/> の戻り値を
    /// 差し替えて「バックアップ復元が発火した」分岐を kill するための helper。
    /// override 未 null=前回タブ復元は必ず skip される(restored != 0)。
    /// </summary>
    private static MainForm ShowMainForm_WithBackupCountOverride(
        AppSettings settings,
        string settingsPath,
        int restoredOverride
    )
    {
        var form = new MainForm(settings, settingsPath);
        form.SetRestoredCountOverrideForTest(restoredOverride);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        PumpUntilShown();
        return form;
    }

    /// <summary>
    /// Task 7: OnShown の前回タブ復元経路を、実 %APPDATA%\yEdit\last-session-buffers.json を
    /// 触らずに検証するための helper。SetLastSessionBuffersPathForTest は Show() より前に
    /// 呼ばないと OnShown 内の Load/Delete が既定パスへ落ちるため、ここで統合的に組み立てる。
    /// suppressFailedDialog=true で FailedPaths が非空でも Warn ダイアログを出さない
    /// (テスト内で MessageBox がブロックするのを回避)。
    /// </summary>
    private static MainForm ShowMainForm_ForRestoreOnShown(
        AppSettings settings,
        string settingsPath,
        string buffersPath,
        bool suppressFailedDialog = false
    )
    {
        var form = new MainForm(settings, settingsPath);
        form.SetLastSessionBuffersPathForTest(buffersPath);
        if (suppressFailedDialog)
            form.SetSuppressFailedRestoreDialogForTest(true);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        PumpUntilShown();
        return form;
    }

    // ===== AutoEnterCsvMode: 2 ガード(設定 ON/拡張子)の kill =====

    [Fact]
    public void AutoCsv_On_OpensCsvIntoCsvMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            // 拡張子は大文字 DATA.CSV: MainForm:114 の StringComparison.OrdinalIgnoreCase が
            // Ordinal に変異したら小文字 .csv と不一致=AutoEnterCsvMode を素通り=CsvMode=false で赤化する。
            string path = tmp.File("DATA.CSV");
            File2.WriteAllText(path, "a,b\n1,2");
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: true), tmp.SettingsPath);

            var doc = form.FileForTest.TryOpenOrActivate(path);

            Assert.NotNull(doc);
            Assert.True(doc!.State.CsvMode); // .csv 判定+自動 CSV モード配線の kill(=結線が生きている)
        });

    [Fact]
    public void AutoCsv_SettingOff_StaysNormalMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("data.csv");
            File2.WriteAllText(path, "a,b\n1,2");
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp.SettingsPath);

            var doc = form.FileForTest.TryOpenOrActivate(path);

            Assert.NotNull(doc);
            // MainForm:113 の設定 ON ガード(!_settings.CsvAutoModeOnOpen return)を削除する変異を kill:
            // 削除されると .csv 判定を通り抜けて CsvMode=true になり本 assertion が赤化する。
            Assert.False(doc!.State.CsvMode);
        });

    [Fact]
    public void AutoCsv_NonCsvExtension_StaysNormalMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("data.txt"); // .csv でない
            File2.WriteAllText(path, "a,b\n1,2");
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: true), tmp.SettingsPath);

            var doc = form.FileForTest.TryOpenOrActivate(path);

            Assert.NotNull(doc);
            // MainForm:114 の拡張子ガード(.csv 判定 return)を削除する変異を kill:
            // 削除されると拡張子に関わらず TryEnterMode を呼び CsvMode=true になり本 assertion が赤化する。
            Assert.False(doc!.State.CsvMode);
        });

    // ===== OpenAndSelect: suppressAutoCsv 配線+選択レンジ =====

    [Fact]
    public void OpenAndSelect_OpensSelectsAndSuppressesAutoCsv() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("data.csv"); // 拡張子だけでは判定できない=auto 設定 ON でも CSV へ入らないことを固定する
            File2.WriteAllText(path, "a,b,c\n1,2,3");
            // auto ON のまま OpenAndSelect: suppressAutoCsv=true が抜けると AutoEnterCsvMode が発火して赤化する
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: true), tmp.SettingsPath);

            form.OpenAndSelect(path, offset: 2, length: 3);

            // OpenAndSelect 後の Active タブを取り戻す: 既に開いているため FileController.TryOpenOrActivate は
            // 既存タブ再利用の fast path(FindByPath ヒット)を通り _openedFresh を呼ばない=
            // 観測対象(CsvMode/Path/選択レンジ)への副作用はない(内部で RegisterRecent →
            // recentChanged/saveSettings は走るが観測外・実 %APPDATA% は tmp 隔離で汚染ゼロ)。
            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);

            // MainForm:416 の suppressAutoCsv: true → false 変異を kill:
            // false だと _openedFresh 経路で AutoEnterCsvMode を通し CsvMode=true 化=本 assertion が赤化する。
            Assert.False(doc!.State.CsvMode);
            Assert.Equal(path, doc.State.Path);
            // SelectCharRange(2, 3): start=2 / end=2+3=5(EditorControl:323-324 のエイリアス経由)
            Assert.Equal((2, 5), doc.Editor.GetSelectionCharRange());
        });

    // ===== Task 6: OnFormClosing での LastSession/buffers 保存 =====

    [Fact]
    public void OnFormClosing_RestoreEnabled_SavesLastSessionAndBuffers() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string txt = tmp.File("a.txt");
            File2.WriteAllText(txt, "hello");
            string buffersPath = Path.Combine(tmp.Root, "last-session-buffers.json");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;

            using (var form = ShowMainForm(settings, tmp.SettingsPath))
            {
                form.SetLastSessionBuffersPathForTest(buffersPath);
                form.FileForTest.TryOpenOrActivate(txt);
                form.Close();
            }

            var loaded = SettingsStore.Load(tmp.SettingsPath);
            Assert.True(loaded.RestoreOpenFilesOnStartup);
            Assert.NotNull(loaded.LastSession);
            Assert.Contains(loaded.LastSession!.Tabs, t => t.Path == txt);
            // Task 6 review I-3: Save 呼び出しが本当に発火したことを検証(Save をコメントアウトすると赤化)
            Assert.True(File2.Exists(buffersPath));
        });

    [Fact]
    public void OnFormClosing_UntitledTabWithContent_PersistsToBuffersFile() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string buffersPath = Path.Combine(tmp.Root, "last-session-buffers.json");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;

            using (var form = ShowMainForm(settings, tmp.SettingsPath))
            {
                form.SetLastSessionBuffersPathForTest(buffersPath);
                // 起動時の空無題タブに本文を入れ、SavePoint を打ち直して Modified=false 状態にする
                // (BuildLastSessionSnapshot は Modified=true の untitled を skip する=Task 6 review I-1)
                var doc = form.FileForTest.DocsForTest[0];
                doc.Editor.SetOrReplaceSource(
                    yEdit.Core.Buffers.TextBuffer.FromString("session-hello")
                );
                doc.Editor.SetSavePoint();
                form.Close();
            }

            // buffers.json に何か 1 件書かれているはず
            var buffers = yEdit.Core.Session.LastSessionBuffersStore.Load(buffersPath);
            Assert.Single(buffers);
            Assert.Contains("session-hello", buffers.Values);
            // 対応する SessionTabRecord は Path=null で BufferKey が buffers のキーと一致
            var reloaded = SettingsStore.Load(tmp.SettingsPath);
            Assert.NotNull(reloaded.LastSession);
            var untitled = reloaded.LastSession!.Tabs.First(t => t.Path is null);
            Assert.NotNull(untitled.BufferKey);
            Assert.True(buffers.ContainsKey(untitled.BufferKey!));
        });

    // §8 補遺 Task 12: dirty パスありタブは本文+エンコーディング/EOL/WasModified を保存する。
    // (以前の "dirty untitled skip" テスト=Task 6 review I-1 は §8.2 で全 dirty 保存へ方針転換したため削除)
    [Fact]
    public void BuildLastSessionSnapshot_IncludesDirtyPathTab_WithBufferAndEncoding() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string txt = tmp.File("a.txt");
            File2.WriteAllText(txt, "original");
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm(settings, tmp.SettingsPath);
            form.FileForTest.TryOpenOrActivate(txt);
            var doc = form.FileForTest.DocsForTest[^1];
            // dirty 化: 末尾に " +edited" を挿入する(EditorControl に AppendText は無いため
            // ReplaceCharRange で純挿入=start=TextLength, length=0, replacement=文字列)。
            doc.Editor.ReplaceCharRange(doc.Editor.TextLength, 0, " +edited");
            Assert.True(doc.Editor.Modified);

            var (snap, buffers) = form.BuildLastSessionSnapshotForTest();
            var rec = snap.Tabs.Single(t => t.Path == txt);
            Assert.NotNull(rec.BufferKey);
            Assert.True(buffers.ContainsKey(rec.BufferKey!));
            Assert.Contains("+edited", buffers[rec.BufferKey!]);
            Assert.Equal(doc.State.Encoding.CodePage, rec.CodePage); // §8.2
            Assert.Equal(doc.State.HasBom, rec.HasBom);
            Assert.Equal((int)doc.State.LineEnding, rec.LineEnding);
            Assert.True(rec.WasModified);
        });

    // §8 補遺 Task 12: dirty 無題タブは(以前は skip されていたが)本文を保存する。
    [Fact]
    public void BuildLastSessionSnapshot_IncludesDirtyUntitledTab_WithBuffer() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm(settings, tmp.SettingsPath);
            // ctor で作られた空無題タブに dirty 内容を入れる(ReplaceCharRange で純挿入)。
            var doc = form.FileForTest.DocsForTest[0];
            doc.Editor.ReplaceCharRange(0, 0, "dirty content");
            Assert.True(doc.Editor.Modified);
            Assert.Null(doc.State.Path);

            var (snap, buffers) = form.BuildLastSessionSnapshotForTest();
            var rec = snap.Tabs.Single(t => t.Path is null);
            Assert.NotNull(rec.BufferKey);
            Assert.Equal("dirty content", buffers[rec.BufferKey!]);
            Assert.True(rec.WasModified); // §8.2
        });

    // §8 補遺 Task 12: WillDirtyContentFitInCaps は per-tab cap 超過を dry-run で捉える。
    // (Task 13 の OnFormClosing 高速経路判定用の pre-check ヘルパ)
    [Fact]
    public void WillDirtyContentFitInCaps_ReturnsFalse_WhenSingleTabExceedsPerTabCap() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm(settings, tmp.SettingsPath);
            var doc = form.FileForTest.DocsForTest[0];
            // 1 M + 1 chars を純挿入=MaxSessionUntitledContentChars (1M) 超過。
            doc.Editor.ReplaceCharRange(0, 0, new string('x', 1024 * 1024 + 1));
            Assert.False(form.WillDirtyContentFitInCapsForTest());
        });

    [Fact]
    public void BuildLastSessionSnapshot_UntitledOverPerTabCap_BufferKeyIsNull() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm(settings, tmp.SettingsPath);
            var doc = form.FileForTest.DocsForTest[0];
            // 1 M + 1 chars = per-tab cap 超過
            int over = 1024 * 1024 + 1;
            doc.Editor.SetOrReplaceSource(
                yEdit.Core.Buffers.TextBuffer.FromString(new string('x', over))
            );
            doc.Editor.SetSavePoint(); // dirty untitled skip 分岐に落ちないように clean 化

            var (snap, buffers) = form.BuildLastSessionSnapshotForTest();

            // 1 タブ分の record は含まれるが BufferKey=null(枠だけ保存)
            Assert.Single(snap.Tabs);
            Assert.Null(snap.Tabs[0].BufferKey);
            Assert.Empty(buffers);
        });

    [Fact]
    public void OnFormClosing_RestoreDisabled_ClearsLastSessionAndDeletesBuffers() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string buffersPath = Path.Combine(tmp.Root, "last-session-buffers.json");
            // 事前に buffers.json 残骸を作っておく → 設定 OFF で消えるはず
            File2.WriteAllText(buffersPath, "{\"k\":\"stale\"}");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = false;
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord> { new(@"C:\stale.txt", 0, null, true, 0, 0) }
            );

            using (var form = ShowMainForm(settings, tmp.SettingsPath))
            {
                form.SetLastSessionBuffersPathForTest(buffersPath);
                form.Close();
            }

            var loaded = SettingsStore.Load(tmp.SettingsPath);
            Assert.False(loaded.RestoreOpenFilesOnStartup);
            Assert.Null(loaded.LastSession);
            Assert.False(File2.Exists(buffersPath));
        });

    // ===== Task 7: OnShown での前回タブ復元経路 =====

    [Fact]
    public void OnShown_RestoreEnabled_NoBackup_RestoresPreviousTabs() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("a.txt");
            File2.WriteAllText(p1, "AAA");
            string buffersPath = Path.Combine(tmp.Root, "last-session-buffers.json");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord> { new(p1, 0, null, true, 0, 0) }
            );

            using var form = ShowMainForm_ForRestoreOnShown(
                settings,
                tmp.SettingsPath,
                buffersPath
            );
            // 復元経路発火 → a.txt が開いている・空無題タブは閉じられている
            Assert.Contains(form.FileForTest.DocsForTest, d => d.State.Path == p1);
            Assert.Single(form.FileForTest.DocsForTest);
        });

    [Fact]
    public void OnShown_RestoreEnabled_BackupPresent_SkipsRestore() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("a.txt");
            File2.WriteAllText(p1, "AAA");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord> { new(p1, 0, null, true, 0, 0) }
            );

            using var form = ShowMainForm_WithBackupCountOverride(
                settings,
                tmp.SettingsPath,
                restoredOverride: 3
            );
            // restored=3 → 前回タブ復元スキップ・a.txt は開かない
            Assert.DoesNotContain(form.FileForTest.DocsForTest, d => d.State.Path == p1);
        });

    [Fact]
    public void OnShown_RestoreDisabled_DoesNotRestore() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("a.txt");
            File2.WriteAllText(p1, "AAA");
            string buffersPath = Path.Combine(tmp.Root, "last-session-buffers.json");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = false; // OFF
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord> { new(p1, 0, null, true, 0, 0) }
            );

            // ShowMainForm_ForRestoreOnShown は Application.DoEvents で OnShown を発火させる=
            // 「復元経路の gate 判定」が実際に評価される(Task 7 review: 純 ShowMainForm では
            // vacuous になり RestoreOpenFilesOnStartup gate を mutation 検証できない)。
            using var form = ShowMainForm_ForRestoreOnShown(
                settings,
                tmp.SettingsPath,
                buffersPath
            );
            Assert.DoesNotContain(form.FileForTest.DocsForTest, d => d.State.Path == p1);
        });

    [Fact]
    public void OnShown_RestoreEnabled_MissingFile_KeepsStartupEmpty() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string missing = tmp.File("no-such.txt");
            string buffersPath = Path.Combine(tmp.Root, "last-session-buffers.json");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord> { new(missing, 0, null, true, 0, 0) }
            );

            using var form = ShowMainForm_ForRestoreOnShown(
                settings,
                tmp.SettingsPath,
                buffersPath,
                suppressFailedDialog: true
            );
            // ファイルが無い→ FailedPaths に載る・startup empty がそのまま残る=通常起動と等価
            Assert.Single(form.FileForTest.DocsForTest);
        });

    [Fact]
    public void MainForm_ControllerFields_AreReadOnly()
    {
        // Task 1a: null! 代入経路を止め、6 Controller を readonly 化する契約を固定。
        // 実装後は宣言時か ctor 初期化リストで確定代入 = readonly が復活する。
        var type = typeof(MainForm);
        var flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        string[] controllerFields = { "_file", "_search", "_grep", "_backup", "_csv", "_kinsoku" };
        foreach (var name in controllerFields)
        {
            var field = type.GetField(name, flags);
            Assert.NotNull(field);
            Assert.True(field!.IsInitOnly, $"{name} must be readonly");
        }
    }
}
