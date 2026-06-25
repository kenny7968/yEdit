# M1: v0.1 ウォーキングスケルトン 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 1ファイルを開く・編集する・文字コードと改行コードを保ったまま保存でき、PC-Talker/NVDA が読む、最小の本番 yEdit アプリ（単一ドキュメント）を作る。

**Architecture:** `yEdit.Core`（UI非依存・純ロジック＝文字コードI/O・設定。TDD）／`yEdit.Editor`（probe の `ScintillaHost` を昇格＝Scintilla継承＋WM_GETOBJECT横取り＋SR適応）／`yEdit.App`（WinForms シェル＝MainForm・メニュー・ステータスバー・ファイル操作）。`yEdit.Accessibility`（既存・無改変流用）。詳細は `docs/plans/2026-06-26-yedit-production-architecture-design.md`。

**Tech Stack:** C# / .NET 9 / WinForms / `Scintilla5.NET` 6.1.2 / UIA（System.Windows.Automation）/ `UTF.Unknown`（文字コード自動判定）/ xUnit。

**アクセシビリティの鉄則（全タスクで厳守）:** ①ウィンドウクラスは "Scintilla" のまま（改名禁止）②NVDA起動中はUIA/MSAAを出さない・それ以外はUIA提供 ③RPCスレッドから `SCI_*`(DirectMessage)を呼ばない（UIスレッドのスナップショット／キャッシュで応答）④フォーカス獲得時にも `TextSelectionChanged` 発火。

---

## 事前準備

### Task 0: フィーチャーブランチを切る

**Step 1: ブランチ作成**

Run:
```bash
git switch -c feature/m1-walking-skeleton
git branch --show-current
```
Expected: `feature/m1-walking-skeleton`

---

## フェーズA: yEdit.Core — 文字コード I/O（TDD）

### Task A1: Core プロジェクト作成

**Files:**
- Create: `src/yEdit.Core/yEdit.Core.csproj`

**Step 1: プロジェクト生成とソリューション追加**

Run:
```bash
dotnet new classlib -n yEdit.Core -o src/yEdit.Core -f net9.0
rm src/yEdit.Core/Class1.cs
dotnet add src/yEdit.Core package UTF.Unknown
dotnet sln yEdit.sln add src/yEdit.Core/yEdit.Core.csproj
```

**Step 2: csproj を確認・調整**

`src/yEdit.Core/yEdit.Core.csproj` を以下に揃える（`Nullable`/`ImplicitUsings` 有効）:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="UTF.Unknown" Version="2.5.1" />
  </ItemGroup>
</Project>
```
> 注: `UTF.Unknown` のバージョンは `dotnet add` が解決した最新で可。上は目安。

**Step 3: ビルド確認**

Run: `dotnet build src/yEdit.Core/yEdit.Core.csproj`
Expected: ビルド成功（0 warning）。

**Step 4: コミット**

```bash
git add src/yEdit.Core yEdit.sln
git commit -m "M1: yEdit.Core プロジェクト追加（UTF.Unknown 参照）"
```

---

### Task A2: テストプロジェクト作成

**Files:**
- Create: `tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`

**Step 1: 生成・参照・ソリューション追加**

Run:
```bash
dotnet new xunit -n yEdit.Core.Tests -o tests/yEdit.Core.Tests -f net9.0
rm tests/yEdit.Core.Tests/UnitTest1.cs
dotnet add tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj reference src/yEdit.Core/yEdit.Core.csproj
dotnet sln yEdit.sln add tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj
```

**Step 2: ビルド確認**

Run: `dotnet build tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
Expected: ビルド成功。

**Step 3: コミット**

```bash
git add tests yEdit.sln
git commit -m "M1: yEdit.Core.Tests プロジェクト追加"
```

---

### Task A3: 改行コード判定（TDD）

**Files:**
- Create: `src/yEdit.Core/Text/LineEnding.cs`
- Test: `tests/yEdit.Core.Tests/Text/LineEndingDetectorTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Text/LineEndingDetectorTests.cs`:
```csharp
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class LineEndingDetectorTests
{
    [Theory]
    [InlineData("a\r\nb", LineEnding.Crlf)]
    [InlineData("a\nb", LineEnding.Lf)]
    [InlineData("a\rb", LineEnding.Cr)]
    public void Detects_dominant_line_ending(string text, LineEnding expected)
        => Assert.Equal(expected, LineEndingDetector.Detect(text));

    [Fact]
    public void Mixed_returns_dominant()
        => Assert.Equal(LineEnding.Lf, LineEndingDetector.Detect("a\nb\nc\r\nd"));

    [Fact]
    public void No_newline_returns_platform_default()
        => Assert.Equal(LineEnding.Crlf, LineEndingDetector.Detect("abc"));
}
```

**Step 2: 失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter LineEndingDetectorTests`
Expected: FAIL（`LineEnding` / `LineEndingDetector` 未定義でコンパイルエラー）。

**Step 3: 最小実装**

`src/yEdit.Core/Text/LineEnding.cs`:
```csharp
namespace yEdit.Core.Text;

public enum LineEnding { Crlf, Lf, Cr }

