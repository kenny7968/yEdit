# P7: 実機SR総合検証+撤去+リリース整備 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** P6 レビュー申し送り(I-3/I-4/I-5)を消化し、v1 UIA/SR二系統機構/UiaProbe 一式を完全撤去し、リリース整備を行い、実機総合検証を経て main へ一括 no-ff マージする(自作エディットコントロール置換プロジェクトの最終フェーズ・第3ゲート)。

**Architecture:**
- **順序=3(I-3/I-4/I-5)→2(撤去)→1(手動検証)→マージ**(ユーザー承認済)。撤去は挙動不変(P6 で dead code 化済)、I-3/I-4/I-5 は Save/Load の挙動変更=手動検証で 1 度に確認できる利点あり。
- 撤去コミットは**粒度細かく**分ける(NG が特定機構に紐づいたら該当コミットだけ revert 可能)。
- 手動検証まで **main には触れない**(2026-07-05 ユーザー指示・設計書§3・`382c44a`)。

**Tech Stack:** C# 12 / .NET 9 / WinForms / xUnit / TextBuffer(UTF-8 永続ピーステーブル)/ TextSnapshot.WriteTo(Stream) 既存 / SnapshotReader(TextReader)既存 / AtomicFile 既存

**Scope 外(P7 スコープ外・別バックログ)**: BackupCoordinator の SnapshotText 依存(P6 Task 12 申し送り・設計書 §P6 line 382 参照)。1GB 級で 300 秒 tick ごとに 2GB 級 string 生成の問題は別 mini-plan で対応。

---

## Part A: P6 レビュー申し送り 3 件の消化(I-3/I-4/I-5)

### Task 1: AtomicFile に Stream 版 Write オーバーロード追加

**Files:**
- Modify: `src/yEdit.Core/IO/AtomicFile.cs`
- Test: `tests/yEdit.Core.Tests/IO/AtomicFileStreamWriteTests.cs`(新規)

**背景**: 現行 `AtomicFile.Write(string, byte[])` は payload を一括で受け取る=呼び出し側で全文 byte 化が必須。Stream 版を追加して呼び出し側がチャンク流し込みできるようにする。既存 byte[] 版は温存(BackupStore などの呼び出しに影響しない)。

**Step 1: 失敗テストを書く**

```csharp
// tests/yEdit.Core.Tests/IO/AtomicFileStreamWriteTests.cs
using System.Text;
using yEdit.Core.IO;
using Xunit;

namespace yEdit.Core.Tests.IO;

public class AtomicFileStreamWriteTests
{
    [Fact]
    public void Write_Stream_CreatesFileWithWrittenBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            AtomicFile.Write(path, stream =>
            {
                var bytes = Encoding.UTF8.GetBytes("hello");
                stream.Write(bytes, 0, bytes.Length);
            });
            Assert.Equal("hello", File.ReadAllText(path, Encoding.UTF8));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Write_Stream_AtomicReplaceOverwritesExisting()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "old");
            AtomicFile.Write(path, stream =>
            {
                var bytes = Encoding.UTF8.GetBytes("new");
                stream.Write(bytes, 0, bytes.Length);
            });
            Assert.Equal("new", File.ReadAllText(path, Encoding.UTF8));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Write_Stream_WriterThrows_LeavesOriginalUntouched()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "original");
            Assert.Throws<InvalidOperationException>(() =>
                AtomicFile.Write(path, _ => throw new InvalidOperationException("boom")));
            Assert.Equal("original", File.ReadAllText(path, Encoding.UTF8));
            // tmp が残っていない(同ディレクトリに *.tmp が無い)
            string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string leftover = Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp").FirstOrDefault() ?? "";
            Assert.True(string.IsNullOrEmpty(leftover), $"leftover tmp: {leftover}");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Write_Stream_NewFile_UsesMove()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Assert.False(File.Exists(path));
            AtomicFile.Write(path, stream =>
            {
                var bytes = Encoding.UTF8.GetBytes("fresh");
                stream.Write(bytes, 0, bytes.Length);
            });
            Assert.Equal("fresh", File.ReadAllText(path, Encoding.UTF8));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

**Step 2: テストが失敗するのを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~AtomicFileStreamWriteTests -v minimal`
Expected: 4 tests FAIL(`AtomicFile.Write(string, Action<Stream>)` overload が無い=コンパイルエラー)

**Step 3: 最小実装**

```csharp
// src/yEdit.Core/IO/AtomicFile.cs に以下を追加(byte[] 版の下)
/// <summary>
/// P7 I-3: 大容量本文向けの Stream ベース原子書込。writer に tmp ファイルの
/// FileStream を渡し、書き終えた後に <see cref="Write(string, byte[])"/> と同じ
/// File.Replace / File.Move で差し替える。writer が例外を投げた場合は tmp を
/// 掃除して例外を伝播する(原本に一切触れない=byte[] 版と同一契約)。
/// </summary>
public static void Write(string path, Action<Stream> writer)
{
    ArgumentNullException.ThrowIfNull(writer);
    string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
    string tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Path.GetRandomFileName() + ".tmp");

    try
    {
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            writer(fs);
    }
    catch
    {
        TryDelete(tmp);
        throw;
    }

    try
    {
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }
    catch
    {
        TryDelete(tmp);
        throw;
    }
}
```

**Step 4: テスト実行**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~AtomicFileStreamWriteTests -v minimal`
Expected: 4 PASS

**Step 5: 全テスト回帰確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj -v minimal`
Expected: 全緑(既存 581 + 新規 4 = 585 前後)

**Step 6: Commit**

```bash
git add src/yEdit.Core/IO/AtomicFile.cs tests/yEdit.Core.Tests/IO/AtomicFileStreamWriteTests.cs
git commit -m "P7 I-3 Task 1: AtomicFile に Stream 版 Write オーバーロード追加(byte[] 版と同一契約・writer 例外時は tmp 掃除+伝播)"
```

---

### Task 2: TextFileService.Save(TextBuffer) を chunk write に置換

**Files:**
- Modify: `src/yEdit.Core/Text/TextFileService.cs:296-303`(既存 `Save(TextBuffer)` を chunk write 化)
- Test: `tests/yEdit.Core.Tests/Text/TextFileServiceSaveTextBufferTests.cs`(既存に追加)

**背景**: 現行 `Save(string, TextBuffer, Encoding, bool)` は内部で `buffer.Current.GetText(0, CharLength)` で全文 string 化してから string 版 Save に委譲=1GB 級で 2GB 常駐+3-4 コピー。UTF-8 は `TextSnapshot.WriteTo(Stream)` 既存で変換ゼロチャンク書き可能。SJIS/EUC-JP は `SnapshotReader`(TextReader)経由の `Encoder.Convert` チャンクループで char[]→bytes を段階変換。共有違反フォールバックは byte[] 版に委譲することで契約温存。

