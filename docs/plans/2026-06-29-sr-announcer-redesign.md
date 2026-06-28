# SR適応 Announcer 再設計 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Announcer を抽象化し、yEdit 起動時に確定した SR 判定（NVDA/PC-Talker）で発声実体（UiaAnnouncer / PcTalkerAnnouncer）を選ぶ構造へ再設計し、PC-Talker で実際に音が鳴るよう発声手段を `PCTKPReadW(text,1,1)` 既定＋1箇所差し替え可能にする。

**Architecture:** 選択ロジックは Core の純関数 `SrSpeechSelector.Select(nvda, pcTalker) → SpeechMode` として単体テスト。App 側で `IAnnouncer` を `UiaAnnouncer`/`PcTalkerAnnouncer` が実装し、`AnnouncerFactory` が起動時キャッシュしたモードで実体を生成。視覚表示（`label.Text`）は全モードで無条件。PC-Talker のハード失敗（false/例外）時のみ UIA 通知へ退避。

**Tech Stack:** .NET / WinForms（`yEdit.App`）、純ロジックは `yEdit.Core`、テストは xUnit（`yEdit.Core.Tests`）、P/Invoke 遅延束縛（`PCTKUsr.dll`）、UIA `RaiseAutomationNotification`。

**設計根拠:** `docs/plans/2026-06-29-sr-announcer-redesign-design.md`

**全タスク共通の確認コマンド（リポジトリルート `<repo>` で実行）:**
- ビルド: `dotnet build yEdit.sln`（期待: 0 警告・0 エラー）
- Core テスト: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`

---

### Task 1: Core — SpeechMode 列挙と SrSpeechSelector（純ロジック・TDD）

**Files:**
- Create: `src/yEdit.Core/Speech/SpeechMode.cs`
- Create: `src/yEdit.Core/Speech/SrSpeechSelector.cs`
- Create: `tests/yEdit.Core.Tests/Speech/SrSpeechSelectorTests.cs`
- Delete: `tests/yEdit.Core.Tests/Speech/SpeechRouterTests.cs`（旧・毎回ルーティング版テスト。`SrSpeechSelector` のテストへ置換）

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Speech/SrSpeechSelectorTests.cs` を作成:

```csharp
using yEdit.Core.Speech;

namespace yEdit.Core.Tests.Speech;

public class SrSpeechSelectorTests
{
    [Fact]
    public void PcTalker_running_and_no_nvda_selects_pctalker()
        => Assert.Equal(SpeechMode.PcTalker, SrSpeechSelector.Select(nvdaRunning: false, pcTalkerRunning: true));

    [Fact]
    public void Nvda_running_selects_uia_even_if_pctalker_running()
        => Assert.Equal(SpeechMode.Uia, SrSpeechSelector.Select(nvdaRunning: true, pcTalkerRunning: true));

    [Fact]
    public void Nvda_only_selects_uia()
        => Assert.Equal(SpeechMode.Uia, SrSpeechSelector.Select(nvdaRunning: true, pcTalkerRunning: false));

    [Fact]
    public void Neither_running_defaults_to_uia()
        => Assert.Equal(SpeechMode.Uia, SrSpeechSelector.Select(nvdaRunning: false, pcTalkerRunning: false));
}
```

そして旧テストを削除:

```bash
git rm tests/yEdit.Core.Tests/Speech/SpeechRouterTests.cs
```

**Step 2: テストが失敗することを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
Expected: コンパイルエラー（`SpeechMode` / `SrSpeechSelector` 未定義）

**Step 3: 最小実装を書く**

`src/yEdit.Core/Speech/SpeechMode.cs`:

```csharp
namespace yEdit.Core.Speech;

/// <summary>SR適応の発声モード。起動時に一度だけ確定する。</summary>
public enum SpeechMode
{
    /// <summary>UIA 通知（RaiseAutomationNotification）。NVDA・その他SR・既定。</summary>
    Uia,
    /// <summary>PC-Talker 直叩き（PCTKUsr.dll）。</summary>
    PcTalker,
}
```

`src/yEdit.Core/Speech/SrSpeechSelector.cs`:

```csharp
namespace yEdit.Core.Speech;

/// <summary>
/// 起動中SRから発声モードを選ぶ純ロジック（WinForms非依存・単体テスト可能）。
/// NVDA 優先（受動読みパス ConfigureForCurrentScreenReader と同じ鉄則）。
/// PC-Talker 専用直叩きは「PC-Talker 稼働かつ NVDA 非稼働」のときのみ。それ以外は無害な既定 Uia。
/// </summary>
public static class SrSpeechSelector
{
    public static SpeechMode Select(bool nvdaRunning, bool pcTalkerRunning)
        => (pcTalkerRunning && !nvdaRunning) ? SpeechMode.PcTalker : SpeechMode.Uia;
}
```