public static class LineEndingDetector
{
    /// <summary>本文中で最も多い改行種別を返す。改行が無ければ CRLF（Windows 既定）。</summary>
    public static LineEnding Detect(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (c == '\n') lf++;
        }
        if (crlf == 0 && lf == 0 && cr == 0) return LineEnding.Crlf;
        if (crlf >= lf && crlf >= cr) return LineEnding.Crlf;
        return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }
}
```

**Step 4: テスト成功を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter LineEndingDetectorTests`
Expected: PASS。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Text/LineEnding.cs tests/yEdit.Core.Tests/Text/LineEndingDetectorTests.cs
git commit -m "M1: 改行コード判定 LineEndingDetector（TDD）"
```

---

### Task A4: 文字コード判定（TDD）

**Files:**
- Create: `src/yEdit.Core/Text/EncodingCatalog.cs`
- Create: `src/yEdit.Core/Text/EncodingDetector.cs`
- Test: `tests/yEdit.Core.Tests/Text/EncodingDetectorTests.cs`

判定方針（design §4.2）: ①BOM 確定判定（UTF-8/UTF-16LE/UTF-16BE）②BOM無しは厳格 UTF-8 デコード成功で UTF-8 確定 ③それ以外は `UTF.Unknown` に委譲し名前→コードページ自前マップ（euc-jp=51932 / shift_jis=932 のみ採用）④低信頼/未対応/null は Shift_JIS(932) へフォールバック。.NET9 は `CodePagesEncodingProvider` 登録が必要。

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Text/EncodingDetectorTests.cs`:
```csharp
using System.Text;
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class EncodingDetectorTests
{
    private const string Jp = "日本語のテスト。ABC 123 半角と　全角。";

    [Fact]
    public void Detects_utf8_bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Jp)).ToArray();
        var r = EncodingDetector.Detect(bytes);
        Assert.Equal(65001, r.CodePage);
        Assert.True(r.HasBom);
    }

    [Fact]
    public void Detects_utf8_no_bom()
    {
        var r = EncodingDetector.Detect(Encoding.UTF8.GetBytes(Jp));
        Assert.Equal(65001, r.CodePage);
        Assert.False(r.HasBom);
    }

    [Fact]
    public void Detects_shift_jis()
    {
        var sjis = EncodingCatalog.Get(932);
        var r = EncodingDetector.Detect(sjis.GetBytes(Jp));
        Assert.Equal(932, r.CodePage);
    }

    [Fact]
    public void Detects_euc_jp()
    {
        var euc = EncodingCatalog.Get(51932);
        var r = EncodingDetector.Detect(euc.GetBytes(Jp));
        Assert.Equal(51932, r.CodePage);
    }

    [Fact]
    public void Detects_utf16_le_bom()
    {
        var r = EncodingDetector.Detect(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Jp)).ToArray());
        Assert.Equal(1200, r.CodePage);
        Assert.True(r.HasBom);
    }

    [Fact]
    public void Empty_defaults_to_utf8()
    {
        var r = EncodingDetector.Detect(Array.Empty<byte>());
        Assert.Equal(65001, r.CodePage);
    }
}
```

**Step 2: 失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter EncodingDetectorTests`
Expected: FAIL（未定義）。

**Step 3: 実装**

`src/yEdit.Core/Text/EncodingCatalog.cs`:
```csharp
using System.Text;

namespace yEdit.Core.Text;

/// <summary>
/// .NET9 で Shift_JIS(932)/EUC-JP(51932) を使うには CodePagesEncodingProvider の登録が要る。
/// 本カタログ初回利用時に一度だけ登録する。
/// </summary>
public static class EncodingCatalog
{
    private static bool _registered;
    private static readonly object _sync = new();

    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (_sync)
        {
            if (_registered) return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _registered = true;
        }
    }

    /// <summary>コードページから Encoding を得る（必要なら provider 登録）。</summary>
    public static Encoding Get(int codePage)
    {
        EnsureRegistered();
        return codePage switch
        {
            65001 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            1200 => Encoding.Unicode,
            1201 => Encoding.BigEndianUnicode,
            _ => Encoding.GetEncoding(codePage),
        };
    }
}
```

`src/yEdit.Core/Text/EncodingDetector.cs`:
```csharp
using System.Text;
using UtfUnknown;

namespace yEdit.Core.Text;

/// <summary>判定結果。CodePage と BOM 有無を持つ。</summary>
public readonly record struct DetectedEncoding(int CodePage, bool HasBom);

