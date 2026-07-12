# P1: TextBuffer(UTF-8永続ピーステーブル)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 自作エディットコントロールの中核となるテキストバッファ(UTF-8永続ピーステーブル)を `yEdit.Core` に純ロジックとして実装し、ファズ無差異+1GBベンチ目標(編集<1ms・スナップショットO(1)・行変換O(log n))を達成する。

**Architecture:** 不変UTF-8バイトチャンク(原文+追記)の上に、各ノードが (byteLen, charLen, lineBreaks) 統計を持つ**永続AVL木**(join-based・経路コピー)を載せる。外部APIはすべてUTF-16文字オフセット。スナップショット=ルート参照コピーO(1)。Undo=編集前ルート保持+オペレーションログでcoalescing。大チャンクは64KB格子の累積統計サンプリングで内部変換をO(log n + 64KB走査)に局所化。設計書: `docs/plans/2026-07-05-custom-editcontrol-design.md` §2-1。

**Tech Stack:** C# / .NET 9 / xUnit 2.9.2(既存 `tests/yEdit.Core.Tests`)/ ベンチは専用コンソールアプリ

**前提:**
- 作業は worktree `<repo>\.worktrees\custom-editcontrol-design`(ブランチ `feature/custom-editcontrol-design`)
- **mainには一切触れない**(全フェーズを本ブランチに閉じ、P7合格後に一括マージ。設計書§3運用)
- 既存テスト289件は全タスクを通して緑を維持(P1は既存コードに触れない純追加)
- 各タスク完了ごとにコミット。全タスク後に別エージェントレビュー(Task 14)

---

## 0. 設計固定事項(全タスク共通の不変条件)

実装全体で守る。レビュー時のチェック観点でもある。

1. **公開オフセットはUTF-16コード単位(int)**。バイトオフセット・UTF-8は内部に完全に閉じる。文書上限は `int.MaxValue` バイト(≒2GB)。内部のバイト集計は `long`
2. **チャンクは不変**。追記ブロックは「公開済み範囲は以後不変」(配列の再確保・上書き禁止。ブロック満杯なら新ブロック)
3. **ピース境界は常にUTF-8コード点境界**(=サロゲートペアも分断しない。4バイト文字はアトミック)。公開APIの位置引数がサロゲートペア中間を指した場合は**前方(低い方)へスナップ**(P0の `MoveCaret` スナップと同方針)
4. **CRLFはピース境界を跨いでよい**。改行数の整合はモノイド結合(§1)で吸収する(分断禁止方式は編集操作が複雑化するため採らない)
5. **木は永続**(ノードのフィールドはすべて readonly・編集は経路コピー)。スナップショットはルート参照を持つだけ
6. **チャンク内容は常に妥当なUTF-8**(構築時に Sanitize 済み)。走査コードは妥当性を仮定してよい
7. 挿入文字列に孤立サロゲートが含まれる場合は UTF-8 変換時に U+FFFD 置換(`Encoding.UTF8` の既定フォールバック)
8. 名前空間は `yEdit.Core.Buffers`(フォルダは `src/yEdit.Core/Buffer/`。`System.Buffer` との型名衝突回避のため複数形)。公開型は `TextBuffer` / `TextSnapshot` / `TextBufferBuilder` / `UndoResult` のみ。他はすべて internal

## 1. 改行セマンティクス(最重要・全実装の基準)

- **改行(break)** = `\n`(LF)/ `\r`(単独CR)/ `\r\n`(CRLF=**1つ**)。`LineCount = breaks + 1`
- **breakの終端文字** = LF(CRLF含む)または単独CR。行 i (0始まり) の開始位置 = i 番目のbreakの終端文字の直後
- **区分(ピース)ローカル統計** `PieceStats`:
  - `Breaks` = 区分内のLF数 + 「区分内でLFが直後に続かないCR」の数(**末尾CRは単独扱いで数える**)
  - `FirstIsLf` = 先頭文字がLF(空なら false)、`LastIsCr` = 末尾文字がCR(空なら false)
- **モノイド結合**(これが正しさの核):

```csharp
combined.Breaks = a.Breaks + b.Breaks - (a.LastIsCr && b.FirstIsLf ? 1 : 0);
```

  aの末尾CR(単独として計上済み)とbの先頭LF(単独として計上済み)が合わさるとCRLF=1つなので1引く。結合は結合律を満たす(部分木統計の事前計算が可能)

- 検算例(テストに必ず含める):

| 連結 | 各Breaks | 結合後 | 全文の期待値 |
|---|---|---|---|
| `"a\r"` + `"\nb"` | 1, 1 | **1** | `a\r\nb` → 1 |
| `"\r"` + `"\r\n"` | 1, 1 | **2** | `\r\r\n` → CR+CRLF = 2 |
| `"\n"` + `"\n"` | 1, 1 | **2** | `\n\n` → 2 |
| `"x"` + `"\r"` + `"\n"` | 0,1,1 | **1** | 結合順によらず同値(結合律) |

- **文字→行**: `GetLineIndexOfChar(pos)` = 接頭辞 `[0,pos)` の PieceStats.Breaks、ただし「pos-1がCR かつ posがLF」なら−1(CRLFの途中=breakはまだ完了していない)
- **バイト走査の高速式**(ASCII の CR/LF は UTF-8 多バイト列に現れないので安全):
  - UTF-16長 = (継続バイト `10xxxxxx` 以外のバイト数) + (`0xF0..0xF4` 先頭バイト数)  ※4バイト文字=サロゲートペア2単位

## 2. 公開APIサーフェス(P2〜P6の呼び出し側契約)