**Step 1: 失敗テストを書く(UTF-8 大容量+SJIS/EUC-JP+BOM)**

```csharp
// tests/yEdit.Core.Tests/Text/TextFileServiceSaveTextBufferTests.cs に追記
[Fact]
public void SaveTextBuffer_Utf8_LargeContent_WritesExactBytes()
{
    // 5MB(TextBufferBuilder のチャンク境界 4MB を跨ぐ)= 全文 string 化していない証拠
    var body = new string('あ', 5 * 1024 * 1024 / 3);  // UTF-8 で 5MB 近辺
    var buffer = TextBuffer.FromString(body);
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: false);
        byte[] actual = File.ReadAllBytes(path);
        byte[] expected = Encoding.UTF8.GetBytes(body);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

[Fact]
public void SaveTextBuffer_Utf8_WithBom_EmitsPreamble()
{
    var buffer = TextBuffer.FromString("hello");
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: true);
        byte[] actual = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, actual.Take(3).ToArray());
        Assert.Equal("hello", Encoding.UTF8.GetString(actual, 3, actual.Length - 3));
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

[Fact]
public void SaveTextBuffer_Sjis_LargeContent_WritesExactBytes()
{
    EncodingCatalog.EnsureRegistered();
    var body = new string('あ', 100_000);  // SJIS で 200KB
    var buffer = TextBuffer.FromString(body);
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        TextFileService.Save(path, buffer, EncodingCatalog.Get(932), hasBom: false);
        byte[] actual = File.ReadAllBytes(path);
        byte[] expected = Encoding.GetEncoding(932).GetBytes(body);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

[Fact]
public void SaveTextBuffer_EucJp_WritesExactBytes()
{
    EncodingCatalog.EnsureRegistered();
    string body = "日本語テキスト EUC-JP\nsecond line\n";
    var buffer = TextBuffer.FromString(body);
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        TextFileService.Save(path, buffer, EncodingCatalog.Get(51932), hasBom: false);
        byte[] actual = File.ReadAllBytes(path);
        byte[] expected = Encoding.GetEncoding(51932).GetBytes(body);
        Assert.Equal(expected, actual);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

[Fact]
public void SaveTextBuffer_EmptyBuffer_WritesZeroBytes()
{
    var buffer = TextBuffer.FromString("");
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: false);
        Assert.Equal(0, new FileInfo(path).Length);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

[Fact]
public void SaveTextBuffer_EmptyBuffer_WithBom_WritesOnlyPreamble()
{
    var buffer = TextBuffer.FromString("");
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: true);
        byte[] actual = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, actual);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}
```

