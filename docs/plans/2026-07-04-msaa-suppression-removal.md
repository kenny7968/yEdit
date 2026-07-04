# MSAA 抑制の撤去 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** NVDA 経路のクライアント MSAA 抑制（`SuppressClientMsaa`）を撤去し、「NVDA が起動しているときだけ行うアクセシビリティ処理」を完全解消する。

**Architecture:** `ScintillaHost` から抑制プロパティと `WndProc` の `OBJID_CLIENT` 分岐を取り除き、SR 適応（`ApplySrAdaptation`）は UIA プロバイダ提供可否（`ServeUiaProvider`）のみに一本化する。あわせて実装と食い違っていた doc コメント（handoff (1)）と HANDOFF §13.4 の表を現仕様へ更新する。設計は docs/plans/2026-07-04-msaa-suppression-removal-design.md、根拠は docs/plans/2026-07-04-validate-uia-handoff.md。

**Tech Stack:** C# / .NET WinForms（yEdit.sln）。ブランチ `refactor/msaa-false`。

**TDD について:** 本変更に新規テストは書かない。`WM_GETOBJECT` 処理は実 HWND＋アクセシビリティクライアントが必要でユニットテスト不能であり、挙動の正しさは実機 NVDA 検証で確認済み（handoff §2・2026-07-04）。`SuppressClientMsaa` を参照するテストは存在しない。回帰安全網は「既存テスト全緑（289 件）＋ビルド 0 警告」。

**前提（重要）:** 作業ツリーの `src/yEdit.Editor/ScintillaHost.cs` には validate/uia の検証用変更（`SuppressClientMsaa = false` 固定＋`★検証中` コメント）が未コミットで残っている。**git restore せず、その状態から直接編集する**（本実装が検証用変更を置き換える）。`tools/verify-msaa-client.ps1`（未追跡）と `installer/`・`publish/`（無関係）には触らない。

---

### Task 1: ScintillaHost / NativeMethods から MSAA 抑制コードを撤去

**Files:**
- Modify: `src/yEdit.Editor/ScintillaHost.cs`（プロパティ 82-87 行・ApplySrAdaptation 91-103 行・WndProc 139-151 行）
- Modify: `src/yEdit.Editor/NativeMethods.cs:10-11`

**Step 1: 変更前の基準を確認する**

Run: `dotnet test yEdit.sln`
Expected: 全テスト PASS（289 件）。失敗があればこの計画の外の問題なので停止して報告。

**Step 2: `SuppressClientMsaa` プロパティを削除**

`src/yEdit.Editor/ScintillaHost.cs` から以下のブロックを丸ごと削除（直前の空行 1 行も詰める）:

```csharp
    /// <summary>
    /// WM_GETOBJECT(OBJID_CLIENT) で 0 を返し、ウィンドウのネイティブ MSAA を抑制する。
    /// ApplySrAdaptation が SR 経路で確定する（ネイティブ読み＝NVDA 経路のときのみ抑制）。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SuppressClientMsaa { get; set; }
```

**Step 3: `ApplySrAdaptation` を UIA 提供可否のみに**

同ファイルの以下を:

```csharp
    /// <summary>
    /// 起動時に確定した SR 経路を UIA/MSAA の提供可否へ反映する（確定アーキテクチャ）。
    /// ネイティブ読み（NVDA 経路）→ 我々は引っ込む。それ以外（PC-Talker 経路）→ UIA 提供。
    /// 判定は App 層（SrContext）が起動時に1回だけ行い、全タブへ同じ値を渡す（タブ間一貫）。
    /// ハンドル生成前に呼ぶこと（WM_GETOBJECT 前に値を確定させる）。
    /// </summary>
    public void ApplySrAdaptation(bool useNativeReading)
    {
        ServeUiaProvider = !useNativeReading;
        // ★検証中(validate/uia): NVDA 経路の MSAA 抑制が本当に必要かを実機確認するため常に false。
        // 元実装: SuppressClientMsaa = useNativeReading;
        SuppressClientMsaa = false;
    }
```

こう書き換える:

```csharp
    /// <summary>
    /// 起動時に確定した SR 経路を UIA プロバイダの提供可否へ反映する（確定アーキテクチャ）。
    /// ネイティブ読み（NVDA 経路）→ 我々は引っ込む。それ以外（PC-Talker 経路）→ UIA 提供。
    /// かつて NVDA 経路で併用したクライアント MSAA 抑制は実測で不要と確定し撤去済み
    /// （docs/plans/2026-07-04-validate-uia-handoff.md §2）。再導入しないこと。
    /// 判定は App 層（SrContext）が起動時に1回だけ行い、全タブへ同じ値を渡す（タブ間一貫）。
    /// ハンドル生成前に呼ぶこと（WM_GETOBJECT 前に値を確定させる）。
    /// </summary>
    public void ApplySrAdaptation(bool useNativeReading)
    {
        ServeUiaProvider = !useNativeReading;
    }
```