public static class EncodingDetector
{
    /// <summary>バイト列から文字コードを推定する（design §4.2 の手順）。</summary>
    public static DetectedEncoding Detect(byte[] bytes)
    {
        EncodingCatalog.EnsureRegistered();

        // ① BOM 確定
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new DetectedEncoding(65001, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new DetectedEncoding(1200, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new DetectedEncoding(1201, true);

        // 空は UTF-8 既定
        if (bytes.Length == 0) return new DetectedEncoding(65001, false);

        // ② 厳格 UTF-8 デコード成功なら UTF-8（BOM無し）
        if (IsStrictUtf8(bytes)) return new DetectedEncoding(65001, false);

        // ③ UTF.Unknown へ委譲
        var result = CharsetDetector.DetectFromBytes(bytes);
        int? cp = MapCharset(result?.Detected?.EncodingName, result?.Detected?.Confidence ?? 0f);
        if (cp is int detected) return new DetectedEncoding(detected, false);

        // ④ フォールバック: Shift_JIS
        return new DetectedEncoding(932, false);
    }

    private static bool IsStrictUtf8(byte[] bytes)
    {
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            strict.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>UTF.Unknown の名前→コードページ（採用は euc-jp/shift_jis のみ。低信頼は不採用）。</summary>
    private static int? MapCharset(string? name, float confidence)
    {
        if (name is null || confidence < 0.5f) return null;
        return name.ToLowerInvariant() switch
        {
            "utf-8" => 65001,
            "shift-jis" or "shift_jis" => 932,
            "euc-jp" => 51932,
            "utf-16le" or "utf-16" => 1200,
            "utf-16be" => 1201,
            _ => null,
        };
    }
}
```

**Step 4: テスト成功を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter EncodingDetectorTests`
Expected: PASS。
> 失敗時の注意: `UTF.Unknown` の名前表記（"Shift-JIS" 等）が版で異なる可能性。実出力を `result.Detected.EncodingName` で確認し `MapCharset` を合わせる。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Text/EncodingCatalog.cs src/yEdit.Core/Text/EncodingDetector.cs tests/yEdit.Core.Tests/Text/EncodingDetectorTests.cs
git commit -m "M1: 文字コード判定 EncodingDetector/EncodingCatalog（TDD）"
```

---

### Task A5: ファイル読み込み（TDD）

**Files:**
- Create: `src/yEdit.Core/Text/LoadedDocument.cs`
- Create: `src/yEdit.Core/Text/TextFileService.cs`
- Test: `tests/yEdit.Core.Tests/Text/TextFileServiceLoadTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Text/TextFileServiceLoadTests.cs`:
```csharp
using System.Text;
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class TextFileServiceLoadTests
{
    private const string Jp = "一行目\r\n二行目\r\n";

    [Fact]
    public void Loads_utf8_no_bom_roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Jp));
            var doc = TextFileService.Load(path);
            Assert.Equal(Jp, doc.Text);
            Assert.Equal(65001, doc.Encoding.CodePage);
            Assert.Equal(LineEnding.Crlf, doc.LineEnding);
            Assert.False(doc.HadReplacementChar);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Loads_shift_jis_with_explicit_codepage()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, EncodingCatalog.Get(932).GetBytes(Jp));
            var doc = TextFileService.Load(path, forcedCodePage: 932);
            Assert.Equal(Jp, doc.Text);
            Assert.Equal(932, doc.Encoding.CodePage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Wrong_encoding_flags_replacement_char()
    {
        string path = Path.GetTempFileName();
        try
        {
            // Shift_JIS バイトを UTF-8 として強制読み → 置換文字混入
            File.WriteAllBytes(path, EncodingCatalog.Get(932).GetBytes(Jp));
            var doc = TextFileService.Load(path, forcedCodePage: 65001);
            Assert.True(doc.HadReplacementChar);
        }
        finally { File.Delete(path); }
    }
}
```

**Step 2: 失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter TextFileServiceLoadTests`
Expected: FAIL（未定義）。

**Step 3: 実装**

`src/yEdit.Core/Text/LoadedDocument.cs`:
```csharp
using System.Text;

namespace yEdit.Core.Text;

/// <summary>読み込み結果。本文と確定した文字コード・改行・付随情報。</summary>
public sealed class LoadedDocument
{
    public required string Text { get; init; }
    public required Encoding Encoding { get; init; }
    public required bool HasBom { get; init; }
    public required LineEnding LineEnding { get; init; }
    /// <summary>デコード時に U+FFFD（置換文字）が出たか（文字コード取り違えの示唆）。</summary>
    public required bool HadReplacementChar { get; init; }
}
```

`src/yEdit.Core/Text/TextFileService.cs`（読み込み部。保存は Task A6 で追記）:
```csharp
using System.Text;

namespace yEdit.Core.Text;

public static partial class TextFileService
{
    /// <summary>
    /// ファイルを読み込み本文・文字コード・改行を確定する。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む（開き直し用）。
    /// </summary>
    public static LoadedDocument Load(string path, int? forcedCodePage = null)
    {
        byte[] bytes = File.ReadAllBytes(path);

        DetectedEncoding det = forcedCodePage is int cp
            ? new DetectedEncoding(cp, HasBomFor(bytes, cp))
            : EncodingDetector.Detect(bytes);

        Encoding enc = EncodingCatalog.Get(det.CodePage);

        // BOM を除いた本文部分をデコード。置換文字検出のため fallback を Replacement に。
        int preambleLen = det.HasBom ? enc.GetPreamble().Length : 0;
        var decoder = Encoding.GetEncoding(det.CodePage,
            EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        string text = decoder.GetString(bytes, preambleLen, bytes.Length - preambleLen);

        bool hadReplacement = text.Contains('�');
        LineEnding eol = LineEndingDetector.Detect(text);

        return new LoadedDocument
        {
            Text = text,
            Encoding = enc,
            HasBom = det.HasBom,
            LineEnding = eol,
            HadReplacementChar = hadReplacement,
        };
    }

    private static bool HasBomFor(byte[] bytes, int codePage)
    {
        var pre = EncodingCatalog.Get(codePage).GetPreamble();
        if (pre.Length == 0 || bytes.Length < pre.Length) return false;
        for (int i = 0; i < pre.Length; i++) if (bytes[i] != pre[i]) return false;
        return true;
    }
}
```
> `EncodingCatalog.Get(65001)` は BOM 無し UTF8Encoding を返すため `GetPreamble()` は空。BOM付きUTF-8は判定で `HasBom=true` になり preambleLen を別途加える必要がある点に注意 → `Get` の代わりに `Encoding.GetEncoding(det.CodePage)` 系で preamble を取得する設計に合わせる。実装時、BOM付きUTF-8読み込みのテストを1件追加し preamble 除去が正しいか確認すること。

**Step 4: テスト成功を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter TextFileServiceLoadTests`
Expected: PASS。

**Step 5: コミット**

```bash
git add src/yEdit.Core/Text/LoadedDocument.cs src/yEdit.Core/Text/TextFileService.cs tests/yEdit.Core.Tests/Text/TextFileServiceLoadTests.cs
git commit -m "M1: ファイル読み込み TextFileService.Load（TDD）"
```

---

### Task A6: 原子的保存（TDD）

**Files:**
- Modify: `src/yEdit.Core/Text/TextFileService.cs`
- Test: `tests/yEdit.Core.Tests/Text/TextFileServiceSaveTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Text/TextFileServiceSaveTests.cs`:
```csharp
using System.Text;
using yEdit.Core.Text;
using Xunit;

namespace yEdit.Core.Tests.Text;

public class TextFileServiceSaveTests
{
    [Fact]
    public void Save_then_load_roundtrips_shift_jis()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            const string text = "保存テスト\r\nおわり\r\n";
            TextFileService.Save(path, text, EncodingCatalog.Get(932), hasBom: false);
            var doc = TextFileService.Load(path);
            Assert.Equal(text, doc.Text);
            Assert.Equal(932, doc.Encoding.CodePage);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_overwrites_existing_atomically()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "old");
            TextFileService.Save(path, "new", EncodingCatalog.Get(65001), hasBom: false);
            Assert.Equal("new", File.ReadAllText(path));
            // temp 残骸が無いこと
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*tmp*"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_utf8_with_bom_writes_preamble()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, "x", EncodingCatalog.Get(65001), hasBom: true);
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

**Step 2: 失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter TextFileServiceSaveTests`
Expected: FAIL（`Save` 未定義）。

**Step 3: 実装（TextFileService に追記）**

`src/yEdit.Core/Text/TextFileService.cs` に以下を追加:
```csharp
public static partial class TextFileService
{
    /// <summary>
    /// 原子的にテキストを保存する。同ディレクトリの temp に書いてから File.Replace で差し替え。
    /// 新規は File.Move。対象がロック中で差し替え不能なら in-place 上書きへフォールバック。
    /// </summary>
    public static void Save(string path, string text, Encoding encoding, bool hasBom)
    {
        // BOM 制御: hasBom に応じて preamble を出す Encoding を用意。
        Encoding enc = encoding.CodePage switch
        {
            65001 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom),
            1200 or 1201 => encoding, // UTF-16 は preamble 既定で出る
            _ => encoding,
        };
        byte[] preamble = hasBom ? enc.GetPreamble() : Array.Empty<byte>();
        byte[] body = enc.GetBytes(text);

        byte[] payload = preamble.Length == 0
            ? body
            : preamble.Concat(body).ToArray();

        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Path.GetRandomFileName() + ".tmp");

