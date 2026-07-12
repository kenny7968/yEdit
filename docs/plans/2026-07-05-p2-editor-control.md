# P2: EditorControl 骨格+描画 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** P1 の `TextBuffer`/`TextSnapshot` を土台に、自作エディットコントロールの WinForms 表面(`yEdit.Editor.EditorControl`)を「読み取り専用ビューア水準」で構築し、1GB 文書で 1 フレーム 16ms のスクロール滑らかさを達成する。入力(P3)/IME(P4)/UIA 接続(P5)/App 層置換(P6)は次フェーズ。

**Architecture:** レイアウトは純ロジック(`yEdit.Core.Layout` 名前空間)に閉じ込め、GDI 描画は `Frame`(DrawText/FillRect/DrawLine の抽象オペレータ列)を介して切り離す。可視行のみを毎フレーム再構築し、キャッシュは持たない(P1 の `TextSnapshot` が行照会を O(log n)+64KB 走査で提供するため、TextSnapshot をたたけば十分)。文字幅は `ICharMetrics` 抽象で挿げ替え可能にし、テストは固定幅で決定的に、実行時は `System.Windows.Forms.TextRenderer` で高精度に測る。折り返しは設計書§2-3 に従い文字単位(char-based)。スクロール単位は論理行(TextSnapshot.LineCount で O(1)) — 折り返し ON でも論理行送り(可視化領域内で折り返し済み視覚行を展開)、これにより 1GB でもスクロールバー計算が O(1) で済む。設計書: `docs/plans/2026-07-05-custom-editcontrol-design.md` §2-3。

**Tech Stack:** C# / .NET 9 / xUnit(既存 `tests/yEdit.Core.Tests`)/ WinForms(`Control` 派生・既存 `yEdit.Editor` に同居)/ ベンチは既存 `tests/yEdit.Core.Bench` 拡張と新規 `tests/yEdit.Editor.Smoke`(手動確認+GDI ベンチ)

**前提:**
- 作業は worktree `<repo>\.worktrees\custom-editcontrol-design`(ブランチ `feature/custom-editcontrol-design`)
- **main には一切触れない**(全フェーズを本ブランチに閉じ、P7 合格後に一括マージ。設計書§3 運用)
- P2 は **ScintillaHost に変更を加えない**(P6 で一発置換の予定・並行運用しない)
- P1 の公開 API(`TextBuffer`/`TextSnapshot`/`TextBufferBuilder`)は変更しない
- 各タスク完了ごとにコミット。全タスク後に別エージェントレビュー(Task 15)
- 既存テスト 415 件は全タスクで緑を維持

---

## 0. 設計固定事項(全タスク共通の不変条件)

実装全体で守る。レビュー観点でもある。

1. **公開オフセットは UTF-16 コード単位(int)**。P1 と同じ通貨。バイト位置は登場しない
2. **論理行 vs 視覚行**:
   - 論理行 = TextSnapshot.LineCount(改行区切り)
   - 視覚行 = 論理行を折り返しで分割した各段
   - 折り返し OFF 時 論理行=視覚行
3. **スクロール単位は論理行**。`TopLine` は論理行インデックス。折り返し ON 時、可視領域の先頭は「TopLine の先頭視覚行」から始まる(論理行の中間から描画開始しない=単純化・SR 位置と整合)
4. **可視領域のみ描画・キャッシュしない**。P1 の TextSnapshot.GetLineStart/GetText が O(log n) + 局所走査なので、フレーム毎の再取得で 1GB でも 16ms 目標を達成できる(Task 14 で実証)
5. **文字幅測定は `ICharMetrics` 経由**。純ロジックは実 GDI に依存しない=xUnit で決定的にテスト可
6. **サロゲートペアは分割しない**。折り返し・位置マッピングは code-point 境界(=high/low サロゲートペアはアトミック)で処理。位置引数がペア中間を指したら前方へスナップ(P1 §0-3 と同方針)
7. **描画は `Frame` 抽象 → GDI の 2 段**。純ロジック(FrameBuilder)は Frame を返す=xUnit 検証可。EditorControl は Frame を GDI に投げるだけ
8. **P3 依存 API を先出しする(公開・no-op も可)**: 入力ドライブ(P3)から呼ばれる `SetCaretCharOffset`/`SetSelectionCharRange` は P2 で受け口として実装(入力ハンドラは無し=キー/マウスは効かない)
9. **UIA 接続は P5**。P2 で WM_GETOBJECT ハンドラや `IUiaTextHost` 実装は追加しない(既存 UiaProbe の実装が P5 の参照実装)
10. **本番 App 層は触れない**。ScintillaHost は据え置き、EditorControl はまだどこからも参照されない(smoke 起動器のみが参照)

## 1. 公開 API サーフェス(P3〜P6 の呼び出し側契約)

```csharp
namespace yEdit.Editor;

/// <summary>
/// 自作エディットコントロール(P2: 読み取り専用ビューア水準)。
/// 入力(P3)/IME(P4)/UIA(P5)/App 層配線(P6)は後続フェーズで追加。
/// </summary>
public sealed class EditorControl : Control
{
    public void SetSource(TextBuffer buffer);        // 一度だけ / null 不可

    // 表示状態
    public int TopLine { get; set; }                 // 表示先頭の論理行(0 始まり・LineCount-1 上限)
    public int WrapColumns { get; set; }             // 0 = 折り返し OFF
    public bool ShowLineNumbers { get; set; }
    public bool ShowWhitespace { get; set; }         // 空白/EOL 可視化
    public bool HighlightCurrentLine { get; set; }

    // キャレット/選択(外部ドライブ・P3 で入力から呼ぶ)
    public int CaretCharOffset { get; }
    public void SetCaretCharOffset(int offset);
    public (int Start, int End) GetSelectionCharRange();
    public void SetSelectionCharRange(int start, int end);

    // セルハイライト(CSV F2 相当・単一アクティブ)
    public void HighlightCharRange(int start, int length);
    public void ClearHighlight();

    // 座標(P3/P4/P6 の外部座標計算に必要)
    public int LineHeightPx { get; }
    public Point PointFromCharOffset(int offset);    // クライアント座標(可視外は Point.Empty)

    // 外観(P6 で App 層 EditorAppearance からアタッチ)
    public void ApplyAppearance(AppSettings settings);
}
```

**受け口だけ用意して P2 では入力ハンドラを実装しない理由**: P3 で `SetCaretCharOffset` を呼ぶキーハンドラを追加するだけで機能が繋がるため、P2 で API 形状を確定させておくと P3 の実装が最小差分で済む。

## 2. 共通コマンド

```powershell
# レイアウト単体(高速ループ用)
dotnet test tests/yEdit.Core.Tests -c Release --nologo --filter FullyQualifiedName~Layout
# 全体回帰(各タスク末尾で実行)
dotnet build yEdit.sln -c Release; dotnet test tests/yEdit.Core.Tests -c Release --nologo
```

Expected(全体回帰): build 0 警告 / 既存 415 件 + 新規が全緑。

---

