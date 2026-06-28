# 禁則処理付き折り返し整形 実装プラン

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 日本語の禁則処理（行頭禁則＝追い出し・行末禁則・句読点ぶら下げ）を、指定桁(半角換算)で実改行を挿入する「折り返し整形」コマンドとして実装する。禁則文字は設定で変更可能・既定は保守的な記号セット。

**Architecture:** 折り位置の判定は Scintilla 非依存の純アルゴリズム（`yEdit.Core/Text`）に切り出し xUnit でTDD。App 側は選択範囲/全文のテキストを取り出して `KinsokuFormatter.Format` に渡し、`ScintillaHost.ReplaceCharRange`（=1 Undo）で置換するだけ。桁数は既存 `WrapColumn` を流用。

**Tech Stack:** C# / .NET 9 / WinForms / ScintillaNET / xUnit。設計: `docs/plans/2026-06-28-kinsoku-design.md`。

**共通コマンド:**
- 単体テスト: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~<ClassName>"`
- 全テスト: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
- ビルド(0警告確認): `dotnet build yEdit.sln`

---

## Task 1: AppSettings に禁則3キーを追加

**Files:**
- Modify: `src/yEdit.Core/Settings/AppSettings.cs`
- Test: `tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs`

**Step 1: 失敗するテストを書く**

`SettingsStoreTests.cs` に追加:

```csharp
[Fact]
public void Defaults_kinsoku_sets_are_conservative_symbols()
{
    var def = new AppSettings();
    Assert.Contains("、", def.KinsokuLineStartChars);
    Assert.Contains("）", def.KinsokuLineStartChars);
    Assert.DoesNotContain("ー", def.KinsokuLineStartChars);   // 長音は既定で入れない
    Assert.Contains("（", def.KinsokuLineEndChars);
    Assert.Equal("、。，．", def.KinsokuHangChars);
}

[Fact]
public void Save_then_load_roundtrips_kinsoku_settings()
{
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    try
    {
        var s = new AppSettings { KinsokuLineStartChars = ")】", KinsokuLineEndChars = "(【", KinsokuHangChars = "。" };
        SettingsStore.Save(path, s);
        var loaded = SettingsStore.Load(path);
        Assert.Equal(")】", loaded.KinsokuLineStartChars);
        Assert.Equal("(【", loaded.KinsokuLineEndChars);
        Assert.Equal("。", loaded.KinsokuHangChars);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}
```

**Step 2: テストが失敗することを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: コンパイルエラー（`KinsokuLineStartChars` 未定義）。

**Step 3: 最小実装**

`AppSettings.cs` の `WrapColumn` プロパティの後・`RecentFiles` の前に追加:

```csharp
/// <summary>行頭に来てはいけない文字（追い出し対象）。空で無効。</summary>
public string KinsokuLineStartChars { get; set; } = ")]}）］｝〕〉》」』】〗〙、。，．・：；！？";
/// <summary>行末に来てはいけない文字（開き括弧。次行へ送る）。空で無効。</summary>
public string KinsokuLineEndChars { get; set; } = "([{（［｛〔〈《「『【〖〘";
/// <summary>行末にぶら下げ可能な文字（句読点）。空で無効。</summary>
public string KinsokuHangChars { get; set; } = "、。，．";
```