**Step 4: `WndProc` の `OBJID_CLIENT` 分岐を削除**

同ファイルの以下を:

```csharp
            int objid = unchecked((int)m.LParam.ToInt64());
            bool serve = objid == NativeMethods.UiaRootObjectId && ServeUiaProvider;
            if (serve)
            {
                _provider ??= new TextControlProvider(this);
                m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, m.WParam, m.LParam, _provider);
                return;
            }
            if (objid == NativeMethods.OBJID_CLIENT && SuppressClientMsaa)
            {
                // ネイティブ MSAA を返さない（PC-Talker を UIA ブリッジへ誘導する試み）。
                m.Result = nint.Zero;
                return;
            }
```

こう書き換える（一時変数 `serve` は分岐 1 つになるため畳む）:

```csharp
            int objid = unchecked((int)m.LParam.ToInt64());
            if (objid == NativeMethods.UiaRootObjectId && ServeUiaProvider)
            {
                _provider ??= new TextControlProvider(this);
                m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, m.WParam, m.LParam, _provider);
                return;
            }
```

**Step 5: 未使用になった `OBJID_CLIENT` 定数を削除**

`src/yEdit.Editor/NativeMethods.cs` から以下 2 行を削除（直前の空行 1 行も詰める）:

```csharp
    /// <summary>MSAA クライアント領域オブジェクト（OBJID_CLIENT）。</summary>
    public const int OBJID_CLIENT = -4;
```

**Step 6: ビルドとテストで回帰なしを確認**

Run: `dotnet build yEdit.sln; dotnet test yEdit.sln`
Expected: ビルド警告 0・エラー 0。全テスト PASS（289 件）。
`SuppressClientMsaa` / `OBJID_CLIENT` のコンパイル参照が残っていればここでエラーになる。

Run: `git grep -n "SuppressClientMsaa\|OBJID_CLIENT" -- src`
Expected: ヒットなし（src 配下から完全消滅）。

**Step 7: Commit**

```powershell
git add src/yEdit.Editor/ScintillaHost.cs src/yEdit.Editor/NativeMethods.cs
git commit -m @'
MSAA抑制撤去: NVDA経路のOBJID_CLIENT分岐とSuppressClientMsaaを削除

2026-07-04の表面計測＋実機NVDA音声確認で抑制不要と確定
（docs/plans/2026-07-04-validate-uia-handoff.md §2）。
ApplySrAdaptationはServeUiaProviderのみとなり、
「NVDA起動時だけ行う処理」が完全解消。

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 2: SrRoute の doc コメント修正（handoff (1)）

**Files:**
- Modify: `src/yEdit.Core/Speech/SrRoute.cs:11`

**Step 1: コメントから「ネイティブ MSAA 抑制」を削除**

以下を:

```csharp
    /// <summary>PC-Talker 経路: 自前 UIA プロバイダ提供＋ネイティブ MSAA 抑制、能動発声は PCTKPReadW 直叩き。</summary>
```

こう書き換える:

```csharp
    /// <summary>PC-Talker 経路: 自前 UIA プロバイダ提供、能動発声は PCTKPReadW 直叩き。</summary>
```

（元コメントは実装と食い違っていた: MSAA 抑制は PC-Talker 経路で行われたことは一度もなく、一貫して NVDA 経路。実装が正・コメントが誤り。handoff §3(1)）

**Step 2: ビルド確認（コメントのみだが念のため）**

Run: `dotnet build yEdit.sln`
Expected: 警告 0・エラー 0。

**Step 3: Commit**

```powershell
git add src/yEdit.Core/Speech/SrRoute.cs
git commit -m @'
docコメント修正: SrRoute.PcTalkerからMSAA抑制の誤記を削除

MSAA抑制がPC-Talker経路で行われたことはなく（一貫してNVDA経路・
前コミットで撤去済み）、実装が正でコメントが誤り（handoff §3(1)）。

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 3: HANDOFF §13.4 の表と冒頭サマリを現仕様へ更新

**Files:**
- Modify: `docs/HANDOFF-scintilla-uia.md:20`（冒頭サマリ）
- Modify: `docs/HANDOFF-scintilla-uia.md:252-257`（§13.4 の表と実装説明）

注意: §13.3「ネイティブ MSAA 抑制（WM_GETOBJECT -4 で 0 返し）→ …試行と棄却」（247 行）は**実験履歴なので変更しない**。

**Step 1: 冒頭サマリ（20 行目）を更新**

以下を:

```markdown
  - **NVDA 起動中 → 我々は UIA も MSAA も出さない**（`ServeUiaProvider=false`/`SuppressClientMsaa=true`）。NVDA は **64bit Scintilla5 をネイティブで読める**（実機確認。Notepad++ 由来の「64bit で壊れる」予想は外れた）。
```