**Step 4: テストが通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
Expected: PASS（4 件緑、既存テストも緑のまま）

**Step 5: コミット**

```bash
git add src/yEdit.Core/Speech/SpeechMode.cs src/yEdit.Core/Speech/SrSpeechSelector.cs tests/yEdit.Core.Tests/Speech/SrSpeechSelectorTests.cs
git rm tests/yEdit.Core.Tests/Speech/SpeechRouterTests.cs
git commit -m "SR発声モード選択ロジックをCore純関数SrSpeechSelectorに新設（旧SpeechRouterTests置換）"
```

---

### Task 2: App — PcTalkerSpeech に静的 Speak を集約（既定 priority=1）

**Files:**
- Modify: `src/yEdit.App/Speech/PcTalkerSpeech.cs`

**注意:** この段階ではまだ `ISpeechChannel` / `UiaNotificationSpeech` / `SpeechRouter` / `SrNotify` / `Announcer` は残す（削除は Task 4）。本タスクは PcTalkerSpeech から `ISpeechChannel` 実装を外し、静的 `Speak` を追加するだけ。各タスク終了時にビルド緑を保つ。

**Step 1: PcTalkerSpeech を書き換える**

`src/yEdit.App/Speech/PcTalkerSpeech.cs` を以下で置換（`using yEdit.Core.Speech;` と `: ISpeechChannel`、`Name`/`TrySpeak` を削除し、静的 `Speak(string): bool` を追加。既定は割り込み `priority=1`）:

```csharp
using System.Runtime.InteropServices;

namespace yEdit.App.Speech;

/// <summary>
/// PC-Talker（高知システム開発／AOK）への直接発声。共有DLL PCTKUsr.dll（PC-Talker本体が System32 に導入）の
/// ネイティブ関数を遅延束縛で呼ぶ。同梱不要。
/// - 稼働判定: PCTKStatus()（PC-Talker稼働中=非0、停止中=0）。ブランド・バージョン非依存。
/// - 発話: 既定 PCTKPReadW(text, 1, 1)（priority=1 割り込み・analyze=1）。yEdit 実行時の文脈で
///   priority=0（キュー）が埋もれ無音化したため割り込みを既定にする。実機で無音なら Speak() の
///   呼び出し行のみを差し替えて再検証する（下記コメント参照）。
/// 非PC-Talker機ではDLL/関数が見つからず IsRunning=false・Speak=false（→ 呼び出し側が UIA へ退避）。
/// 静的 [DllImport("PCTKUsr.dll")] は非PC-Talker機で DllNotFoundException を誘発し得るため避け、LoadLibrary/GetProcAddress を使う。
/// </summary>
internal static class PcTalkerSpeech
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PcTkpReadDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string text, int priority, int analyze);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PcTkStatusDelegate();

    private static PcTkpReadDelegate? _read;
    private static PcTkStatusDelegate? _status;
    private static bool _resolved;

    private static void EnsureResolved()
    {
        if (_resolved) return;
        try
        {
            nint h = LoadLibraryW("PCTKUsr.dll");
            if (h != 0)
            {
                nint pr = GetProcAddress(h, "PCTKPReadW");
                nint ps = GetProcAddress(h, "PCTKStatus");
                if (pr != 0) _read = Marshal.GetDelegateForFunctionPointer<PcTkpReadDelegate>(pr);
                if (ps != 0) _status = Marshal.GetDelegateForFunctionPointer<PcTkStatusDelegate>(ps);
            }
        }
        catch { _read = null; _status = null; }
        _resolved = true; // 成功・失敗の両方を解決完了後にキャッシュ（順序ハザード回避）
    }

    /// <summary>PC-Talker が現在稼働中か（PCTKStatus が非0）。DLL/関数が無ければ false。</summary>
    public static bool IsRunning()
    {
        EnsureResolved();
        if (_status is null) return false;
        try { return _status() != 0; }
        catch { return false; }
    }

    /// <summary>
    /// PC-Talker で発声する。発声手段はここ1箇所に集約。戻り値はハード成否（true=呼べた／false=DLL未解決・例外）。
    /// 無音でも false にはならない点に注意（DLL は可聴を通知しない）。実機で無音なら下記の呼び出し行を差し替える。
    /// </summary>
    public static bool Speak(string message)
    {
        EnsureResolved();
        if (_read is null) return false;
        try
        {
            _read(message, 1, 1); // 既定: priority=1（割り込み）, analyze=1
            // 差し替え候補（実機で無音なら上行をいずれかに変更して再検証）:
            //   _read(message, 0, 1);   // 旧実装（非割り込み・キュー）
            //   PCTKCGuide 等のガイド系（別途 GetProcAddress("PCTKCGuide") の解決と delegate 定義が必要）
            return true;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string procName);
}
```