**Step 4: テストが通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: PASS（既存のラウンドトリップ含め全緑）。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Settings/AppSettings.cs tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs
git commit -m "禁則整形: AppSettings に禁則文字3キーを追加"
```

---

## Task 2: EastAsianWidth（半角換算の文字幅）

**Files:**
- Create: `src/yEdit.Core/Text/EastAsianWidth.cs`
- Test: `tests/yEdit.Core.Tests/Text/EastAsianWidthTests.cs`

**Step 1: 失敗するテストを書く**

```csharp
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class EastAsianWidthTests
{
    [Theory]
    [InlineData('A', 1)]        // ASCII
    [InlineData('0', 1)]
    [InlineData('あ', 2)]       // ひらがな
    [InlineData('漢', 2)]       // 漢字
    [InlineData('Ａ', 2)]       // 全角英字
    [InlineData('。', 2)]       // 全角句読点
    [InlineData('ｱ', 1)]        // 半角カタカナ
    [InlineData('　', 2)]       // 全角スペース(U+3000)
    [InlineData(' ', 1)]        // 半角スペース
    public void ColumnWidth_basic(char c, int expected)
        => Assert.Equal(expected, EastAsianWidth.ColumnWidth(c));

    [Fact]
    public void ColumnWidth_combining_is_zero()
        => Assert.Equal(0, EastAsianWidth.ColumnWidth(0x0301)); // 結合アクセント

    [Fact]
    public void ColumnWidth_astral_cjk_is_two()
        => Assert.Equal(2, EastAsianWidth.ColumnWidth(0x20000)); // 拡張B
}
```

**Step 2: 失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~EastAsianWidthTests"`
Expected: コンパイルエラー（`EastAsianWidth` 未定義）。

**Step 3: 実装**

`src/yEdit.Core/Text/EastAsianWidth.cs`:

```csharp
namespace yEdit.Core.Text;

/// <summary>
/// コードポイントの半角換算表示幅（Wide/Fullwidth=2, 結合/ゼロ幅=0, その他=1）。
/// Unicode East Asian Width の実用的近似（厳密テーブルは将来精緻化）。
/// </summary>
public static class EastAsianWidth
{
    public static int ColumnWidth(int cp)
    {
        if (IsZeroWidth(cp)) return 0;
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsZeroWidth(int cp) =>
        cp is (>= 0x0300 and <= 0x036F)   // 結合分音記号
            or (>= 0x1AB0 and <= 0x1AFF)
            or (>= 0x1DC0 and <= 0x1DFF)
            or (>= 0x20D0 and <= 0x20FF)
            or (>= 0xFE20 and <= 0xFE2F)
            or 0x200B or 0x200C or 0x200D or 0xFEFF; // ゼロ幅スペース/接合子/BOM

    private static bool IsWide(int cp) =>
        cp is (>= 0x1100 and <= 0x115F)   // Hangul Jamo
            or (>= 0x2E80 and <= 0x303E)  // CJK部首補助〜CJK記号(全角句読点・全角スペース含む)
            or (>= 0x3041 and <= 0x33FF)  // かな〜CJK互換
            or (>= 0x3400 and <= 0x4DBF)  // 拡張A
            or (>= 0x4E00 and <= 0x9FFF)  // 統合漢字
            or (>= 0xA000 and <= 0xA4CF)  // イ文字
            or (>= 0xAC00 and <= 0xD7A3)  // Hangul音節
            or (>= 0xF900 and <= 0xFAFF)  // 互換漢字
            or (>= 0xFE30 and <= 0xFE4F)  // CJK互換形
            or (>= 0xFF00 and <= 0xFF60)  // 全角形（半角カナ FF61〜 は除外）
            or (>= 0xFFE0 and <= 0xFFE6)  // 全角記号
            or (>= 0x1F300 and <= 0x1FAFF) // 絵文字（概ね全角）
            or (>= 0x20000 and <= 0x3FFFD); // CJK拡張B〜（astral）
}
```

**Step 4: 通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~EastAsianWidthTests"`
Expected: PASS

**Step 5: コミット**

```bash
git add src/yEdit.Core/Text/EastAsianWidth.cs tests/yEdit.Core.Tests/Text/EastAsianWidthTests.cs
git commit -m "禁則整形: EastAsianWidth（半角換算幅）を追加"
```

---

## Task 3: KinsokuFormatter の骨格（行分割・桁折り・改行保持）

禁則を含めない素の「桁折り＋既存改行/EOL保持」をまず通す。次タスクで禁則を足す。

**Files:**
- Create: `src/yEdit.Core/Text/KinsokuFormatter.cs`
- Test: `tests/yEdit.Core.Tests/Text/KinsokuFormatterTests.cs`

**Step 1: 失敗するテストを書く**