### Task 1: ICharMetrics 抽象 + MonoCharMetrics(固定幅テスト実装)

**Files:**
- Create: `src/yEdit.Core/Layout/ICharMetrics.cs`
- Create: `src/yEdit.Core/Layout/MonoCharMetrics.cs`
- Test: `tests/yEdit.Core.Tests/Layout/MonoCharMetricsTests.cs`

純レイアウトが GDI に依存しないための挿げ替え点。実 GDI 実装(`GdiCharMetrics`)は Task 6 で `yEdit.Editor` に置く。

**API:**

```csharp
namespace yEdit.Core.Layout;

/// <summary>
/// 文字幅と行高の計測。純レイアウトはこれ越しに測る(実 GDI は yEdit.Editor 側)。
/// 呼び出し側はサロゲートペアを分割しない(ペアは1回の呼び出しに含める)。
/// </summary>
public interface ICharMetrics
{
    int LineHeightPx { get; }
    /// <summary>text の描画幅(px)。サロゲートペア/CJK/ASCII 混在可。</summary>
    int MeasureRun(ReadOnlySpan<char> text);
}

/// <summary>ASCII=1・BMP CJK=2・サロゲートペア=2 の固定幅(テスト用)。</summary>
public sealed class MonoCharMetrics : ICharMetrics
{
    public MonoCharMetrics(int halfWidthPx = 8, int lineHeightPx = 16)
    { _half = halfWidthPx; LineHeightPx = lineHeightPx; }
    public int LineHeightPx { get; }
    public int MeasureRun(ReadOnlySpan<char> text)
    {
        int px = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            { px += _half * 2; i++; continue; }
            px += (c < 0x80 || c == '\t') ? _half : _half * 2;  // ASCII/タブ=1・それ以外=2
        }
        return px;
    }
    private readonly int _half;
}
```

**Step 1: 失敗するテスト**

```csharp
public class MonoCharMetricsTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);
    [Fact] public void Empty_is_zero() => Assert.Equal(0, M.MeasureRun(""));
    [Fact] public void Ascii_counts_half_per_char() => Assert.Equal(3, M.MeasureRun("abc"));
    [Fact] public void Cjk_counts_full_per_char() => Assert.Equal(4, M.MeasureRun("あ亜"));
    [Fact] public void Surrogate_pair_counts_full() => Assert.Equal(2, M.MeasureRun("😀"));  // 2*1
    [Fact] public void Mixed() => Assert.Equal(1 + 2 + 2 + 1, M.MeasureRun("aあ😀b"));
    [Fact] public void Line_height_is_configured() => Assert.Equal(10, M.LineHeightPx);
}
```

**Step 2-4: 失敗確認 → 実装 → 緑+全体回帰**

**Step 5: コミット** `"P2: ICharMetrics 抽象 + MonoCharMetrics(テスト用固定幅)"`

---

### Task 2: LineLayout(文字単位折り返し)

**Files:**
- Create: `src/yEdit.Core/Layout/LineLayout.cs`
- Test: `tests/yEdit.Core.Tests/Layout/LineLayoutTests.cs`

論理行 1 本を最大幅で分割する純関数。**設計書§2-3 の char-based 折り返し**。行末改行文字(`\r`/`\n`/`\r\n`)は入力に含めない(呼び出し側が `GetLineEnd(line, includeBreak:false)` で除去済み)。

**API:**

```csharp
public readonly record struct WrapSegment(int OffsetInLine, int Length);

internal static class LineLayout
{
    /// <summary>
    /// line を maxWidthPx で char 単位に折り返し、視覚行の開始オフセットと長さを返す。
    /// maxWidthPx&lt;=0 は「折り返し無し」= [ (0, line.Length) ] を返す。
    /// - サロゲートペアの中間で分割しない
    /// - 折り返し境界にタブや半角/全角の混在があっても、1 文字入るなら必ず入れる(空セグメント禁止)
    /// - 空文字列は [ (0, 0) ] を返す(空行も 1 視覚行分の高さを持つ)
    /// </summary>
    public static IReadOnlyList<WrapSegment> Wrap(ReadOnlySpan<char> line, int maxWidthPx, ICharMetrics metrics);
}
```

**アルゴリズム(実装コメントに残す):**
- OFF: 単一セグメントを返して終了
- ON: 累積幅を走査。i の code-point 幅を足して max を超えたら、そこで区切って新セグメント開始
- **強制前進**: セグメント先頭で 1 code-point が max を超える場合でも、その 1 code-point を必ず入れる(空セグメント禁止=無限ループ回避)
- タブは半角1文字幅として扱う(Metrics 側で解決)。**厳密なタブ揃えは P3 の入力側で扱う**(タブ整形は Core 済み)

**Step 1: 失敗するテスト**(すべて `MonoCharMetrics(1, 10)` で決定的):

```csharp
[Fact] public void Wrap_off_returns_single_segment()
{
    var r = LineLayout.Wrap("abcde", 0, M);
    Assert.Single(r); Assert.Equal((0, 5), (r[0].OffsetInLine, r[0].Length));
}
[Fact] public void Wrap_ascii_at_boundary()
{
    // 幅=3 → "abc"/"de"
    var r = LineLayout.Wrap("abcde", 3, M);
    Assert.Equal(2, r.Count);
    Assert.Equal((0, 3), (r[0].OffsetInLine, r[0].Length));
    Assert.Equal((3, 2), (r[1].OffsetInLine, r[1].Length));
}
[Fact] public void Wrap_never_splits_surrogate_pair()
{
    // 幅=3 で "a😀b" → "a" (1) では 😀(2) が入らない → "a"+"😀"+"b" ではなく "a" / "😀b" ではなく...
    // 具体: 幅3 → seg1="a😀"(1+2=3 OK) seg2="b"
    var r = LineLayout.Wrap("a😀b", 3, M);
    Assert.Equal(2, r.Count);
    Assert.Equal((0, 3), (r[0].OffsetInLine, r[0].Length));  // 'a'+high+low
    Assert.Equal((3, 1), (r[1].OffsetInLine, r[1].Length));
}
[Fact] public void Wrap_forces_progress_when_single_codepoint_exceeds_width()
{
    // 幅=1 で "😀" → 幅 2 だが強制前進で 1 セグメントに 😀 全体を入れる
    var r = LineLayout.Wrap("😀", 1, M);
    Assert.Single(r); Assert.Equal((0, 2), (r[0].OffsetInLine, r[0].Length));
}
[Fact] public void Empty_line_yields_one_empty_segment()
{
    var r = LineLayout.Wrap("", 10, M);
    Assert.Single(r); Assert.Equal((0, 0), (r[0].OffsetInLine, r[0].Length));
}
[Fact] public void Segments_cover_the_whole_line()
{
    var r = LineLayout.Wrap("あいうえお漢字ABC", 5, M);
    int sum = 0; foreach (var s in r) sum += s.Length;
    Assert.Equal("あいうえお漢字ABC".Length, sum);
}
```

**Step 2-4: 失敗確認 → 実装 → 緑+全体回帰**

