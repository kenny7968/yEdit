# テスト戦略 Phase 2 Stage 3: FileController シーム導入+テスト 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** FileController の Form/OS 境界(MessageBox・ファイル系ダイアログ)を IUserPrompt/IFileDialogService で注入化し、SaveAs ロールバック(データ破損防止の要)を最優先に App.Tests で挙動を固定する。

**Architecture:** ストラングラー方式のシーム導入。ダイアログ/MessageBox を「結果だけを返す薄い Adapter」に置き換え(条件分岐・文言・順序は一切変えない=挙動不変)、テストは実 DocumentManager+実 EditorControl+実ファイル I/O(TextFileService は温存対象)に Fake 境界(FakePrompt/FakeFileDialogService)だけを差して特徴付けする(green から開始)。

**Tech Stack:** .NET 9 / WinForms / xUnit v2(STA ヘルパ=`Sta.Run`・可視 HostForm パターン)/ 実ファイルは `Directory.CreateTempSubdirectory` の使い捨てフォルダ

- 日付: 2026-07-13
- 上位文書: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` §2.1〜2.2・§3 FileController 行・§4 Stage 3
- ベースライン: main `6fc1626`・テスト数 805(Core 570+Editor 216+App 19)

---

## 0. PC-Talker サポート廃止の反映(2026-07-13 コード精査で確認)

Stage 2 計画の申し送り「PC-Talker 非依存のため再スコープ不要」を実コードで検証した。

| 確認項目 | 結果 | 本計画での扱い |
|---|---|---|
| FileController の Speech/IAnnouncer/PcTalker 参照 | **ゼロ**(grep 確認。通知はすべて MessageBox=同期ダイアログで、Announcer 経路を使っていない) | 再スコープ不要。設計書 §3 FileController 行の観点をそのまま採用 |
| `LoadInto` の `RaiseUiaSelectionEvents = true`(開き直し時に CSV モードの UIA 抑止を確実に戻す配線) | 廃止スコープの**温存対象**(UiaAnnouncer・MSAA 抑制・RaiseUiaSelectionEvents は残す決定) | 開き直しテストで**この配線を固定**し退行を防ぐ(Task 5) |
| L5(実機 SR)スポット確認 | 設計書 §5「他 Stage はダイアログ抽象化のみで SR 経路不変」に該当。PC-Talker は L5 マトリクスから恒久除外済み | **L5 不要**。代わりに軽い手動スモーク(§DoD)を任意で実施 |
| 削除済み API(CaretEnteredEmptyLine/WordNavigated/AnnouncerFactory 等)への言及 | 本計画のテスト・シームは一切触れない | 対象外 |
| MessageBox の SR 読み上げ | Adapter は従来と**同一引数**の MessageBox.Show を呼ぶだけ | SR がダイアログを読む挙動も不変 |

## 1. スコープ

- **導入するシーム**(設計書 §2.1〜2.2 のとおり):
  - `IUserPrompt` — Info/Warn/Error/OkCancel/YesNoCancel(FileController 内の MessageBox 7 箇所を置換)
  - `IFileDialogService` — PickOpenPath/PickSaveAs/PickEncoding(OpenFileDialog/SaveAsDialog/EncodingPickDialog の直 new 3 箇所を置換)+`SaveAsRequest`/`SaveAsResult` レコード
- **テスト**: `FileControllerTests` 22 件(SaveAs ロールバック最優先)。テスト数 805 → **827**(App 19→41・純増 +22)
- **触らないもの**: SaveAsDialog/EncodingPickDialog のダイアログ内部(L5 手動の領分)・TextFileService(実ファイル=温存)・EditorControl(モックしない)・他 Controller・GoToLineDialog/SettingsDialog(Stage 8 で判断=YAGNI)

## 規約(全 Task 共通)

- ブランチ: `feature/test-strategy-phase2-stage3`(同一ディレクトリのフィーチャーブランチ→main へ no-ff マージ=いつもの運用)
- コミットメッセージは日本語。末尾に `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` を付ける
- 各 Task 末尾で `dotnet build yEdit.sln -c Release -warnaserror` が 0 警告であること
- git status に見えている untracked の `installer/`・`publish/` はこの作業と無関係。**絶対にコミットに含めない**(`git add` はパス指定で行う)
- 特徴付けテストが赤になった場合: 原則テスト側の期待を現行挙動へ合わせる。**ただし SaveAs ロールバック系が赤の場合は実装バグの可能性があるため、修正せずユーザーへ報告する**

---

### Task 1: ブランチ作成

**Step 1: main から作業ブランチを切る**

Run:
```powershell
git -C <repo> switch -c feature/test-strategy-phase2-stage3 main
```
Expected: `Switched to a new branch 'feature/test-strategy-phase2-stage3'`

---

### Task 2: シーム定義+Adapter 実装(未配線・コンパイルのみ)

**Files:**
- Create: `src/yEdit.App/Abstractions/IUserPrompt.cs`
- Create: `src/yEdit.App/Abstractions/IFileDialogService.cs`
- Create: `src/yEdit.App/MessageBoxUserPrompt.cs`
- Create: `src/yEdit.App/WinFormsFileDialogService.cs`

**Step 1: IUserPrompt を定義**

Create `src/yEdit.App/Abstractions/IUserPrompt.cs`:

```csharp
namespace yEdit.App;

/// <summary>
/// ユーザーへの確認・警告(MessageBox のラップ)。Phase 2 設計書 §2.1。
/// テストではフェイクに差し替え、本番は MessageBoxUserPrompt が同一引数の MessageBox を出す。
/// </summary>
public interface IUserPrompt
{
    void Info(string text, string caption);
    void Warn(string text, string caption);
    void Error(string text, string caption);
    /// <summary>OK/キャンセル(警告アイコン)。OK で true。文字コード劣化警告など。</summary>
    bool OkCancel(string text, string caption);
    /// <summary>はい/いいえ/キャンセル(警告アイコン)。未保存確認。</summary>
    DialogResult YesNoCancel(string text, string caption);
}
```

**Step 2: IFileDialogService と要求/結果レコードを定義**

Create `src/yEdit.App/Abstractions/IFileDialogService.cs`:

```csharp
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>SaveAs ダイアログへ渡す現在値(初期選択の種)。</summary>
public sealed record SaveAsRequest(string? Path, int CodePage, bool HasBom, LineEnding LineEnding);