```csharp
public sealed class TextBuffer
{
    public static TextBuffer FromString(string text);
    public TextSnapshot Current { get; }
    public bool Modified { get; }                 // 現在ルート != 保存時ルート(参照比較)
    public void Insert(int pos, string text);
    public void Delete(int pos, int length);
    public void Replace(int pos, int length, string text); // 1 Undo単位のsplice
    public bool CanUndo { get; }  public bool CanRedo { get; }
    public UndoResult? Undo();    public UndoResult? Redo();  // null=不可
    public void BreakUndoCoalescing();            // キャレット移動・保存時にApp側が呼ぶ
    public void MarkSaved();                      // SavePoint
    public void ClearUndo();                      // EmptyUndoBuffer相当
}

public readonly record struct UndoResult(int CaretPos); // 復元後の推奨キャレット位置

public sealed class TextSnapshot
{
    public int CharLength { get; }
    public int LineCount { get; }
    public string GetText(int start, int length);
    public char GetChar(int pos);
    public int GetLineStart(int line);
    public int GetLineEnd(int line, bool includeBreak);
    public int GetLineIndexOfChar(int pos);
    public TextReader CreateReader();             // Markdig/regex行適用向けストリーム
    public void WriteTo(Stream stream);           // UTF-8チャンク直書き(全文string化しない)
    internal int PieceCount { get; }              // 診断(テスト・ベンチ)
}

public sealed class TextBufferBuilder                     // ストリーム読込用
{
    public void Add(ReadOnlySpan<byte> utf8Bytes);        // チャンク境界のコード点分断はビルダーが繰越し
    public bool HadReplacement { get; }                   // 不正UTF-8をU+FFFD置換したか(既存の置換警告ノウハウに接続)
    public TextBuffer Build();
}
```

行末系のP5対応: `LineStartOf`=GetLineStart(GetLineIndexOfChar(pos))、`LineEndNoBreakOf`=GetLineEnd(line, includeBreak:false)。単語境界(WordStart/WordEnd)はバッファの責務外(P5でスナップショット上に実装)。EOL一括変換(ConvertEols)はP6で Builder 再構築として実装(YAGNI)。

## 3. 共通コマンド

```powershell
# 対象テストのみ(高速ループ用)
dotnet test tests/yEdit.Core.Tests -c Release --nologo --filter FullyQualifiedName~Buffers
# 全体回帰(各タスク末尾で実行)
dotnet build yEdit.sln -c Release; dotnet test tests/yEdit.Core.Tests -c Release --nologo
```

Expected(全体回帰): build 0警告 / 既存289件+新規が全緑。

---

### Task 1: 足場+Utf8Scan(バイト列統計スキャナ)

**Files:**
- Modify: `src/yEdit.Core/yEdit.Core.csproj`(InternalsVisibleTo追加)
- Create: `src/yEdit.Core/Buffer/Utf8Scan.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/Utf8ScanTests.cs`

**Step 1: csproj に InternalsVisibleTo を追加**(internal型のツリー/チャンクを直接テストするため)

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="yEdit.Core.Tests" />
  </ItemGroup>
```

**Step 2: 失敗するテストを書く**

`Utf8Scan.Stats(ReadOnlySpan<byte>)` が `(int CharLen, int Breaks, bool FirstIsLf, bool LastIsCr)` を返す仕様。テスト名は既存規約(英語アンダースコア)で:

```csharp
using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.Tests.Buffers;

public class Utf8ScanTests
{
    private static (int CharLen, int Breaks, bool FirstIsLf, bool LastIsCr) S(string s)
        => Utf8Scan.Stats(Encoding.UTF8.GetBytes(s));

    [Fact] public void Empty_is_all_zero() => Assert.Equal((0, 0, false, false), S(""));
    [Fact] public void Ascii_counts_utf16_units() => Assert.Equal(3, S("abc").CharLen);
    [Fact] public void Cjk_3byte_counts_one_unit() => Assert.Equal(2, S("あ亜").CharLen);
    [Fact] public void Emoji_4byte_counts_two_units() => Assert.Equal(2, S("😀").CharLen);
    [Fact] public void Lf_cr_crlf_each_count_one_break()
    { Assert.Equal(1, S("a\nb").Breaks); Assert.Equal(1, S("a\rb").Breaks); Assert.Equal(1, S("a\r\nb").Breaks); }
    [Fact] public void Trailing_cr_counts_as_lone_break() => Assert.Equal(1, S("ab\r").Breaks);
    [Fact] public void Cr_cr_lf_is_two_breaks() => Assert.Equal(2, S("\r\r\n").Breaks);
    [Fact] public void First_lf_last_cr_flags()
    { var t = S("\nabc\r"); Assert.True(t.FirstIsLf); Assert.True(t.LastIsCr); }
    [Fact] public void Mixed_document_matches_naive()
    {
        const string s = "ABC abc 123\r\nあいう\r😀えお\n\nx\r";
        var t = S(s);
        Assert.Equal(s.Length, t.CharLen);       // UTF-16単位=string.Length
        Assert.Equal(5, t.Breaks);               // \r\n, \r, \n, \n, 末尾\r
    }
}
```

**Step 2.5: 実行して失敗確認**

Run: `dotnet test tests/yEdit.Core.Tests -c Release --nologo --filter FullyQualifiedName~Utf8Scan`
Expected: **FAIL**(CS0246: Utf8Scan が存在しない)

**Step 3: 最小実装**

```csharp
namespace yEdit.Core.Buffers;