**Step 2: テストが失敗するのを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~SaveTextBufferTests -v minimal`
Expected: 追加した 6 テストのうち一部は現行実装(string 経由)でも PASS するはず。5MB UTF-8/100k SJIS は現行でも PASS するが、これは「動作を保存」する回帰テスト=**Step 3 の chunk 化後も PASS する**ことを保証する。よって Step 2 は「既存動作の spec を固定するテストを追加」段階で PASS=Step 3 で内部実装を差し替えても PASS 継続=挙動不変を証明する。

**Step 3: 実装を chunk write 化**

```csharp
// src/yEdit.Core/Text/TextFileService.cs の Save(string, TextBuffer, ...) を置換
/// <summary>
/// P7 I-3: <see cref="TextBuffer"/> をファイルに保存する(Stream I/O 経路・chunk write)。
/// UTF-8 は <see cref="TextSnapshot.WriteTo(Stream)"/> で変換ゼロチャンク直書き。
/// SJIS/EUC-JP は <see cref="TextSnapshot.CreateReader"/>(SnapshotReader)経由の
/// <see cref="Encoder.Convert"/> チャンクループで char[] → bytes を段階変換=1GB 級でも peak ~O(chunk)。
/// AtomicFile.Write(Stream) で原子書込。共有違反時のみ payload を一括ビルドして byte[] 版 Save に委譲
/// (in-place 上書きフォールバックの契約温存・fallback は稀=このパスだけ全文化を許容)。
/// EOL 変換は事前に <see cref="EditorControl.ConvertEols"/> 済み前提。
/// </summary>
public static void Save(string path, TextBuffer buffer, Encoding encoding, bool hasBom)
{
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(buffer);
    ArgumentNullException.ThrowIfNull(encoding);
    EncodingCatalog.EnsureRegistered();

    var snap = buffer.Current;

    try
    {
        IO.AtomicFile.Write(path, stream =>
        {
            if (encoding.CodePage == 65001)
            {
                if (hasBom)
                    stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                snap.WriteTo(stream);
            }
            else
            {
                // SJIS/EUC-JP: SnapshotReader(TextReader over UTF-16 chunks)+ Encoder.Convert
                Encoding enc = Encoding.GetEncoding(encoding.CodePage,
                    EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
                if (hasBom)
                {
                    byte[] preamble = enc.GetPreamble();
                    if (preamble.Length > 0) stream.Write(preamble, 0, preamble.Length);
                }
                using var reader = snap.CreateReader();
                var encoder = enc.GetEncoder();
                const int CharBufLen = 8 * 1024;
                char[] charBuf = new char[CharBufLen];
                // 最大 bytes/char は enc.GetMaxByteCount(CharBufLen) で保守的確保
                byte[] byteBuf = new byte[enc.GetMaxByteCount(CharBufLen)];
                int charRead;
                while ((charRead = reader.Read(charBuf, 0, CharBufLen)) > 0)
                {
                    int charsUsed = 0, bytesUsed = 0;
                    bool completed = false;
                    int offset = 0;
                    while (offset < charRead)
                    {
                        encoder.Convert(charBuf, offset, charRead - offset,
                            byteBuf, 0, byteBuf.Length, flush: false,
                            out charsUsed, out bytesUsed, out completed);
                        if (bytesUsed > 0) stream.Write(byteBuf, 0, bytesUsed);
                        offset += charsUsed;
                    }
                }
                // 最終 flush(サロゲート途中で終わっていたら FFFD 化される=既存挙動と等価)
                encoder.Convert(Array.Empty<char>(), 0, 0, byteBuf, 0, byteBuf.Length,
                    flush: true, out _, out int flushBytes, out _);
                if (flushBytes > 0) stream.Write(byteBuf, 0, flushBytes);
            }
        });
    }
    catch (IOException ex) when (IO.AtomicFile.IsShareOrLockViolation(ex))
    {
        // 共有違反フォールバック=byte[] 版に委譲(全文化する稀ケース=in-place 上書き契約を温存)。
        // string 版 Save に string 経由で流し込むが、fallback 経路のみ=大容量時も原本は保護される。
        string text = snap.GetText(0, snap.CharLength);
        Save(path, text, encoding, hasBom);
    }
}
```

**Step 4: テスト実行(新規+既存)**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~SaveTextBufferTests -v minimal`
Expected: 全 PASS(新規 6 + 既存)

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj -v minimal`
Expected: 全緑

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -c Release`
Expected: 0 warning

**Step 5: Commit**

```bash
git add src/yEdit.Core/Text/TextFileService.cs tests/yEdit.Core.Tests/Text/TextFileServiceSaveTextBufferTests.cs
git commit -m "P7 I-3 Task 2: TextFileService.Save(TextBuffer) を chunk write 化(UTF-8=WriteTo(Stream)/SJIS/EUC-JP=SnapshotReader+Encoder.Convert・共有違反 fallback のみ全文化)"
```

---

### Task 3: EditorControl.ConvertEols を chunk rebuild に置換

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.cs:358-406`(ConvertEols + CountNonBreakAndBreaks)
- Test: `tests/yEdit.Editor.Tests/EditorControlConvertEolsTests.cs`(既存 6 件維持+新規 2 件)

**背景**: 現行 `ConvertEols` は `SnapshotText`(全文 string)+ `src.Replace(...)` 2 段階 + `TextBuffer.FromString(converted)`=1GB 級で peak 5-8GB。新実装は:
1. UTF-8 byte 列を chunk 単位で走査し、EOL バイト(0x0D/0x0A)を検出→ターゲット EOL バイト列に置換して `TextBufferBuilder` に流す
2. CRLF がチャンク境界を跨ぐケースは 1 バイト carry(pendingCr)で吸収
3. caret/anchor 位置マップは既存の「改行以外の文字数 + 改行数」座標を維持=`SnapshotReader`(chunked TextReader)で走査(SnapshotText 依存を撤去)

既存 6 件の ConvertEols 回帰テスト(fast-path/non-fast-path/caret 保持/anchor 保持/before SetSource no-op)は全て PASS を維持=挙動不変を保証。

**Step 1: 失敗テストを書く(大容量+チャンク境界)**

```csharp
// tests/yEdit.Editor.Tests/EditorControlConvertEolsTests.cs に追記
[Fact]
public void ConvertEols_Utf8_LargeContent_ChunkBoundary_CrlfSpansChunks()
{
    // 4MB(TextBufferBuilder.TargetChunkBytes)近傍で CRLF が切れるように文字列を組む
    // ASCII のみで 4MB - 1 バイトのフィラー + "\r\n" を境界に置く
    int fill = 4 * 1024 * 1024 - 1;
    string body = new string('a', fill) + "\r\n" + "tail\n";
    using var ctrl = new EditorControl();
    ctrl.SetSource(TextBuffer.FromString(body));
    ctrl.ConvertEols(LineEnding.Lf);
    string result = ctrl.SnapshotText;
    // 4MB フィラー + "\n" + "tail\n"(CRLF が LF に統一・境界跨ぎも正しく処理)
    Assert.Equal(new string('a', fill) + "\n" + "tail\n", result);
}

[Fact]
public void ConvertEols_Utf8_MixedEols_AllConvertedToTarget()
{
    // CRLF / CR / LF 混在 → CRLF 統一
    string body = "a\r\nb\rc\nd\r\ne";
    using var ctrl = new EditorControl();
    ctrl.SetSource(TextBuffer.FromString(body));
    ctrl.ConvertEols(LineEnding.Crlf);
    Assert.Equal("a\r\nb\r\nc\r\nd\r\ne", ctrl.SnapshotText);
}
```

**Step 2: テストが失敗するのを確認**

Run: `dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj --filter FullyQualifiedName~ConvertEols -v minimal`
Expected: 既存 6 件は PASS、新規 2 件も現行実装で PASS するはず(仕様固定=Step 3 後も PASS を維持する)。

**Step 3: 実装を chunk rebuild 化**

```csharp
// src/yEdit.Editor/EditorControl.cs の ConvertEols を置換
public void ConvertEols(LineEnding eol)
{
    if (_buffer is null) return;
    byte[] targetBytes = eol switch
    {
        LineEnding.Crlf => new byte[] { 0x0D, 0x0A },
        LineEnding.Lf => new byte[] { 0x0A },
        LineEnding.Cr => new byte[] { 0x0D },
        _ => new byte[] { 0x0A },
    };
    int targetCharLen = targetBytes.Length;  // ASCII のみ=byte 数 = char 数
    var snap = _buffer.Current;

    // fast-path: 既に統一されているか?(全 EOL がターゲット EOL と一致するかを byte スキャンで判定)
    if (IsEolAlreadyUniform(snap, targetBytes))
    {
        EolMode = eol;
        return;
    }

    // caret/anchor を「改行以外文字数(M) + 改行数(K)」に分解(SnapshotReader で chunked に走査)
    var (caretM, caretK) = CountNonBreakAndBreaksInSnapshot(snap, _caret);
    var (anchorM, anchorK) = CountNonBreakAndBreaksInSnapshot(snap, _anchor);
    int savedTopLine = _topLine;
    int savedScrollX = _scrollX;

    // chunk 走査で EOL 置換した新バッファを構築
    var builder = new TextBufferBuilder();
    byte[] outBuf = new byte[64 * 1024];
    int outLen = 0;
    bool pendingCr = false;

    foreach (var piece in PieceTree.Enumerate(snap.Root))
    {
        var span = piece.Chunk.Span.Slice(piece.ByteStart, piece.ByteLen);
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (pendingCr)
            {
                pendingCr = false;
                if (b == 0x0A)
                {
                    // CRLF 境界跨ぎ = ターゲット EOL 1 回、LF は消費
                    EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                    continue;
                }
                // 前チャンク末尾の孤立 CR = ターゲット EOL 1 回、b は通常処理へ
                EmitEol(targetBytes, ref outBuf, ref outLen, builder);
            }
            if (b == 0x0D)
            {
                if (i + 1 < span.Length && span[i + 1] == 0x0A)
                {
                    EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                    i++; // consume LF
                }
                else if (i + 1 < span.Length)
                {
                    EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                }
                else
                {
                    pendingCr = true; // 次チャンク先頭で LF か否かを見る
                }
            }
            else if (b == 0x0A)
            {
                EmitEol(targetBytes, ref outBuf, ref outLen, builder);
            }
            else
            {
                outBuf[outLen++] = b;
                if (outLen == outBuf.Length) FlushBuf(ref outBuf, ref outLen, builder);
            }
        }
    }
    if (pendingCr) EmitEol(targetBytes, ref outBuf, ref outLen, builder);
    if (outLen > 0) FlushBuf(ref outBuf, ref outLen, builder);

    ReplaceSource(builder.Build());
    int total = _buffer!.Current.CharLength;
    _caret = Math.Min(caretM + caretK * targetCharLen, total);
    _anchor = Math.Min(anchorM + anchorK * targetCharLen, total);
    TopLine = savedTopLine;
    ScrollX = savedScrollX;
    EolMode = eol;
}