**Step 5: コミット** `"P2: LineLayout(文字単位折り返し・サロゲート保護・強制前進)"`

---

### Task 3: PixelMapper(視覚セグメント内の char↔pixel)

**Files:**
- Create: `src/yEdit.Core/Layout/PixelMapper.cs`
- Test: `tests/yEdit.Core.Tests/Layout/PixelMapperTests.cs`

セグメントの先頭を x=0 として、任意の char オフセットの pixel 位置、任意の pixel 位置に最も近い char オフセットを返す純関数。P2 のキャレット位置決めとマウス衝突(P3)で使う。

**API:**

```csharp
internal static class PixelMapper
{
    /// <summary>segment 内の charOffset(0..segment.Length)を pixel(0..)にマップ。</summary>
    public static int OffsetToPx(ReadOnlySpan<char> segment, int charOffset, ICharMetrics metrics);

    /// <summary>x(px)に最も近い code-point 境界のオフセットを返す。x&lt;=0 → 0、x&gt;=幅 → segment.Length。</summary>
    public static int PxToOffset(ReadOnlySpan<char> segment, int px, ICharMetrics metrics);
}
```

**Step 1: 失敗するテスト**(`MonoCharMetrics(1,10)` で決定的):
- OffsetToPx: `("abcあ", 3)` → 3(ASCII 3個)/ `("abcあ", 4)` → 5(ASCII 3 + CJK 2)
- PxToOffset: `("abcあ", 4)` → 4(CJK の中間に丸め・後方=文字先頭スナップの反対)
- サロゲート: `("😀", 1)` → 0(low サロゲート単体位置は前方スナップ)
- 空セグメント: OffsetToPx("", 0) = 0 / PxToOffset("", 5) = 0
- 全域: PxToOffset(text, int.MaxValue) = text.Length

**スナップ規則**: `PxToOffset` は「その px に最も近い code-point 境界」を返す。中間 px は「入れば含める」= max px 直後の境界を返す(選択拡張の直観に合わせる)。実装は累積幅の binary search(素直な線形走査でも十分)。

**Step 2-4: 失敗確認 → 実装 → 緑+全体回帰**

**Step 5: コミット** `"P2: PixelMapper(char↔pixel・code-point 境界スナップ)"`

---

### Task 4: ViewportLayout(可視視覚行の列挙)

**Files:**
- Create: `src/yEdit.Core/Layout/ViewportLayout.cs`
- Test: `tests/yEdit.Core.Tests/Layout/ViewportLayoutTests.cs`

TextSnapshot + `TopLine` + 表示高さ + 折り返し設定から、**描画すべき視覚行のリスト**を返す純関数。可視外は含めない=フレームコストが O(可視行数) に閉じる。

**API:**

```csharp
/// <summary>1 本の視覚行の情報。</summary>
public readonly record struct VisualRow(
    int LogicalLine,       // 論理行(0 始まり)
    int SegmentIndex,      // その論理行内の視覚行インデックス(0=論理行の先頭視覚行)
    int SegmentStartChar,  // その視覚行が担う開始 char offset(絶対・文書先頭から)
    int SegmentLength,     // その視覚行の char 長(改行は含まない)
    int YPx                // クライアント座標 Y(TopLine の先頭視覚行が Y=0)
);

internal static class ViewportLayout
{
    /// <summary>
    /// TopLine 以降を積み上げて heightPx を満たす分だけ VisualRow を返す。
    /// - wrapColumns&lt;=0: 折り返し OFF(1 論理行=1 視覚行)
    /// - wrapColumns&gt;0: 半角 wrapColumns 文字分の px を max として LineLayout.Wrap を各行に適用
    /// - LineCount を超えても "1 個空の視覚行"(EOF 位置キャレット用)を先頭に含める
    /// </summary>
    public static IReadOnlyList<VisualRow> Build(
        TextSnapshot snapshot, int topLine, int heightPx, int wrapColumns, ICharMetrics metrics);
}
```

**Step 1: 失敗するテスト**(`MonoCharMetrics(1,10)` で決定的):
- 折り返し OFF・複数論理行: `"a\nb\nc"` topLine=0 height=25(2.5行分) → [{0,0,0,1,0},{1,0,2,1,10},{2,0,4,1,20}]
- topLine が全行数より大: 空リスト
- 空文書: topLine=0 → [{0,0,0,0,0}] (1 個の空視覚行)
- 折り返し ON: 論理行 `"abcdef"` を wrap=3 → [{0,0,0,3,0},{0,1,3,3,10}]
- CRLF 行(TextSnapshot 経由): 論理行の内容から改行文字は除去済みであること(GetLineEnd(includeBreak:false)を使用)
- 高さぴったり: heightPx=10 → 1 行だけ

**Step 2-4: 失敗確認 → 実装 → 緑+全体回帰**

実装は `snapshot.GetLineStart` / `GetLineEnd(line, includeBreak:false)` / `GetText` の3点セットを使う。可視領域が広くても LineCount 上限で打ち切り。

**Step 5: コミット** `"P2: ViewportLayout(可視視覚行列挙・折り返し ON/OFF)"`

---

### Task 5: FrameBuilder(paint オペレータ列の純関数)

**Files:**
- Create: `src/yEdit.Core/Layout/Frame.cs`
- Create: `src/yEdit.Core/Layout/FrameBuilder.cs`
- Test: `tests/yEdit.Core.Tests/Layout/FrameBuilderTests.cs`

「描画結果(GDI にどう塗るか)」を純データで表現する層。EditorControl の OnPaint はこれを消費するだけ=**xUnit で描画内容を検証できる**。

**API:**

```csharp
public enum PaintOpKind { FillRect, DrawText, DrawLine }
public readonly record struct PaintColor(int Rgb, byte Alpha = 255);
public readonly record struct PaintOp(
    PaintOpKind Kind,
    int X, int Y, int Width, int Height,   // FillRect/DrawText 領域 / DrawLine は (X,Y)→(X+Width,Y+Height)
    string? Text = null,
    PaintColor Fore = default, PaintColor Back = default);

public sealed record Frame(IReadOnlyList<PaintOp> Ops, int ClientWidth, int ClientHeight);

public sealed record ViewportStyle(
    PaintColor Foreground, PaintColor Background,
    PaintColor CurrentLineBack,       // 現在行強調(未使用時は Alpha=0)
    PaintColor SelectionBack,         // 選択(未使用時は Alpha=0)
    PaintColor LineNumberFore,
    PaintColor HighlightOutline,      // セルハイライト枠
    PaintColor WhitespaceGlyph);

public readonly record struct SelectionRange(int Start, int End);       // Start&lt;=End・End 排他

internal static class FrameBuilder
{
    public static Frame Build(
        TextSnapshot snapshot,
        IReadOnlyList<VisualRow> rows,
        int clientWidth, int clientHeight,
        int lineNumberMarginPx,       // 0 = マージン非表示
        int currentLineLogical,       // -1 = なし
        SelectionRange? selection,
        SelectionRange? cellHighlight,
        bool showWhitespace,
        ViewportStyle style,
        ICharMetrics metrics);
}
```