/// <summary>妥当なUTF-8バイト列の統計走査(§1の改行セマンティクス)。CR/LFはASCIIなので多バイト列と衝突しない。</summary>
internal static class Utf8Scan
{
    public static (int CharLen, int Breaks, bool FirstIsLf, bool LastIsCr) Stats(ReadOnlySpan<byte> s)
    {
        int chars = 0, breaks = 0;
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            if ((b & 0xC0) != 0x80) chars++;          // 継続バイト以外=コード点先頭
            if (b >= 0xF0) chars++;                   // 4バイト文字はサロゲートペア=+1
            if (b == (byte)'\n') breaks++;
            else if (b == (byte)'\r' && (i + 1 >= s.Length || s[i + 1] != (byte)'\n')) breaks++;
        }
        bool firstLf = s.Length > 0 && s[0] == (byte)'\n';
        bool lastCr = s.Length > 0 && s[^1] == (byte)'\r';
        return (chars, breaks, firstLf, lastCr);
    }
}
```

**Step 4: 緑確認+全体回帰**(§3コマンド)

**Step 5: コミット**

```bash
git add src/yEdit.Core tests/yEdit.Core.Tests
git commit -m "P1: Utf8Scan(UTF-8統計走査)とBuffers名前空間の足場"
```

---

### Task 2: Utf8Sanitizer(不正UTF-8の検証・置換)

**Files:**
- Create: `src/yEdit.Core/Buffer/Utf8Sanitizer.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/Utf8SanitizerTests.cs`

**Step 1: 失敗するテスト**

仕様: `Sanitize(ReadOnlyMemory<byte>) → (ReadOnlyMemory<byte> Clean, bool Replaced)`。妥当なら**入力をそのまま返す(ゼロコピー・Replaced=false)**、不正なら U+FFFD 置換した新バイト列(Replaced=true)。

テストケース:
- 妥当ASCII/CJK/絵文字 → 同一参照(`MemoryMarshal`不要、`Assert.True(input.Span == result.Clean.Span)` は不可なので `Replaced==false` と内容一致で判定)
- 孤立継続バイト `0x80` → `EF BF BD` に置換・Replaced=true
- 切断された3バイト文字(`E3 81` で終端)→ 置換
- 過長エンコーディング(`C0 AF`)→ 置換(.NETの検証が拒否することの確認)

**Step 2: 失敗確認 → Step 3: 実装**

```csharp
internal static class Utf8Sanitizer
{
    public static (ReadOnlyMemory<byte> Clean, bool Replaced) Sanitize(ReadOnlyMemory<byte> input)
    {
        if (System.Text.Unicode.Utf8.IsValid(input.Span)) return (input, false);
        // 稀ケースなので decode→re-encode で十分(チャンク単位=最大4MB)
        string s = Encoding.UTF8.GetString(input.Span);   // 既定で U+FFFD 置換
        return (Encoding.UTF8.GetBytes(s), true);
    }
}
```

**Step 4: 緑+全体回帰 → Step 5: コミット** `"P1: Utf8Sanitizer(不正UTF-8のU+FFFD置換・妥当時ゼロコピー)"`

---

### Task 3: TextChunk(不変チャンク+64KBサンプリング照会)

**Files:**
- Create: `src/yEdit.Core/Buffer/TextChunk.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/TextChunkTests.cs`

チャンク=不変バイト列+構築時に作る**64KB格子の累積統計表**。ピース分割・行検索・文字⇔バイト変換の走査を格子1マス(≤64KB)に局所化する。**格子幅はテスト用にctor注入可(既定 64*1024)**——テストでは幅8などにして格子跨ぎを必ず踏ませる。

**API(internal):**

```csharp
internal sealed class TextChunk
{
    public TextChunk(ReadOnlyMemory<byte> bytes, int gridBytes = 64 * 1024);
    public ReadOnlySpan<byte> Span { get; }
    public int ByteLength { get; }

    // [byteStart, byteStart+byteLen) の PieceStats(§1のピースローカル統計)
    public PieceStats StatsOfRange(int byteStart, int byteLen);
    // 範囲先頭から charDelta 文字(UTF-16単位)進んだバイトオフセット(範囲内保証・サロゲート中間は呼び出し側でスナップ済み)
    public int CharToByte(int byteStart, int byteLen, int charDelta);
    // 範囲内 k 番目(1始まり)のbreak終端文字の文字オフセット(ピースローカル semantics=末尾CRも終端扱い)
    public int NthBreakEndChar(int byteStart, int byteLen, int k);
    // 範囲を string へデコード
    public string GetString(int byteStart, int byteLen);
}
```

**格子表の設計**(実装の指針・コメントに残す):
- エントリ: `(int ByteOff, int CharOff, int BreaksTo)`。ByteOff は格子点をコード点境界へ**前方スナップ**した位置。CharOff/BreaksTo はチャンク先頭からの累積
- `BreaksTo(x)` の規約: `[0,x)` 内の LF数+「LFが直後に続かないCR」数、ただし**x-1のCRは(次を見ずに)単独扱いで数える**
- 範囲統計の導出式(テストで検証する):
  `Breaks[a,b) = BreaksTo(b) − BreaksTo(a) + (a>0 && bytes[a-1]=='\r' && bytes[a]=='\n' ? 1 : 0)`
  (検算: `"\r\n"` の a=1,b=2 → 1−1+1=1=区分`"\n"`単体のBreaks ✓)
- 照会はすべて「最寄り格子点まで累積表・残り≤64KBを線形走査」

**Step 1: 失敗するテスト**(ナイーブ実装との突合を基本とする)

- `StatsOfRange` 全域 = `Utf8Scan.Stats` と一致(ASCII/CJK/絵文字/CRLF混在の1KB文書・gridBytes=8)
- `StatsOfRange` 部分範囲: ランダム100範囲を `Utf8Scan.Stats(bytes[a..b])` と突合(シード固定 `new Random(20260705)`)
- CRLF跨ぎ範囲: `"x\r\ny"` の `[a=2,b=4)`(`\ny`)は Breaks=1、`[0,2)`(`x\r`)は Breaks=1
- `CharToByte`: 絵文字直後・CJK直後など10ケースを `Encoding.UTF8.GetByteCount(s[..k])` と突合
- `NthBreakEndChar`: `"a\nb\r\nc\rd"` で k=1→1(`\n`), k=2→4(CRLFのLF), k=3→6(単独`\r`)
- 格子点スナップ: 格子幅3で3バイト文字を跨がせて `CharToByte` が壊れないこと

**Step 2: 失敗確認 → Step 3: 実装 → Step 4: 緑+全体回帰**

**Step 5: コミット** `"P1: TextChunk(不変チャンク+64KB格子サンプリングの範囲統計/変換/行検索)"`

※ `PieceStats` 構造体は Task 3 では TextChunk 内部で必要になるため、このタスクで `Piece.cs` に先行定義してよい(Combineは Task 4 で)。

---

### Task 4: Piece+PieceStatsモノイド

**Files:**
- Create/Modify: `src/yEdit.Core/Buffer/Piece.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/PieceStatsTests.cs`

**Step 1: 失敗するテスト** — §1の検算表4行をそのままテスト化+結合律のランダム検証:

```csharp
[Fact]
public void Combine_is_associative_over_random_splits()
{
    const string s = "ab\r\ncd\r\r\n\n\ref😀\nぁ\r";
    var rnd = new Random(42);
    for (int t = 0; t < 200; t++)
    {
        int i = rnd.Next(s.Length + 1), j = rnd.Next(s.Length + 1);
        if (i > j) (i, j) = (j, i);
        var a = StatsOf(s[..i]); var b = StatsOf(s[i..j]); var c = StatsOf(s[j..]);
        var left = PieceStats.Combine(PieceStats.Combine(a, b), c);
        var right = PieceStats.Combine(a, PieceStats.Combine(b, c));
        Assert.Equal(left, right);
        Assert.Equal(StatsOf(s), left);   // 常に全体統計と一致
    }
}
```

**Step 3: 実装**

```csharp
internal readonly record struct PieceStats(long ByteLen, int CharLen, int Breaks, bool FirstIsLf, bool LastIsCr)
{
    public static readonly PieceStats Empty = default;

    public static PieceStats Combine(in PieceStats a, in PieceStats b)
    {
        if (a.CharLen == 0) return b;
        if (b.CharLen == 0) return a;
        return new PieceStats(
            a.ByteLen + b.ByteLen,
            a.CharLen + b.CharLen,
            a.Breaks + b.Breaks - (a.LastIsCr && b.FirstIsLf ? 1 : 0),
            a.FirstIsLf, b.LastIsCr);
    }
}