**Step 2: ビルドを確認**

Run: `dotnet build yEdit.sln`
Expected: 0 警告・0 エラー。

> 補足: この時点で `SpeechRouter` / `SrNotify` はまだ `ISpeechChannel`（`PcTalkerSpeech` ではなく旧実装を介して）を参照しているが、`SrNotify` は `new PcTalkerSpeech()` を呼んでいた。PcTalkerSpeech を static 化したため `SrNotify.cs` の `new PcTalkerSpeech()` と `PcTalkerSpeech.IsRunning` 参照がコンパイルエラーになる。**Task 2 では `SrNotify` も暫定修正が必要**: 下記 Step 3 を実施。

**Step 3: SrNotify の暫定コンパイル修正（Task 4 で完全削除する前のつなぎ）**

`src/yEdit.App/SrNotify.cs` の静的フィールド初期化が `new PcTalkerSpeech()` を使えなくなるため、暫定的に旧 `SpeechRouter` 経路を切り、直接分岐に置換する（このファイルは Task 4 で削除予定だが、各タスクでビルド緑を保つための最小修正）:

```csharp
using System.Windows.Forms.Automation;

namespace yEdit.App;

internal static class SrNotify
{
    public static void Raise(Label label, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        label.Text = message;
        if (Speech.PcTalkerSpeech.IsRunning() && Speech.PcTalkerSpeech.Speak(message)) return;
        try
        {
            label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent, message);
        }
        catch { }
    }
}
```

> これで `SpeechRouter` / `ISpeechChannel` / `UiaNotificationSpeech` は未参照になる（削除は Task 4）。`PcTalkerSpeech` の static 化と priority=1 化が反映される。

**Step 4: ビルド・テストを確認**

Run: `dotnet build yEdit.sln` → 0 警告
Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj` → 緑

**Step 5: コミット**

```bash
git add src/yEdit.App/Speech/PcTalkerSpeech.cs src/yEdit.App/SrNotify.cs
git commit -m "PcTalkerSpeechをstatic化し発声を1箇所に集約・既定priority=1（SrNotifyは暫定直分岐に修正）"
```

---

### Task 3: App — IAnnouncer と実体・ファクトリを新設（既存は残置）

**Files:**
- Create: `src/yEdit.App/IAnnouncer.cs`
- Create: `src/yEdit.App/AnnouncerFactory.cs`
- Create: `src/yEdit.App/Speech/UiaAnnouncer.cs`
- Create: `src/yEdit.App/Speech/PcTalkerAnnouncer.cs`

**注意:** この段階では既存 `Announcer.cs` 等は削除しない（呼び出し元の差し替えは Task 4）。新クラスを追加してビルドが通ることだけ確認する。

**Step 1: IAnnouncer を作成**

`src/yEdit.App/IAnnouncer.cs`:

```csharp
namespace yEdit.App;

/// <summary>
/// SR への能動通知。底部/ステータス Label を視覚表示しつつ、起動時に確定した SR 別手段で発声する。
/// 実体は AnnouncerFactory が SpeechMode に応じて生成（UiaAnnouncer / PcTalkerAnnouncer）。
/// </summary>
public interface IAnnouncer
{
    void Say(string message);
}
```

**Step 2: UiaAnnouncer を作成**

`src/yEdit.App/Speech/UiaAnnouncer.cs`:

```csharp
using System.Windows.Forms.Automation;

namespace yEdit.App.Speech;

/// <summary>
/// UIA 通知（RaiseAutomationNotification）で読ませる Announcer。NVDA・その他SR・既定。
/// 視覚表示（label.Text）は無条件。PC-Talker のハード失敗退避でも Raise を再利用する。
/// </summary>
internal sealed class UiaAnnouncer : IAnnouncer
{
    private readonly Label _label;
    public UiaAnnouncer(Label label) => _label = label;

    public void Say(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _label.Text = message; // 視覚フィードバックは無条件（晴眼/弱視も第一級）
        Raise(_label, message);
    }