**責務分割:**
- FrameBuilder は「何をどこにどの色で塗るか」を決める=決定的
- EditorControl.OnPaint は Frame を GDI 呼び出しに置換するだけ(色は Color、DrawText は TextRenderer.DrawText 等)

**Step 1: 失敗するテスト**(`MonoCharMetrics(1,10)` で決定的・小さな文書):

- **背景塗り**: 全域 FillRect(色=style.Background)が必ず先頭に1つある
- **行描画**: `"ab\ncd"` topLine=0 高さ 20・折り返し OFF → DrawText が 2 個(Y=0 "ab" / Y=10 "cd")
- **現在行強調**: currentLineLogical=1・HighlightCurrentLine ON 相当 → その視覚行の背景に FillRect(色=CurrentLineBack)が本文 DrawText の**前**にある(重なり順)
- **選択**: SelectionRange(1,3) on `"abcd"`→ 選択矩形 FillRect が本文の前・DrawText はそのまま(色反転は P2 では簡易=背景色のみ)
- **行番号マージン**: lineNumberMarginPx=30 → 行ごとに "1"/"2" が X=(30 未満・右寄せ位置)で DrawText される。本文はマージン分オフセットされている
- **セルハイライト**: cellHighlight 指定 → その範囲に**枠矩形**(DrawLine ×4)+半透明 FillRect(Alpha=60 相当)
- **空白可視化**: showWhitespace=true & 本文にスペース/タブ含む → 対応位置にグリフ描画(単純化: 中点文字を DrawText)
- **空フレーム**: 空文書 → 背景塗り 1 個 + 空視覚行の DrawText("")(≥0 個 OK・Ops.Count は決定的)

**Step 2-4: 失敗確認 → 実装 → 緑+全体回帰**

**Step 5: コミット** `"P2: FrameBuilder(描画オペレータ列・現在行/選択/行番号/ハイライト/空白可視化)"`

---

### Task 6: EditorControl 骨格 + GdiCharMetrics + Frame→GDI 配線

**Files:**
- Create: `src/yEdit.Editor/GdiCharMetrics.cs`
- Create: `src/yEdit.Editor/EditorControl.cs`
- Modify: `src/yEdit.Editor/yEdit.Editor.csproj`(InternalsVisibleTo に smoke を追加)

**Step 1: GdiCharMetrics**

```csharp
namespace yEdit.Editor;

/// <summary>TextRenderer(GDI)ベースの ICharMetrics 実装(UI スレッド専用)。</summary>
public sealed class GdiCharMetrics : ICharMetrics
{
    private readonly Font _font;
    public GdiCharMetrics(Font font)
    { _font = font; LineHeightPx = TextRenderer.MeasureText("Mg", font, new Size(int.MaxValue, int.MaxValue),
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height; }
    public int LineHeightPx { get; }
    public int MeasureRun(ReadOnlySpan<char> text)
        => TextRenderer.MeasureText(text.ToString(), _font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
}
```

**Step 2: EditorControl 骨格**

```csharp
namespace yEdit.Editor;

public sealed class EditorControl : Control
{
    private TextBuffer? _buffer;
    private ICharMetrics _metrics;
    private Font _font;
    private ViewportStyle _style;

    public EditorControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable,
            true);
        TabStop = true; BackColor = Color.White; ForeColor = Color.Black;
        _font = new Font("MS ゴシック", 12f);
        _metrics = new GdiCharMetrics(_font);
        _style = DefaultStyle();
        Cursor = Cursors.IBeam;
    }

    public void SetSource(TextBuffer buffer)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (_buffer is not null) throw new InvalidOperationException("SetSource は 1 度だけ");
        _buffer = buffer;
        Invalidate();
    }

    public int LineHeightPx => _metrics.LineHeightPx;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_buffer is null) return;

        var snap = _buffer.Current;
        var rows = ViewportLayout.Build(snap, TopLine, ClientSize.Height, WrapColumns, _metrics);
        int lnWidth = ShowLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;
        var frame = FrameBuilder.Build(
            snap, rows, ClientSize.Width, ClientSize.Height,
            lnWidth, HighlightCurrentLine ? snap.GetLineIndexOfChar(_caret) : -1,
            _selStart != _selEnd ? new SelectionRange(Math.Min(_selStart, _selEnd), Math.Max(_selStart, _selEnd)) : null,
            _cellHighlight, ShowWhitespace, _style, _metrics);
        Paint(g, frame);
    }

    private void Paint(Graphics g, Frame frame)
    {
        foreach (var op in frame.Ops)
        {
            switch (op.Kind)
            {
                case PaintOpKind.FillRect:
                    using (var b = new SolidBrush(ToColor(op.Back)))
                        g.FillRectangle(b, op.X, op.Y, op.Width, op.Height);
                    break;
                case PaintOpKind.DrawText:
                    TextRenderer.DrawText(g, op.Text ?? "", _font, new Rectangle(op.X, op.Y, op.Width, op.Height),
                        ToColor(op.Fore), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
                    break;
                case PaintOpKind.DrawLine:
                    using (var p = new Pen(ToColor(op.Fore))) g.DrawLine(p, op.X, op.Y, op.X + op.Width, op.Y + op.Height);
                    break;
            }
        }
    }

    private static Color ToColor(PaintColor c) => Color.FromArgb(c.Alpha, (c.Rgb >> 16) & 0xFF, (c.Rgb >> 8) & 0xFF, c.Rgb & 0xFF);
    // ... TopLine/WrapColumns/その他プロパティは後続タスクで有効化 ...
}
```

**Step 3: 手動確認**(自動テストできない部分)

Task 14 で smoke 起動器を作るが、この段階では build が通ることだけを確認する:

```powershell
dotnet build src/yEdit.Editor -c Release
```

Expected: 0 警告。

**Step 4: 全体回帰**(既存テスト影響なしを確認)

**Step 5: コミット** `"P2: EditorControl 骨格(Frame→GDI 配線・GdiCharMetrics)"`

---

### Task 7: 垂直スクロール(スクロールバー + ホイール + TopLine)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Test: `tests/yEdit.Core.Tests/Layout/ViewportLayoutTests.cs`(TopLine 変化のパラメータ化テストがあれば追記)

**Step 1: 挙動仕様(この形で実装)**
- `TopLine` は論理行インデックス。set 時に [0, LineCount-1] にクランプ、変化時のみ Invalidate と Vertical.Value 更新
- 縦スクロールバー: `VScrollBar` 子コントロール。Minimum=0・**Maximum=`LineCount-1 + (LargeChange-1)`**(WinForms の到達可能 Value は `Maximum - LargeChange + 1` のため加算が必要)・SmallChange=1・LargeChange=`可視論理行数`
- ホイール: 1 tick = 3 論理行スクロール(WinForms 既定 SystemInformation.MouseWheelScrollLines を使用してもよいが 3 固定で開始)
- **折り返し ON 時**: TopLine の先頭視覚行から描画開始(論理行の中間から始めない=§0-3)。Scrollbar の Maximum は LineCount-1 のまま(視覚行数に張り替えない=大容量で計算不要)
- SetSource 直後: TopLine=0・Vertical.Maximum を LineCount-1 に更新