/// <summary>チャンクの半開バイト範囲への参照+事前計算済み統計。不変。</summary>
internal readonly record struct Piece(TextChunk Chunk, int ByteStart, int ByteLen, PieceStats Stats)
{
    public int CharLen => Stats.CharLen;
    public static Piece Of(TextChunk chunk, int byteStart, int byteLen)
        => new(chunk, byteStart, byteLen, chunk.StatsOfRange(byteStart, byteLen));
}
```

**Step 4: 緑+全体回帰 → Step 5: コミット** `"P1: Piece/PieceStats(改行モノイド・結合律テスト付き)"`

---

### Task 5: 永続AVL PieceTree(Join/Split/列挙)

**Files:**
- Create: `src/yEdit.Core/Buffer/PieceTree.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/PieceTreeTests.cs`

永続平衡木の核。**join-based AVL**("Just Join for Parallel Ordered Sets" の手法)を採用——`Join(left, piece, right)` だけで挿入/削除/分割が組み立てられ、経路コピー永続化と相性がよい。

**ノードと基本操作**(実装コードの骨格。この形をそのまま使う):

```csharp
internal sealed class PieceTree
{
    internal sealed class Node
    {
        public readonly Node? Left, Right;
        public readonly Piece Piece;
        public readonly byte Height;      // 葉=1
        public readonly PieceStats Sum;   // Left+Piece+Right の結合統計

        public Node(Node? left, Piece piece, Node? right)
        {
            Left = left; Right = right; Piece = piece;
            Height = (byte)(1 + Math.Max(H(left), H(right)));
            Sum = PieceStats.Combine(PieceStats.Combine(SumOf(left), piece.Stats), SumOf(right));
        }
    }

    private static int H(Node? n) => n?.Height ?? 0;
    private static PieceStats SumOf(Node? n) => n?.Sum ?? PieceStats.Empty;

    private static Node RotL(Node t) => new(new Node(t.Left, t.Piece, t.Right!.Left), t.Right.Piece, t.Right.Right);
    private static Node RotR(Node t) => new(t.Left!.Left, t.Left.Piece, new Node(t.Left.Right, t.Piece, t.Right));

    /// <summary>高さ差が任意の2木を p を挟んで結合(AVL join)。</summary>
    public static Node Join(Node? l, Piece p, Node? r)
    {
        if (H(l) > H(r) + 1) return JoinOnLeft(l!, p, r);
        if (H(r) > H(l) + 1) return JoinOnRight(l, p, r!);
        return new Node(l, p, r);
    }

    private static Node JoinOnLeft(Node l, Piece p, Node? r)
    {   // 左が高い: 左の右背骨を降りて r と釣り合う位置で接ぐ
        if (H(l.Right) <= H(r) + 1)
        {
            var t = new Node(l.Right, p, r);
            return t.Height <= l.Height + 1 && H(l.Left) + 2 > t.Height
                ? new Node(l.Left, l.Piece, t)
                : RotL(new Node(l.Left, l.Piece, RotR(t)));   // 標準の再平衡
        }
        var joined = JoinOnLeft(l.Right!, p, r);
        var node = new Node(l.Left, l.Piece, joined);
        return joined.Height <= H(l.Left) + 1 ? node : RotL(node);
    }
    // JoinOnRight は対称形

    /// <summary>文字オフセット pos で分割(pos はコード点境界にスナップ済み前提)。</summary>
    public static (Node? Left, Node? Right) Split(Node? t, int pos)
    {
        if (t is null) return (null, null);
        int leftChars = SumOf(t.Left).CharLen;
        if (pos < leftChars)
        { var (a, b) = Split(t.Left, pos); return (a, Join(b, t.Piece, t.Right)); }
        pos -= leftChars;
        if (pos >= t.Piece.CharLen)
        { var (a, b) = Split(t.Right, pos - t.Piece.CharLen); return (Join(t.Left, t.Piece, a), b); }
        // ピース内部分割: チャンク照会でバイト境界を求め2ピース化
        int byteMid = t.Piece.Chunk.CharToByte(t.Piece.ByteStart, t.Piece.ByteLen, pos);
        var p1 = Piece.Of(t.Piece.Chunk, t.Piece.ByteStart, byteMid - t.Piece.ByteStart);
        var p2 = Piece.Of(t.Piece.Chunk, byteMid, t.Piece.ByteStart + t.Piece.ByteLen - byteMid);
        return (Join(t.Left, p1, null), Join(null, p2, t.Right));
    }