        try
        {
            File.WriteAllBytes(tmp, payload);
            if (File.Exists(path))
            {
                // 既存の ACL・属性を保持して差し替え（バックアップ無し）。
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch (IOException)
        {
            // ロック等で差し替え不能 → in-place 上書きにフォールバック。
            TryDelete(tmp);
            File.WriteAllBytes(path, payload);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* 残骸は実害小 */ }
    }
}
```
> 注: `UTF8Encoding(... hasBom)` を使うため、Save 内で `enc.GetBytes(text)` は preamble を含まない。preamble は別途 `GetPreamble()` で先頭付与する設計。二重付与しないよう、UTF-8 は `encoderShouldEmitUTF8Identifier:false` の Encoding で body を作り preamble を手前に付ける（上記コードは整合済み）。

**Step 4: テスト成功を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter TextFileServiceSaveTests`
Expected: PASS。

**Step 5: 全 Core テスト確認＋コミット**

Run: `dotnet test tests/yEdit.Core.Tests`
Expected: 全 PASS。
```bash
git add src/yEdit.Core/Text/TextFileService.cs tests/yEdit.Core.Tests/Text/TextFileServiceSaveTests.cs
git commit -m "M1: 原子的保存 TextFileService.Save（TDD）"
```

---

## フェーズB: yEdit.Core — 設定（TDD）

### Task B1: AppSettings と SettingsStore（TDD）

**Files:**
- Create: `src/yEdit.Core/Settings/AppSettings.cs`
- Create: `src/yEdit.Core/Settings/SettingsStore.cs`
- Test: `tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs`:
```csharp
using yEdit.Core.Settings;
using Xunit;

namespace yEdit.Core.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_returns_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var s = SettingsStore.Load(path);
        Assert.Equal(new AppSettings().FontName, s.FontName);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings { FontName = "BIZ UDゴシック", FontSize = 14, WindowWidth = 1000 };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.Equal("BIZ UDゴシック", loaded.FontName);
            Assert.Equal(14, loaded.FontSize);
            Assert.Equal(1000, loaded.WindowWidth);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Corrupt_file_returns_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            var s = SettingsStore.Load(path);
            Assert.Equal(new AppSettings().FontSize, s.FontSize);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

**Step 2: 失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter SettingsStoreTests`
Expected: FAIL（未定義）。

**Step 3: 実装**

`src/yEdit.Core/Settings/AppSettings.cs`:
```csharp
namespace yEdit.Core.Settings;

/// <summary>永続化するアプリ設定（v0.1 最小キー）。今後マイルストーンで拡張。</summary>
public sealed class AppSettings
{
    public string FontName { get; set; } = "ＭＳ ゴシック";
    public float FontSize { get; set; } = 12f;
    public int WindowWidth { get; set; } = 960;
    public int WindowHeight { get; set; } = 640;
    /// <summary>新規ファイル・既定の保存文字コード（コードページ）。</summary>
    public int DefaultCodePage { get; set; } = 65001;
    /// <summary>新規ファイルの既定改行（0=CRLF,1=LF,2=CR）。</summary>
    public int DefaultLineEnding { get; set; } = 0;
}
```

`src/yEdit.Core/Settings/SettingsStore.cs`:
```csharp
using System.Text.Json;

namespace yEdit.Core.Settings;

/// <summary>settings.json の読み書き。壊れていれば既定値で続行（握り潰さず既定へ）。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>既定の設定ファイルパス（%APPDATA%\yEdit\settings.json）。</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yEdit", "settings.json");

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public static void Save(string path, AppSettings settings)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(path, json);
    }
}
```

**Step 4: テスト成功を確認**

Run: `dotnet test tests/yEdit.Core.Tests --filter SettingsStoreTests`
Expected: PASS。

**Step 5: 全 Core テスト確認＋コミット**

Run: `dotnet test tests/yEdit.Core.Tests`
Expected: 全 PASS。
```bash
git add src/yEdit.Core/Settings tests/yEdit.Core.Tests/Settings
git commit -m "M1: 設定 AppSettings/SettingsStore（TDD）"
```

---

## フェーズC: yEdit.Editor — ホスト本番化

### Task C1: yEdit.Editor プロジェクト作成とホスト移設

**Files:**
- Create: `src/yEdit.Editor/yEdit.Editor.csproj`
- Move: `src/yEdit.ScintillaProbe/{ScintillaHost,ScreenReaders,Sci,NativeMethods}.cs` → `src/yEdit.Editor/`
- Modify: `src/yEdit.ScintillaProbe/yEdit.ScintillaProbe.csproj`（Editor 参照に変更）
- Modify: `src/yEdit.ScintillaProbe/MainForm.cs`（`using yEdit.Editor;` 追加）

**Step 1: プロジェクト生成**

Run:
```bash
dotnet new classlib -n yEdit.Editor -o src/yEdit.Editor -f net9.0-windows
rm src/yEdit.Editor/Class1.cs
dotnet sln yEdit.sln add src/yEdit.Editor/yEdit.Editor.csproj
```

**Step 2: csproj を設定**

`src/yEdit.Editor/yEdit.Editor.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Scintilla5.NET" Version="6.1.2" />
    <ProjectReference Include="..\yEdit.Accessibility\yEdit.Accessibility.csproj" />
  </ItemGroup>
</Project>
```

**Step 3: ファイルを移動し namespace を変更**

`ScintillaHost.cs` / `ScreenReaders.cs` / `Sci.cs` / `NativeMethods.cs` を `src/yEdit.ScintillaProbe/` から `src/yEdit.Editor/` へ移動。各ファイルの `namespace yEdit.ScintillaProbe;` を `namespace yEdit.Editor;` に変更。`ScreenReaders` の可視性を `internal static` → `public static` に変更（App から使う）。`Sci` / `NativeMethods` は `internal` のままで可（同アセンブリ内）。

**Step 4: ScintillaProbe を Editor 参照に変更**

`src/yEdit.ScintillaProbe/yEdit.ScintillaProbe.csproj` の ItemGroup を:
```xml
  <ItemGroup>
    <ProjectReference Include="..\yEdit.Editor\yEdit.Editor.csproj" />
  </ItemGroup>
```
（`Scintilla5.NET` と `yEdit.Accessibility` への直接参照は Editor 経由になるため削除。ただし Probe の MainForm が `ControlType`/`UiaDiag` 等 Accessibility 型を直接使う場合は `yEdit.Accessibility` 参照を残す。）

`src/yEdit.ScintillaProbe/MainForm.cs` と `Program.cs` の先頭に `using yEdit.Editor;` を追加（`ScintillaHost`/`ScreenReaders` 参照のため）。

**Step 5: ソリューション全体ビルド**

Run: `dotnet build yEdit.sln`
Expected: 全プロジェクト成功（0 warning）。ScintillaProbe が Editor の `ScintillaHost` を使ってビルドできること。

**Step 6: 移設後の SR 非依存検証（無回帰確認）**

Run:
```bash
dotnet build src/yEdit.ScintillaProbe
& "tools/verify-uia-sci.ps1"
```
Expected: 既存と同じく要素発見・TextPattern・選択往復・空行 len=0 が PASS（probe の実行 exe パスは変わらないため tools はそのまま）。
> 失敗時: 移設で `_provider` 生成や WM_GETOBJECT 経路が壊れていないか確認。

**Step 7: コミット**

```bash
git add -A
git commit -m "M1: yEdit.Editor 新設し ScintillaHost 群を移設・本番化（probe は Editor 参照へ）"
```

---

### Task C2: SR 適応設定を Editor に集約

probe の `MainForm` にあった「NVDA 検出 → ServeUiaProvider/SuppressClientMsaa 切替」ロジックを Editor 側のメソッドに集約し、App から1行で呼べるようにする（鉄則②をEditorに封じ込め）。

**Files:**
- Modify: `src/yEdit.Editor/ScintillaHost.cs`

**Step 1: 実装（ScintillaHost にメソッド追加）**

`ScintillaHost` に追加:
```csharp
/// <summary>
/// 起動中のスクリーンリーダーに応じて UIA/MSAA の提供可否を確定する（確定アーキテクチャ）。
/// NVDA 起動中 → 我々は引っ込む（ネイティブ Scintilla に任せる）。それ以外 → UIA 提供。
/// ハンドル生成前に呼ぶこと（WM_GETOBJECT 前に値を確定させる）。
/// </summary>
public void ConfigureForCurrentScreenReader()
{
    if (ScreenReaders.IsNvdaRunning())
    {
        ServeUiaProvider = false;
        SuppressClientMsaa = true;
    }
    else
    {
        ServeUiaProvider = true;
        SuppressClientMsaa = false;
    }
}
```

**Step 2: ビルド確認**

Run: `dotnet build src/yEdit.Editor`
Expected: 成功。

**Step 3: コミット**

```bash
git add src/yEdit.Editor/ScintillaHost.cs
git commit -m "M1: SR 適応設定 ConfigureForCurrentScreenReader を Editor に集約"
```

---

### Task C3: 状態取得用の SCI 定数を追加

App のステータスバー（行・桁）と EOL 設定で使う定数を `Sci.cs` に足す（ScintillaNET のマネージドプロパティで代替できるものは App 側でそちらを使うが、不足時の保険）。

**Files:**
- Modify: `src/yEdit.Editor/Sci.cs`

**Step 1: 追記**

`Sci.cs` に追加:
```csharp
    public const int SCI_LINEFROMPOSITION = 2166;   // byte pos → 行番号(0始まり)
    public const int SCI_GETCOLUMN = 2129;          // byte pos → 桁(0始まり, タブ考慮)
    public const int SCI_SETEOLMODE = 2031;         // 0=CRLF,1=CR,2=LF
    public const int SCI_GETMODIFY = 2159;          // 変更フラグ
    public const int SCI_SETSAVEPOINT = 2014;       // 保存点を設定（clean 化）
    public const int SCI_EMPTYUNDOBUFFER = 2175;    // Undo バッファ消去
```

**Step 2: ビルド確認＋コミット**

Run: `dotnet build src/yEdit.Editor`
Expected: 成功。
```bash
git add src/yEdit.Editor/Sci.cs
git commit -m "M1: 行桁/EOL/保存点用の SCI 定数を追加"
```

---

## フェーズD: yEdit.App — シェル（単一ドキュメント）

> 以降の UI は単体テストが難しいため、各タスクは「ビルド成功＋起動して手動確認」で検証する。最終の実機SR検証は Task F1。ScintillaNET のマネージド API（`Text`/`CurrentLine`/`GetColumn`/`EolMode`/`Modified`/`SetSavePoint()`/`Undo()`/`Redo()`/`Cut()`/`Copy()`/`Paste()`/`SelectAll()`）を優先利用する。

### Task D1: App プロジェクト作成と最小起動

**Files:**
- Create: `src/yEdit.App/yEdit.App.csproj`
- Create: `src/yEdit.App/Program.cs`
- Create: `src/yEdit.App/MainForm.cs`

**Step 1: プロジェクト生成**

Run:
```bash
dotnet new winforms -n yEdit.App -o src/yEdit.App -f net9.0-windows
rm src/yEdit.App/Form1.cs
dotnet sln yEdit.sln add src/yEdit.App/yEdit.App.csproj
dotnet add src/yEdit.App/yEdit.App.csproj reference src/yEdit.Editor/yEdit.Editor.csproj src/yEdit.Core/yEdit.Core.csproj
```

**Step 2: csproj 調整**

`src/yEdit.App/yEdit.App.csproj` の PropertyGroup に `UseWPF` を足す（UIA 型の連鎖参照のため）。`Nullable`/`ImplicitUsings` は enable。`<ApplicationManifest>` で DPI/UIA 対応は後続で。

**Step 3: Program.cs**

`src/yEdit.App/Program.cs`:
```csharp
using System.Text;
using yEdit.Core.Text;

namespace yEdit.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Shift_JIS/EUC-JP を使うため CodePagesEncodingProvider を登録（Core も内部登録するが明示）。
        EncodingCatalog.EnsureRegistered();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
```

**Step 4: MainForm.cs（最小・エディタ配置のみ）**

`src/yEdit.App/MainForm.cs`:
```csharp
using yEdit.Editor;

namespace yEdit.App;

public sealed partial class MainForm : Form
{
    private readonly ScintillaHost _editor;

    public MainForm()
    {
        Text = "yEdit";
        Width = 960;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;

        _editor = new ScintillaHost { Dock = DockStyle.Fill };
        _editor.ConfigureForCurrentScreenReader(); // ハンドル生成前に SR 適応を確定
        Controls.Add(_editor);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _editor.Focus();
    }
}
```

**Step 5: ビルド＆起動確認**

Run: `dotnet build src/yEdit.App`
Expected: 成功。
Run: `dotnet run --project src/yEdit.App`
Expected: ウィンドウが出てエディタにフォーカス。文字入力・Undo/Redo・コピペが動く。

**Step 6: コミット**

```bash
git add src/yEdit.App yEdit.sln
git commit -m "M1: yEdit.App 新設（最小起動・エディタ配置・SR 適応）"
```

---

### Task D2: メニューバーとステータスバー

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`
- Create: `src/yEdit.App/DocumentState.cs`

**Step 1: DocumentState（単一ドキュメントの状態）**

`src/yEdit.App/DocumentState.cs`:
```csharp
using System.Text;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// 現在開いているドキュメントの状態。v0.1 は単一だが、将来 DocumentManager で
/// タブ毎に持てるよう独立クラスにしておく（design M2 への布石）。
/// </summary>
public sealed class DocumentState
{
    public string? Path { get; set; }              // 未保存なら null
    public Encoding Encoding { get; set; } = EncodingCatalog.Get(65001);
    public bool HasBom { get; set; }
    public LineEnding LineEnding { get; set; } = LineEnding.Crlf;