こう書き換える:

```markdown
  - **NVDA 起動中 → 我々は UIA プロバイダを出さない**（`ServeUiaProvider=false`）。NVDA は **64bit Scintilla5 をネイティブで読める**（実機確認。Notepad++ 由来の「64bit で壊れる」予想は外れた）。※当初併用していたクライアント MSAA 抑制（`SuppressClientMsaa`）は 2026-07-04 の実測で不要と確定し撤去（docs/plans/2026-07-04-validate-uia-handoff.md §2）。
```

**Step 2: §13.4 の表から SuppressClientMsaa 列を除去し注記を追加**

以下を:

```markdown
| 判定 | ServeUiaProvider | SuppressClientMsaa | 読まれ方 |
|---|---|---|---|
| **NVDA 起動中** | **false** | **true** | NVDA が**ネイティブ Scintilla**を読む（64bit でも読めた・実機確認） |
| それ以外（PC-Talker 等） | **true** | false | PC-Talker が**我々の UIA**を読む |
- 判定の要は「**NVDA が動いているか**」だけ: `ScreenReaders.IsNvdaRunning()` = プロセス `nvda` の有無。NVDA があれば譲り、無ければ UIA を出す（PC-Talker・SRなし・他UIA系SR で安全）。
- 実装: `MainForm` 起動時に判定 → `_editor.ServeUiaProvider`/`SuppressClientMsaa` を設定。手動上書き `--nvda`/`--pctalker`、低レベル `--no-uia`/`--no-msaa`/`--rename-class`/`--edit` も残置。起動構成はログ `[config]` 行に出る。
```

こう書き換える:

```markdown
| 判定 | ServeUiaProvider | 読まれ方 |
|---|---|---|
| **NVDA 起動中** | **false** | NVDA が**ネイティブ Scintilla**を読む（64bit でも読めた・実機確認） |
| それ以外（PC-Talker 等） | **true** | PC-Talker が**我々の UIA**を読む |
- 判定の要は「**NVDA が動いているか**」だけ: `ScreenReaders.IsNvdaRunning()` = プロセス `nvda` の有無。NVDA があれば譲り、無ければ UIA を出す（PC-Talker・SRなし・他UIA系SR で安全）。
- 注: 当初 NVDA 経路ではクライアント MSAA 抑制（`SuppressClientMsaa=true`＝WM_GETOBJECT(OBJID_CLIENT) に 0 返し）も併用していたが、2026-07-04 の表面計測＋実機 NVDA 音声確認で不要と確定し撤去した（docs/plans/2026-07-04-validate-uia-handoff.md）。現在の SR 適応は `ServeUiaProvider` の 1 点のみ。
- 実装: `MainForm` 起動時に判定 → `_editor.ServeUiaProvider` を設定（現行は `ApplySrAdaptation`）。プローブ当時は手動上書き `--nvda`/`--pctalker`、低レベル `--no-uia`/`--no-msaa`/`--rename-class`/`--edit` も残置していた（本番アプリでは廃止）。起動構成はログ `[config]` 行に出る。
```

**Step 3: 取りこぼしがないか確認**

Run: `git grep -n "SuppressClientMsaa"`
Expected: ヒットは `docs/plans/`（過去の計画書＝歴史記録）と `docs/HANDOFF-scintilla-uia.md` の撤去注記のみ。`src/` にはなし。

**Step 4: Commit**

```powershell
git add docs/HANDOFF-scintilla-uia.md
git commit -m @'
HANDOFF更新: §13.4のSR適応表からSuppressClientMsaaを除去

MSAA抑制の撤去（2026-07-04実測で不要確定）を反映し、
撤去の経緯と根拠文書への参照を注記として追加。

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 4: 最終検証

**Step 1: クリーン状態の確認**

Run: `git status`
Expected: 追跡ファイルの未コミット変更なし。未追跡は `installer/`・`publish/`・`tools/verify-msaa-client.ps1` のみ（意図的残置・触らない）。

**Step 2: ビルド 0 警告＋全テスト緑**

Run: `dotnet build yEdit.sln; dotnet test yEdit.sln`
Expected: 警告 0・エラー 0・全テスト PASS（289 件）。

**Step 3: レビューとマージ（実装完了後の作法）**

- 別エージェントにコードレビューを依頼（superpowers:requesting-code-review）。
- 指摘対応後、main へ no-ff マージ:

```powershell
git checkout main
git merge --no-ff refactor/msaa-false -m @'
Merge branch 'refactor/msaa-false': NVDA経路のクライアントMSAA抑制を撤去

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

実機 SR 再検証は不要（handoff §2 で抑制なしビルドの実機 NVDA 確認済み・チェックリスト 6 項目 OK）。