```csharp
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class KinsokuFormatterTests
{
    // 禁則無し（空セット）でのテスト用ヘルパ。
    private static string Wrap(string text, int columns, string eol = "\n")
        => KinsokuFormatter.Format(text, columns, "", "", "", eol);

    [Fact]
    public void Empty_or_zero_columns_returns_as_is()
    {
        Assert.Equal("", KinsokuFormatter.Format("", 80, "", "", "", "\n"));
        Assert.Equal("abc", KinsokuFormatter.Format("abc", 0, "", "", "", "\n"));
    }

    [Fact]
    public void Short_line_is_unchanged()
        => Assert.Equal("あいう", Wrap("あいう", 80));

    [Fact]
    public void Wraps_halfwidth_at_columns()
        // 10桁: "abcdefghij" でちょうど、次の "k" で折る
        => Assert.Equal("abcdefghij\nk", Wrap("abcdefghijk", 10));

    [Fact]
    public void Fullwidth_counts_as_two_columns()
        // 4桁 = 全角2文字。3文字目で折る
        => Assert.Equal("ああ\nあ", Wrap("あああ", 4));

    [Fact]
    public void Existing_newlines_preserved_only_long_lines_split()
        // columns=2 では "abc" も "def" も超過するため両方分割。既存の \n は保持。
        => Assert.Equal("ab\nc\nde\nf", Wrap("abc\ndef", 2));

    [Fact]
    public void Preserves_crlf_terminator_and_inserts_given_eol()
        => Assert.Equal("ab\r\ncd\r\ne", KinsokuFormatter.Format("ab\r\ncde", 2, "", "", "", "\r\n"));

    [Fact]
    public void Surrogate_pair_not_split()
    {
        // 𩸽(U+29E3D, 幅2) を含む。columns=2 なら1文字ずつ。ペアを割らない。
        string s = "\U00029E3D\U00029E3D";
        Assert.Equal("\U00029E3D\n\U00029E3D", Wrap(s, 2));
    }

    [Fact]
    public void Oversized_single_char_still_progresses()
        // columns=1 でも全角1文字は1行に出して前進（無限ループしない）
        => Assert.Equal("あ\nい", Wrap("あい", 1));
}
```

**Step 2: 失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~KinsokuFormatterTests"`
Expected: コンパイルエラー（`KinsokuFormatter` 未定義）。

**Step 3: 実装（骨格＋禁則調整フック）**

`src/yEdit.Core/Text/KinsokuFormatter.cs`:

```csharp
using System.Text;

namespace yEdit.Core.Text;

/// <summary>
/// 日本語の禁則処理付き折り返し整形（純アルゴリズム・UI/Scintilla 非依存）。
/// 指定桁(半角換算)を超える論理行に実改行(eol)を挿入して分割し、行頭禁則(追い出し)・
/// 行末禁則・句読点ぶら下げで折り位置を調整する。既存の改行は保持する。
/// </summary>
public static class KinsokuFormatter
{
    private readonly record struct Cell(int Idx, int Len, int Cp);

    /// <summary>
    /// text を columns 桁(半角換算)で禁則整形する。columns&lt;=0 や空文字はそのまま返す。
    /// 挿入する改行は eol。各禁則文字集合は空文字でそのルールを無効化する。
    /// </summary>
    public static string Format(
        string text, int columns,
        string lineStartChars, string lineEndChars, string hangChars,
        string eol, int tabWidth = 8)
    {
        if (string.IsNullOrEmpty(text) || columns <= 0) return text;

        var lineStart = ToSet(lineStartChars);
        var lineEnd = ToSet(lineEndChars);
        var hang = ToSet(hangChars);

        var sb = new StringBuilder(text.Length + text.Length / 8 + 16);
        int i = 0, n = text.Length;
        while (i < n)
        {
            int contentBeg = i, j = i;
            while (j < n && text[j] != '\n' && text[j] != '\r') j++;
            int contentEnd = j;
            string term = "";
            if (j < n)
            {
                if (text[j] == '\r' && j + 1 < n && text[j + 1] == '\n') { term = "\r\n"; j += 2; }
                else { term = text[j].ToString(); j += 1; }
            }
            WrapLine(text, contentBeg, contentEnd, columns, tabWidth, lineStart, lineEnd, hang, eol, sb);
            sb.Append(term);
            i = j;
        }
        return sb.ToString();
    }

    private static void WrapLine(
        string text, int beg, int end, int columns, int tabWidth,
        HashSet<int> lineStart, HashSet<int> lineEnd, HashSet<int> hang, string eol, StringBuilder sb)
    {
        var cells = BuildCells(text, beg, end);
        if (cells.Count == 0) return;

        int startCell = 0;
        while (startCell < cells.Count)
        {
            int cut = FindCut(cells, startCell, columns, tabWidth);
            if (cut >= cells.Count) { AppendCells(sb, text, cells, startCell, cells.Count); return; }
            cut = AdjustForHang(cells, cut, hang);
            if (cut >= cells.Count) { AppendCells(sb, text, cells, startCell, cells.Count); return; }
            cut = AdjustForKinsoku(cells, startCell, cut, lineStart, lineEnd);
            AppendCells(sb, text, cells, startCell, cut);
            sb.Append(eol);
            startCell = cut;
        }
    }

    /// <summary>startCell から貪欲に columns 桁まで詰め、次行先頭になるセル index を返す（最低1セル前進）。</summary>
    private static int FindCut(List<Cell> cells, int startCell, int columns, int tabWidth)
    {
        int width = 0, k = startCell;
        while (k < cells.Count)
        {
            int cp = cells[k].Cp;
            int w = cp == '\t' ? tabWidth - (width % tabWidth) : EastAsianWidth.ColumnWidth(cp);
            if (width + w > columns && k > startCell) break;
            width += w;
            k++;
        }
        return k;
    }

    /// <summary>cut（次行先頭）がぶら下げ文字なら現在行へ取り込む（桁超過を許容）。</summary>
    private static int AdjustForHang(List<Cell> cells, int cut, HashSet<int> hang)
    {
        if (hang.Count == 0) return cut;
        while (cut < cells.Count && hang.Contains(cells[cut].Cp)) cut++;
        return cut;
    }

    /// <summary>行頭禁則(追い出し)・行末禁則で cut を上限付きに戻す。隣も禁則/空行化なら処理せず違反許容。</summary>
    private static int AdjustForKinsoku(List<Cell> cells, int startCell, int cut, HashSet<int> lineStart, HashSet<int> lineEnd)
    {
        const int maxPush = 8;
        for (int g = 0; g < maxPush; g++)
        {
            bool startBad = cut < cells.Count && lineStart.Contains(cells[cut].Cp);
            bool endBad = cut - 1 > startCell && lineEnd.Contains(cells[cut - 1].Cp);
            if (!startBad && !endBad) break;
            if (cut - 1 <= startCell) break;                                  // 現在行に最低1セル残す
            if (startBad && lineStart.Contains(cells[cut - 1].Cp)) break;     // 連鎖防止(行頭)
            if (endBad && lineEnd.Contains(cells[cut - 2].Cp)) break;         // 連鎖防止(行末)
            cut--;
        }
        return cut;
    }

    private static List<Cell> BuildCells(string text, int beg, int end)
    {
        var cells = new List<Cell>(end - beg);
        int p = beg;
        while (p < end)
        {
            int len = 1, cp;
            if (char.IsHighSurrogate(text[p]) && p + 1 < end && char.IsLowSurrogate(text[p + 1]))
            { cp = char.ConvertToUtf32(text, p); len = 2; }
            else cp = text[p];
            cells.Add(new Cell(p, len, cp));
            p += len;
        }
        return cells;
    }

    private static void AppendCells(StringBuilder sb, string text, List<Cell> cells, int from, int to)
    {
        if (to <= from) return;
        int charStart = cells[from].Idx;
        int charEnd = to < cells.Count ? cells[to].Idx : cells[to - 1].Idx + cells[to - 1].Len;
        sb.Append(text, charStart, charEnd - charStart);
    }

    private static HashSet<int> ToSet(string chars)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrEmpty(chars)) return set;   // null/空セット = そのルール無効（手編集 null での NRE も防ぐ）
        for (int i = 0; i < chars.Length;)
        {
            int len = 1, cp;
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            { cp = char.ConvertToUtf32(chars, i); len = 2; }
            else cp = chars[i];
            set.Add(cp); i += len;
        }
        return set;
    }
}
```