    public string DisplayName => Path is null ? "無題" : System.IO.Path.GetFileName(Path);
}
```

**Step 2: MainForm にメニュー・ステータスバーを追加**

`MainForm.cs` を拡張（メニュー: ファイル/編集/ヘルプ、ステータス: 行桁・文字コード・改行）。骨子:
```csharp
using yEdit.Core.Settings;
using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.App;

public sealed partial class MainForm : Form
{
    private readonly ScintillaHost _editor;
    private readonly DocumentState _doc = new();
    private readonly ToolStripStatusLabel _posLabel = new("行 1, 桁 1");
    private readonly ToolStripStatusLabel _encLabel = new("UTF-8");
    private readonly ToolStripStatusLabel _eolLabel = new("CRLF");
    private readonly string _settingsPath = SettingsStore.DefaultPath;
    private AppSettings _settings = new();

    public MainForm()
    {
        _settings = SettingsStore.Load(_settingsPath);

        Text = "yEdit";
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        StartPosition = FormStartPosition.CenterScreen;

        _editor = new ScintillaHost { Dock = DockStyle.Fill };
        _editor.ConfigureForCurrentScreenReader();
        ApplyFont();

        var menu = BuildMenu();
        var status = BuildStatusBar();

        Controls.Add(_editor);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        _editor.UpdateUI += (_, _) => UpdateStatus();
        // 変更/保存点で件名（dirty）更新
        _editor.SavePointLeft += (_, _) => UpdateTitle();
        _editor.SavePointReached += (_, _) => UpdateTitle();

        UpdateTitle();
        UpdateStatus();
    }

