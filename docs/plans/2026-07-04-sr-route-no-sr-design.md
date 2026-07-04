# SR 非稼働時の既定経路の退行修正（汎用 UIA 経路の導入）設計

対応する引き継ぎ: docs/plans/2026-07-04-validate-uia-handoff.md §3(2)

## 背景と問題

2026-07-04 の優先 SR 設計（`SrRouteSelector`）は「優先 SR が稼働 or どちらも非稼働 → 優先 SR の経路」とした。この結果、既定設定（`PreferredScreenReader="nvda"`）では **SR なし環境で NVDA 経路＝UIA プロバイダ不提供**になる。

旧規則（HANDOFF §13.4）は「NVDA 不在なら UIA 提供＝PC-Talker・SR なし・他 UIA 系 SR で安全」。"nvda" は既定値なので、設定を触っていない晴眼者・ナレーター/JAWS 等のサードパーティ SR ユーザー全員がこの退行を踏む。yEdit は全盲専用ではなく晴眼/弱視も第一級のユーザーであり、SR なし環境を「NVDA ユーザーの起動前」と同一視するのは誤り。

## 決定: 新しい解決規則

> **検出された SR の経路。両方稼働なら「優先するスクリーンリーダー」設定が決める。どちらも非検出なら汎用 UIA 経路（新設）。**

決定表（ユーザー確認済み 2026-07-04「どちらのときも UIA は出した方がいい」）:

| 起動時の状況 | 優先=NVDA（既定） | 優先=PC-Talker |
|---|---|---|
| NVDA のみ稼働 | NVDA 経路 | NVDA 経路（検出優先） |
| PC-Talker のみ稼働 | PC-Talker 経路（検出優先） | PC-Talker 経路 |
| 両方稼働 | NVDA 経路 | PC-Talker 経路（設定が勝つ） |
| どちらも非稼働 | **汎用 UIA 経路（新・変更）** | **汎用 UIA 経路（新・変更）** |

- 「優先するスクリーンリーダー」設定が効くのは**両方稼働の競合時のみ**となり、設定の意味が単純化する（旧「救済」概念は「検出された方を使う」に吸収）。
- 経路の中身:
  - **NVDA 経路**: UIA プロバイダ不提供（ネイティブ Scintilla 読み）＋能動発声は UIA 通知（従来どおり）。
  - **PC-Talker 経路**: 自前 UIA プロバイダ提供＋`PCTKPReadW` 直叩き＋空行「空行」能動発声（従来どおり）。
  - **汎用 UIA 経路（新設）**: 自前 UIA プロバイダ提供＋能動発声は UIA 通知。空行の能動発声はしない（`SpeechMode.PcTalker` 限定のまま）。ナレーター/JAWS 等の UIA 系 SR・SR なしで安全。

## トレードオフ（受容済み）

いずれも再起動で回復し、起動時確定方針（SR の起動/終了に追従しない）は変えない。

1. **優先=NVDA＋SR なしで起動→後から NVDA 起動** → 無音（自前 UIA プロバイダと NVDA の Scintilla オーバーレイの競合・HANDOFF §13.2）。旧規則と同じ挙動への回帰。NVDA は通常ログイン時に起動済みでレアケース。引き継ぎ §3(2) の修正方針案どおり。
2. **優先=PC-Talker＋SR なしで起動→後から PC-Talker 起動** → 受動読み（UIA プロバイダ）は機能するが、能動発声（通知・空行）が UIA 通知になり PC-Talker では読まれない可能性（現行規則からの新たな縮退・軽微）。

## 実装形

### Core

- `SrRoute` に第 3 値 **`Uia`** を追加（doc コメント: 汎用 UIA 経路 = SR 非検出時。プロバイダ提供＋能動発声は UIA 通知）。enum の doc コメント「受動読みと能動発声は常にペア」は 3 値でも維持される。
- `SrRouteSelector.Select` を新規則へ:

```csharp
public static SrRoute Select(bool preferNvda, bool nvdaRunning, bool pcTalkerRunning)
{
    if (nvdaRunning && pcTalkerRunning) return preferNvda ? SrRoute.Nvda : SrRoute.PcTalker;
    if (nvdaRunning) return SrRoute.Nvda;
    if (pcTalkerRunning) return SrRoute.PcTalker;
    return SrRoute.Uia;
}
```

### App（導出は無変更で自然に正しくなる）

- `SrContext.UseNativeReading`（= `Route == SrRoute.Nvda`）→ **無変更**。汎用 UIA 経路では false = プロバイダ提供。
- `SrContext.Mode`（= `Route == PcTalker ? SpeechMode.PcTalker : SpeechMode.Uia`）→ **無変更**。汎用 UIA 経路は UIA 通知 Announcer。
- `AnnouncerFactory` / `PcTalkerAnnouncer` / `UiaAnnouncer` / `MainForm` 空行発声ガード → **無変更**。
- `SrContext.Route` の未検出時既定を `SrRoute.Nvda` から **`SrRoute.Uia` へ変更**（意味の一貫性: 「何も検出していない」状態＝汎用 UIA 経路。`Detect` は Program.Main で必ず呼ばれるため挙動影響なし）。
- doc コメント更新: `SrRouteSelector.cs`・`SrContext.cs` の規則記述（「優先 SR が稼働 or どちらも非稼働 → 優先 SR」→ 新規則）。

### 設定 UI

- `SpeechSettingsTab` の UI 文言・項目は**無変更**（「優先するスクリーンリーダー」は競合解決の意味として引き続き正確。説明過多を避ける）。設定値 "nvda"/"pctalker" の正規化も無変更（汎用 UIA 経路は設定値ではなく検出結果）。
- 将来のヘルプに「両方稼働時にどちらを使うかの設定。非稼働時は標準の UIA 対応で動作」と記載（申し送り）。

### ドキュメント

- HANDOFF §13.4: SR 適応表に「どちらも非稼働 → ServeUiaProvider=true（汎用 UIA 経路）」の行と新規則（本設計への参照）を反映。「判定の要は NVDA が動いているかだけ」の記述を新規則に合わせて更新。
- docs/plans/2026-07-04-validate-uia-handoff.md §3(2) に対応済みの旨を追記（マージ時）。

## テスト・検証

- **Core 単体テスト**: `SrRouteSelectorTests` の決定表を更新。変わるのは非稼働 2 ケースのみ:
  - `(preferNvda: true,  nvda: false, pct: false)` → `SrRoute.Uia`（旧 Nvda）
  - `(preferNvda: false, nvda: false, pct: false)` → `SrRoute.Uia`（旧 PcTalker）
  - 他 6 ケースは期待値不変（回帰確認）。
- **スクリプト検証**: SR なし環境で起動し `UiaHasServerSideProvider == True` を確認（tools/verify-msaa-client.ps1 の計測部を流用可・作業ツリーに残置あり）。
- **実機 SR 検証**（マージ前 DoD）: NVDA 稼働起動→従来どおり読める（経路不変の回帰確認）。PC-Talker 稼働起動→従来どおり（同）。可能ならナレーターで SR なし既定設定の読み上げ確認。

## 影響ファイル

- 変更: `src/yEdit.Core/Speech/SrRoute.cs`、`src/yEdit.Core/Speech/SrRouteSelector.cs`、`src/yEdit.App/Speech/SrContext.cs`、`tests/yEdit.Core.Tests/Speech/SrRouteSelectorTests.cs`、`docs/HANDOFF-scintilla-uia.md`
- 無変更: `ScintillaHost`（`ApplySrAdaptation`/`ServeUiaProvider`）、`AnnouncerFactory`・Announcer 各実装、`SpeechMode`、`MainForm`、`Program.cs`、設定タブ・設定永続化