**Step 4: 通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~KinsokuFormatterTests"`
Expected: PASS（Task 3 の全テスト）。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Text/KinsokuFormatter.cs tests/yEdit.Core.Tests/Text/KinsokuFormatterTests.cs
git commit -m "禁則整形: KinsokuFormatter の骨格（桁折り・改行/EOL保持）"
```

---

## Task 4: 行頭禁則（追い出し）と連鎖ガードのテスト

実装は Task 3 で入っているので、ここは**振る舞いをテストで固定**する（既に通るはず＝リグレッション網）。

**Files:**
- Test: `tests/yEdit.Core.Tests/Text/KinsokuFormatterTests.cs`

**Step 1: テストを追加**

```csharp
// 行頭禁則のみを有効にしたヘルパ
private static string Start(string text, int columns, string startChars)
    => KinsokuFormatter.Format(text, columns, startChars, "", "", "\n");

[Fact]
public void LineStart_kinsoku_pushes_forbidden_char_down()
    // columns=6(全角3)で "あいう" の後 "。" が行頭に来るのを避け、"う" ごと次行へ追い出す。
    // 追い出し後の "う。え"(=6桁) は収まるので再分割は起きない。
    => Assert.Equal("あい\nう。え", Start("あいう。え", 6, "。"));

[Fact]
public void LineStart_kinsoku_skips_when_previous_also_forbidden()
    // 直前も禁則文字(。)なら処理しない＝幾何位置で折る（違反許容・連鎖防止）
    => Assert.Equal("あ。\n。い", Start("あ。。い", 4, "。"));
```

> 期待値の根拠: 1つ目は cut が「。」の前(=次行先頭が「。」)になるため追い出して「う」を次行へ送り "あい" / "う。え"。
> 2つ目は戻そうとした直前(cut-1)が「。」で連鎖防止に当たり処理せず "あ。" / "。い"。
> （追い出し例で columns=4 全角だと追い出し後の行が再び超過し連鎖するため columns=6 を用いる。）

**Step 2: 実行（緑のはず）**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~KinsokuFormatterTests"`
Expected: PASS。もし FAIL なら期待値/アルゴリズムを `superpowers:systematic-debugging` で突き合わせて調整（実装側のオフバイワン修正を優先）。

**Step 3: コミット**

```bash
git add tests/yEdit.Core.Tests/Text/KinsokuFormatterTests.cs
git commit -m "禁則整形: 行頭禁則(追い出し)と連鎖ガードのテストを追加"
```

---

## Task 5: 行末禁則とぶら下げのテスト

**Files:**
- Test: `tests/yEdit.Core.Tests/Text/KinsokuFormatterTests.cs`

**Step 1: テストを追加**

```csharp
[Fact]
public void LineEnd_kinsoku_pushes_opening_bracket_down()
    // columns=6 で現在行末尾が開き括弧「（」になるのを避け、次行へ送る。
    // "あい（" の末尾「（」を追い出し "あい" / "（うえ"(=6桁・収まる)。
    => Assert.Equal("あい\n（うえ", KinsokuFormatter.Format("あい（うえ", 6, "", "（", "", "\n"));

[Fact]
public void Hang_punctuation_exceeds_column()
    // columns=4(全角2) "あい" の直後の "。" はぶら下げて行末に残す（次行先頭にしない）
    => Assert.Equal("あい。\nう", KinsokuFormatter.Format("あい。う", 4, "", "", "。", "\n"));

[Fact]
public void Hang_takes_precedence_over_linestart()
    // 同じ "。" が行頭禁則とぶら下げ両方に入っていてもぶら下げが優先（行末に残る）
    => Assert.Equal("あい。\nう", KinsokuFormatter.Format("あい。う", 4, "。", "", "。", "\n"));
```