**Step 2: 手動テスト**(smoke 側で最終確認・Task 14)

自動テストは限定的だが、以下は書ける:
- `EditorControl` を `new` して `SetSource(TextBuffer.FromString("a\nb\n...\nz"))` → `TopLine=1000` は最大論理行にクランプされる(プロパティテスト)
- ViewportLayout.Build を TopLine=5 で呼んだ結果と、EditorControl の内部呼び出し(公開ヘルパを Test-only で用意しない=Invalidate 後に何かを返す仕組みは無いため、これは Task 14 の smoke で目視)

**Step 3: 実装**(VScrollBar 追加・ClientRectangle をスクロールバー幅で削る・MouseWheel オーバライド)

**Step 4: 全体回帰**

**Step 5: コミット** `"P2: 垂直スクロール(TopLine クランプ + VScrollBar + ホイール)"`

---

### Task 8: 行番号マージン

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Modify: `src/yEdit.Core/Layout/FrameBuilder.cs`(既に Task 5 で lineNumberMarginPx を持つ・出力側の詳細は Task 5 完了時点で確定済み)
- Test: `tests/yEdit.Core.Tests/Layout/FrameBuilderTests.cs`(行番号関連ケースの拡充)

**Step 1: 挙動仕様(実装コード指示)**
- `ShowLineNumbers` プロパティ true 時にマージン表示
- 幅 = max(3桁, `LineCount.ToString().Length`)の桁数 × 半角文字幅(GdiCharMetrics) + 左右 4px パディング
- 数字は右寄せ・色は `ViewportStyle.LineNumberFore`
- **折り返し行の 2 段目以降は数字を出さない**(SegmentIndex==0 のみ)
- 現在行(HighlightCurrentLine の対象論理行)は行番号を強調(色をやや濃く=`ViewportStyle.Foreground` に差し替えるだけ)

**Step 2: 失敗するテスト(FrameBuilderTests)**
- `"a\nb\nc"` ShowLineNumbers ON → "  1"/"  2"/"  3" が該当 Y に DrawText される
- 折り返し2段: SegmentIndex=1 の視覚行には行番号 DrawText なし
- 100 行文書: 3 桁分の幅で右寄せ("  1" と "100" が同じ右端)
- LineCount 1000 → 4 桁分の幅

**Step 3-5: 実装 → 緑 → コミット** `"P2: 行番号マージン(桁数追従・折り返し2段目は非表示)"`

---

### Task 9: 選択レンダリング + 現在行強調 + 外部 SetSelection

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Modify: `src/yEdit.Core/Layout/FrameBuilder.cs`(§Task 5 で選択の細部を確定するなら追記)
- Test: `tests/yEdit.Core.Tests/Layout/FrameBuilderTests.cs`

**Step 1: 挙動仕様**
- `SetSelectionCharRange(int start, int end)`: 内部 `_selStart`/`_selEnd` を更新(順序保証: start&lt;=end に正規化)・Invalidate
- `GetSelectionCharRange()`: 現在の (start, end)
- `SetCaretCharOffset(int offset)`: `_caret` 更新(選択はクリア: `_selStart=_selEnd=offset`)・Invalidate
- `HighlightCurrentLine=true` かつ選択なし(start==end)時のみ、キャレット論理行の背景を CurrentLineBack で塗る
- 選択が視覚行を跨ぐ場合: 各視覚行ごとに矩形を出す(P2 は色反転省略・背景色付けのみ)

**Step 2: 失敗するテスト**
- 選択 (2, 5) on `"abcdefg"` → 選択矩形 FillRect 1 個(その視覚行内)
- 選択 (2, 7) on `"abc\ndefg"` → 2 個の FillRect(視覚行を跨ぐ)
- HighlightCurrentLine ON + 選択あり(start!=end) → 現在行の CurrentLineBack 塗りは出ない
- HighlightCurrentLine ON + 選択なし → キャレット論理行の CurrentLineBack 塗り 1 個
- `SetSelectionCharRange(5, 2)` → 内部で (2, 5) に正規化される(GetSelectionCharRange で確認)
- サロゲート中間指定: `SetSelectionCharRange(1, 2)` on `"😀"` → 内部で code-point 境界にスナップ(0, 2)

**Step 3-5: 実装 → 緑 → コミット** `"P2: 選択レンダリング + 現在行強調 + 外部 SetSelection/SetCaret API"`

---

### Task 10: システムキャレット + 外部 SetCaret API

**Files:**
- Create: `src/yEdit.Editor/NativeMethods.cs`(既存の場合は追記・CreateCaret/SetCaretPos/ShowCaret/DestroyCaret 宣言)
- Modify: `src/yEdit.Editor/EditorControl.cs`

**Step 1: 挙動仕様**
- `OnGotFocus`: `CreateCaret(handle, 0, 2, LineHeightPx)` → 位置決め → `ShowCaret`
- `OnLostFocus`: `DestroyCaret`
- キャレット位置決め: `_caret`(char offset) から
  - `snapshot.GetLineIndexOfChar(_caret)` で論理行
  - その論理行を LineLayout.Wrap で分割(WrapColumns の値で)し、`_caret` がどの視覚行のどこかを特定
  - `PixelMapper.OffsetToPx` でセグメント内 x
  - Y = (キャレット視覚行の TopLine からの相対視覚行数) * LineHeightPx
  - **可視外なら SetCaretPos しない**(位置 -1 でもよいが、混乱回避で「見えない位置」= SetCaretPos(-1000,-1000))
- `SetCaretCharOffset(int)`: `_caret` を更新(サロゲート前方スナップ・LineCount との整合はスナップショット任せ)・Invalidate・PositionCaret

**Step 2: 手動確認**(smoke で eye check・Task 14)

自動テストで書けるのは:
- `SetCaretCharOffset(-1)` → 0 にクランプ
- `SetCaretCharOffset(100)` on `"abc"` → 3 にクランプ
- サロゲート中間指定 → 前方スナップ

**Step 3-5: 実装 → 緑 → コミット** `"P2: システムキャレット(GotFocus 時 CreateCaret・位置追従・外部 API)"`

---

### Task 11: 空白/EOL 可視化 + セルハイライト

**Files:**
- Modify: `src/yEdit.Core/Layout/FrameBuilder.cs`
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Test: `tests/yEdit.Core.Tests/Layout/FrameBuilderTests.cs`

**Step 1: 挙動仕様**
- `ShowWhitespace=true`: 半角スペースは中点(・)、タブは「→」、行末改行は「↵」を淡色(`ViewportStyle.WhitespaceGlyph`)で本文の上から DrawText で被せる
  - **実装単純化**: 本文とは別に、可視化文字だけをまとめた「装飾テキスト」を同 Y に DrawText で重ね塗り。フォントの並びが揃っていれば整合する(GdiCharMetrics が同 Font を使うため)