    /// <summary>指定 Label の UIA プロバイダから通知を上げる。非対応環境では握りつぶし（視覚のみ）。</summary>
    internal static void Raise(Label label, string message)
    {
        try
        {
            label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message);
        }
        catch { /* 通知非対応環境では視覚表示のみ */ }
    }
}
```

**Step 3: PcTalkerAnnouncer を作成**

`src/yEdit.App/Speech/PcTalkerAnnouncer.cs`:

```csharp
namespace yEdit.App.Speech;

/// <summary>
/// PC-Talker 直叩きで発声する Announcer（起動時に PC-Talker 稼働＆NVDA非稼働で選択）。
/// 視覚表示（label.Text）は無条件。発声のハード失敗（false/例外）時のみ UIA 通知へ退避する
/// （無音は false にならないため audibility フォールバックではない）。
/// </summary>
internal sealed class PcTalkerAnnouncer : IAnnouncer
{
    private readonly Label _label;
    public PcTalkerAnnouncer(Label label) => _label = label;

    public void Say(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _label.Text = message; // 視覚フィードバックは無条件
        if (!PcTalkerSpeech.Speak(message))
            UiaAnnouncer.Raise(_label, message); // ハード失敗時のみ退避
    }
}
```

**Step 4: AnnouncerFactory を作成**

`src/yEdit.App/AnnouncerFactory.cs`:

```csharp
using yEdit.App.Speech;
using yEdit.Core.Speech;
using yEdit.Editor;

namespace yEdit.App;

/// <summary>
/// 起動時に一度だけ SR を判定して発声モードを確定し、各呼び出し元 Label 用の IAnnouncer を生成する。
/// 起動後の SR 起動/終了には追従しない（受動読みパスと一貫した起動時確定方針）。
/// </summary>
internal static class AnnouncerFactory
{
    private static SpeechMode? _mode;

    /// <summary>確定済みの発声モード（初回アクセス時に1回だけ評価しキャッシュ）。</summary>
    public static SpeechMode Mode =>
        _mode ??= SrSpeechSelector.Select(ScreenReaders.IsNvdaRunning(), PcTalkerSpeech.IsRunning());

    /// <summary>指定 Label に束縛した IAnnouncer を、確定済みモードに応じて生成する。</summary>
    public static IAnnouncer Create(Label label) => Mode switch
    {
        SpeechMode.PcTalker => new PcTalkerAnnouncer(label),
        _ => new UiaAnnouncer(label),
    };
}
```

**Step 5: ビルドを確認**

Run: `dotnet build yEdit.sln`
Expected: 0 警告・0 エラー（新クラスは追加のみ。旧クラスと共存）。

**Step 6: コミット**

```bash
git add src/yEdit.App/IAnnouncer.cs src/yEdit.App/AnnouncerFactory.cs src/yEdit.App/Speech/UiaAnnouncer.cs src/yEdit.App/Speech/PcTalkerAnnouncer.cs
git commit -m "IAnnouncerとUiaAnnouncer/PcTalkerAnnouncer/AnnouncerFactoryを新設（起動時モード確定・既存は残置）"
```

---

### Task 4: App — 呼び出し元を IAnnouncer に切替え、旧クラスを削除

**Files:**
- Modify: `src/yEdit.App/MainForm.cs:28,52`（`Announcer` → `IAnnouncer`、生成を `AnnouncerFactory.Create`）
- Modify: `src/yEdit.App/CsvController.cs:13,15`（ctor 引数・フィールド型 `Announcer` → `IAnnouncer`）
- Modify: `src/yEdit.App/FindReplaceDialog.cs:23-27,72`（`IAnnouncer` フィールド追加・`RaiseNotification` 差し替え）
- Modify: `src/yEdit.App/GrepDialog.cs:24-44,70`（同上）
- Delete: `src/yEdit.App/Announcer.cs`
- Delete: `src/yEdit.App/SrNotify.cs`
- Delete: `src/yEdit.App/Speech/UiaNotificationSpeech.cs`
- Delete: `src/yEdit.Core/Speech/SpeechRouter.cs`
- Delete: `src/yEdit.Core/Speech/ISpeechChannel.cs`

**Step 1: MainForm を修正**

`src/yEdit.App/MainForm.cs:28` のフィールド宣言:

```csharp
    private IAnnouncer _announcer = null!; // AnnouncerFactory で生成（起動時モード確定）
```

`src/yEdit.App/MainForm.cs:52` の生成:

```csharp
        _announcer = AnnouncerFactory.Create(_announceLabel);