**Step 2: 実行**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~KinsokuFormatterTests"`
Expected: PASS（ぶら下げが追い出しより前に適用される実装順のため優先される）。

**Step 3: コミット**

```bash
git add tests/yEdit.Core.Tests/Text/KinsokuFormatterTests.cs
git commit -m "禁則整形: 行末禁則・ぶら下げ・優先順位のテストを追加"
```

---

## Task 6: 設定ダイアログに禁則処理グループ（3テキストボックス）

**Files:**
- Modify: `src/yEdit.App/SettingsDialog.cs`

> WinForms UI は実機確認。ここはユニットテスト無し（既存方針＝UI は実機SR検証）。

**Step 1: フィールドを追加**

`_wrapColumn` フィールド定義の直後に追加:

```csharp
private readonly TextBox _kinsokuStart = new() { Width = 320, AccessibleName = "行頭禁則文字" };
private readonly TextBox _kinsokuEnd = new() { Width = 320, AccessibleName = "行末禁則文字" };
private readonly TextBox _kinsokuHang = new() { Width = 320, AccessibleName = "ぶら下げ文字" };
```

**Step 2: 初期値をコンストラクタで設定**

`BuildLayout();` の直前（`UpdateFontLabel();` の後）に追加:

```csharp
_kinsokuStart.Text = s.KinsokuLineStartChars;
_kinsokuEnd.Text = s.KinsokuLineEndChars;
_kinsokuHang.Text = s.KinsokuHangChars;
```

**Step 3: 公開プロパティを追加**

`public int WrapColumn => (int)_wrapColumn.Value;` の直後に追加:

```csharp
public string KinsokuLineStartChars => _kinsokuStart.Text;
public string KinsokuLineEndChars => _kinsokuEnd.Text;
public string KinsokuHangChars => _kinsokuHang.Text;
```

**Step 4: レイアウトに3行を追加し、ボタン行を下げる**

`BuildLayout()` 内、折り返し行（`root.Controls.Add(wrapPanel, 1, 4); _wrapEnabled.TabIndex = 8;`）の後、ボタン生成の前に追加:

```csharp
// 禁則処理: 行頭/行末/ぶら下げの文字セット（TabIndex 11..16・OK/Cancel=100 の前）。
AddRow(root, 5, "行頭禁則文字(&1):", _kinsokuStart, tabBase: 11);
AddRow(root, 6, "行末禁則文字(&2):", _kinsokuEnd, tabBase: 13);
AddRow(root, 7, "ぶら下げ文字(&3):", _kinsokuHang, tabBase: 15);
```

そして既存のボタン追加行を **row 5 → row 8** に変更:

```csharp
root.Controls.Add(buttons, 0, 8);   // 禁則処理行(5..7)の下へ
```

（`root.SetColumnSpan(buttons, 2);` はそのまま。）

**Step 5: ビルドして手動確認**

Run: `dotnet build yEdit.sln`
Expected: 0 警告・0 エラー。
手動: 設定ダイアログを開き、3つのテキストボックスに既定値が入っていること、Alt+1/2/3 でフォーカス移動、Tab 順がフォント→…→折り返し→行頭→行末→ぶら下げ→OK/キャンセル になること（実機SR検証で確認）。

**Step 6: コミット**

```bash
git add src/yEdit.App/SettingsDialog.cs
git commit -m "禁則整形: 設定ダイアログに禁則文字3欄を追加"
```

---

## Task 7: コマンド本体（メニュー＋ホットキー＋FormatWithKinsoku）と設定反映

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`

**Step 1: 編集メニューにコマンドを追加**

`BuildMenu()` 内、grep 行 `AddMenuItem(edit, "フォルダ検索(grep)(&G)...", ...);` の直後に追加:

```csharp
edit.DropDownItems.Add(new ToolStripSeparator());
AddMenuItem(edit, "折り返し整形（禁則処理）(&K)", (_, _) => FormatWithKinsoku(),
    Keys.Control | Keys.Shift | Keys.J);