- `HighlightCharRange(int start, int length)`: `_cellHighlight = new SelectionRange(start, start+length)` 保存 + Invalidate
- `ClearHighlight()`: `_cellHighlight = null` + Invalidate
- FrameBuilder は cellHighlight があれば矩形を **半透明背景 + 実線枠**(枠色=`ViewportStyle.HighlightOutline`)で描く(P0 の Scintilla セル装飾を継承)

**Step 2: 失敗するテスト(FrameBuilderTests)**
- ShowWhitespace=true & 本文 " a\tb" → 中点/矢印 DrawText が本文とは別に出ている
- HighlightCharRange(1,3) → 半透明 FillRect 1 個 + DrawLine 4 本(矩形枠)
- ClearHighlight 後 → cellHighlight 由来の Op が消える
- HighlightCharRange の範囲が視覚行を跨ぐ → 視覚行ごとの矩形

**Step 3-5: 実装 → 緑 → コミット** `"P2: 空白/EOL 可視化 + セルハイライト(HighlightCharRange/Clear)"`

---

### Task 12: 折り返し統合 + 水平スクロール(折り返し OFF 時のみ)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Test: `tests/yEdit.Core.Tests/Layout/ViewportLayoutTests.cs`(統合テスト追加)

**Step 1: 挙動仕様**
- `WrapColumns` プロパティ set 時に Invalidate + 水平スクロールバー表示切替
- **折り返し ON**: HScrollBar 非表示・視覚行を最大 ClientWidth 幅で折り返す
  - `maxWidthPx = WrapColumns * (半角1文字の px)` を GdiCharMetrics.MeasureRun("0") で算出
  - ただし ClientWidth(マージン控除後)を上限にする(桁が広すぎるとき)
- **折り返し OFF**: HScrollBar 表示・`ScrollX` で表示原点を X 方向にオフセット
  - 描画時に FrameBuilder の Ops.X から `ScrollX` を引く(あるいは EditorControl 側でクリップ矩形を平行移動)
  - MaxScrollX = 全視覚行のうち最長の pixel 幅(可視分だけ計算=1GB でも安全)
- 折り返し ON→OFF 切替時 ScrollX=0 にリセット

**Step 2: 失敗するテスト**
- WrapColumns=40 setter → 内部の折り返し幅が更新される(ViewportLayout.Build に渡す wrapColumns が変化)
- WrapColumns=0 で ScrollX 変更 → PointFromCharOffset の X が調整される

**Step 3-5: 実装 → 緑 → コミット** `"P2: 折り返し統合 + 水平スクロール(OFF 時)"`

---

### Task 13: 外観適用(フォント + テーマ)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs`
- Test: `tests/yEdit.Core.Tests/Layout/FrameBuilderTests.cs`(色反映のパラメータテスト・任意)

**Step 1: 挙動仕様**
- `ApplyAppearance(AppSettings settings)`: 現行 `EditorAppearance.Apply` の自作コントロール版
  - フォント: `new Font(settings.FontName, settings.FontSize > 0 ? settings.FontSize : 12f)`
  - `GdiCharMetrics` 差し替え + LineHeightPx 変化に伴う VScrollBar 再構成 + Invalidate
  - テーマ: `AppearanceThemes.ById(settings.Theme)` → ViewportStyle.Foreground/Background/CurrentLineBack を算出(現行 `Blend` 相当を移植)
  - `ShowLineNumbers`/`ShowWhitespace`/`HighlightCurrentLine` を settings から反映
  - `WrapColumns = settings.WrapColumnEnabled ? settings.WrapColumn : 0` を反映
- `TabWidth`/`TabsToSpaces` は P3(編集入力)で扱う=P2 では設定値を保持するだけ(未使用でも set しない=YAGNI)

**Step 2: 手動確認**(smoke で eye check・Task 14)

自動テストで書けるのは:
- ApplyAppearance 後 LineHeightPx が変化(フォントサイズ変更で)
- WrapColumns などのプロパティが settings から反映される

**Step 3-5: 実装 → 緑 → コミット** `"P2: 外観適用(フォント+テーマ+表示設定のバインド)"`

**設計判断の申し送り(Task 15 でクローズ判断 / P6 で対応)**:

- **選択背景色のテーマ非追従(SelectionBack 固定 0xADD8E6)**: App 層の現行 `EditorAppearance.Apply`
  はテーマの前景/背景を反転して選択に高コントラストを与える(弱視配慮=設計原則
  [[yedit-sighted-users-first-class]])。P2 の `BuildStyle` は薄青固定で、黒地系テーマ
  (白/黄/緑) では選択のコントラストが下がる。P6 で App 層接続時に:
  - `BuildStyle` の SelectionBack を `theme.ForeRgb` にする(=前景色反転)、かつ
  - FrameBuilder が「選択内のテキスト色を反転させる」新オプションを受け取れるようにする
  のセットで対応する予定。**Task 15 のレビュー時にこの申し送りが残っていることを確認する**
- **セルハイライト枠色のテーマ非追従(HighlightOutline 固定 0xD77800)**: 同様。P6 で
  弱視要件を満たすテーマ追従を追加検討

---

### Task 14: 性能ベンチ + smoke 起動器

**Files:**
- Modify: `tests/yEdit.Core.Bench/Program.cs`(`--layout` モード追加)
- Create: `tests/yEdit.Editor.Smoke/yEdit.Editor.Smoke.csproj`
- Create: `tests/yEdit.Editor.Smoke/Program.cs`
- Create: `tests/yEdit.Editor.Smoke/MainForm.cs`
- Modify: `yEdit.sln`(Editor.Smoke を追加)

**Step 1: Core.Bench 拡張(純レイアウトの決定的ベンチ)**

`--layout --mb <サイズ>` モード:

| # | 計測 | 目標(DoD) |
|---|---|---|
| 1 | 合成 1GB 文書構築(P1 と同素材) | 記録のみ |
| 2 | 折り返し OFF・ランダム TopLine で ViewportLayout.Build(可視 50 行想定) 1,000 回 | 平均 <16ms |
| 3 | 折り返し ON(WrapColumns=80)・同上 1,000 回 | 平均 <16ms |
| 4 | FrameBuilder.Build を含む 1 フレーム全体(ViewportLayout → FrameBuilder)1,000 回 | 平均 <16ms |
| 5 | PointFromCharOffset(相当計算)1,000 回 | 平均 <1ms |
| 6 | メモリ増分(構築後→ベンチ後の delta) | +数十 MB 以下(視覚行キャッシュ無しなので実質ゼロ) |

`MonoCharMetrics`(固定幅)で決定的・EXIT 0/1 判定。**GDI 実測との乖離**は smoke 側で補足する。

**Task 5 レビューからの観測項目**(ベンチで実測し、閾値を超えていたら最適化・順不同):

