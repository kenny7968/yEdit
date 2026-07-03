# 読み上げコンポーネント リファクタリング実装計画（2026-07-03）

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** NVDA/PC-Talker 読み上げコンポーネントの調査で確定した改善5項目＋UiaProbe 削除を、挙動を変えずに（項目3の UIA Name 文言のみ意図的変更）実装する。

**Architecture:** SR 環境判定を App 層の `SrContext`（起動時1回）へ一元化し、能動通知（Announcer）と受動読み（UIA プロバイダ提供可否）の両経路が同じ判定結果を消費する形にする。診断計装（UiaDiag）は唯一の利用者 UiaProbe ごと削除。Announcer の Say 契約を「空＝視覚クリアのみ」に変更して UI 側の二重呼び出しを畳む。

**Tech Stack:** .NET 9 / WinForms / Scintilla5.NET / UIA Provider（yEdit.Accessibility）/ PCTKUsr.dll 遅延束縛

**前提・制約:**
- Core（yEdit.Core）は無変更。既存 270 テストが緑のままであること。
- `PcTalkerSpeech.Speak` の呼び出し行（`PCTKPReadW(text,1,1)`）と差し替え候補コメントは維持（yEdit 内可聴検証は別途ユーザーが実施）。
- 各タスク完了ごとにビルド 0 警告を確認してコミット。
- ユーザー決定: 調査報告の項目1〜5は推奨案・項目6（テスト可能化）は見送り・項目7のうち UiaProbe は削除。
  UiaProbe 削除に伴い項目2は「ゲート化（案A）」ではなく「完全削除（案B）」へ調整（唯一の利用者が消えるため）。

---

### Task 1: PC-Talker 実機検証レポートのコミット

**Files:**
- Add: `docs/report-pctalker-speech/2026-06-29-pctk-speech-manual-verification.md`（既存・未追跡）

`PCTKPReadW(text,1,1)` がプローブアプリで実機可聴確認済みという一次資料。

**Step 1:** `git add docs/report-pctalker-speech && git commit`
コミットメッセージ: `docs: PC-Talker 発声プローブの実機手動検証レポートを追加`

### Task 2: SR判定の SrContext 一元化（項目1＋項目4の死にコード削除）

**Files:**
- Create: `src/yEdit.App/Speech/SrContext.cs`
- Modify: `src/yEdit.App/Program.cs`（Main 冒頭で `SrContext.Detect()`）
- Modify: `src/yEdit.App/AnnouncerFactory.cs`（`_mode` キャッシュ削除・`SrContext.Mode` 参照）
- Modify: `src/yEdit.Editor/ScintillaHost.cs:80-92`（`ConfigureForCurrentScreenReader()` → `ApplySrAdaptation(bool nvdaRunning)`）
- Modify: `src/yEdit.App/MainForm.cs:87`（呼び出し置換）
- Delete: `src/yEdit.Editor/ScreenReaders.cs`（NVDA 判定は SrContext へ移設。`IsPcTalkerRunning()` は未使用の死にコードとして削除）
- Modify: `src/yEdit.Core/Speech/SrSpeechSelector.cs`（doc コメントの `ConfigureForCurrentScreenReader` 参照を更新）
- Modify: `src/yEdit.App/Speech/PcTalkerSpeech.cs`（プローブ可聴確認レポートへの注記1行）

**SrContext 設計:**
```csharp
internal static class SrContext
{
    public static bool NvdaRunning { get; private set; }
    public static SpeechMode Mode { get; private set; } = SpeechMode.Uia; // 未検出時の無害な既定
    public static void Detect() { ... } // Program.Main が UI 開始前に1回だけ呼ぶ
}
```
- 判定は「起動時1回・以後不追従」（受動読み・能動通知の両経路で一貫。従来はタブ生成ごとに NVDA を再判定しており、起動後の SR 変化でタブ間不整合が起き得た）。
- `ApplySrAdaptation` は判定結果を受け取るだけにし、Editor 層から Process 依存を除去。

**Step 1:** 上記を実装
**Step 2:** `dotnet build` → 0 警告
**Step 3:** コミット `App/Editor: SR判定を SrContext へ一元化（起動時1回・タブ間一貫）`

### Task 3: IAnnouncer/AnnouncerFactory の Speech/ 移動（項目4）

**Files:**
- Move: `src/yEdit.App/IAnnouncer.cs` → `src/yEdit.App/Speech/IAnnouncer.cs`（namespace `yEdit.App.Speech`）
- Move: `src/yEdit.App/AnnouncerFactory.cs` → `src/yEdit.App/Speech/AnnouncerFactory.cs`（同上）
- Modify: `MainForm.cs` / `CsvController.cs` / `FindReplaceDialog.cs` / `GrepDialog.cs` に `using yEdit.App.Speech;`

**Step 1:** git mv＋namespace 変更＋using 追加
**Step 2:** `dotnet build` → 0 警告
**Step 3:** コミット `App: IAnnouncer/AnnouncerFactory を Speech/ へ移動し namespace を統一`

### Task 4: Say 契約の統一（項目5）

**Files:**
- Modify: `src/yEdit.App/Speech/AnnouncerBase.cs`（空メッセージ＝Label クリアのみ・発声なし）
- Modify: `src/yEdit.App/Speech/IAnnouncer.cs`（契約の doc コメント）
- Modify: `src/yEdit.App/SearchController.cs`（`Announce`＋`SetStatus` の同文字列ペア約10箇所を `Announce` 1呼び出しへ）
- Modify: `src/yEdit.App/GrepController.cs:82-83`（発声→視覚の順に整理: label は「検索中…」を保持）