    private void ApplyFont()
        => _editor.Styles[ScintillaNET.Style.Default].Font = _settings.FontName; // size 等は M7 で詳細化

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("ファイル(&F)");
        file.DropDownItems.Add("新規(&N)", null, (_, _) => NewFile()).ShortcutKeys = Keys.Control | Keys.N;
        file.DropDownItems.Add("開く(&O)...", null, (_, _) => OpenFile()).ShortcutKeys = Keys.Control | Keys.O;
        file.DropDownItems.Add("文字コードを指定して開き直す(&R)...", null, (_, _) => ReopenWithEncoding());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("上書き保存(&S)", null, (_, _) => Save()).ShortcutKeys = Keys.Control | Keys.S;
        file.DropDownItems.Add("名前を付けて保存(&A)...", null, (_, _) => SaveAs());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("終了(&X)", null, (_, _) => Close());

        var edit = new ToolStripMenuItem("編集(&E)");
        edit.DropDownItems.Add("元に戻す(&U)", null, (_, _) => _editor.Undo()).ShortcutKeys = Keys.Control | Keys.Z;
        edit.DropDownItems.Add("やり直し(&R)", null, (_, _) => _editor.Redo()).ShortcutKeys = Keys.Control | Keys.Y;
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add("切り取り(&T)", null, (_, _) => _editor.Cut()).ShortcutKeys = Keys.Control | Keys.X;
        edit.DropDownItems.Add("コピー(&C)", null, (_, _) => _editor.Copy()).ShortcutKeys = Keys.Control | Keys.C;
        edit.DropDownItems.Add("貼り付け(&P)", null, (_, _) => _editor.Paste()).ShortcutKeys = Keys.Control | Keys.V;
        edit.DropDownItems.Add("すべて選択(&A)", null, (_, _) => _editor.SelectAll()).ShortcutKeys = Keys.Control | Keys.A;

        var help = new ToolStripMenuItem("ヘルプ(&H)");
        help.DropDownItems.Add("バージョン情報(&A)", null, (_, _) =>
            MessageBox.Show("yEdit v0.1", "バージョン情報", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, help });
        return menu;
    }

    private StatusStrip BuildStatusBar()
    {
        var strip = new StatusStrip();
        _posLabel.Spring = true;
        _posLabel.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.AddRange(new ToolStripItem[] { _posLabel, _encLabel, _eolLabel });
        return strip;
    }

    private void UpdateStatus()
    {
        int line = _editor.CurrentLine + 1;
        int col = _editor.GetColumn(_editor.CurrentPosition) + 1;
        _posLabel.Text = $"行 {line}, 桁 {col}";
        _encLabel.Text = EncodingDisplayName(_doc.Encoding, _doc.HasBom);
        _eolLabel.Text = _doc.LineEnding switch
        {
            LineEnding.Crlf => "CRLF", LineEnding.Lf => "LF", _ => "CR"
        };
    }

    private void UpdateTitle()
        => Text = $"{(_editor.Modified ? "* " : "")}{_doc.DisplayName} - yEdit";

    private static string EncodingDisplayName(Encoding enc, bool bom) => enc.CodePage switch
    {
        65001 => bom ? "UTF-8 (BOM)" : "UTF-8",
        932 => "Shift_JIS",
        51932 => "EUC-JP",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        _ => enc.WebName,
    };
}
```
> ファイル操作メソッド（NewFile/OpenFile/ReopenWithEncoding/Save/SaveAs）は Task E1〜E3 で実装。ここでは未定義参照を避けるため空メソッドの stub を置いてビルドを通す。

**Step 3: stub を置いてビルド**

`NewFile()`/`OpenFile()`/`ReopenWithEncoding()`/`Save()`/`SaveAs()` を `private void X() { }` の空実装で追加し、ビルドを通す。

Run: `dotnet build src/yEdit.App`
Expected: 成功。

**Step 4: 起動確認**

Run: `dotnet run --project src/yEdit.App`
Expected: メニューとステータスバーが表示。カーソル移動でステータスの行桁が更新。

**Step 5: コミット**

```bash
git add src/yEdit.App
git commit -m "M1: メニュー/ステータスバー/DocumentState（ファイル操作は stub）"
```

---

## フェーズE: ファイル I/O 配線

### Task E1: 新規・開く

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`

