using yEdit.Core.Backup;

namespace yEdit.App.Tests;

/// <summary>
/// BK-L-4 回帰: <see cref="RestoreDialog.Describe(BackupRecord)"/> が攻撃者 JSON 由来の
/// U+202E RLO 等の BiDi/format 制御文字と改行を 1 行表示に載せないことを固定する。
/// 実 UI(CheckedListBox/Form)には触れずヘルパの戻り値のみを検証する(App.Tests の
/// SmokeTests 以外での慣例)。
///
/// 制御文字はソースの line terminator として解釈されないよう <c>\uXXXX</c> エスケープで
/// 記述する(Task 1 SanitizeForDisplayTests と同じ流儀)。また U+202E は UnicodeCategory.Format
/// のため culture-sensitive な Contains/IndexOf では常に "見つかる" 側に倒れる。RLO の実在
/// 有無を厳密に問うために <c>StringComparison.Ordinal</c> を明示する。
/// </summary>
public class RestoreDialogTests
{
    private static BackupRecord Rec(string? path, int untitled = 0) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            OriginalPath: path,
            UntitledNumber: untitled,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: "",
            TimestampUtc: new DateTime(2026, 7, 19, 12, 34, 56, DateTimeKind.Utc)
        );

    // ---------- 通常経路(挙動不変) ----------

    [Fact]
    public void Describe_UntitledRecord_ReturnsUntitledLabel()
    {
        var rec = Rec(path: null, untitled: 5);
        var d = RestoreDialog.Describe(rec);
        Assert.StartsWith("無題 5", d);
        Assert.Contains("2026-07-19", d);
    }

    [Fact]
    public void Describe_UntitledRecord_NoNumber_ReturnsUntitledOnly()
    {
        // UntitledNumber が 0 以下なら "無題" のみ(番号なし)
        var rec = Rec(path: null, untitled: 0);
        var d = RestoreDialog.Describe(rec);
        Assert.StartsWith("無題", d);
        Assert.DoesNotContain("無題 ", d);
    }

    [Fact]
    public void Describe_PathRecord_ReturnsFilenameAndDir()
    {
        var rec = Rec(@"C:\docs\hello.txt");
        var d = RestoreDialog.Describe(rec);
        Assert.Contains("hello.txt", d);
        Assert.Contains(@"C:\docs", d);
    }

    // ---------- BK-L-4 core regression tests ----------

    [Fact]
    public void Describe_StripsRloOverride_InFilename()
    {
        // 攻撃者 JSON に U+202E RLO を含むファイル名を植えた場合:
        //   実体: evil-{RLO}txt.exe(表示上 evil-exe.txt に化ける)
        //   → Describe は RLO を drop して "evil-txt.exe" として 1 行表示する
        var rec = Rec("C:\\docs\\evil-\u202Etxt.exe");
        var d = RestoreDialog.Describe(rec);
        // Ordinal 明示の理由はクラス header 参照。生の RLO がダイアログテキストに載らない。
        Assert.DoesNotContain("\u202E", d, StringComparison.Ordinal);
        Assert.Contains("evil-txt.exe", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_StripsRloOverride_InDirectory()
    {
        var rec = Rec("C:\\\u202Emalicious\\file.txt");
        var d = RestoreDialog.Describe(rec);
        Assert.DoesNotContain("\u202E", d, StringComparison.Ordinal);
        Assert.Contains("file.txt", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ReplacesLineBreaks_InPath()
    {
        // OneLine は CR/LF を単一空白へ置換する(BK-L-5 と同族の防御)
        var rec = Rec("C:\\docs\\evil\r\ninjected\\file.txt");
        var d = RestoreDialog.Describe(rec);
        Assert.DoesNotContain("\r", d, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", d, StringComparison.Ordinal);
        // 内容は 1 行として存在(改行崩壊しない)
        Assert.Single(d.Split('\n'));
    }
}