**新契約:**
```csharp
public void Say(string message)
{
    _label.Text = message ?? "";                  // 視覚は無条件（空はクリア）
    if (string.IsNullOrEmpty(message)) return;    // 空は視覚クリアのみ（発声なし）
    Speak(message);
}
```
- `SetStatus` は「視覚のみ更新（発声させたくない）」用途（検索の増分カウント・grep 進捗）として存置。
- `ReplaceOne` の「置換しました。これ以上見つかりません」は Announce の文字列に統一（従来 label は短文だった＝意図的な軽微変更）。

**Step 1:** 実装
**Step 2:** `dotnet build` → 0 警告
**Step 3:** コミット `App: Say契約を「空=視覚クリアのみ」に統一し SetStatus 二重呼び出しを解消`

### Task 5: UiaProbe の削除（項目7）

**Files:**
- Delete: `src/yEdit.UiaProbe/`（ディレクトリごと）
- Modify: `yEdit.sln`（`dotnet sln remove`）

役目を終えた実験機（ユーザー判断）。UIA 路線検証・PC-Talker トレースのハーネスだったが、本番は Scintilla 路線で確定済み。git 履歴からいつでも復元可能。

**Step 1:** `dotnet sln yEdit.sln remove src/yEdit.UiaProbe/yEdit.UiaProbe.csproj`
**Step 2:** ディレクトリ削除
**Step 3:** `dotnet build` → 0 警告
**Step 4:** コミット `chore: UiaProbe をディレクトリごと削除`

### Task 6: UiaDiag と計装呼び出しの削除（項目2・案B）

**Files:**
- Delete: `src/yEdit.Accessibility/UiaDiag.cs`
- Modify: `src/yEdit.Accessibility/TextRangeProvider.cs`（全 Log 呼び出し・`_id`/`_seq`/`Tag`/`Tid` 削除）
- Modify: `src/yEdit.Accessibility/TextProviderImpl.cs`（全 Log 呼び出し・`Tid` 削除）

UiaProbe 削除で Sink 設定者が消滅（本番は常時 null）。`GetText` の `UiaDiag.Trunc` は全文 2 回コピーを誘発しており、
PC-Talker の文字/行歩き読みのホットパスから確実に除去する。

**Step 1:** 実装（挙動同一・ログ行のみ削除）
**Step 2:** `dotnet build` → 0 警告
**Step 3:** コミット `Accessibility: UiaDiag 計装を削除（本番ホットパスの全文コピー解消）`

### Task 7: UIA Name/AutomationId の本番化（項目3）

**Files:**
- Modify: `src/yEdit.Accessibility/IUiaTextHost.cs`（`Name` / `AutomationId` を追加）
- Modify: `src/yEdit.Accessibility/TextControlProvider.cs:39-42`（ホスト供給へ置換）
- Modify: `src/yEdit.Editor/ScintillaHost.cs`（明示実装: Name=`"本文"` / AutomationId=`"editor"`）

プローブ残骸 `"UIA Probe Document"` / `"uiaProbeDocument"` の本番露出を解消（M2 申し送り「UIA Name 残骸」）。
**SR 可聴面の変更**: PC-Talker がフォーカス時に読む名前が変わる → 実機検証項目に追加（別途ユーザー実施）。

**Step 1:** 実装
**Step 2:** `dotnet build` → 0 警告
**Step 3:** コミット `Accessibility/Editor: UIA Name/AutomationId を本番文言へ（プローブ残骸解消）`

### Task 8: 検証と記録

**Step 1:** `dotnet build`（Release も）→ 0 警告 / `dotnet test` → Core 270 緑
**Step 2:** アプリ起動スモーク（起動5秒生存→終了）
**Step 3:** 本計画ファイルに対応記録（結果）を追記しコミット
**Step 4:** 別エージェントへコードレビュー依頼 → 指摘反映
**Step 5:** main へ no-ff マージ

## 実機SR検証への申し送り

- Task 7 で PC-Talker がフォーカス時に読む名前が「UIA Probe Document」→「本文」に変わる。次回実機検証で確認。
- yEdit 内での PC-Talker 可聴検証（`PCTKPReadW(text,1,1)`）は引き続き未実施（ユーザーが別途実施）。

## 対応記録（2026-07-03 実施結果）

全タスク完了。Task 順の実装で、各コミットともビルド 0 警告。

- Task 1: `8ae4dbf` レポート＋本計画コミット
- Task 2: `5b46e2e` SrContext 一元化（ScreenReaders.cs 削除・ApplySrAdaptation 化）
- Task 3: `9d6c3c7` IAnnouncer/AnnouncerFactory を Speech/ へ移動
- Task 4: `97c98ac` Say 契約統一（SearchController のペア10箇所解消・GrepController 順序整理）
- Task 5: `757613b` UiaProbe 削除（sln からも除去）
- Task 6: `8288cc0` UiaDiag 計装削除（GetText の全文2回コピー解消）
- Task 7: `b3088d1` UIA Name=「本文」/ AutomationId="editor" へ

**検証**: Debug/Release ビルド 0 警告・0 エラー、Core テスト 270 件全緑、
起動スモーク（5秒生存→終了）OK。Core は SrSpeechSelector の doc コメント以外無変更。

**意図的な軽微変更（挙動）**:
- 検索の「置換しました。これ以上見つかりません」時、ダイアログの視覚ステータスも
  発声と同一文字列になる（従来は短文「これ以上見つかりません」）。
- grep 開始時、視覚ステータスは「検索中…」を保持（従来は「検索を開始しました」で即上書き）。
- SR 適応の判定がタブ生成時→起動時1回になり、起動後の SR 起動/終了にタブ単位で
  追従しなくなった（起動時確定方針への統一。従来はタブ間で不整合が起き得た）。