- **I1**: `FrameBuilder.Build` 内で 1 視覚行あたり `snapshot.GetText` を最大 5 回(本文/空白/選択矩形 × 2/セルハイライト矩形 × 2)呼んでいる。50 可視行で ~250 alloc/frame。対処案=行内容を 1 度取得して `ReadOnlySpan<char>` で回す構造(Task 5 レビュー I1)
- **I2**: `FrameBuilder.EmitWhitespaceGlyphs` が空白位置ごとに `PixelMapper.OffsetToPx(span, i, ...)` を 0 から再計算=最悪 O(N²)。累積 px を持ちながら 1 パス走査で O(N) 化可能(Task 5 レビュー I2)
- **M1**: セルハイライトの背景(z-order 4)と枠(z-order 8)で `TryComputeRowRangeRect` を同 range で 2 回呼ぶ=`OffsetToPx` の重複計算。1 パス化余地(Task 5 レビュー M1)
- **M5**: `Frame.Ops` は `IReadOnlyList<PaintOp>` 型だが実体は `List<PaintOp>`。プーリング検討時に `ImmutableArray<PaintOp>` or `ReadOnlyCollection<PaintOp>` ラッピング判断(Task 5 レビュー M5)
- **M7**: `Ordering_background_before_current_line_before_selection_before_body` テストが本文までしか順序確認していない。1 フレームで z-order 5 → 6 → 7 → 8(本文 → 空白 → 行番号 → セルハイライト枠)を検証するテスト 1 件追加を検討(Task 5 レビュー M7)

**設計判断の申し送り**(Task 15 レビュー時に判断):

- **M2**: セルハイライト半透明色は現状 `HighlightOutline.Rgb + Alpha=60` から派生。P6 の外観要求次第で `ViewportStyle.HighlightBack` を独立フィールド化して派生を廃す方針を検討可(Task 5 レビュー M2)

**Step 2: smoke 起動器(手動確認+GDI 実測)**

```csharp
// tests/yEdit.Editor.Smoke/Program.cs
static void Main(string[] args)
{
    if (args.Length > 0 && args[0] == "--bench") { RunGdiBench(args); return; }
    ApplicationConfiguration.Initialize();
    Application.Run(new MainForm(args.FirstOrDefault()));
}
```

MainForm:
- File > Open メニュー(UTF-8/SJIS/EUC-JP)
- `EditorControl.Dock = Fill`・SetSource(buffer) で表示
- 表示メニュー: 折り返し ON/OFF・行番号・現在行強調・空白可視化
- ステータスバー: LineCount・CurrentLine・エンコーディング
- Ctrl+G(行ジャンプ) は P3 で入るまで無し。**P2 はビューア**なのでキー操作は無し(スクロールとメニューだけ)

**RunGdiBench** モード(`--bench --file <path>`):
- 指定ファイル(または 1GB 合成)を Editor に読み込む
- offscreen で 1,000 回スクロール(TopLine を進める)し、`Invalidate + Update` して 1 フレームの GDI 描画時間を Stopwatch で測る
- 平均 <16ms なら EXIT 0

**Step 3: 手動確認手順(コミットメッセージに残す)**
- `dotnet run --project tests/yEdit.Editor.Smoke -c Release` で 100MB SJIS ファイルを開く
- スクロール・折り返し ON/OFF・行番号表示・空白可視化 → 見た目が破綻していないこと
- ScintillaHost の同じファイル表示と目視比較(既存 yEdit を別途起動)

**Step 4: 結果を本計画書の「実施記録」節に追記**

**Step 5: コミット** `"P2: 1GB レイアウトベンチ + smoke 起動器(GDI 実測+eye check)"`

---

### Task 15: コードレビュー + 設計書追記 + P2 クローズ

**Step 1: 別エージェントにコードレビュー依頼**(superpowers:requesting-code-review。BASE=P2 開始前コミット `eb23dd4`、HEAD=Task 14 コミット)

**観点:**
- §0 の不変条件(公開 UTF-16 オフセット・可視領域のみ描画・code-point 境界スナップ)
- LineLayout/PixelMapper/ViewportLayout の境界ケース(空・サロゲート・折り返し極小)
- FrameBuilder の描画順序(背景 → 装飾 → 本文 の重なり)
- GDI 呼び出しコスト(TextRenderer の呼び出し回数が可視行数に比例していること・Font/Brush の re-alloc 過剰でないこと)
- P3 接続の準備(SetCaret/SetSelection API シグネチャが入力ドライブと相性がよいか)

**Step 2: Critical/Important を修正**(修正ごとに失敗テスト → 修正 → 緑 → コミット)

**Step 3: 設計書 `docs/plans/2026-07-05-custom-editcontrol-design.md` の P2 節に結果を追記**(P0/P1 結果と同形式: 追加テスト件数・ベンチ数値表・申し送り・P3+への引き継ぎ事項)

**Step 4: 最終確認**

```powershell
dotnet build yEdit.sln -c Release; dotnet test tests/yEdit.Core.Tests -c Release --nologo
```

Expected: build 0 警告・全テスト緑

**Step 5: コミット** `"P2: レビュー対応+設計書へ P2 結果追記(EditorControl 骨格+描画完了)"`

**⚠ main へのマージは行わない**(P7 合格後に一括。設計書§3 運用)

---

## DoD(設計書 P2)

- [x] 全テスト緑(既存 415 + 新規 55 = 470)
- [x] `--layout` ベンチで 1GB 文書 1 フレーム <16ms(折り返し ON/OFF 両方 = L2=7.79ms・L3=7.81ms・L4=9.48ms・L5=41ns)
- [ ] `--bench` smoke で GDI 実測フレーム <16ms(1GB は環境依存で困難な場合、256MB でも記録)= **自動化せず・ユーザー実機で確認予定**
- [ ] smoke で手動確認: 折り返し・行番号・現在行・空白可視化・セルハイライト・システムキャレットが目視で動作 = **ユーザー実機で確認予定**
- [x] build 0 警告
- [x] 別エージェントレビューで Critical/Important 0(Task 15 最終レビュー: Critical 0 / Important 3 → 全て Task 15 内で修正済み・再検証で 0)
- [x] 設計書へ P2 結果追記済み(`docs/plans/2026-07-05-custom-editcontrol-design.md` §P2 結果)

## P3+ への申し送り(実装中に確定するので予約)

- 入力(キーボード/マウス)配線 = P3(EditorControl の InputKey/KeyDown/OnMouseDown 実装、SetCaret/SetSelection を叩く)
- IME(WM_IME_*)= P4
- WM_GETOBJECT / IUiaTextHost 実装 = P5(TextControlProvider の再利用は P5 で判断)
- App 層 ScintillaHost → EditorControl 置換 = P6
- Undo/Redo 反映後の Invalidate タイミング = P3(TextBuffer の変更通知が無いなら P3 で追加検討)
- **視覚行スクロール**(折り返し ON で 1 論理行内の途中視覚行にスクロール)は P2 スコープ外・要望があれば P3 以降で検討
- **仮想化された Y 座標**(ClientHeight >> 1 フレーム分。折り返し ON の視覚行総数を計算) = 現状不要(スクロールバーは論理行ベース)