private static void EmitEol(byte[] eol, ref byte[] outBuf, ref int outLen, TextBufferBuilder builder)
{
    if (outLen + eol.Length > outBuf.Length) FlushBuf(ref outBuf, ref outLen, builder);
    for (int i = 0; i < eol.Length; i++) outBuf[outLen++] = eol[i];
}

private static void FlushBuf(ref byte[] outBuf, ref int outLen, TextBufferBuilder builder)
{
    if (outLen == 0) return;
    builder.Add(new ReadOnlySpan<byte>(outBuf, 0, outLen));
    outLen = 0;
}

/// <summary>
/// Snapshot 全 EOL がターゲット EOL と一致するかを byte スキャンで判定(fast-path 判定用)。
/// CRLF 混在なら target=CRLF でも false(混在の統一が必要=非 fast-path 経路へ)。
/// </summary>
private static bool IsEolAlreadyUniform(TextSnapshot snap, byte[] targetBytes)
{
    bool pendingCr = false;
    foreach (var piece in PieceTree.Enumerate(snap.Root))
    {
        var span = piece.Chunk.Span.Slice(piece.ByteStart, piece.ByteLen);
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (pendingCr)
            {
                pendingCr = false;
                if (b == 0x0A)
                {
                    // 前チャンク末 CR + 今回先頭 LF = CRLF
                    if (!(targetBytes.Length == 2 && targetBytes[0] == 0x0D && targetBytes[1] == 0x0A)) return false;
                    continue;
                }
                // 孤立 CR(前チャンク末)
                if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0D)) return false;
            }
            if (b == 0x0D)
            {
                if (i + 1 < span.Length && span[i + 1] == 0x0A)
                {
                    // in-chunk CRLF
                    if (!(targetBytes.Length == 2 && targetBytes[0] == 0x0D && targetBytes[1] == 0x0A)) return false;
                    i++;
                }
                else if (i + 1 < span.Length)
                {
                    // in-chunk lone CR
                    if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0D)) return false;
                }
                else pendingCr = true;
            }
            else if (b == 0x0A)
            {
                if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0A)) return false;
            }
        }
    }
    if (pendingCr)
    {
        if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0D)) return false;
    }
    return true;
}

/// <summary>
/// [0, pos) 範囲の「改行以外文字数」と「改行数」を返す(SnapshotReader で chunked 走査)。
/// CRLF は 1 改行として数える(既存の <c>CountNonBreakAndBreaks(string, int)</c> と等価)。
/// </summary>
private static (int NonBreakChars, int Breaks) CountNonBreakAndBreaksInSnapshot(TextSnapshot snap, int pos)
{
    int m = 0, k = 0;
    int p = Math.Min(pos, snap.CharLength);
    if (p == 0) return (0, 0);
    using var reader = snap.CreateReader();
    char[] buf = new char[8192];
    int consumed = 0;
    int carry = -1; // 前ブロック末尾の '\r' を持ち越し
    while (consumed < p)
    {
        int want = Math.Min(buf.Length, p - consumed);
        int n = reader.Read(buf, 0, want);
        if (n == 0) break;
        for (int j = 0; j < n; j++)
        {
            char c = buf[j];
            if (carry >= 0)
            {
                if (c == '\n') { k++; consumed++; carry = -1; continue; }
                k++; carry = -1; // 孤立 CR 確定
            }
            if (c == '\r')
            {
                if (j + 1 < n && buf[j + 1] == '\n') { k++; j++; consumed += 2; }
                else if (j + 1 == n) { carry = '\r'; consumed++; }
                else { k++; consumed++; }
            }
            else if (c == '\n') { k++; consumed++; }
            else { m++; consumed++; }
        }
    }
    if (carry >= 0) k++; // 末尾が孤立 CR
    return (m, k);
}

// 旧 CountNonBreakAndBreaks(string, int) は削除(SnapshotText 依存を撤去)
```

**Step 4: テスト実行(既存 6 件+新規 2 件+全緑確認)**

Run: `dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj --filter FullyQualifiedName~ConvertEols -v minimal`
Expected: 8 tests PASS(既存 6 + 新規 2)

Run: `dotnet test -v minimal`
Expected: Core + Editor 全緑

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -c Release`
Expected: 0 warning

**Step 5: Commit**

```bash
git add src/yEdit.Editor/EditorControl.cs tests/yEdit.Editor.Tests/EditorControlConvertEolsTests.cs
git commit -m "P7 I-3 Task 3: ConvertEols を chunk rebuild 化(SnapshotText 全文化を撤廃・byte 単位 EOL 置換+TextBufferBuilder 再構築・fast-path 判定も byte スキャン・caret/anchor は SnapshotReader で座標復元)"
```

---

### Task 4: 1GB 級 Save/Load ラウンドトリップ ベンチ(memory peak 確認)

**Files:**
- Create: `tests/yEdit.Editor.Smoke/Program.cs` に `--bench-save` サブコマンド追加(または新規スメーク)
- Modify: `tests/yEdit.Editor.Smoke/Program.cs`

**背景**: P6 レビュー I-3 は「1GB 級で 5GB peak」の問題定義。修正後の実測を smoke bench で確認する(自動テストは 1GB を扱わない=手動で 1 回計測)。

**Step 1: bench-save サブコマンド追加**

```csharp
// tests/yEdit.Editor.Smoke/Program.cs の Main に --bench-save 分岐を追加
if (args.Length >= 2 && args[0] == "--bench-save")
{
    string inPath = args[1];
    string outPath = Path.Combine(Path.GetTempPath(), "yedit-bench-save-" + Path.GetRandomFileName() + ".txt");
    try
    {
        long peak0 = GC.GetTotalMemory(true);
        var loaded = TextFileService.LoadAsBufferAuto(inPath);
        long peakLoad = GC.GetTotalMemory(false);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ctrl = new EditorControl();
        ctrl.SetSource(loaded.Buffer);
        ctrl.EolMode = loaded.LineEnding;
        ctrl.ConvertEols(loaded.LineEnding);
        long peakConvert = GC.GetTotalMemory(false);
        long convertMs = sw.ElapsedMilliseconds;

        sw.Restart();
        TextFileService.Save(outPath, ctrl.CurrentBuffer, loaded.Encoding, loaded.HasBom);
        long peakSave = GC.GetTotalMemory(false);
        long saveMs = sw.ElapsedMilliseconds;

        long inSize = new FileInfo(inPath).Length;
        long outSize = new FileInfo(outPath).Length;
        Console.WriteLine($"in={inSize:N0}B out={outSize:N0}B match={(inSize == outSize)}");
        Console.WriteLine($"peak0={peak0:N0} peakLoad={peakLoad:N0} peakConvert={peakConvert:N0} peakSave={peakSave:N0}");
        Console.WriteLine($"convertMs={convertMs} saveMs={saveMs}");
    }
    finally { if (File.Exists(outPath)) File.Delete(outPath); }
    return;
}
```

**Step 2: 1GB のテストファイル生成(手動)**

