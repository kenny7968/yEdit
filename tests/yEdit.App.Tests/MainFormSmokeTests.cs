using System.IO;
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
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* 掃除失敗はテスト失敗にしない(バックアップ Timer 等の掴みが残る可能性) */ }
        }
    }

    /// <summary>
    /// スモーク用の既定設定。BackupEnabled=false により OnShown 内の
    /// <see cref="BackupCoordinator.OfferRestoreOnStartup"/> が先頭の !_enabled ガードで no-op となり、
    /// 実バックアップ処理(SweepTempFiles/LoadAll)が走らないため、テストが実 backup ディレクトリを触らない。
    /// </summary>
    private static AppSettings NewSettings(bool csvAutoModeOnOpen) => new()
    {
        BackupEnabled = false,
        CsvAutoModeOnOpen = csvAutoModeOnOpen,
    };

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

    // ===== AutoEnterCsvMode: 2 ガード(設定 ON/拡張子)の kill =====

    [Fact]
    public void AutoCsv_On_OpensCsvIntoCsvMode() => Sta.Run(() =>
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
    public void AutoCsv_SettingOff_StaysNormalMode() => Sta.Run(() =>
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
    public void AutoCsv_NonCsvExtension_StaysNormalMode() => Sta.Run(() =>
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
    public void OpenAndSelect_OpensSelectsAndSuppressesAutoCsv() => Sta.Run(() =>
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

    [Fact]
    public void MainForm_ControllerFields_AreReadOnly()
    {
        // Task 1a: null! 代入経路を止め、6 Controller を readonly 化する契約を固定。
        // 実装後は宣言時か ctor 初期化リストで確定代入 = readonly が復活する。
        var type = typeof(MainForm);
        var flags = System.Reflection.BindingFlags.Instance
                  | System.Reflection.BindingFlags.NonPublic;
        string[] controllerFields = {
            "_file", "_search", "_grep", "_backup", "_csv", "_kinsoku"
        };
        foreach (var name in controllerFields)
        {
            var field = type.GetField(name, flags);
            Assert.NotNull(field);
            Assert.True(field!.IsInitOnly, $"{name} must be readonly");
        }
    }
}