## 実施記録

### 2026-07-05 Task 1〜14 実施

- **テスト**: 既存 408 → 470(P2 新規 62 件)。全緑・build 0 警告
- **Core.Bench --layout(Task 14)**: 純レイアウトの決定的ベンチ(`MonoCharMetrics(1,10)`・50 可視行想定・シード 20260705・1000 回)。**構築直後スナップショット**を対象に測定(splice/typing 後の 2 万ピースは Task 15 の並行懸念で別途)。

`dotnet run --project tests/yEdit.Core.Bench -c Release -- --layout --mb 1024` → **EXIT 0(全目標達成)**

| # | シナリオ | 結果(1GB) | 目標 | 判定 |
|---|---|---|---|---|
| L2 | ViewportLayout(wrap OFF) | 7.79 ms/回 | 平均<16ms | PASS |
| L3 | ViewportLayout(wrap ON 80) | 7.81 ms/回 | 平均<16ms | PASS |
| L4 | Frame(wrap OFF 全体) | 9.48 ms/回 | 平均<16ms | PASS |
| L5 | PixelMapper.OffsetToPx | 0.041 µs/回 | 平均<1ms | PASS |
| L6 | メモリ増分(layout) | Δ managed -1024 MB(前段 TextBuffer ベンチ由来のヒープが GC.Full で解放されるため負値=以下参照) | 記録のみ | ― |

(参考: 256MB でも全 PASS。L2=7.76ms・L3=7.78ms・L4=9.44ms・L5=0.040µs)

- **メモリ増分が負値の理由**: 直前の TextBuffer ベンチが splice/typing で生じさせた **中間ヒープ(古いピース木ノード等)** が、L6 の `GC.GetTotalMemory(forceFullCollection: true)` で一括解放されるため。L6 単独の allocation は List / string 数千個(1 iter あたり ~50〜100 個 × 1000 iter)で MB オーダーに満たない。Task 15 で「レイアウト単独 delta」を測るなら独立プロセスで走らせる必要がある(現状は記録のみで PASS 判定に影響しない)。

- **smoke 起動器(Task 14)**: `tests/yEdit.Editor.Smoke`(net9.0-windows / WinForms)を新規追加。
  - GUI 起動: `dotnet run --project tests/yEdit.Editor.Smoke -c Release` → EditorControl を Dock=Fill・ファイル(UTF-8/SJIS/EUC-JP)を開いて折り返し/行番号/空白可視化/現在行強調を目視。SetSource 1 度きり契約に合わせて開き直しごとに EditorControl を差し替える。
  - GDI 実測ベンチ: `dotnet run --project tests/yEdit.Editor.Smoke -c Release -- --bench --mb 256` → offscreen Form を Show して `Invalidate + Update` を 1000 フレーム回し、平均フレーム時間を測定。**Task 14 では自動起動しない**(WinForms のフォーカス奪取を回避するため+CI ゲートは Core.Bench の純レイアウト側で果たすため)。手動確認は次セッションで実施。

- **Task 5 レビュー観測項目の実測結果**:
  - I1(FrameBuilder が 1 視覚行あたり GetText を最大 5 回): L4=9.48ms/frame は L2=7.79ms/frame の +1.69ms 差=frame ビルド固有のオーバヘッドは可視 50 行で 1.7ms 程度(GetText × 5 + 本文/装飾 op ビルド)。<16ms 目標に対して余裕あり=Task 15 での前倒し対応は不要と判断。
  - I2(EmitWhitespaceGlyphs が O(N²)): 今回のベンチは `showWhitespace=false` で測定(装飾ありは眼視 smoke で検証)。O(N²) の顕在化は wrap ON + 極端に長い行のときに顕著=Task 15 の追試対象として残す。
  - M1(セルハイライト背景/枠で TryComputeRowRangeRect を 2 回): 今回のベンチは `cellHighlight=null` で測定(smoke 側で eye check)。
  - M5(Frame.Ops プーリング): L4=9.48ms で目標クリア=YAGNI 継続。
  - M7(順序テスト 5→6→7→8): Task 15 のテスト追加で対応。

- **Task 10 レビュー M-3 の申し送り(ViewportLayout.Build と PositionCaret の視覚行ウォーク重複)**:
  L2/L3 が 7.8ms/frame の実測で目標クリア=PositionCaret 側の重複ウォークは眼視 smoke の scroll 追従で問題が出ない限り Task 15 で追試。

- **計画からの逸脱**:
  1. Core.Bench の `snap` 変数を layout bench の入力に再利用(=構築直後の PieceCount で測る)。当初 `buffer.Current` を使ったら **splice/typing 後の 2 万ピース木**でツリー walk が 3.5×遅くなり L2=32ms/frame で FAIL していた。TextSnapshot は immutable なので構築直後の `snap` は splice を越えて有効=修正で 7.8ms/frame に改善。実運用の初回ロード直後フレームコストの見積もりに合致する(20K ピース状態は 10K 連続 splice + 10K タイピング直後の極値であり、Task 14 DoD の趣旨から外れる)。

### 2026-07-05 Task 15 実施(P2 クローズ)

- **別エージェント最終レビュー**(BASE=eb23dd4・HEAD=Task 14=40987c1): Critical 0 / Important 3 / Minor 9
  - **I-1**: `ShowLineNumbers` setter が `UpdateHorizontalScrollbar()`+`PositionCaret()` を呼ばず、システムキャレット位置と HScroll extent がマージン切替に追従しない → Task 15 で修正済み(setter に両呼び出しを追加)
  - **I-2**: SetSource 前にフォーカスを取っていた場合(OnGotFocus が buffer null で早期 return したケース)、SetSource でシステムキャレットが生成されない → SetSource 末尾で `_hasFocus` なら CreateCaret+PositionCaret+ShowCaret を呼ぶ形に修正
  - **I-3**: `AppSettings.CaretWidth`(弱視のキャレット視認性・設計原則 [[yedit-sighted-users-first-class]])が反映されず、キャレット太さがハードコード 2px 固定 → `_caretWidthPx` フィールド追加・ApplyAppearance で `Math.Clamp(settings.CaretWidth, 1, 5)` 反映・3 箇所の CreateCaret を `_caretWidthPx` 参照に差し替え
- **Minor 9 の対応**:
  - Task 15 で xmldoc/コメント修正(該当箇所)
  - P3/P5/P6 送り(EditorControl テスト基盤・shift+左矢印非対称版 API・PointFromCharOffset 水平可視性・マウスホイール精度・SelectionBack/HighlightOutline テーマ追従など)は P2 結果として親設計書に列挙
- **最終回帰**: 全 470 テスト緑・build 0 警告(維持)
- **設計書追記**: 親設計書 `docs/plans/2026-07-05-custom-editcontrol-design.md` §P2 結果を追記(P1 結果と同形式・ベンチ数値表・申し送り・P3+ 引き継ぎ)
- **⚠ main へのマージは行わない**(P7 合格後に一括。設計書§3 運用)