```

**Step 2: CsvController を修正**

`src/yEdit.App/CsvController.cs:13`:

```csharp
    private readonly IAnnouncer _announcer;
```

`src/yEdit.App/CsvController.cs:15`（ctor シグネチャ）:

```csharp
    public CsvController(DocumentManager docs, IAnnouncer announcer)
```

**Step 3: FindReplaceDialog を修正**

`_status` フィールド宣言（`:23` 付近）の直後に Announcer フィールドを追加:

```csharp
    private readonly IAnnouncer _announcer;
```

ctor 内（`BuildLayout();` の後あたり、`:36` 付近）で生成:

```csharp
        _announcer = AnnouncerFactory.Create(_status);
```

`RaiseNotification`（`:72`）を差し替え:

```csharp
    /// <summary>ステータス Label を視覚表示しつつ SR 別手段で読ませる。</summary>
    public void RaiseNotification(string message) => _announcer.Say(message);
```

**Step 4: GrepDialog を修正**

`_status` フィールド宣言（`:24`）の直後に追加:

```csharp
    private readonly IAnnouncer _announcer;
```

ctor 内（`BuildLayout();` の後、`:37` 付近）で生成:

```csharp
        _announcer = AnnouncerFactory.Create(_status);
```

`RaiseNotification`（`:70`）を差し替え:

```csharp
    /// <summary>ステータス Label を視覚表示しつつ SR 別手段で読ませる。</summary>
    public void RaiseNotification(string message) => _announcer.Say(message);
```

**Step 5: 旧クラスを削除**

```bash
git rm src/yEdit.App/Announcer.cs src/yEdit.App/SrNotify.cs src/yEdit.App/Speech/UiaNotificationSpeech.cs src/yEdit.Core/Speech/SpeechRouter.cs src/yEdit.Core/Speech/ISpeechChannel.cs
```

**Step 6: ビルド・テストを確認**

Run: `dotnet build yEdit.sln`
Expected: 0 警告・0 エラー（`Announcer` / `SrNotify` / `ISpeechChannel` / `SpeechRouter` への未解決参照が無いこと）。
Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
Expected: 緑。

**Step 7: コミット**

```bash
git add -A
git commit -m "呼び出し元をIAnnouncer/AnnouncerFactoryへ切替え、旧Announcer/SrNotify/SpeechRouter/ISpeechChannel/UiaNotificationSpeechを削除"
```

---

### Task 5: 検証（自動＋実機手動）

**Files:** なし（検証のみ）

**Step 1: 全体ビルド 0 警告**

Run: `dotnet build yEdit.sln`
Expected: 0 警告・0 エラー。

**Step 2: Core テスト緑**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj`
Expected: 全緑（`SrSpeechSelectorTests` 4 件含む。既存も緑のまま）。

**Step 3: PC-Talker 実機検証（ユーザ手動・耳で可聴判定）**

`docs/plans/2026-06-29-sr-announcer-redesign-design.md` §10 の手順:
1. PC-Talker 稼働下で yEdit を起動。
2. 以下で可聴を確認: モード切替（挿入/上書き）、行ジャンプ、文字照会、整形（変更なし/整形しました）、CSV モード オン/オフ、CSV セル移動、検索/置換ダイアログのステータス、grep のステータス。
3. **無音なら** `src/yEdit.App/Speech/PcTalkerSpeech.cs` の `Speak()` 内呼び出し行を差し替え（`_read(message,0,1)` または PCTKCGuide 系）て再検証。
4. 鳴る手段を既定として確定。

**Step 4: NVDA 回帰確認（ユーザ手動）**

NVDA 稼働下で yEdit を起動し、同操作で UIA 通知が従来どおり読まれることを確認（モードが Uia に確定し、発声が回帰していないこと）。

**Step 5: 検証結果を記録**

実機結果を `docs/report-pctalker-speech/` に追記（確定した PC-Talker 手段・NVDA 回帰の可否）。必要なら設計ドキュメント §5 の既定手段を更新。

---

## メモ（YAGNI / 申し送り）

- 起動後の SR 起動/終了へのライブ追従は対象外（起動時確定方針）。将来必要なら `AnnouncerFactory.Mode` のキャッシュ無効化＋再生成口を足す。
- PCTKCGuide は署名未確認。差し替えで採用する場合は `GetProcAddress("PCTKCGuide")` の解決と delegate 定義を `PcTalkerSpeech` に追加し、実機で署名を確定すること。
- 受動読みパス（Scintilla UIA プロバイダ）との統合は別関心事のため対象外。
