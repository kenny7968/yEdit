using yEdit.Core.Backup;

namespace yEdit.App.Tests;

/// <summary>
/// BK-L-4 回帰: <see cref="RestoreDialog.Describe(BackupRecord)"/> が攻撃者 JSON 由来の
/// U+202E RLO 等の BiDi/format 制御文字と改行を 1 行表示に載せないことを固定する。
/// 実 UI(CheckedListBox/Form)には触れずヘルパの戻り値のみを検証する(App.Tests の
/// SmokeTests 以外での慣例)。
///
/// 制御文字はソース line terminator 解釈(U+2028 等)と csharpier の意図せぬ再整形の
/// 双方を避けるため <c>\uXXXX</c> エスケープで統一する(Task 1 SanitizeForDisplayTests は
/// RLO のみインライン直書き=Task 4 は一段厳しめで RLO も含めて全制御文字をエスケープに
/// 寄せる)。
/// また U+202E は UnicodeCategory.Format のため culture-sensitive な Contains/IndexOf では
/// 常に "見つかる" 側に倒れる。RLO の実在有無を厳密に問うために
/// <c>StringComparison.Ordinal</c> を明示する。
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

    // ---------- BK-M-3: path-only fallback marker ----------

    private static BackupRecord RecWithContent(string? path, string? content, int untitled = 0) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            OriginalPath: path,
            UntitledNumber: untitled,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: content,
            TimestampUtc: new DateTime(2026, 7, 19, 12, 34, 56, DateTimeKind.Utc)
        );

    [Fact]
    public void Describe_ShowsPathOnlyMarker_WhenContentIsNull_ForPathRecord()
    {
        // BK-M-3: 上限超過で Content=null にフォールバックした record は、Describe 末尾に
        // 「本文なし=元ファイルから開き直してください」旨の marker を付ける契約を pin。
        // OriginalPath 有りの record は fileName/dir/timestamp の後に marker が来る。
        var rec = RecWithContent(@"C:\docs\huge.csv", content: null);
        var d = RestoreDialog.Describe(rec);
        Assert.Contains(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
        Assert.EndsWith(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
        Assert.Contains("huge.csv", d, StringComparison.Ordinal); // ファイル名は通常通り表示
    }

    [Fact]
    public void Describe_ShowsPathOnlyMarker_WhenContentIsNull_ForUntitledRecord()
    {
        // BK-M-3: 無題タブの path-only fallback も marker を付ける(OriginalPath=null 経路)。
        var rec = RecWithContent(path: null, content: null, untitled: 7);
        var d = RestoreDialog.Describe(rec);
        Assert.Contains(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
        Assert.EndsWith(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
        Assert.Contains("無題 7", d, StringComparison.Ordinal); // 無題ラベルは通常通り
    }

    [Fact]
    public void Describe_DoesNotShowPathOnlyMarker_WhenContentIsPresent()
    {
        // 通常経路 regression: Content が非 null(=通常サイズ) の record には marker は付かない。
        var recPath = RecWithContent(@"C:\docs\normal.txt", content: "本文あり");
        Assert.DoesNotContain(
            RestoreDialog.PathOnlyMarker,
            RestoreDialog.Describe(recPath),
            StringComparison.Ordinal
        );
        var recUntitled = RecWithContent(path: null, content: "本文あり", untitled: 3);
        Assert.DoesNotContain(
            RestoreDialog.PathOnlyMarker,
            RestoreDialog.Describe(recUntitled),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Describe_DoesNotShowPathOnlyMarker_WhenContentIsEmpty()
    {
        // Content="" (空文字) は「意図した空ファイルの通常保存」= marker 対象外。
        // marker は Content is null=path-only fallback のみに付く契約を pin。
        var rec = RecWithContent(@"C:\docs\empty.txt", content: "");
        var d = RestoreDialog.Describe(rec);
        Assert.DoesNotContain(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
    }
}