```

**Step 2: 設定反映を OpenSettings に追加**

`OpenSettings()` 内、`_settings.WrapColumn = dlg.WrapColumn;` の直後に追加（`foreach ... Apply` の前）:

```csharp
_settings.KinsokuLineStartChars = dlg.KinsokuLineStartChars;
_settings.KinsokuLineEndChars = dlg.KinsokuLineEndChars;
_settings.KinsokuHangChars = dlg.KinsokuHangChars;
```

**Step 3: コマンド本体を実装**

`ToggleOvertype()` メソッドの直後あたり（読み上げ系の近く）に追加:

```csharp
/// <summary>選択範囲（無ければ全文）を WrapColumn 桁で禁則整形する（実改行挿入・1 Undo）。</summary>
private void FormatWithKinsoku()
{
    var doc = _docs.Active;
    var ed = doc?.Editor;
    if (ed is null) return;

    string text = ed.SnapshotText;
    var (selStart, selEnd) = ed.GetSelectionCharRange();
    bool whole = selStart == selEnd;
    int start = whole ? 0 : selStart;
    int len = whole ? text.Length : selEnd - selStart;
    if (len <= 0) return;

    string target = text.Substring(start, len);
    string eol = EolString(doc!.State.LineEnding);
    string formatted = KinsokuFormatter.Format(
        target, _settings.WrapColumn,
        _settings.KinsokuLineStartChars, _settings.KinsokuLineEndChars, _settings.KinsokuHangChars,
        eol);

    if (formatted == target) { _announcer.Say("変更なし"); return; }
    ed.ReplaceCharRange(start, len, formatted);   // SCI_REPLACETARGET = 1 アンドゥ
    ed.SelectCharRange(start, formatted.Length);  // 変化箇所を選択して提示
    ed.Focus();
    _announcer.Say("整形しました");
}

private static string EolString(LineEnding eol) => eol switch
{
    LineEnding.Lf => "\n",
    LineEnding.Cr => "\r",
    _ => "\r\n",
};
```

> `yEdit.Core.Text`（`LineEnding`/`KinsokuFormatter`）は MainForm で既に using 済み（`CharacterDescriber` 等で参照）。未解決ならファイル先頭に `using yEdit.Core.Text;` を追加。

**Step 4: ビルド（0警告）**

Run: `dotnet build yEdit.sln`
Expected: 0 警告・0 エラー。

**Step 5: コミット**

```bash
git add src/yEdit.App/MainForm.cs
git commit -m "禁則整形: 編集メニュー＋Ctrl+Shift+J＋FormatWithKinsoku を追加"
```

---

## Task 8: 仕上げ（全テスト・0警告・実機SR検証チェックリスト）

**Step 1: 全 Core テスト**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
Expected: 全 PASS（既存 + 新規）。

**Step 2: ソリューションビルド 0 警告**

Run: `dotnet build yEdit.sln`
Expected: `0 Warning(s) 0 Error(s)`。

**Step 3: 実機 SR 検証チェックリスト（手動・@superpowers:verification-before-completion）**

- [ ] 全角主体の長行を Ctrl+Shift+J で整形 → 指定桁で折れ、句読点・閉じ括弧が行頭に来ない。
- [ ] 開き括弧が行末に来ない。句読点が行末にぶら下がる。
- [ ] 選択範囲がある場合はそこだけ整形。無い場合は全文。
- [ ] 整形後 Ctrl+Z 一回で全部元に戻る。
- [ ] 設定ダイアログで禁則文字を変更 → 保存 → 再起動後も反映（settings.json 往復）。
- [ ] 整形後に「整形しました」、変化なしで「変更なし」を SR が読む。
- [ ] WrapColumn を変えると整形桁も追従する。
- [ ] 0 警告・既存機能（折り返し桁数表示・検索等）に回帰なし。

**Step 4: マージ前レビュー（@superpowers:requesting-code-review / 別エージェント）**

メモリ方針に従い、main への no-ff マージ前に別エージェントへコードレビューを依頼する。

**Step 5: 完了報告**

実装完了後、`superpowers:finishing-a-development-branch` で main への no-ff マージ方法を提示。