/// <summary>
/// SaveAs ダイアログの選択結果。Path は未検証のまま返す(空白のみの可能性あり。
/// 検証と警告は従来どおり FileController の責務=挙動不変)。
/// </summary>
public sealed record SaveAsResult(string Path, int CodePage, bool HasBom, LineEnding LineEnding);

/// <summary>
/// ファイル系ダイアログの結果だけを返す抽象(Phase 2 設計書 §2.2)。
/// 実装は既存ダイアログをラップする薄い Adapter で、キャンセルは null。
/// </summary>
public interface IFileDialogService
{
    string? PickOpenPath(IWin32Window owner);
    SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current);
    int? PickEncoding(IWin32Window owner, int currentCodePage);
}
```

**Step 3: MessageBox Adapter を実装**

Create `src/yEdit.App/MessageBoxUserPrompt.cs`:

```csharp
namespace yEdit.App;

/// <summary>
/// <see cref="IUserPrompt"/> の本番実装。従来 FileController 内に直書きされていた
/// MessageBox.Show を同一引数のまま包むだけの薄い Adapter(ロジックなし=挙動不変)。
/// </summary>
internal sealed class MessageBoxUserPrompt : IUserPrompt
{
    public void Info(string text, string caption)
        => MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void Warn(string text, string caption)
        => MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public void Error(string text, string caption)
        => MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public bool OkCancel(string text, string caption)
        => MessageBox.Show(text, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

    public DialogResult YesNoCancel(string text, string caption)
        => MessageBox.Show(text, caption, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
}
```

**Step 4: ダイアログ Adapter を実装**

Create `src/yEdit.App/WinFormsFileDialogService.cs`:

```csharp
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// <see cref="IFileDialogService"/> の本番実装。既存ダイアログ
/// (OpenFileDialog/SaveAsDialog/EncodingPickDialog)を従来と同一の引数・フィルタで
/// 表示し、結果だけを返す薄い Adapter(ロジックなし=挙動不変)。
/// </summary>
internal sealed class WinFormsFileDialogService : IFileDialogService
{
    public string? PickOpenPath(IWin32Window owner)
    {
        using var dlg = new OpenFileDialog { Filter = "対応ファイル (*.txt, *.md, *.csv)|*.txt;*.md;*.csv|すべてのファイル (*.*)|*.*" };
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
    }

    public SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current)
    {
        using var dlg = new SaveAsDialog(current.Path, current.CodePage, current.HasBom, current.LineEnding);
        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
        return new SaveAsResult(dlg.SelectedPath, dlg.SelectedCodePage, dlg.SelectedHasBom, dlg.SelectedLineEnding);
    }

    public int? PickEncoding(IWin32Window owner, int currentCodePage)
    {
        using var dlg = new EncodingPickDialog(currentCodePage);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedCodePage : null;
    }
}
```

**Step 5: ビルド確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
```
Expected: 0 警告(新規 4 ファイルは未参照でもコンパイルされる)

**Step 6: Commit**

```powershell
git -C <repo> add src/yEdit.App/Abstractions/IUserPrompt.cs src/yEdit.App/Abstractions/IFileDialogService.cs src/yEdit.App/MessageBoxUserPrompt.cs src/yEdit.App/WinFormsFileDialogService.cs
git -C <repo> commit -m "feat: IUserPrompt/IFileDialogService シームと薄い Adapter を追加(Stage 3・未配線)"
```

---

### Task 3: FileController 注入化+MainForm 配線(挙動不変)

置換は**機械的**に行う。条件分岐・文言・呼び出し順序を一切変えない(diff レビューで確認できる粒度)。

**Files:**
- Modify: `src/yEdit.App/FileController.cs`(ctor+MessageBox 7 箇所+ダイアログ 3 箇所)
- Modify: `src/yEdit.App/MainForm.cs:53-55`(生成引数 2 つ追加)

**Step 1: フィールドと ctor に注入点を追加**

`src/yEdit.App/FileController.cs:24-25` 付近(`_openedFresh` の直後)にフィールド追加:

```csharp
    private readonly Action<Document> _openedFresh; // 開く系で新規ロード成功した直後（.csv 自動モードの判定は MainForm 側）
    private readonly IUserPrompt _prompt;              // 確認・警告の注入点（テストでは FakePrompt）
    private readonly IFileDialogService _fileDialogs;  // ファイル系ダイアログの注入点（テストでは FakeFileDialogService）
```

ctor(`FileController.cs:27-39`)を変更:

```csharp
    public FileController(
        DocumentManager docs, Form owner, Func<AppSettings> settings,
        Action saveSettings, Action recentChanged, Action metaChanged,
        Action<Document> openedFresh, IUserPrompt prompt, IFileDialogService fileDialogs)
    {
        _docs = docs;
        _owner = owner;
        _settings = settings;
        _saveSettings = saveSettings;
        _recentChanged = recentChanged;
        _metaChanged = metaChanged;
        _openedFresh = openedFresh;
        _prompt = prompt;
        _fileDialogs = fileDialogs;
    }
```

**Step 2: OpenFileWithDialog(`FileController.cs:62-68`)**

```csharp
    /// <summary>「開く」ダイアログでファイルを選んで開く。</summary>
    public void OpenFileWithDialog()
    {
        var path = _fileDialogs.PickOpenPath(_owner);
        if (path is null) return;
        TryOpenOrActivate(path);
    }
```

**Step 3: ReopenWithEncoding(`FileController.cs:96-113`)の案内表示とダイアログ**

```csharp
// 変更前
        if (doc.State.Path is null)
        {
            MessageBox.Show("ファイルを開いてから実行してください。", "yEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!ConfirmDiscardIfDirty(doc)) return;
        using var dlg = new EncodingPickDialog(doc.State.Encoding.CodePage);
        if (dlg.ShowDialog(_owner) != DialogResult.OK) return;
        if (!LoadInto(doc, doc.State.Path, forcedCodePage: dlg.SelectedCodePage)) return;
// 変更後
        if (doc.State.Path is null)
        {
            _prompt.Info("ファイルを開いてから実行してください。", "yEdit");
            return;
        }
        if (!ConfirmDiscardIfDirty(doc)) return;
        int? picked = _fileDialogs.PickEncoding(_owner, doc.State.Encoding.CodePage);
        if (picked is null) return;
        if (!LoadInto(doc, doc.State.Path, forcedCodePage: picked)) return;
```

**Step 4: LoadInto の警告 2 箇所(`FileController.cs:152-158`・`162-167`)**

```csharp
// 変更前(HadReplacementChar)
                MessageBox.Show(
                    "このファイルには現在の文字コードで表せない文字（置換文字）が含まれています。" +
                    "別の文字コードで開き直してください。",
                    "文字コードの警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
// 変更後
                _prompt.Warn(
                    "このファイルには現在の文字コードで表せない文字（置換文字）が含まれています。" +
                    "別の文字コードで開き直してください。",
                    "文字コードの警告");
```

```csharp
// 変更前(catch)
            MessageBox.Show($"開けませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
// 変更後
            _prompt.Error($"開けませんでした: {ex.Message}", "エラー");
```

**Step 5: SaveAsDocument(`FileController.cs:191-236`)**

```csharp
    /// <summary>指定ドキュメントを名前を付けて保存。成功で State.Path/Encoding/LineEnding とラベルを更新する。</summary>
    private bool SaveAsDocument(Document doc)
    {
        var picked = _fileDialogs.PickSaveAs(_owner,
            new SaveAsRequest(doc.State.Path, doc.State.Encoding.CodePage, doc.State.HasBom, doc.State.LineEnding));
        if (picked is null) return false;
        if (string.IsNullOrWhiteSpace(picked.Path))
        {
            _prompt.Warn("ファイル名を指定してください。", "エラー");
            return false;
        }

        var newEncoding = EncodingCatalog.Get(picked.CodePage);

        // C-2 追補 I-2: 選択エンコードで表せない文字があれば警告して続行/中止を選ばせる。
        // Load 経路の HadReplacementChar 警告と対称。UTF-8(65001) は BMP+astral 全表現可でスキップ。
        if (picked.CodePage != 65001 && !CanEncodeBuffer(doc.Editor.CurrentBuffer, newEncoding))
        {
            if (!_prompt.OkCancel(
                "選択した文字コードで表せない文字が含まれています。'?' として保存されデータが失われます。続行しますか?",
                "文字コードの警告"))
            {
                return false;
            }
        }

        // 新エンコード/改行/BOM を State に反映してから WriteToPath へ(既存 WriteToPath は State を参照する)。
        // C-2 追補 I-1: WriteToPath 失敗時は元の Encoding/LineEnding/HasBom へロールバック
        // (State だけ更新済で Path が旧のままだと後続の Ctrl+S が元ファイルを別エンコードで
        // サイレント上書きする=データ破損)。
        var oldEncoding = doc.State.Encoding;
        var oldLineEnding = doc.State.LineEnding;
        var oldHasBom = doc.State.HasBom;
        doc.State.Encoding = newEncoding;
        doc.State.LineEnding = picked.LineEnding;
        doc.State.HasBom = picked.HasBom;

        if (!WriteToPath(doc, picked.Path))
        {
            doc.State.Encoding = oldEncoding;
            doc.State.LineEnding = oldLineEnding;
            doc.State.HasBom = oldHasBom;
            return false;
        }
        doc.State.Path = picked.Path;
        _docs.UpdateLabel(doc);
        _metaChanged();
        RegisterRecent(picked.Path); // 保存先も最近のファイルへ
        return true;
    }
```

**Step 6: WriteToPath の catch(`FileController.cs:278-282`)**

```csharp
// 変更前
            MessageBox.Show($"保存できませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
// 変更後
            _prompt.Error($"保存できませんでした: {ex.Message}", "エラー");
```

**Step 7: ConfirmDiscardIfDirty(`FileController.cs:291-303`)**

```csharp
// 変更前
        var r = MessageBox.Show(
            $"{doc.State.DisplayName} の変更を保存しますか？",
            "yEdit", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
// 変更後
        var r = _prompt.YesNoCancel($"{doc.State.DisplayName} の変更を保存しますか？", "yEdit");
```

**Step 8: MainForm の生成箇所(`MainForm.cs:53-55`)に Adapter を渡す**

```csharp
// 変更前
        _file = new FileController(_docs, this, () => _settings,
            SaveSettingsSafe, RebuildRecentMenu, () => { UpdateTitle(); UpdateStatus(); },
            AutoEnterCsvMode);
// 変更後
        _file = new FileController(_docs, this, () => _settings,
            SaveSettingsSafe, RebuildRecentMenu, () => { UpdateTitle(); UpdateStatus(); },
            AutoEnterCsvMode, new MessageBoxUserPrompt(), new WinFormsFileDialogService());
```

**Step 9: 消し漏れがないことを確認**

Run:
```powershell
git -C <repo> grep -n "MessageBox.Show" -- "src/yEdit.App/FileController.cs"
```
Expected: ヒットなし(exit code 1)

**Step 10: ビルド+既存全テストで挙動不変を確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.Core.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.Editor.Tests -c Release --no-build
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Core 570+Editor 216+App 19 全緑

**Step 11: Commit**

```powershell
git -C <repo> add src/yEdit.App/FileController.cs src/yEdit.App/MainForm.cs
git -C <repo> commit -m "refactor: FileController の MessageBox/ダイアログ直 new を IUserPrompt/IFileDialogService 注入へ置換(挙動不変)"
```

---

### Task 4: Fake 群+FileControllerTests 第 1 弾(SaveAs ロールバック最優先・7 件)

**Files:**
- Create: `tests/yEdit.App.Tests/Fakes/FakePrompt.cs`
- Create: `tests/yEdit.App.Tests/Fakes/FakeFileDialogService.cs`
- Create: `tests/yEdit.App.Tests/FileControllerTests.cs`

**Step 1: FakePrompt を作成**

Create `tests/yEdit.App.Tests/Fakes/FakePrompt.cs`:

```csharp
namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IUserPrompt"/> のテスト用フェイク。応答を事前登録し、
/// 呼ばれた種別・文言・キャプションを順序どおり記録する(Phase 2 設計書 §3 共通ユーティリティ)。
/// </summary>
public sealed class FakePrompt : IUserPrompt
{
    public List<(string Kind, string Text, string Caption)> Log { get; } = new();
    public bool OkCancelResult { get; set; } = true;
    public DialogResult YesNoCancelResult { get; set; } = DialogResult.Cancel;

    public void Info(string text, string caption) => Log.Add(("Info", text, caption));
    public void Warn(string text, string caption) => Log.Add(("Warn", text, caption));
    public void Error(string text, string caption) => Log.Add(("Error", text, caption));

    public bool OkCancel(string text, string caption)
    {
        Log.Add(("OkCancel", text, caption));
        return OkCancelResult;
    }

    public DialogResult YesNoCancel(string text, string caption)
    {
        Log.Add(("YesNoCancel", text, caption));
        return YesNoCancelResult;
    }
}
```

**Step 2: FakeFileDialogService を作成**

Create `tests/yEdit.App.Tests/Fakes/FakeFileDialogService.cs`:

```csharp
using yEdit.Core.Text;

namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IFileDialogService"/> のテスト用フェイク。返す値を事前登録する(null=キャンセル)。
/// PickSaveAs へ渡った現在値(SaveAsRequest)を記録し、ダイアログ初期値の配線を検証できるようにする。
/// </summary>
public sealed class FakeFileDialogService : IFileDialogService
{
    public string? OpenPath { get; set; }
    public SaveAsResult? SaveAs { get; set; }
    public int? EncodingCodePage { get; set; }

    public List<SaveAsRequest> SaveAsRequests { get; } = new();
    public int PickOpenCount;
    public int PickEncodingCount;

    public string? PickOpenPath(IWin32Window owner) { PickOpenCount++; return OpenPath; }

    public SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current)
    {
        SaveAsRequests.Add(current);
        return SaveAs;
    }

    public int? PickEncoding(IWin32Window owner, int currentCodePage) { PickEncodingCount++; return EncodingCodePage; }
}
```

**Step 3: テストハーネス+第 1 弾テスト(SaveAs ロールバック 4 件+符号化劣化警告 3 件)を書く**

Create `tests/yEdit.App.Tests/FileControllerTests.cs`:

```csharp
using System.Text;
using yEdit.App.Tests.Fakes;
using yEdit.Core.Backup;
using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 3: FileController の配線・状態遷移・ロールバックのテスト(設計書 §3)。
/// 実 DocumentManager+実 EditorControl+実ファイル I/O(TextFileService=温存対象)を使い、
/// Form/OS 境界(FakePrompt/FakeFileDialogService)だけを偽物にする。
/// Core が検証済みの照合・I/O 正しさ(TextFileService/RecentFilesList/EncodingCatalog)は再検証しない。
/// </summary>
public class FileControllerTests
{
    private sealed class HostForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    /// <summary>
    /// FileController を Fake 境界で配線したテストホスト。DocumentManagerTests と同じ
    /// 「可視・画面外・非アクティブ」の HostForm パターン(実運用 MainForm は常に可視のため)。
    /// </summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FileController File { get; }
        public AppSettings Settings = new();
        public FakePrompt Prompt { get; } = new();
        public FakeFileDialogService Dialogs { get; } = new();
        public int SaveSettingsCount;
        public int RecentChangedCount;
        public int MetaChangedCount;
        public List<Document> OpenedFresh { get; } = new();

        public Host()
        {
            Form = new HostForm
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
            };
            Docs = new DocumentManager(() => new EditorControl());
            Form.Controls.Add(Docs.TabHost);
            Form.Show();
            File = new FileController(Docs, Form, () => Settings,
                () => SaveSettingsCount++, () => RecentChangedCount++, () => MetaChangedCount++,
                d => OpenedFresh.Add(d), Prompt, Dialogs);
        }

        public void Dispose() => Form.Dispose();
    }

    /// <summary>テスト毎に使い捨ての一時フォルダ(実ファイル I/O 用)。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("yEditAppTests_").FullName;
        public string File(string name) => System.IO.Path.Combine(Root, name);
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* 掃除失敗はテスト失敗にしない(読み取り専用属性等は UnauthorizedAccessException) */ }
        }
    }

    // ===== SaveAs ロールバック(データ破損防止の要=最優先) =====

    [Fact]
    public void SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc"; // 既定 State=UTF-8/BOM なし/CRLF
        // 存在しないフォルダ配下を保存先にして TextFileService.Save を確実に失敗させる
        // (DirectoryNotFoundException は IOException 派生=想定内エラー経路)。
        // CodePage は 932 を選ぶ: 既定(65001)と同値だと Encoding ロールバックの assert が
        // 空振りする(レビュー I-1)。"abc" は ASCII なので 932 でも劣化警告は出ない。
        host.Dialogs.SaveAs = new SaveAsResult(tmp.File(@"no-such-dir\a.txt"), 932, HasBom: true, LineEnding.Lf);

        Assert.False(host.File.SaveAs());

        Assert.Null(doc.State.Path); // Path は旧のまま(後続 Ctrl+S の別エンコード上書き事故防止)
        Assert.Equal(65001, doc.State.Encoding.CodePage);   // ロールバック(932→65001)
        Assert.False(doc.State.HasBom);                    // ロールバック
        Assert.Equal(LineEnding.Crlf, doc.State.LineEnding); // ロールバック
        Assert.Contains(host.Prompt.Log, e => e.Kind == "Error" && e.Text.StartsWith("保存できませんでした"));
    });

    [Fact]
    public void SaveAs_Success_UpdatesMeta_SetsSavePoint_AndRegistersRecent() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty にして SetSavePoint の効果を観測する
        string path = tmp.File("a.txt");
        host.Dialogs.SaveAs = new SaveAsResult(path, 65001, HasBom: true, LineEnding.Lf);

        Assert.True(host.File.SaveAs());

        Assert.Equal(path, doc.State.Path);
        Assert.True(doc.State.HasBom);
        Assert.Equal(LineEnding.Lf, doc.State.LineEnding);
        Assert.False(doc.Editor.Modified); // SetSavePoint 済み
        var bytes = File2.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray()); // HasBom が Save まで配線される
        Assert.Equal(path, host.Settings.RecentFiles[0]); // RegisterRecent の配線
        Assert.True(host.SaveSettingsCount >= 1);
        Assert.True(host.RecentChangedCount >= 1);
        // ダイアログへ現在値が初期値として渡る
        Assert.Equal(new SaveAsRequest(null, 65001, false, LineEnding.Crlf), Assert.Single(host.Dialogs.SaveAsRequests));
    });

    [Fact]
    public void SaveAs_Cancelled_ReturnsFalse_AndChangesNothing() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        host.Dialogs.SaveAs = null; // キャンセル

        Assert.False(host.File.SaveAs());

        Assert.Null(doc.State.Path);
        Assert.Empty(host.Prompt.Log);
        Assert.Empty(host.Settings.RecentFiles);
    });

    [Fact]
    public void SaveAs_WhitespacePath_WarnsAndAborts() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        host.Dialogs.SaveAs = new SaveAsResult("   ", 65001, HasBom: false, LineEnding.Crlf);

        Assert.False(host.File.SaveAs());

        Assert.Null(doc.State.Path);
        Assert.Contains(host.Prompt.Log, e => e.Kind == "Warn" && e.Text == "ファイル名を指定してください。");
    });

    // ===== 符号化劣化警告(CanEncodeBuffer 経由) =====

    [Fact]
    public void SaveAs_LossyEncoding_CancelKeepsStateAndWritesNothing() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "こんにちは😀"; // 😀 は Shift_JIS(932) で表せない
        string path = tmp.File("a.txt");
        host.Dialogs.SaveAs = new SaveAsResult(path, 932, HasBom: false, LineEnding.Crlf);
        host.Prompt.OkCancelResult = false; // 中止

        Assert.False(host.File.SaveAs());

        Assert.False(File2.Exists(path));
        Assert.Equal(65001, doc.State.Encoding.CodePage); // 警告は State 反映前=変化なし
        Assert.Contains(host.Prompt.Log, e => e.Kind == "OkCancel" && e.Caption == "文字コードの警告");
    });

    [Fact]
    public void SaveAs_LossyEncoding_OkProceedsAndWrites() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "こんにちは😀";
        string path = tmp.File("a.txt");
        host.Dialogs.SaveAs = new SaveAsResult(path, 932, HasBom: false, LineEnding.Crlf);
        host.Prompt.OkCancelResult = true; // 続行

        Assert.True(host.File.SaveAs());

        Assert.True(File2.Exists(path));
        Assert.Equal(932, doc.State.Encoding.CodePage);
    });

    [Fact]
    public void SaveAs_Utf8_SkipsLossyWarning() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "😀"; // astral でも UTF-8 は全表現可
        host.Dialogs.SaveAs = new SaveAsResult(tmp.File("a.txt"), 65001, HasBom: false, LineEnding.Crlf);

        Assert.True(host.File.SaveAs());

        Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "OkCancel");
    });
}

// System.IO.File とテストクラス内の File(FileController プロパティ)の衝突回避エイリアス。
// GlobalUsings で解決できないローカル事情のためファイル内 using で完結させる。
```

> **実装メモ(エイリアス)**: `Host.File` プロパティと `System.IO.File` が衝突するため、ファイル先頭の using に
> `using File2 = System.IO.File;` と `using Directory = System.IO.Directory;` `using IOException = System.IO.IOException;`
> を追加する(ImplicitUsings に System.IO は含まれない点にも注意)。コンパイルエラーが出た場合は
> `System.IO.File.Exists(...)` の完全修飾へ倒してもよい(挙動は同じ・可読性で選ぶ)。

**Step 4: テスト実行(green を確認=特徴付けの成立)**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerTests"
```
Expected: **Passed! 7 件**(現行コードのまま通る。**SaveAs ロールバック系が赤の場合は実装バグの可能性=修正せずユーザーへ報告**)

**Step 5: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/Fakes/FakePrompt.cs tests/yEdit.App.Tests/Fakes/FakeFileDialogService.cs tests/yEdit.App.Tests/FileControllerTests.cs
git -C <repo> commit -m "test: FileController の SaveAs ロールバック+符号化劣化警告 7 件(Fake 境界導入)"
```

---

### Task 5: FileControllerTests 第 2 弾(開く系 5 件+開き直し 3 件)

**Files:**
- Modify: `tests/yEdit.App.Tests/FileControllerTests.cs`(テスト追記)

**Step 1: 開く系(TryOpenOrActivate/OpenFileWithDialog)のテストを追記**

```csharp
    // ===== 開く系(TryOpenOrActivate は path を開く唯一の経路) =====

    [Fact]
    public void TryOpenOrActivate_NewFile_LoadsMetaContent_AndFiresOpenedFresh() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        // 本文は LF 改行: 既定(Crlf)と同値だと改行検出配線のアサートが空振りする(レビュー I-2)
        File2.WriteAllBytes(path,
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("あい\nう")).ToArray());

        var doc = host.File.TryOpenOrActivate(path);

        Assert.NotNull(doc);
        Assert.Equal(path, doc!.State.Path);
        Assert.Equal(65001, doc.State.Encoding.CodePage);
        Assert.True(doc.State.HasBom);                       // BOM 検出の配線(既定 false に対し非デフォルト)
        Assert.Equal(LineEnding.Lf, doc.State.LineEnding);   // 改行検出の配線(既定 Crlf に対し非デフォルト)
        Assert.Equal("あい\nう", doc.Editor.Text);
        Assert.False(doc.Editor.Modified);                   // SetSavePoint 済み
        Assert.Same(doc, Assert.Single(host.OpenedFresh));   // .csv 自動モード判定への通知
        Assert.Equal(path, host.Settings.RecentFiles[0]);
    });

    [Fact]
    public void TryOpenOrActivate_AlreadyOpen_ActivatesExistingTab_WithoutReload() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "abc");
        var first = host.File.TryOpenOrActivate(path);
        _ = host.Docs.CreateNew(); // 別タブをアクティブにしてから再オープン

        var second = host.File.TryOpenOrActivate(path);

        Assert.Same(first, second);              // 既存タブ再利用(二重編集の上書き事故防止)
        Assert.Same(first, host.Docs.Active);    // アクティブ化
        Assert.Equal(2, host.Docs.Count);        // タブは増えない
        Assert.Single(host.OpenedFresh);         // 再ロードなし=openedFresh は初回のみ
    });

    [Fact]
    public void TryOpenOrActivate_LoadFailure_DiscardsScratchTab_AndRestoresPrevious() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        // タブ 3 枚構成にして prev を先頭以外に置く: TabControl は選択中タブの除去後に
        // 先頭(index 0)を自動選択するため、prev が先頭だと自動選択と明示復帰(Activate)を
        // 判別できない(レビュー I-1・ミューテーションで実証)。prev=2 枚目なら自動選択(先頭)と区別できる
        _ = host.Docs.CreateNew();        // 1 枚目(自動選択の着地先)
        var prev = host.Docs.CreateNew(); // 2 枚目(作成時点でアクティブ=直前のアクティブ)

        // Task 4 と同じ方式: 実在し得る絶対パス直書きを避け、一時フォルダ配下の
        // 存在しないサブフォルダを使う(レビュー申し送り)。
        var doc = host.File.TryOpenOrActivate(tmp.File(@"no-such-dir\no-such-file.txt"));

        Assert.Null(doc);
        Assert.Equal(2, host.Docs.Count);      // 作りかけタブは破棄
        // 作りかけ(末尾)除去後の TabControl 自動選択は先頭=明示復帰がないと落ちる
        Assert.Same(prev, host.Docs.Active);   // 直前のアクティブへ復帰
        Assert.Contains(host.Prompt.Log, e => e.Kind == "Error" && e.Text.StartsWith("開けませんでした"));
    });

    [Fact]
    public void TryOpenOrActivate_SuppressAutoCsv_DoesNotFireOpenedFresh() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.csv");
        File2.WriteAllText(path, "a,b");

        var doc = host.File.TryOpenOrActivate(path, suppressAutoCsv: true); // grep ジャンプ経路

        Assert.NotNull(doc);
        Assert.Empty(host.OpenedFresh); // 選択+エディタフォーカスを機能させるため自動 CSV を抑止
    });

    [Fact]
    public void OpenFileWithDialog_UsesPickedPath_AndCancelDoesNothing() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        host.Dialogs.OpenPath = null; // キャンセル
        host.File.OpenFileWithDialog();
        Assert.Equal(0, host.Docs.Count);

        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "abc");
        host.Dialogs.OpenPath = path;
        host.File.OpenFileWithDialog();
        Assert.Equal(path, host.Docs.Active!.State.Path); // 選択パスが唯一の開く経路へ流れる
    });

    // ===== 文字コード指定の開き直し =====

    [Fact]
    public void ReopenWithEncoding_WithoutPath_InformsAndSkipsDialog() => Sta.Run(() =>
    {
        using var host = new Host();
        _ = host.Docs.CreateNew(); // Path=null の無題

        host.File.ReopenWithEncoding();

        Assert.Contains(host.Prompt.Log, e => e.Kind == "Info" && e.Text == "ファイルを開いてから実行してください。");
        Assert.Equal(0, host.Dialogs.PickEncodingCount); // ダイアログまで進まない
    });

    [Fact]
    public void ReopenWithEncoding_ForcedCodePage_Reloads_AndReenablesUiaSelectionEvents() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "abc"); // ASCII=どのコードページでも同一内容(判定を決定的にする)
        var doc = host.File.TryOpenOrActivate(path)!;
        // PC-Talker 廃止後も温存の UIA 配線: LoadInto が RaiseUiaSelectionEvents を確実に戻すことを固定
        doc.Editor.RaiseUiaSelectionEvents = false;
        host.Dialogs.EncodingCodePage = 932;

        host.File.ReopenWithEncoding();

        Assert.Equal(932, doc.State.Encoding.CodePage);
        Assert.True(doc.Editor.RaiseUiaSelectionEvents);
        Assert.Equal(2, host.OpenedFresh.Count); // 開き直しも .csv 自動モードの対象
    });

    [Fact]
    public void ReopenWithEncoding_DirtyCancelled_AbortsBeforeDialog() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "abc");
        var doc = host.File.TryOpenOrActivate(path)!;
        doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty
        host.Prompt.YesNoCancelResult = DialogResult.Cancel;

        host.File.ReopenWithEncoding();

        Assert.Equal(0, host.Dialogs.PickEncodingCount); // 未保存確認で中止=ダイアログまで進まない
        Assert.True(doc.Editor.Modified);
        Assert.Equal(65001, doc.State.Encoding.CodePage);
    });
```

**Step 2: テスト実行**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerTests"
```
Expected: **Passed! 15 件**(7+8)

**Step 3: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/FileControllerTests.cs
git -C <repo> commit -m "test: FileController の開く系+文字コード開き直し 8 件(失敗時タブ破棄・UIA 配線固定を含む)"
```

---

### Task 6: FileControllerTests 第 3 弾(未保存確認 4 件+NewFile/復元 3 件)

**Files:**
- Modify: `tests/yEdit.App.Tests/FileControllerTests.cs`(テスト追記)

**Step 1: ConfirmDiscardIfDirty と NewFile/RestoreFromBackup のテストを追記**

```csharp
    // ===== 未保存確認(Yes=保存成否/No=true/Cancel=false) =====

    [Fact]
    public void ConfirmDiscardIfDirty_CleanDocument_TrueWithoutPrompt() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc"; // Text セッター=新規バッファで Modified=false

        Assert.True(host.File.ConfirmDiscardIfDirty(doc));
        Assert.Empty(host.Prompt.Log); // クリーンなら問わない
    });

    [Fact]
    public void ConfirmDiscardIfDirty_No_ReturnsTrueWithoutSaving() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        doc.Editor.ReplaceCharRange(0, 0, "x");
        doc.State.Path = tmp.File("a.txt"); // まだ存在しないファイル
        host.Prompt.YesNoCancelResult = DialogResult.No;

        Assert.True(host.File.ConfirmDiscardIfDirty(doc)); // 破棄=続行してよい

        Assert.False(File2.Exists(doc.State.Path)); // 保存はしない
        Assert.True(doc.Editor.Modified);
    });

    [Fact]
    public void ConfirmDiscardIfDirty_Yes_SavesDocument_AndReturnsSaveResult() => Sta.Run(() =>
    {
        using var host = new Host();
        using var tmp = new TempDir();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        doc.Editor.ReplaceCharRange(0, 0, "x");
        doc.State.Path = tmp.File("a.txt");
        host.Prompt.YesNoCancelResult = DialogResult.Yes;

        Assert.True(host.File.ConfirmDiscardIfDirty(doc));

        Assert.True(File2.Exists(doc.State.Path)); // Yes=保存してから続行
        Assert.False(doc.Editor.Modified);
        Assert.Contains(host.Prompt.Log, e => e.Kind == "YesNoCancel" && e.Text.Contains("の変更を保存しますか"));
    });

    [Fact]
    public void ConfirmDiscardIfDirty_Yes_WithoutPath_FallsBackToSaveAs_CancelMeansFalse() => Sta.Run(() =>
    {
        using var host = new Host();
        var doc = host.Docs.CreateNew();
        doc.Editor.Text = "abc";
        doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty な無題(Path=null)
        host.Prompt.YesNoCancelResult = DialogResult.Yes;
        host.Dialogs.SaveAs = null; // SaveAs ダイアログでキャンセル

        Assert.False(host.File.ConfirmDiscardIfDirty(doc)); // Yes→SaveAs 失敗=続行しない(閉じない)
    });

    // ===== NewFile 既定+無題連番 / バックアップ復元 =====

    [Fact]
    public void NewFile_AppliesSettingsDefaults_AndNumbersUntitledTabs() => Sta.Run(() =>
    {
        using var host = new Host();
        host.Settings.DefaultCodePage = 932;
        host.Settings.DefaultLineEnding = 1; // LineEnding.Lf

        host.File.NewFile();
        var doc1 = host.Docs.Active!;
        host.File.NewFile();
        var doc2 = host.Docs.Active!;

        Assert.Equal(932, doc1.State.Encoding.CodePage);   // 設定の既定コードページ
        Assert.Equal(LineEnding.Lf, doc1.State.LineEnding); // 設定の既定改行
        Assert.False(doc1.State.HasBom);                   // 既定と同値=契約の文書化(NewFile は BOM なし固定)
        Assert.Equal(1, doc1.State.UntitledNumber);
        Assert.Equal(2, doc2.State.UntitledNumber);        // セッション内で再利用しない連番
        Assert.Equal("無題 1", doc1.Page.Text);
        Assert.False(doc2.Editor.Modified);
        Assert.True(host.MetaChangedCount >= 2);           // タイトル・ステータス更新の配線
    });

    [Fact]
    public void RestoreFromBackup_UntitledRecord_KeepsNumber_AndAdvancesSeq() => Sta.Run(() =>
    {
        using var host = new Host();
        var rec = new BackupRecord("id-1", OriginalPath: null, UntitledNumber: 5,
            CodePage: 932, HasBom: false, LineEndingId: 1, Content: "abc", TimestampUtc: DateTime.UtcNow);

        var doc = host.File.RestoreFromBackup(rec);

        Assert.Equal(5, doc.State.UntitledNumber);         // ダイアログ表示と復元後タブの番号一致
        Assert.Equal(932, doc.State.Encoding.CodePage);
        Assert.Equal(LineEnding.Lf, doc.State.LineEnding);
        Assert.Equal("abc", doc.Editor.Text);
        // 【既知バグの特徴付け】実装コメントの意図は「SetSavePoint しない → Modified=true のまま」だが、
        // TextBuffer.FromString は生成時に保存点を持つ(TextBuffer.cs の _savedRoot=root)ため現行挙動は
        // Modified=false=「*」も付かない。復元内容がサイレント喪失し得る(本計画の申し送り参照)。
        // 修正ブランチでこの 2 assert を本来意図(True/「* 無題 5」)へ反転する。
        Assert.False(doc.Editor.Modified);
        Assert.Equal("無題 5", doc.Page.Text);

        host.File.NewFile();                               // 連番カウンタは既存最大値の先へ進む
        Assert.Equal(6, host.Docs.Active!.State.UntitledNumber);
    });

    [Fact]
    public void RestoreFromBackup_PathRecord_SetsMetaFromRecord_AndToleratesNullContent() => Sta.Run(() =>
    {
        using var host = new Host();
        // UntitledNumber: 7 は「path レコードでは旧無題番号を無視して 0 化する」契約を実効検証するため
        // (0 のままだとコピー実装でも 0 化実装でも通ってしまう=レビュー I-1 と同型の空振り)
        var rec = new BackupRecord("id-2", OriginalPath: @"C:\backup-origin\b.txt", UntitledNumber: 7,
            CodePage: 65001, HasBom: true, LineEndingId: 0, Content: null!, TimestampUtc: DateTime.UtcNow);

        var doc = host.File.RestoreFromBackup(rec); // 復元はディスクを読まない=実在しないパスでよい

        Assert.Equal(@"C:\backup-origin\b.txt", doc.State.Path);
        Assert.Equal(0, doc.State.UntitledNumber);         // path レコードは旧無題番号(7)を無視して 0 化
        Assert.True(doc.State.HasBom);
        Assert.Equal("", doc.Editor.Text);                 // JSON 破損(null)でも空タブ復元で継続(レビュー M-5 の防御)
        // 【既知バグの特徴付け】上のテストと同じ(申し送り参照)。修正ブランチで Modified と
        // ラベルの 2 assert を本来意図(True/「* b.txt」)へ反転する。
        Assert.False(doc.Editor.Modified);
        Assert.Equal("b.txt", doc.Page.Text);
    });
```

**Step 2: テスト実行**

Run:
```powershell
dotnet test <repo>\tests\yEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerTests"
```
Expected: **Passed! 22 件**(15+7)

**Step 3: App.Tests 全体+ビルドを確認**

Run:
```powershell
dotnet build <repo>\yEdit.sln -c Release -warnaserror
dotnet test <repo>\tests\yEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・Passed! **41 件**(既存 19+新規 22)

**Step 4: Commit**

```powershell
git -C <repo> add tests/yEdit.App.Tests/FileControllerTests.cs
git -C <repo> commit -m "test: FileController の未保存確認+NewFile 既定+バックアップ復元 7 件"
```

---

### Task 7: ローカルゲート+設計書へ実施記録

**Files:**
- Modify: `docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md`(「Stage 2(縮小版)実施記録」節の直後に追記)

**Step 1: ローカルゲートを全実行**

Run:
```powershell
powershell -File <repo>\tools\pre-merge-check.ps1
```
Expected: `OK: pre-merge チェック全通過`(Release 0 警告・Core 570+Editor 216+App 41=827 緑)

**Step 2: 設計書に実施記録を追記**

`docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md` の「Stage 2(縮小版)実施記録」節の直後に追記:

```markdown
### Stage 3 実施記録(2026-07-13)

- **完了**: 実装計画=`docs/plans/2026-07-13-test-strategy-phase2-stage3.md`。①IUserPrompt/IFileDialogService シーム+薄い Adapter(MessageBoxUserPrompt/WinFormsFileDialogService) ②FileController 注入化(MessageBox 7 箇所+ダイアログ直 new 3 箇所の機械的置換・挙動不変) ③FileControllerTests 22 件(SaveAs ロールバック最優先・FakePrompt/FakeFileDialogService 導入)。
- **PC-Talker 廃止の反映**: FileController は Speech 非依存(参照ゼロを精査)=再スコープ不要。温存対象の RaiseUiaSelectionEvents 復帰配線(LoadInto)を開き直しテストで固定。L5 スポット確認は不要(§5 のとおりダイアログ抽象化のみで SR 経路不変)。
- **テスト数**: 805 → 827(App 19→41)。ゲート全通過(Release 0 警告)。
```

(マージコミットのハッシュはマージ後にユーザー確認のうえ追記)

**Step 3: Commit**

```powershell
git -C <repo> add docs/plans/2026-07-13-test-strategy-phase2-app-di-design.md docs/plans/2026-07-13-test-strategy-phase2-stage3.md
git -C <repo> commit -m "docs: Phase2 設計書に Stage 3 実施記録を追記+実装計画を追加"
```

---

### Task 8: レビュー→手動スモーク(任意)→マージ

**Step 1: 別エージェントによるコードレビュー**(いつもの運用)

ブランチ全 diff(`git diff main...feature/test-strategy-phase2-stage3`)を対象に依頼。観点:
- **挙動不変**(文言・ダイアログ表示順序・戻り値・State 反映順を変えていないか。特に SaveAsDocument の「State 反映→WriteToPath→失敗でロールバック」の順序)
- Adapter にロジックが混入していないか(薄いラップに徹しているか)
- 消し漏れ(FileController 内の MessageBox/直 new)
- テストの妥当性(Core 検証済み事項の再検証をしていないか・実ファイル I/O の後始末)

**Step 2: 手動スモーク(ユーザー任意・L5 実機 SR は不要)**

SR 経路不変(ダイアログ抽象化のみ)のため L5 は実施しない(設計書 §5)。ダイアログ配線の実感確認として 1 分のスモークを任意で:
- 起動→Ctrl+O で開く→Ctrl+Shift+S で SaveAs(ダイアログに現在値が初期表示される)→文字コード指定の開き直し

**Step 3: main へ no-ff マージ**

```powershell
git -C <repo> switch main
git -C <repo> merge --no-ff feature/test-strategy-phase2-stage3 -m "テスト戦略 Phase2 Stage3: FileController シーム導入+テスト 22 件をマージ"
powershell -File <repo>\tools\pre-merge-check.ps1
git -C <repo> branch -d feature/test-strategy-phase2-stage3
```
Expected: マージ後ゲート全緑(827)

**Step 4: 実施記録へマージコミットのハッシュを追記**(小コミット)

---

## DoD(Stage 3)

1. `tools/pre-merge-check.ps1` 全緑(Release ビルド 0 警告)
2. テスト数 805 → **827**(App 19→41・純増 +22)
3. **挙動不変**: ダイアログ・警告の文言/順序/戻り値・SaveAs ロールバック順序を変えない(diff レビューで機械的確認)
4. 別エージェントによるコードレビュー(マージ前)
5. L5 実機 SR スポット確認は**不要**(根拠: 設計書 §5「他 Stage はダイアログ抽象化のみで SR 経路不変」・PC-Talker は恒久除外済み)。手動スモーク 1 分は任意
6. main へ no-ff マージ+設計書へ実施記録・マージハッシュ追記

## リスクと対策

- **実ファイル I/O のテスト**: `Directory.CreateTempSubdirectory` 配下のみ使用・テスト毎に使い捨て・削除失敗は握る(フレーク防止)。CI(windows-latest)実機は未検証のまま=Phase 1 からの既知の申し送りと同じ穴。初回 push で観察。
- **Shift_JIS(932) の利用**: EncodingCatalog.EnsureRegistered が CodePagesEncodingProvider を登録済み(本番 SaveAs と同一経路)。CI でも NuGet 依存は Core 経由で解決される。
- **ctor シグネチャ変更の影響範囲**: FileController の生成箇所は MainForm.cs:53 の 1 箇所のみ(grep 確認済み)。
- **特徴付けの赤**: 原則テスト側を現行挙動へ合わせるが、SaveAs ロールバック系の赤は実装バグの可能性=ユーザーへ報告(規約参照)。

## 申し送り(Stage 4 へ)

- **【重要・別ブランチで修正予定】バックアップ復元タブが dirty にならない実バグを Task 6 のテストが発見**(2026-07-14・ユーザー判断=Stage 3 は現行挙動の特徴付けで完了し、マージ直後の修正ブランチで対応):
  - 現象: `FileController.RestoreFromBackup` のコメント意図「SetSavePoint しない → Modified=true のまま」に対し、`TextBuffer.FromString` は生成時点で保存点を持つ(`TextBuffer.cs` の `_savedRoot = root`)ため復元直後は **Modified=false**。
  - 影響(サイレントなデータ恒久喪失): ①タブに「*」が付かない ②復元後は HasBackup=true で登録され(BackupCoordinator.cs:102,125)、次の Reconcile で BackupPlanner が Delete を返し**バックアップ本体が削除される**(BackupPlanner.cs:22) ③終了時の保存確認がスキップされる(MainForm.cs:138 の `if (!doc.Editor.Modified) continue;`)。
  - 由来: 旧 Scintilla の SetText 意味論(Modified=true)前提のコメントが、自作 EditorControl/TextBuffer 移行後の意味論(新規バッファ=クリーン)と乖離した回帰。
  - 修正ブランチの内容: 復元経路の dirty 化+本計画の RestoreFromBackup 2 テストの期待反転(`Assert.False`→`Assert.True`・ラベル「* 」付き)+テスト名へ StaysDirty を戻す。
- **WriteToPath 失敗時の本文 EOL 変換はロールバック対象外**(既知・非ブロッカー): WriteToPath は保存前に `ConvertEols` で本文の改行を State.LineEnding へ正規化してから失敗し得る。State はロールバックされるが変換済み本文は元に戻らない。実害は限定的(次の保存で State どおり再変換され整合する)が、厳密なロールバックが必要になったら別途判断。本計画のテストは改行を含まない本文で特徴付けし、この灰色域を仕様として固定しない。
- Stage 2 レビュー由来の「空白のみメッセージ(`" "`)の特徴付け」は Stage 4(SearchController=FakeAnnouncer 実使用開始)で再検討(継続)。
- 次 Stage: SearchController(IFindReplaceView 導入)= Phase 2 設計書 §4 Stage 4。FindReplaceDialog↔SearchController の相互参照はビュー→Controller 方向をイベント/コールバックへ置き換えて循環を断つ(設計書 §5)。