Run(PowerShell / bash 手動 · コミット対象外):
```bash
# 1GB UTF-8 ASCII のダミーファイルを生成
dotnet run --project tests/yEdit.Editor.Smoke -- --gen-1gb %TEMP%/yedit-1gb.txt
```

(必要なら `--gen-1gb` サブコマンドも Task 4 で追加=1MB ブロック × 1024 で書き出し)

**Step 3: ベンチ実行(手動・計測記録)**

Run: `dotnet run --project tests/yEdit.Editor.Smoke -c Release -- --bench-save %TEMP%/yedit-1gb.txt`
Expected 目安:
- `peakLoad - peak0 ≦ 1.2GB`(UTF-8 チャンク木の常駐≈本文サイズ)
- `peakConvert - peakLoad ≦ 1.2GB`(chunk rebuild の一時=もう 1 セット)
- `peakSave - peakConvert ≦ 100MB`(WriteTo(Stream)=変換ゼロ、増分 ~0)
- `convertMs`/`saveMs` は参考(数秒台想定)

Save の RSS ピークが 5GB 級で無いこと=I-3 の効能確認。

**Step 4: 結果を設計書§P7 実装記録に追記(bench 数値のみ)**

```markdown
# docs/plans/2026-07-05-custom-editcontrol-design.md §3 P7 実装記録
- Task 4 bench(1GB UTF-8 ASCII / <マシン仕様>): peakLoad=<>B / peakConvert=<>B / peakSave=<>B / convertMs=<> / saveMs=<>
```

**Step 5: Commit**

```bash
git add tests/yEdit.Editor.Smoke/Program.cs docs/plans/2026-07-05-custom-editcontrol-design.md
git commit -m "P7 I-3 Task 4: bench-save 1GB ラウンドトリップ計測(Save/ConvertEols の chunk 化効能を実測記録)"
```

---

### Task 5: TextFileService.LoadAsBuffer SJIS/EUC-JP を chunk read 化(I-4)

**Files:**
- Modify: `src/yEdit.Core/Text/TextFileService.cs:63-79`(LoadAsBuffer の SJIS/EUC-JP 分岐)
- Test: `tests/yEdit.Core.Tests/Text/TextFileServiceLoadAsBufferTests.cs`(既存に追加)

**背景**: 現行 `LoadAsBuffer` の SJIS/EUC-JP 分岐は `StreamReader.ReadToEnd` で全文 UTF-16 char[] 化してから `TextBuffer.FromString`。数百 MB 級で 2x メモリ(char[] + TextBuffer 内 UTF-8 bytes)。`StreamReader.Read(char[], 0, len)` チャンクループに置換して chunk-by-chunk で `Encoding.UTF8.GetBytes` → `TextBufferBuilder.Add`。UTF-8 経路(既存)と統一。

**Step 1: 失敗テストを書く**

```csharp
// tests/yEdit.Core.Tests/Text/TextFileServiceLoadAsBufferTests.cs に追記
[Fact]
public void LoadAsBuffer_Sjis_LargeContent_ChunkedRead()
{
    EncodingCatalog.EnsureRegistered();
    // TextBufferBuilder の 4MB チャンク境界を跨ぐ SJIS ファイル
    string body = new string('あ', 3 * 1024 * 1024);  // SJIS 6MB
    byte[] sjisBytes = Encoding.GetEncoding(932).GetBytes(body);
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        File.WriteAllBytes(path, sjisBytes);
        var (buf, hadRepl) = TextFileService.LoadAsBuffer(path, EncodingCatalog.Get(932), hasBom: false);
        Assert.False(hadRepl);
        Assert.Equal(body.Length, buf.Current.CharLength);
        Assert.Equal(body.Substring(0, 100), buf.Current.GetText(0, 100));
        Assert.Equal(body.Substring(body.Length - 100, 100),
                     buf.Current.GetText(body.Length - 100, 100));
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

[Fact]
public void LoadAsBuffer_EucJp_ChunkedRead_PreservesContent()
{
    EncodingCatalog.EnsureRegistered();
    string body = "EUC-JP 日本語テスト\nsecond line 日本語\n" + new string('か', 100_000);
    byte[] eucBytes = Encoding.GetEncoding(51932).GetBytes(body);
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        File.WriteAllBytes(path, eucBytes);
        var (buf, hadRepl) = TextFileService.LoadAsBuffer(path, EncodingCatalog.Get(51932), hasBom: false);
        Assert.False(hadRepl);
        Assert.Equal(body.Length, buf.Current.CharLength);
        Assert.Equal(body, buf.Current.GetText(0, buf.Current.CharLength));
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

[Fact]
public void LoadAsBuffer_Sjis_InvalidBytes_ReportsReplacement()
{
    EncodingCatalog.EnsureRegistered();
    // 不正な SJIS バイト(0x81 0x00 = 第2バイト範囲外)
    byte[] bytes = new byte[] { 0x81, 0x00, 0x82, 0xA0 };
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        File.WriteAllBytes(path, bytes);
        var (buf, hadRepl) = TextFileService.LoadAsBuffer(path, EncodingCatalog.Get(932), hasBom: false);
        Assert.True(hadRepl);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}
```

**Step 2: テストが失敗するのを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~LoadAsBufferTests -v minimal`
Expected: 追加した 3 テストは現行 `ReadToEnd` 実装でも PASS するはず=挙動固定テスト=Step 3 後も PASS を維持することで内部実装差替の等価性を証明。

**Step 3: 実装を chunk read 化**

```csharp
// src/yEdit.Core/Text/TextFileService.cs の LoadAsBuffer 内 SJIS/EUC-JP 分岐を置換
else
{
    // P7 I-4: SJIS/EUC-JP も StreamReader.Read(char[], len) チャンクループで UTF-16 化 → UTF-8 化 → Builder.Add。
    // 数百 MB 級ファイルで ReadToEnd による全文 char[] 常駐(2x メモリ)を回避する。
    var decoding = Encoding.GetEncoding(encoding.CodePage,
        EncoderFallback.ReplacementFallback,
        new DecoderReplacementFallback("�"));
    using var reader = new StreamReader(stream, decoding, detectEncodingFromByteOrderMarks: false);
    var builder = new TextBufferBuilder();
    const int CharBufLen = 8 * 1024;
    char[] charBuf = new char[CharBufLen];
    // UTF-8 で char あたり最大 3 バイト(BMP 内)+ サロゲート考慮=4 バイト、余裕 4×CharBufLen で確保
    byte[] utf8Buf = new byte[Encoding.UTF8.GetMaxByteCount(CharBufLen)];
    bool hadReplacement = false;
    int n;
    while ((n = reader.Read(charBuf, 0, CharBufLen)) > 0)
    {
        // 置換文字検出(chunk 内)。Contains より for ループのほうがオーバーヘッド少
        if (!hadReplacement)
        {
            for (int i = 0; i < n; i++)
                if (charBuf[i] == '�') { hadReplacement = true; break; }
        }
        int written = Encoding.UTF8.GetBytes(charBuf, 0, n, utf8Buf, 0);
        builder.Add(new ReadOnlySpan<byte>(utf8Buf, 0, written));
    }
    return (builder.Build(), hadReplacement);
}
```

**Step 4: テスト実行**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~LoadAsBufferTests -v minimal`
Expected: 追加 3 + 既存全 PASS