    /// <summary>ピースを挟まない結合(削除で使用)。左木の最右ピースを抜いて Join。</summary>
    public static Node? Join2(Node? l, Node? r);
    /// <summary>最左/最右ピースの取り出し(隣接マージ用)。</summary>
    public static (Node? Rest, Piece Last) SplitLast(Node l);
    public static (Piece First, Node? Rest) SplitFirst(Node r);
    /// <summary>ピース列から平衡木を一括構築(中央分割・O(n))。ビルダー用。</summary>
    public static Node? BuildBalanced(ReadOnlySpan<Piece> pieces);
    /// <summary>in-order列挙(WriteTo/Reader用)。</summary>
    public static IEnumerable<Piece> Enumerate(Node? t);
}
```

**Step 1: 失敗するテスト**(internal直テスト・シード固定ランダム):

- `BuildBalanced` + `Enumerate` の往復がピース列を保存
- ランダムなピース列(1〜200個・各ピースはランダム文字列のチャンク)で:
  - `Split(k)` → 左右の `Sum.CharLen` が k と total−k(スナップ考慮なしのASCIIのみで)
  - `Split→Join2` 往復後、`Enumerate` を連結した文字列が不変
  - `Sum` が「全ピース統計の逐次Combine」と一致(**CRLF跨ぎピースを必ず混ぜる**: `"a\r"`,`"\nb"` など)
  - 高さ制約: `Height <= 1.45 * log2(count+2) + 2`(AVL保証の確認)
- ピース内部分割: CJK/絵文字を含むピースを文字中間でSplit→両側統計がナイーブ計算と一致

**Step 2: 失敗確認 → Step 3: 実装(JoinOnRight対称形・Join2/SplitLast/SplitFirst/BuildBalanced/Enumerate を埋める)→ Step 4: 緑+全体回帰**

**Step 5: コミット** `"P1: 永続AVLピース木(join-based・Split/Join2/一括構築・統計集約)"`

---

### Task 6: 行照会(NthBreakEnd / PrefixStats)

**Files:**
- Modify: `src/yEdit.Core/Buffer/PieceTree.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/PieceTreeLineTests.cs`

CRLF跨ぎを正しく扱う行照会。**「break終端が部分木内にいくつあるか」**を、部分木の直後文字がLFかどうか(`followedByLf`)を引き回して数える。

**実装コード(この形をそのまま使う):**

```csharp
/// <summary>k番目(1始まり)のbreak終端文字(LFまたは単独CR)の文字オフセット。</summary>
public static int NthBreakEnd(Node t, int k, bool followedByLf)
{
    // 部分木 S の直後文字が LF のとき、S 末尾の CR は CRLF の一部なので終端は S 内に無い
    static int EndsIn(PieceStats s, bool nextIsLf) => s.Breaks - (s.LastIsCr && nextIsLf ? 1 : 0);

    bool afterLeftIsLf = t.Piece.CharLen > 0 ? t.Piece.Stats.FirstIsLf
                       : t.Right is not null ? t.Right.Sum.FirstIsLf : followedByLf;
    int endsInLeft = t.Left is null ? 0 : EndsIn(t.Left.Sum, afterLeftIsLf);
    if (k <= endsInLeft) return NthBreakEnd(t.Left!, k, afterLeftIsLf);
    k -= endsInLeft;

    int pieceStart = t.Left?.Sum.CharLen ?? 0;
    bool afterPieceIsLf = t.Right is not null ? t.Right.Sum.FirstIsLf : followedByLf;
    int endsInPiece = EndsIn(t.Piece.Stats, afterPieceIsLf);
    if (k <= endsInPiece)
        return pieceStart + t.Piece.Chunk.NthBreakEndChar(t.Piece.ByteStart, t.Piece.ByteLen, k);
    k -= endsInPiece;

    return pieceStart + t.Piece.CharLen + NthBreakEnd(t.Right!, k, followedByLf);
}