**Step 1: 実装**

stub を実装に差し替え:
```csharp
private bool ConfirmDiscardIfDirty()
{
    if (!_editor.Modified) return true;
    var r = MessageBox.Show(
        $"{_doc.DisplayName} の変更を保存しますか？",
        "yEdit", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
    return r switch
    {
        DialogResult.Yes => Save(),
        DialogResult.No => true,
        _ => false,
    };
}

private void NewFile()
{
    if (!ConfirmDiscardIfDirty()) return;
    _editor.Text = string.Empty;
    _doc.Path = null;
    _doc.Encoding = EncodingCatalog.Get(_settings.DefaultCodePage);
    _doc.HasBom = false;
    _doc.LineEnding = (LineEnding)_settings.DefaultLineEnding;
    ApplyEol();
    _editor.EmptyUndoBuffer();
    _editor.SetSavePoint();
    UpdateTitle();
    UpdateStatus();
}

private void OpenFile()
{
    if (!ConfirmDiscardIfDirty()) return;
    using var dlg = new OpenFileDialog { Filter = "テキスト ファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
    if (dlg.ShowDialog(this) != DialogResult.OK) return;
    LoadPath(dlg.FileName, forcedCodePage: null);
}

private void LoadPath(string path, int? forcedCodePage)
{
    try
    {
        var doc = TextFileService.Load(path, forcedCodePage);
        _doc.Path = path;
        _doc.Encoding = doc.Encoding;
        _doc.HasBom = doc.HasBom;
        _doc.LineEnding = doc.LineEnding;

        _editor.Text = doc.Text;
        ApplyEol();
        _editor.EmptyUndoBuffer();
        _editor.SetSavePoint();

        UpdateTitle();
        UpdateStatus();

        if (doc.HadReplacementChar)
        {
            MessageBox.Show(
                "このファイルには現在の文字コードで表せない文字（置換文字）が含まれています。" +
                "別の文字コードで開き直してください。",
                "文字コードの警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    catch (Exception ex)
    {
        MessageBox.Show($"開けませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

private void ApplyEol()
    => _editor.EolMode = _doc.LineEnding switch
    {
        LineEnding.Crlf => ScintillaNET.Eol.CrLf,
        LineEnding.Lf => ScintillaNET.Eol.Lf,
        _ => ScintillaNET.Eol.Cr,
    };
```
> `_editor.Text = doc.Text;` の後、改行が混在していても EolMode 設定だけで足りる（既存改行は保持）。統一したい場合は `_editor.ConvertEols(_editor.EolMode);` を足す（buffer を modified にするので直後に `SetSavePoint()`）。v0.1 は ConvertEols を呼んで正規化する方針とし、保存時に `_doc.LineEnding` の改行で書き出す。

**Step 2: ビルド確認**

Run: `dotnet build src/yEdit.App`
Expected: 成功。

**Step 3: 起動して手動確認**

Run: `dotnet run --project src/yEdit.App`
- UTF-8/Shift_JIS/EUC-JP の .txt を開き、本文・ステータスの文字コード表示・改行表示が正しいこと。
- 変更後に「開く」→ 保存確認ダイアログが出ること。

**Step 4: コミット**

```bash
git add src/yEdit.App/MainForm.cs
git commit -m "M1: 新規・開く・dirty 確認・置換文字警告を実装"
```

---

### Task E2: 上書き保存・名前を付けて保存

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`

**Step 1: 実装**

```csharp
private bool Save()
{
    if (_doc.Path is null) return SaveAs();
    return WriteToPath(_doc.Path);
}