Run: `dotnet test -v minimal`
Expected: 全緑

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -c Release`
Expected: 0 warning

**Step 5: Commit**

```bash
git add src/yEdit.Core/Text/TextFileService.cs tests/yEdit.Core.Tests/Text/TextFileServiceLoadAsBufferTests.cs
git commit -m "P7 I-4: LoadAsBuffer SJIS/EUC-JP を chunk read 化(StreamReader.Read チャンクループ+UTF-8 変換→TextBufferBuilder・数百MB級で ReadToEnd 2x メモリを回避)"
```

---

### Task 6: SnapshotSearcher regex アンカー docstring 追記 + 回帰テスト(I-5)

**Files:**
- Modify: `src/yEdit.Core/Text/SnapshotSearcher.cs`(docstring だけ)
- Test: `tests/yEdit.Core.Tests/Text/SnapshotSearcherRegexAnchorTests.cs`(新規)

**背景**: `SearchController` の 64MB 閾値超では regex が行単位で `_inner.FindNext/FindPrev/ReplaceInRange` を呼ぶ=`^`/`$`/`\A`/`\Z`/`\G` が「文書の先頭/末尾」ではなく「行の先頭/末尾」に anchor される。閾値以下との挙動差異を docstring に明記+挙動を凍結する回帰テスト 1 件で「壊れる契約」を仕様化。

**Step 1: 回帰テストを書く**

```csharp
// tests/yEdit.Core.Tests/Text/SnapshotSearcherRegexAnchorTests.cs
using yEdit.Core.Buffers;
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class SnapshotSearcherRegexAnchorTests
{
    [Fact]
    public void SnapshotSearcher_RegexAnchor_MatchesLineStart_NotDocumentStart()
    {
        // SnapshotSearcher は行単位 regex=`^` が各行頭にヒットする(閾値以下の TextSearcher なら文書先頭のみ)。
        // I-5 で明記した「壊れる契約」= regex アンカーは行内 anchor。
        var snap = TextBuffer.FromString("apple\nbanana\napple\n").Current;
        var searcher = new SnapshotSearcher(snap);
        var opts = new SearchOptions { Pattern = "^apple", UseRegex = true, MatchCase = true };
        var m1 = searcher.FindNext(0, opts);
        Assert.NotNull(m1);
        Assert.Equal(0, m1!.Value.Start);
        // 2 件目(閾値以下ならヒットしないが、SnapshotSearcher は各行の先頭にヒットする挙動を凍結)
        var m2 = searcher.FindNext(m1.Value.Start + m1.Value.Length, opts);
        Assert.NotNull(m2);
        Assert.Equal(13, m2!.Value.Start); // "apple\nbanana\n".Length = 13
    }

    [Fact]
    public void SnapshotSearcher_RegexAnchor_MatchesLineEnd_NotDocumentEnd()
    {
        var snap = TextBuffer.FromString("apple\nbanana\napple\n").Current;
        var searcher = new SnapshotSearcher(snap);
        var opts = new SearchOptions { Pattern = "apple$", UseRegex = true, MatchCase = true };
        var m1 = searcher.FindNext(0, opts);
        Assert.NotNull(m1);
        Assert.Equal(0, m1!.Value.Start);
        var m2 = searcher.FindNext(m1.Value.Start + m1.Value.Length, opts);
        Assert.NotNull(m2);
        Assert.Equal(13, m2!.Value.Start);
    }
}
```

(パスや SearchOptions の実際のフィールド名は既存 `SearchController` テストに合わせて修正すること)

**Step 2: テスト実行(挙動凍結)**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter FullyQualifiedName~SnapshotSearcherRegexAnchorTests -v minimal`
Expected: PASS(現行挙動を仕様化)

**Step 3: SnapshotSearcher の docstring に「壊れる契約」を追記**

```csharp
// src/yEdit.Core/Text/SnapshotSearcher.cs のクラス docstring に追記
/// <summary>
/// ...(既存の docstring 先頭部)...
/// </summary>
/// <remarks>
/// <para>【壊れる契約(閾値超経路のみ)】</para>
/// <list type="bullet">
/// <item>改行を跨ぐパターンは絶対にヒットしない(既存明記)。</item>
/// <item>regex アンカー(<c>^</c> / <c>$</c> / <c>\A</c> / <c>\Z</c> / <c>\G</c>)は
/// 「文書の先頭/末尾」ではなく「行の先頭/末尾」に anchor される(閾値以下の
/// <see cref="TextSearcher"/> は文書全体をひとつの入力として扱うため、閾値境界で
/// アンカー挙動が変わる)。行単位マッチという性質上の必然=呼び出し側が閾値超と
/// 閾値以下で厳密に同一挙動を必要とするなら regex アンカーは使わない設計にすること
/// (P7 I-5 で仕様化=<c>SnapshotSearcherRegexAnchorTests</c> が挙動を凍結)。</item>
/// </list>
/// </remarks>
```

**Step 4: 全テスト回帰**

Run: `dotnet test -v minimal`
Expected: 全緑

**Step 5: Commit**

```bash
git add src/yEdit.Core/Text/SnapshotSearcher.cs tests/yEdit.Core.Tests/Text/SnapshotSearcherRegexAnchorTests.cs
git commit -m "P7 I-5: SnapshotSearcher regex アンカー行内化を docstring 明記+挙動凍結テスト(閾値超経路の \"^\" は行頭・文書先頭ではない)"
```

---

## Part B: 撤去(SR二系統機構+v1 UIA+UiaProbe 一式)

各 Task で:
1. 削除対象を明示リスト化
2. `git rm` / `dotnet` プロジェクト参照剥がしを実行
3. `dotnet build -c Release`(0 warning)+ `dotnet test`(残存全緑)
4. `dotnet publish src/yEdit.App/yEdit.App.csproj -c Release -r win-x64 --self-contained false -o <tmp>` で出力に該当 DLL/ファイルが**無い**ことを確認
5. Commit(1 粒度 = 1 コミット)

### Task 7: v1 UIA 4 ファイル削除(IUiaTextHostLegacy 系)

**削除対象**(実ファイル名は先に grep で確認・**推定**):
- `src/yEdit.Editor/Uia/IUiaTextHostLegacy.cs`(または名称違い)
- `src/yEdit.Editor/Uia/TextControlProvider.cs`(v1)
- `src/yEdit.Editor/Uia/TextProviderImpl.cs`(v1)
- `src/yEdit.Editor/Uia/TextRangeProvider.cs`(v1)