/// <summary>接頭辞 [0, pos) の結合統計(O(log n)+格子1マス走査)。</summary>
public static PieceStats PrefixStats(Node? t, int pos)
{
    var acc = PieceStats.Empty;
    while (t is not null)
    {
        int leftChars = SumOf(t.Left).CharLen;
        if (pos < leftChars) { t = t.Left; continue; }
        acc = PieceStats.Combine(acc, SumOf(t.Left));
        pos -= leftChars;
        if (pos < t.Piece.CharLen)
        {
            if (pos == 0) return acc;
            int byteMid = t.Piece.Chunk.CharToByte(t.Piece.ByteStart, t.Piece.ByteLen, pos);
            return PieceStats.Combine(acc,
                t.Piece.Chunk.StatsOfRange(t.Piece.ByteStart, byteMid - t.Piece.ByteStart));
        }
        acc = PieceStats.Combine(acc, t.Piece.Stats);
        pos -= t.Piece.CharLen;
        t = t.Right;
    }
    return acc;
}
```

**Step 1: 失敗するテスト** — ナイーブ実装(string走査で全break終端位置を列挙)との突合:

- 固定文書 `"a\nb\r\nc\rd\r"` : NthBreakEnd(1..4) = 1, 4, 6, 8
- **CRLFがピース境界を跨ぐ配置を強制**(`BuildBalanced` に `["a\r", "\nb\r", "\n"]` 等を直接与える)して同じ検証
- ランダム: 改行過多の文字列(改行率30%・CR/LF/CRLF混合・長さ〜2000)を5〜50個のランダム片に割ってツリー構築→全kでナイーブと一致(シード3種)
- `PrefixStats`: 同文書のランダムposで `Utf8Scan.Stats(prefix)` と一致

**Step 2〜4: 失敗確認→実装→緑+全体回帰 → Step 5: コミット** `"P1: 行照会(NthBreakEnd/PrefixStats・CRLF跨ぎモノイド対応)"`

---

### Task 7: TextSnapshot(公開照会API)

**Files:**
- Create: `src/yEdit.Core/Buffer/TextSnapshot.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/TextSnapshotTests.cs`

ルート参照を包む不変ファサード。§2のシグネチャどおり(CreateReader/WriteToはTask 11)。

**意味の確定(テストで固定):**
- `LineCount = Sum.Breaks + 1`(空文書=1行)
- `GetLineStart(0) = 0`、`GetLineStart(i) = NthBreakEnd(i, followedByLf:false) + 1`
- `GetLineEnd(line, includeBreak:false)` = 行のbreak開始位置(CRLF行なら`\r`の位置)。最終行は `CharLength`
- `GetLineIndexOfChar(pos)` = `PrefixStats(pos).Breaks`、ただし CRLF 中間(pos-1=CR かつ pos=LF)なら−1。`pos==CharLength` は最終行を返す(キャレットがEOF位置に立つため)
- `GetChar(pos)` / `GetText(start, length)`: 範囲外は `ArgumentOutOfRangeException`。**GetTextの開始・終了がサロゲート中間ならスナップせず例外にしない**——中間開始は許容(UIA/描画は任意窓を切るため)。UTF-8デコードは「コード点境界へ広げて切り出し→部分stringスライス」で実装
- `TextSnapshot.Empty` 相当(空文書)も正しく動く

**Step 1: 失敗するテスト** — 代表文書での期待値表+ナイーブ突合:

```
doc = "これは1行目\r\n2nd line\nempty next\r\r\n\n最終行😀"
```
- CharLength / LineCount(=6)/ 全行の GetLineStart / GetLineEnd(includeBreak両方)を string ナイーブ計算と一致
- GetLineIndexOfChar を全posで一致(CRLF中間posを含む)
- 空文書: CharLength=0, LineCount=1, GetLineStart(0)=0, GetLineEnd(0,*)=0
- 末尾が改行の文書: 最終行は空行(GetLineStart(last)==CharLength)
- GetText: 絵文字の中間開始/中間終了を含むランダム窓50個を `doc.Substring` と一致

**Step 2〜4 → Step 5: コミット** `"P1: TextSnapshot(文字/行/テキスト照会の公開API)"`

---

### Task 8: TextBufferBuilder(ストリーム構築)+FromString

**Files:**
- Create: `src/yEdit.Core/Buffer/TextBufferBuilder.cs`, `src/yEdit.Core/Buffer/TextBuffer.cs`(骨格: FromString/Current/読み取りのみ)
- Test: `tests/yEdit.Core.Tests/Buffer/TextBufferBuilderTests.cs`

**仕様:**
- `Add(bytes)`: 末尾の不完全UTF-8シーケンス(最大3バイト)を**繰越しバッファに保持**し次のAddの先頭に連結(=チャンク境界がコード点を割らない)。各Addごとに Sanitize→`TextChunk`(目標サイズ4MB。Addが大きい場合は4MB単位に割る)→ピース化
- `Build()`: 繰越しに不完全列が残っていれば Sanitize(→U+FFFD)して吐き、`BuildBalanced` で木を一括構築(O(n))
- `TextBuffer.FromString(s)`: `Encoding.UTF8.GetBytes` → Builder 1回で構築(テスト・小文書用)
- Builderは再利用不可(Build後のAddは `InvalidOperationException`)

**Step 1: 失敗するテスト:**
- `FromString("")` → CharLength=0
- 「あ」(3バイト)を 1+2 バイトに割って2回Addしても `GetText` 全文が「あ」・HadReplacement=false
- 「😀」(4バイト)を 2+2 に割る同上
- 妥当な2MB文書を 64KB刻みでAdd → 全文一致・PieceCount ≥ 1
- 末尾に不完全列(`E3 81` で終わる)→ Build後 U+FFFD で終わる・HadReplacement=true
- Build後のAdd → InvalidOperationException

**Step 2〜4 → Step 5: コミット** `"P1: TextBufferBuilder(ストリーム構築・コード点繰越し)+TextBuffer骨格"`

---

### Task 9: 編集(Splice/Insert/Delete/Replace)+Appendバッファ+隣接マージ

**Files:**
- Create: `src/yEdit.Core/Buffer/AppendBuffer.cs`
- Modify: `src/yEdit.Core/Buffer/TextBuffer.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/TextBufferEditTests.cs`

**AppendBuffer仕様:**
- 64KBの固定 `byte[]` ブロック列。`Append(string)` → UTF-8変換してブロックへ書き込み、`(TextChunk, start, len)` のピース列を返す(ブロック跨ぎで複数可)
- **公開済み範囲は以後不変**(ブロックはいっぱいになったら新規作成。配列再確保禁止=スナップショット安全)
- 32KB超の挿入は専用チャンク(ピース断片化防止)
- ブロックを包む `TextChunk` は同一ブロックにつき1個を共有し、範囲だけ変える(格子表はブロック=64KBなら実質不要だが共通コードでOK)

**TextBuffer.Splice(中核・全編集の共通経路):**

```csharp
private void Splice(int pos, int delLen, string insert)
{
    // 1) スナップ: pos と pos+delLen をサロゲートペア中間から前方へ(§0-3)
    // 2) (l, rest) = Split(root, pos); (mid, r) = Split(rest, delLen')
    // 3) ピース列 = AppendBuffer.Append(insert)
    // 4) 隣接マージ: 新ピース先頭が l の最右ピースと同一チャンク・バイト連続なら SplitLast→Combine で1ピース化
    //    (連続タイピングでピース数が伸びない。新ピース末尾×rの最左も同様)
    // 5) root' = Join(l ⊕ 新ピース列 ⊕ r); Undoログへ (rootBefore, root', pos, delLen', insLen) を記録(Task 10)
}
```

**Step 1: 失敗するテスト**(すべて string ナイーブとの一致で判定):
- Insert/Delete/Replace の基本(先頭・中間・末尾・全文削除・空文字insert=無変化)
- **CRLFを割る削除**: `"a\r\nb"` から `\n` だけ削除 → `"a\rb"`・LineCount不変(2)/`\r`だけ削除 → `"a\nb"`
- **CRLFを作る挿入**: `"a\rb"` の `\r` 直後に `"\n"` 挿入 → LineCount 2 のまま(CR+LF が1つのbreakに融合)
- サロゲートスナップ: `"a😀b"` の pos=2(ペア中間)に `"x"` 挿入 → pos=1 扱いで `"ax😀b"`。ペア中間をまたぐ Delete も両端スナップ
- 孤立サロゲート挿入(`"\uD83D"` 単体)→ U+FFFD として格納
- スナップショット独立性: 編集前に取った `Current` が編集後も旧内容を返す(**永続性の直接検証**)
- 隣接マージ: `"a"` を1000回末尾タイプ → `Current.PieceCount <= 初期+2`
- 大量挿入(100KB)1回 → 内容一致(専用チャンク経路)

**Step 2〜4 → Step 5: コミット** `"P1: 編集Splice(Insert/Delete/Replace)+Appendバッファ+隣接ピースマージ"`

---

### Task 10: Undo/Redo・coalescing・SavePoint

**Files:**
- Create: `src/yEdit.Core/Buffer/UndoHistory.cs`
- Modify: `src/yEdit.Core/Buffer/TextBuffer.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/UndoTests.cs`

**仕様(オペレーションログ):**
- エントリ: `(Node? RootBefore, Node? RootAfter, int Pos, int RemovedLen, int InsertedLen)`
- `Undo()`: root←RootBefore、返値 `UndoResult(Pos + RemovedLen …ではなく Pos+RemovedLenを選択せずPos+削除復元後の自然位置)` → **確定仕様: Undo後キャレット= Pos + RemovedLen(削除が復元された末尾)、Redo後= Pos + InsertedLen**
- 新規編集で Redo スタックは破棄
- **coalescing規則**(直前エントリに融合する条件):
  - タイプ: 両方とも純挿入(RemovedLen=0)・今回 `pos == prev.Pos + prev.InsertedLen`・今回長≤2・改行を含まない
  - Backspace: 両方とも純削除(InsertedLen=0)・今回 `pos + len == prev.Pos`・今回長≤2
  - Delete前方: 両方とも純削除・今回 `pos == prev.Pos`・今回長≤2
  - `BreakUndoCoalescing()` で強制分割(App層がキャレット移動・保存時に呼ぶ)
  - 融合時は `RootAfter`/長さのみ更新(`RootBefore` は据え置き)
- `MarkSaved()`: 現在rootを保存点として記録。`Modified` = `!ReferenceEquals(root, savedRoot)`——**Undoで保存点まで戻ると Modified=false に戻る**(永続構造の参照一致で自然に成立)
- `ClearUndo()`: 両スタック破棄(保存点は維持)

**Step 1: 失敗するテスト:**
- 単発 Insert→Undo→Redo の内容・CaretPos
- タイプ5文字("hello" を1字ずつ)→ **Undo1回で全部消える**(coalescing)/途中に BreakUndoCoalescing → 2エントリ
- 改行タイプはcoalesceしない("a" → "\n" → "b" = 3エントリ)
- Backspace連打の融合(逆方向pos)
- Insert→Delete交互は融合しない
- MarkSaved→編集→Undo で Modified が true→false と遷移
- Undo後に新規編集 → CanRedo=false
- ClearUndo → CanUndo=false・Modified判定は不変

**Step 2〜4 → Step 5: コミット** `"P1: Undo/Redo(coalescing・SavePoint参照一致・オペレーションログ)"`

---

### Task 11: CreateReader(TextReaderアダプタ)+WriteTo(Stream)

**Files:**
- Create: `src/yEdit.Core/Buffer/SnapshotReader.cs`
- Modify: `src/yEdit.Core/Buffer/TextSnapshot.cs`
- Test: `tests/yEdit.Core.Tests/Buffer/SnapshotIoTests.cs`

**仕様:**
- `CreateReader()`: ピース列挙をたどり、ピース単位でUTF-8デコードしながら `Read(char[],int,int)`/`Peek`/`Read` を供給(ピースはコード点境界保証なのでデコーダ状態の持ち越し不要)。全文string非実体化
- `WriteTo(Stream)`: ピースの `ReadOnlySpan<byte>` を順次 `stream.Write`(変換ゼロ)

**Step 1: 失敗するテスト:**
- `new StreamReader(...)` 相当の突合: 混在文書で `ReadToEnd()` == `GetText(0, CharLength)`
- 小刻みRead(バッファ7文字)でも同一
- **往復不変**: 妥当UTF-8バイト列 → Builder → `WriteTo` → 元バイト列と完全一致(**ゼロ変換の直接証明**)
- 編集後のWriteTo == ナイーブ編集結果のUTF-8

**Step 2〜4 → Step 5: コミット** `"P1: TextReaderアダプタ+WriteTo(UTF-8チャンク直書き・往復不変)"`

---

### Task 12: ランダム編集ファズ(ナイーブモデル突合)

**Files:**
- Test: `tests/yEdit.Core.Tests/Buffer/FuzzTests.cs`

**ハーネス設計:**
- モデル=素朴string実装(**スナップ規則も独立に実装**: `char.IsLowSurrogate` で判定。バッファ側実装をコピーしない)
- 操作分布: Insert 40%(長さ1〜20・素材プール: ASCII語 / ひらがな漢字 / `😀🈴` / `\n` `\r` `\r\n` / 混合複数行)、Delete 30%(長さ1〜30)、Replace 10%、Undo 10%、Redo 5%、MarkSaved 5%
- **毎操作後に `BreakUndoCoalescing()`**(モデル側のUndoは全文スタックで単純化。coalescing の正しさは Task 10 の決定的テストが担保)
- 文書長が 300,000 を超えたら Delete を優先(ファズ時間の抑制)
- 検証: 毎操作 `CharLength`/`LineCount`一致。25操作ごとに全文一致+ランダム行の `GetLineStart/GetLineEnd` +ランダムposの `GetLineIndexOfChar` +ランダム窓 `GetText`。開始時に取った初期スナップショットが終了時も初期内容のまま(永続性)
- `[Theory]` シード {1, 2, 3, 42, 20260705}×既定3,000操作。環境変数 `YEDIT_FUZZ_OPS` で増量可

**Step 1: ハーネスを書く → Step 2: 既定3,000で緑確認 → Step 3: 深掘り実行(DoDの「数万操作」)**

```powershell
$env:YEDIT_FUZZ_OPS = '30000'
dotnet test tests/yEdit.Core.Tests -c Release --nologo --filter FullyQualifiedName~Fuzz
Remove-Item Env:YEDIT_FUZZ_OPS
```

Expected: 5シード×30,000操作 全PASS(数分)。**差異が出たら該当シード+操作番号を最小再現として決定的テストに追加してから修正**(systematic-debugging)

**Step 4: 全体回帰 → Step 5: コミット** `"P1: ランダム編集ファズ(ナイーブモデル突合・シード固定・30k操作PASS)"`

---

### Task 13: 1GBベンチ(DoD性能ゲート)

**Files:**
- Create: `tests/yEdit.Core.Bench/yEdit.Core.Bench.csproj`(`<OutputType>Exe`・net9.0・Core参照・ソリューション登録)
- Create: `tests/yEdit.Core.Bench/Program.cs`

**シナリオ**(`--mb <サイズ>` 引数・既定1024。Stopwatch計測・ウォームアップ後に計測):

| # | 計測 | 目標(DoD) |
|---|---|---|
| 1 | 合成文書構築(日本語+ASCII+改行混合を`--mb`分Builder投入) | 記録のみ(参考: 数秒〜10秒台) |
| 2 | ランダム位置 splice 10,000回(タイプ相当1〜3文字) | **平均<1ms・p99<1ms** |
| 3 | `Current` 取得 1,000,000回 | **O(1)実証(1回あたり数ns〜数十ns)** |
| 4 | ランダム行→`GetLineStart` 100,000回 | 平均<100µs(O(log n)+格子走査) |
| 5 | ランダムpos→`GetLineIndexOfChar` 100,000回 | 同上 |
| 6 | ランダム窓 `GetText(pos, 200)` 100,000回 | 平均<100µs |
| 7 | 連続タイピング10,000字後の `PieceCount` | 断片化しない(数十以下) |
| 8 | メモリ: 構築直後の `GC.GetTotalMemory(true)` と WorkingSet | 記録(文書サイズ+O(ピース)であること) |

**Step 1: プロジェクト作成+sln登録**

```powershell
dotnet sln yEdit.sln add tests/yEdit.Core.Bench/yEdit.Core.Bench.csproj
```

**Step 2: シナリオ実装(結果はコンソールに表形式・目標未達は EXIT 1)**

**Step 3: まず256MBでスモーク → 1GB本番**

```powershell
dotnet run --project tests/yEdit.Core.Bench -c Release -- --mb 256
dotnet run --project tests/yEdit.Core.Bench -c Release -- --mb 1024
```

Expected: 全シナリオ目標達成(EXIT 0)。**未達の場合はプロファイル→原因特定してから最適化**(推測最適化禁止)。1GB構築でメモリ不足になる環境なら `--mb 512` の結果とスケーリング傾向で判定し、その旨を記録

**Step 4: 結果数値を本計画書の末尾「実施記録」節に追記+全体回帰**

**Step 5: コミット** `"P1: 1GBベンチ(編集<1ms・スナップショットO(1)・行照会・断片化・メモリ計測)"`

---

### Task 14: コードレビュー+設計書追記+P1クローズ

**Step 1: 別エージェントにコードレビュー依頼**(superpowers:requesting-code-review。BASE=P1開始前コミット、HEAD=Task 13コミット。観点: §0の不変条件・改行モノイドの正しさ・永続性・性能)

**Step 2: Critical/Important を修正**(修正ごとに失敗テスト→修正→緑→コミット)

**Step 3: 設計書 `docs/plans/2026-07-05-custom-editcontrol-design.md` の P1 節に結果を追記**(P0結果と同形式: テスト件数・ファズ操作数・ベンチ数値表・申し送り)

**Step 4: 最終確認**

```powershell
dotnet build yEdit.sln -c Release; dotnet test tests/yEdit.Core.Tests -c Release --nologo
```

Expected: build 0警告・全テスト緑(289+新規)

**Step 5: コミット** `"P1: レビュー対応+設計書へP1結果追記(TextBuffer完了)"`

**⚠ mainへのマージは行わない**(P7合格後に一括。設計書§3運用)

---

## DoD(設計書P1)

- [ ] 全テスト緑(既存289+新規)
- [ ] ファズ無差異(5シード×30,000操作)
- [ ] ベンチ目標達成: 1GBで編集<1ms・スナップショットO(1)・行変換O(log n)実証
- [ ] build 0警告
- [ ] 別エージェントレビューで Critical/Important 0
- [ ] 設計書へP1結果追記済み

## 実施記録

### 2026-07-05 Task 1〜13 実施

- **テスト**: 既存289 → 408(P1新規119件)。全緑・build 0警告
- **ファズ(Task 12)**: 5シード{1,2,3,42,20260705}×30,000操作=計150,000操作 無差異PASS(1分28秒)
- **1GBベンチ(Task 13)**: `dotnet run --project tests/yEdit.Core.Bench -c Release -- --mb 1024` → **EXIT 0(全目標達成)**

| # | シナリオ | 結果(1GB) | 目標 | 判定 |
|---|---|---|---|---|
| 1 | 構築 | 2.6s / 561,841,654文字 / 18,728,056行 / 1025ピース | 記録のみ | ― |
| 2 | splice 10,000回 | 平均 78.8 µs / p99 188.0 µs | 平均<1ms かつ p99<1ms | PASS |
| 3 | Current取得 | 2.3 ns/回 | O(1)(<1µs) | PASS |
| 4 | GetLineStart | 26.9 µs/回 | 平均<100µs | PASS |
| 5 | GetLineIndexOfChar | 19.2 µs/回 | 平均<100µs | PASS |
| 6 | GetText(200) | 28.1 µs/回 | 平均<100µs | PASS |
| 7 | 連続タイピング断片化 | Δ2(21425→21427) | Δ≤50 | PASS |
| 8 | メモリ | managed 1027MB / WorkingSet 1053MB(文書1024MB) | 記録のみ | ― |

(参考: 256MBでも全PASS。構築0.6s・splice平均96µs/p99 199µs)

- **計画からの逸脱(いずれも性能ゲート達成のための実測ベース最適化・全テストで検証済み)**:
  1. `TextChunk.SplitStats` を追加(分割点+接頭辞統計を1走査で返す)。`PieceTree.Split` のピース内部分割は後半統計をモノイド差分でO(1)導出(旧: CharToByte+Piece.Of×2 で同領域を4〜5回走査 → splice p99 1.19msでFAILしていた)
  2. `TextBuffer.Splice` の SnapLow(GetChar×2)を廃止し、実効位置を Split 結果の統計から導出(スナップは Split 内のコード点境界スナップに一本化・意味は同一)
  3. `TextSnapshot.GetLineIndexOfChar` のCRLF中間判定は PrefixStats の LastIsCr を利用し、LF照会(IsLfAt=バイト1点照会)は接頭辞末尾CR時のみ実行(旧: GetChar×2常時 → 107.8µsでFAILしていた)
  4. `MarkSaved()` は coalescing 境界とする(Undoが保存点を飛び越えて融合エントリを戻さないため。Task 10のテスト仕様が要求)