private bool SaveAs()
{
    using var dlg = new SaveFileDialog { Filter = "テキスト ファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
    if (_doc.Path is not null) dlg.FileName = System.IO.Path.GetFileName(_doc.Path);
    if (dlg.ShowDialog(this) != DialogResult.OK) return false;
    if (!WriteToPath(dlg.FileName)) return false;
    _doc.Path = dlg.FileName;
    UpdateTitle();
    return true;
}

private bool WriteToPath(string path)
{
    try
    {
        // buffer を _doc.LineEnding に正規化してから本文取得（改行コード保持）。
        ApplyEol();
        _editor.ConvertEols(_editor.EolMode);
        TextFileService.Save(path, _editor.Text, _doc.Encoding, _doc.HasBom);
        _editor.SetSavePoint();
        UpdateTitle();
        return true;
    }
    catch (Exception ex)
    {
        MessageBox.Show($"保存できませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
    }
}
```

**Step 2: ビルド＆手動確認**

Run: `dotnet build src/yEdit.App`
Expected: 成功。
Run: `dotnet run --project src/yEdit.App`
- 新規→入力→保存→指定文字コードで読めること（別ツールやアプリ再起動で確認）。
- 既存ファイルの上書き保存後、文字コード・改行が維持されること。
- 保存後にタイトルの `*`（dirty 印）が消えること。

**Step 3: コミット**

```bash
git add src/yEdit.App/MainForm.cs
git commit -m "M1: 上書き保存・名前を付けて保存（原子的保存・改行保持）"
```

---

### Task E3: 文字コードを指定して開き直す

**Files:**
- Create: `src/yEdit.App/EncodingPickDialog.cs`
- Modify: `src/yEdit.App/MainForm.cs`

**Step 1: 文字コード選択ダイアログ**

`src/yEdit.App/EncodingPickDialog.cs`（アクセシブルな ComboBox + OK/キャンセル）:
```csharp
namespace yEdit.App;

public sealed class EncodingPickDialog : Form
{
    private readonly ComboBox _combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    public int SelectedCodePage { get; private set; } = 65001;

    private static readonly (string Name, int Cp)[] Choices =
    {
        ("UTF-8", 65001), ("Shift_JIS", 932), ("EUC-JP", 51932),
        ("UTF-16 LE", 1200), ("UTF-16 BE", 1201),
    };

    public EncodingPickDialog(int currentCodePage)
    {
        Text = "文字コードを指定して開き直す";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        ClientSize = new Size(320, 110);

        var label = new Label { Text = "文字コード(&E):", AutoSize = true, Left = 12, Top = 16 };
        _combo.Left = 12; _combo.Top = 38;
        foreach (var c in Choices) _combo.Items.Add(c.Name);
        _combo.SelectedIndex = Math.Max(0, Array.FindIndex(Choices, c => c.Cp == currentCodePage));

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 150, Top = 72, Width = 75 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Left = 232, Top = 72, Width = 75 };
        ok.Click += (_, _) => SelectedCodePage = Choices[_combo.SelectedIndex].Cp;

        Controls.AddRange(new Control[] { label, _combo, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;
    }
}
```

**Step 2: ReopenWithEncoding を実装**

```csharp
private void ReopenWithEncoding()
{
    if (_doc.Path is null)
    {
        MessageBox.Show("ファイルを開いてから実行してください。", "yEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }
    if (!ConfirmDiscardIfDirty()) return;
    using var dlg = new EncodingPickDialog(_doc.Encoding.CodePage);
    if (dlg.ShowDialog(this) != DialogResult.OK) return;
    LoadPath(_doc.Path, forcedCodePage: dlg.SelectedCodePage);
}
```

**Step 3: ビルド＆手動確認**

Run: `dotnet build src/yEdit.App`
Expected: 成功。
Run: `dotnet run --project src/yEdit.App`
- 文字化けしたファイルを開き直しで Shift_JIS/EUC-JP を選ぶと正しく表示されること。

**Step 4: コミット**

```bash
git add src/yEdit.App
git commit -m "M1: 文字コードを指定して開き直す（EncodingPickDialog）"
```

---

### Task E4: ウィンドウ終了時の dirty 確認と設定保存

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`

**Step 1: 実装（OnFormClosing）**

```csharp
protected override void OnFormClosing(FormClosingEventArgs e)
{
    if (!ConfirmDiscardIfDirty()) { e.Cancel = true; return; }
    // ウィンドウサイズを設定に保存（最小キーのみ）。
    _settings.WindowWidth = Width;
    _settings.WindowHeight = Height;
    try { SettingsStore.Save(_settingsPath, _settings); } catch { /* 設定保存失敗は致命でない */ }
    base.OnFormClosing(e);
}
```

**Step 2: ビルド＆手動確認**

Run: `dotnet build src/yEdit.App`
Expected: 成功。
- 変更ありで×→保存確認。キャンセルで閉じない。
- リサイズ→終了→再起動でサイズが復元（settings.json 書き込み確認）。

**Step 3: コミット**

```bash
git add src/yEdit.App/MainForm.cs
git commit -m "M1: 終了時 dirty 確認・ウィンドウサイズの設定永続化"
```

---

## フェーズF: 統合・検証・マージ

### Task F1: 実機 SR 手動検証（DoD 中核）

**Step 1: リリース起動**

Run: `dotnet run --project src/yEdit.App -c Release`

**Step 2: PC-Talker で確認**（PC-Talker 起動中＝自動で UIA モード）
- ファイルを開き、↑↓で行・←→で文字が読み上げられる。
- 日本語入力（ATOK/IME オン）が読み上げられる。
- 別窓へ移動→戻る（リフォーカス）で即読み（2秒待ちが無い）。
- 全角／半角スペースが区別される（既知課題なら記録）。
- 保存・開き直しの結果が読み上げで把握できる。

**Step 3: NVDA で確認**（NVDA 起動中＝自動でネイティブ譲り）
- ファイルを開き、↑↓↑↓・←→でネイティブ Scintilla 読みが機能。
- 我々の UIA を出していない（無音化していない）こと。

**Step 4: 結果を記録**

`docs/HANDOFF-scintilla-uia.md` に「M1 実機検証結果」節を追記（PASS/課題）。空行読み・座標APIの残課題は M6 へ送る旨を明記。

```bash
git add docs/HANDOFF-scintilla-uia.md
git commit -m "M1: 実機 SR 検証結果を記録"
```

---

### Task F2: 仕上げ確認

**Step 1: ソリューション全体ビルド（0 warning）**

Run: `dotnet build yEdit.sln -c Release`
Expected: 0 warning / 0 error。warning があれば解消。

**Step 2: 全テスト**

Run: `dotnet test`
Expected: 全 PASS。

**Step 3: probe 無回帰の最終確認**

Run: `& "tools/verify-uia-sci.ps1"`; `& "tools/walk-test-sci.ps1"`
Expected: 既存どおり PASS（Move スパン保持＝PC-Talker 致命バグ無回帰）。

---

### Task F3: 別エージェントレビュー → no-ff マージ

**Step 1: コードレビュー依頼**

別エージェント（superpowers:requesting-code-review / code-reviewer）に M1 差分のレビューを依頼（メモリ [[review-by-separate-agent]]）。指摘を反映。

**Step 2: main へ no-ff マージ**（[[phase-work-git-flow]]）

Run:
```bash
git switch main
git merge --no-ff feature/m1-walking-skeleton -m "M1: v0.1 ウォーキングスケルトン（編集・ファイルI/O・文字コード・SR適応）"
```

**Step 3: 完了確認**

Run: `git log --oneline --graph -10`
Expected: no-ff マージコミットがある。

---

## M1 完了の定義（DoD）

- [ ] `dotnet build yEdit.sln` が 0 warning / `dotnet test` 全 PASS。
- [ ] UTF-8(BOM有/無)・Shift_JIS・EUC-JP・UTF-16 の開く／保存／開き直しが正しい（往復一致）。
- [ ] 改行コード（CRLF/LF/CR）が判定・保持される。
- [ ] 原子的保存（temp→差し替え、temp 残骸なし）。
- [ ] dirty 追跡・終了時確認・ウィンドウサイズ永続化。
- [ ] **PC-Talker で開く・編集・保存・カーソル読みが機能**。
- [ ] **NVDA でネイティブ読みが機能（無音化しない）**。
- [ ] probe の SR 非依存検証が無回帰。
- [ ] 別エージェントレビュー済み・main へ no-ff マージ済み。

## スコープ外（後続マイルストーンへ）

タブ（M2）、検索・置換（M3）、grep（M4）、バックアップ/復元（M5）、SR照会ホットキー・空行読み決着・座標API（M6）、設定ダイアログ・外観（M7）、シンタックスハイライト（M8）。