**Step 1: 事前確認**

Run: `git grep -l "IUiaTextHostLegacy\|TextControlProvider\b" -- src/`
Expected: 上記 4 ファイル+参照経路の一覧を得る。**v2 経路(TextControlProviderV2/TextProviderImplV2/TextRangeProviderV2)への参照が残存**していることを確認(v2 は削除しない)。

**Step 2: 削除**

Run:
```bash
git rm src/yEdit.Editor/Uia/IUiaTextHostLegacy.cs  # 実ファイル名に置換
git rm src/yEdit.Editor/Uia/TextControlProvider.cs
git rm src/yEdit.Editor/Uia/TextProviderImpl.cs
git rm src/yEdit.Editor/Uia/TextRangeProvider.cs
```

参照している場所(EditorControl.WndProc の legacy 経路がもし残っていれば)を Edit で除去。

**Step 3: ビルド+テスト**

Run: `dotnet build -c Release`
Expected: 0 warning、v1 系のシンボル未解決エラーが出れば呼び出し元も削除

Run: `dotnet test -v minimal`
Expected: 全緑(v1 系テストが減る=想定内)

**Step 4: Commit**

```bash
git commit -m "P7 撤去 (1/5): v1 UIA 4 ファイル削除(IUiaTextHostLegacy/TextControlProvider/TextProviderImpl/TextRangeProvider・P5 で v2 に完全移行済のため撤退安全性の並存を解消)"
```

---

### Task 8: SrRoute.Nvda / SrRouteSelector / SrContext / ApplySrAdaptation / ServeUiaProvider 削除 + 設定ダイアログの優先SR タブ削除

**削除対象**:
- `src/yEdit.App/Sr/SrRoute.cs`(enum)/ `SrRouteSelector.cs` / `SrContext.cs` / `ApplySrAdaptation.cs` / `ServeUiaProvider.cs`(実パスは先に grep で確認)
- **設定ダイアログの優先SR タブ**(SettingsDialog の対応 TabPage 削除・関連 AppSettings フィールド削除)

**★注意**: 単に enum 削除だけでなく、**設定ダイアログの該当タブとコントロールも削除**しないと起動時に設定復元で落ちる。両者を同一コミットに含める。

**Step 1: 事前確認**

Run: `git grep -l "SrRoute\|SrContext\|SrRouteSelector\|ApplySrAdaptation\|ServeUiaProvider" -- src/ tests/`
Expected: 削除対象 5 ファイル+呼び出し元一覧。**AppSettings の該当プロパティ**(例: `PreferredSrRoute`)/ SettingsDialog の TabPage / 復元コードも列挙。

**Step 2: 削除**

各ファイルを `git rm` + 呼び出し元を Edit で除去(EditorControl の `ApplySrAdaptation()` 呼び出し・MainForm の SR 判定分岐・SettingsDialog の TabPage 追加行・AppSettings のフィールドと JSON デシリアライズ属性)。

**Step 3: 設定ダイアログの前方互換**

**設定ファイル(settings.json)に旧 PreferredSrRoute フィールドが残る可能性**あり=`System.Text.Json` の `JsonSerializerOptions` は既定で **未知プロパティを無視**(`UnmappedMemberHandling.Skip` 相当)なので、フィールドを消しても既存ユーザーの設定ファイル読み込みは安全。**明示的にコメントで記録**:
```csharp
// AppSettings.cs のクラス docstring に追記
// P7 撤去: PreferredSrRoute フィールドは削除。既存 settings.json に該当キーが残っていても
// System.Text.Json の既定挙動で無視される=移行不要。
```

**Step 4: ビルド+テスト**

Run: `dotnet build -c Release`
Expected: 0 warning

Run: `dotnet test -v minimal`
Expected: 全緑(SR 二系統テストが減る=想定内)

**Step 5: 起動確認(手動)**

Run: `dotnet run --project src/yEdit.App`
- 空タブが正常に開く
- 設定ダイアログを開き、優先SR タブが無いこと
- 既存 settings.json(古い PreferredSrRoute キー付き)でも起動に失敗しない

**Step 6: Commit**

```bash
git commit -m "P7 撤去 (2/5): SrRoute.Nvda/SrRouteSelector/SrContext/ApplySrAdaptation/ServeUiaProvider 削除+設定ダイアログ優先SR タブ削除(P6 Task 15 で UseNativeReading=false 固定・実質死済み)"
```

---

### Task 9: CsvFocusSink 削除(§0-8 猶予解消)

**削除対象**:
- `src/yEdit.App/Csv/CsvFocusSink.cs`(または実パス)
- 呼び出し側の生成コード・FocusTarget 分岐の残骸

**Step 1: 事前確認**

Run: `git grep -l "CsvFocusSink" -- src/ tests/`
Expected: `CsvFocusSink.cs` 本体 + 生成/参照コード。P6 Task 13 で `FocusTarget=Editor 固定` にしているので、`CsvFocusSink` の実行経路には呼ばれていないはず。

**Step 2: 削除**

`git rm` + 参照除去。

**Step 3: ビルド+テスト**

Run: `dotnet build -c Release && dotnet test -v minimal`
Expected: 0 warning、全緑

**Step 4: Commit**

```bash
git commit -m "P7 撤去 (3/5): CsvFocusSink 削除(P6 Task 13 で FocusTarget=Editor 固定・§0-8 猶予を解消)"
```

---

### Task 10: yEdit.UiaProbe プロジェクト削除

**削除対象**:
- `src/yEdit.UiaProbe/`(ディレクトリごと)
- ソリューションファイルの該当プロジェクト参照

**Step 1: 削除**

Run:
```bash
git rm -r src/yEdit.UiaProbe/
dotnet sln yEdit.sln remove src/yEdit.UiaProbe/yEdit.UiaProbe.csproj
```

**Step 2: ビルド+テスト**

Run: `dotnet build -c Release && dotnet test -v minimal`
Expected: 0 warning、全緑

**Step 3: Commit**

```bash
git commit -m "P7 撤去 (4/5): yEdit.UiaProbe プロジェクト削除(P0 SR プローブ完了・自作エディットコントロールに統合済み)"
```

---

### Task 11: UIA 検証スクリプト + HANDOFF ドキュメント削除

**削除対象**:
- `tools/verify-uia.ps1`
- `tools/walk-test.ps1`
- `tools/dump-uia.ps1`
- `tools/selftest-caret.ps1`
- `docs/HANDOFF-scintilla-uia.md`

**Step 1: 削除**

Run:
```bash
git rm tools/verify-uia.ps1 tools/walk-test.ps1 tools/dump-uia.ps1 tools/selftest-caret.ps1
git rm docs/HANDOFF-scintilla-uia.md
```

**Step 2: 参照確認**

Run: `git grep -l "verify-uia\|walk-test\|dump-uia\|selftest-caret\|HANDOFF-scintilla-uia"`
Expected: docs/plans/* の履歴記述のみ(過去の設計書は参照残置=履歴として温存)

**Step 3: ビルド+テスト**

Run: `dotnet build -c Release && dotnet test -v minimal`
Expected: 0 warning、全緑

**Step 4: Commit**

```bash
git commit -m "P7 撤去 (5/5): tools/verify-uia.ps1/walk-test.ps1/dump-uia.ps1/selftest-caret.ps1 + docs/HANDOFF-scintilla-uia.md 削除(Scintilla 前提の検証資産を完全撤去)"
```

---

## Part C: リリース整備

### Task 12: リリース CI + 説明書更新

**Files:**
- Modify: `.github/workflows/release.yml`(ネイティブ DLL 同梱の残骸があれば削除)
- Modify: `説明書/yEdit説明書.md`(Scintilla 前提記述の差し替え・**ユーザー編集版が正=文言変更は最小限**)

**Step 1: release.yml 確認**

Run: `git grep -l "Scintilla\|Lexilla\|scilexer" -- .github/`
Expected: 該当行があれば削除(publish 出力にネイティブ DLL は既にゼロだが、CI レシピに残骸があれば掃除)。

**Step 2: 説明書の Scintilla 記述**

Run: `git grep -n "Scintilla" -- 説明書/`
Expected: あればユーザーに一覧提示して**差し替え文言をユーザー承認**してもらう(memory: 説明書はユーザー編集版が正・勝手に書き換えない)。

**Step 3: 変更が発生した場合の Commit**

```bash
git commit -m "P7 リリース整備: CI/説明書から Scintilla 前提記述を除去"
```

変更が無ければこの Task はスキップ(commit なし)。

---

### Task 13: 別エージェント最終レビュー

**背景**: プロジェクト運用ルール(memory: [[review-by-separate-agent]])= main マージ前に別エージェントレビュー必須。

**Step 1: 別エージェント起動**

superpowers:code-reviewer サブエージェントに以下の観点でレビュー依頼:
- Part A(I-3/I-4/I-5)のコード変更が「大容量 peak メモリ削減」の目的を果たしているか
- Part B(撤去 5 コミット)で dead code/参照残骸/import 残骸が無いか
- Task 4 bench の数値が計画目標(peak ~O(text))を満たしているか
- テストカバレッジ(既存回帰+新規)の妥当性
- 設計書§P7 との整合性

**Step 2: Critical/Important 対応**

Critical / Important があればコミット内で修正 → Minor は判断して修正 or 申し送り。

**Step 3: 設計書§3 に P7 実装記録追記**

`docs/plans/2026-07-05-custom-editcontrol-design.md` §3 に:
- P7 自動 DoD(全緑テスト数・0 warning・publish DLL ゼロ)
- Task 4 bench 数値
- Part B 撤去ファイル一覧
- 別エージェントレビュー結果
- **手動チェックリスト未実施**(次 Task で実施)

**Step 4: Commit**

```bash
git commit -m "P7 レビュー+§3 追記: 別エージェント最終レビュー Critical/Important 対応+設計書 P7 結果表追記(手動チェックリスト実施待ち)"
```

---

## Part D: 手動検証+マージ

### Task 14: 実機総合検証(第3ゲート・ユーザー実施)

**Files:**
- 使用: `docs/plans/2026-07-06-p6-manual-checklist.md`(A〜P・90+ 項目)

**Step 1: ユーザーへの依頼**

以下を伝える:
- チェックリスト実行環境=撤去済ビルド(v1 UIA/CsvFocusSink/SrContext なし)
- マトリクス: NVDA / PC-Talker / ナレーター × チェックリスト A〜P
- 判別困難な挙動は SrDiagLog(ファイルログ)で切り分け(既存手順)
- 結果を「合格 / NG(項目名+症状)」形式で報告

**Step 2: NG があった場合の判断**

- Critical(SR で読めない・キャレット追従しない・空行無音・保存で本文破損): **該当撤去コミットの revert** で切り戻し検討+根本原因調査
- Non-critical: 申し送りとして記録→設計書§P7 に追記→マージ判定

**Step 3: 設計書§3 に手動検証結果追記**

`docs/plans/2026-07-05-custom-editcontrol-design.md` §3 P7 実装記録に:
- 手動検証: 合格 / 部分合格(申し送りリスト)/ NG(revert 判断)
- 検証環境(OS / SR バージョン / ATOK バージョン)
- 判定=**Go/No-Go**

**Step 4: Commit(判定確定後)**

```bash
git commit -m "P7 実機総合検証: <判定>(<簡潔な結果サマリ>)"
```

---

### Task 15: main へ no-ff マージ

**★条件**: Task 14 が **Go 判定**+設計書§3 に P7 全結果追記完了。

**Step 1: 事前確認**

Run: `git status`
Expected: worktree クリーン

Run: `git log --oneline main..HEAD | wc -l`
Expected: P0〜P7 全コミット数(参考値)

**Step 2: main へ切替+マージ**

Run:
```bash
git checkout main
git status  # クリーンを再確認
git merge --no-ff feature/custom-editcontrol-design -m "自作エディットコントロール置換プロジェクト完了(P0〜P7 一括マージ・NVDA Scintilla 特別扱いを回避する v2 UIA 単一経路・1GB 級 Stream I/O)"
```

**Step 3: マージ後の smoke 起動確認**

Run: `dotnet build -c Release && dotnet run --project src/yEdit.App`
Expected: 起動 OK・ファイル開く/保存 OK

**Step 4: push 判断**

**★ push はユーザー承認後**(memory: main は本人管理・push を勝手にしない)。

**Step 5: メモリ更新**

memory `custom-editcontrol.md` を「**プロジェクト完了・main マージ済**」で更新+関連メモリ(scintilla-uia-architecture / pctalker-speech-control / production-build-started)からの参照を整理。

---

## 実行方針まとめ

- **順序**: Task 1〜6(Part A: I-3/I-4/I-5)→ Task 7〜11(Part B: 撤去)→ Task 12〜13(Part C: リリース+レビュー)→ Task 14〜15(Part D: 手動検証+マージ)
- **各 Task 完了時**: build 0 warning + 全緑を確認してから次へ進む
- **worktree 完結**: Task 15 まで main には触れない(設計書§3 運用)
- **撤退安全性**: Part B は 5 コミット粒度=NG 検出時に該当だけ revert 可能
- **手動検証はユーザー実施**: Task 14 は指示を出してから待つ(自動実行不可)

---

## 実行ハンドオフ

計画完了。`docs/plans/2026-07-12-p7-verify-remove-release.md` に保存済。

**2 つの実行方式**:

1. **Subagent-Driven(本セッション内)**: 各 Task をサブエージェントに委譲→戻り確認→次 Task。速い反復。
2. **Parallel Session(別セッション)**: 新セッションを開いて `superpowers:executing-plans` でバッチ実行+チェックポイント。

どちらで進めますか?
