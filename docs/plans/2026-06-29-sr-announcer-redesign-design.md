# SR適応 Announcer 再設計（起動時インスタンス確定）設計

- 日付: 2026-06-29
- ブランチ: `fix/pctalker-announce`
- 関連: `docs/report-pctalker-speech/2026-06-29-pctk-speech-manual-verification.md`（PC-Talker発声プローブ実機検証）
- 既存設計: `docs/plans/2026-06-28-sr-speech-design.md`（毎回ルーティング版。本設計で置換）

## 1. 背景と問題

CSVモード実装後の実機検証で、**PC-Talker が意図した音声発声を行わない**ことが判明（NVDAは問題なし）。

調査レポートの要点：
- yEdit が現在使う `PCTKPReadW(text, 0, 1)` は、**単体プローブでは可聴**だが、**yEdit 内では無音**。
- `PCTKPReadW(text, 1, 1)`（priority=1 割り込み）と `PCTKCGuide(text)` も**プローブで可聴**。
- → 無音の原因は「手段の選択ミス」ではなく、**yEdit 実行時の文脈**（yEdit が常時出す UIA/フォーカス発話が、非割り込み `priority=0` のキュー発話を埋もれさせる等）の可能性が高い。

CSVモード個別に発声を改善するのではなく、**Announcer の抽象化＋起動時インスタンス確定**で、SR ごとに適切な発声手段を選ぶ構造へ再設計する。

## 2. ゴール

- Announcer を抽象化し、**yEdit 起動時に** PC-Talker 稼働か NVDA 稼働かで**インスタンス化する実体を確定**する。
- NVDA 用は変更不要（UIA 通知で鳴る）。**PC-Talker 用は実際に音が鳴るよう調整**する（既定 `PCTKPReadW(text,1,1)`、1箇所で差し替え可能）。
- 発声 I/O の可聴判定はツールで自動化できない（`TrySpeak` は無音でも成功扱い＝true を返す）ため、**実機で鳴るまで手段を差し替えて検証**する反復を前提とする。

## 3. 採用アプローチ（A案）

Announcer をインターフェース化し、起動時確定の SR 判定でファクトリが実体を選ぶ。

```
[App] IAnnouncer.Say(string msg)
   ├ UiaAnnouncer(label)      : label.Text = msg; label.AccessibilityObject.RaiseAutomationNotification(...)
   └ PcTalkerAnnouncer(label) : label.Text = msg; PcTalkerSpeech.Speak(msg)   // 既定 PCTKPReadW(msg, 1, 1)

[App] AnnouncerFactory.Create(Label) → 上記いずれか（起動時キャッシュした SpeechMode に従う）

[Core] SrSpeechSelector.Select(bool nvdaRunning, bool pcTalkerRunning) → enum SpeechMode { Uia, PcTalker }
       （純関数・WinForms 非依存・単体テスト対象）
```

### なぜ A 案か
- ユーザ意図「Announcer を抽象化し、起動時にインスタンス化する実体を決める」に直結。
- 選択ロジックを Core の純関数に切り出して**単体テスト可能**。
- PC-Talker の発声手段が `PcTalkerSpeech` の1箇所に集約され、**実機検証での差し替えが容易**。

## 4. SR 判定（起動時1回・キャッシュ）

- NVDA: `ScreenReaders.IsNvdaRunning()`（既存・プロセス名 "nvda"）
- PC-Talker: `PcTalkerSpeech.IsRunning()`（既存・DLL `PCTKStatus()`。ブランド/バージョン非依存で信頼性が高い）

選択規則（`SrSpeechSelector.Select`）：

| NVDA | PC-Talker | → モード | 理由 |
|---|---|---|---|
| 稼働 | 不問 | **Uia** | NVDA 優先（受動読みパス `ConfigureForCurrentScreenReader` と同じ鉄則） |
| 停止 | 稼働 | **PcTalker** | PC-Talker 専用の直叩き |
| 停止 | 停止 | **Uia** | 無害な既定（視覚は常時表示／他 SR は UIA 通知を読む可能性） |

実装上は「`pcTalker && !nvda` のとき `PcTalker`、それ以外は `Uia`」。

### 論点A（確定）：起動時1回で確定
- 受動読みパス（`ConfigureForCurrentScreenReader`＝ハンドル生成時に1回）と**一貫**させる。
- 代償: **起動後に SR を起動/終了しても追従しない**（旧能動パスはライブ追従していた）。
- SR ユーザは通常アプリ起動前に SR を立ち上げるため**許容**する。

判定結果のキャッシュ場所: アプリ起動時に一度 `SrSpeechSelector.Select(...)` を評価し、`AnnouncerFactory` が保持する静的フィールド（`SpeechMode`）へ格納。各呼び出し元（`MainForm` ctor／ダイアログ ctor）はこの確定済みモードで実体を生成する。

## 5. PC-Talker 発声手段（1箇所に集約・差し替え容易）

`PcTalkerSpeech` に発声メソッド `Speak(string)` を集約。既定は `PCTKPReadW(text, 1, 1)`（priority=1 割り込み）。

差し替え候補（実機で鳴るまで切り替えて検証する）:
- `PCTKPReadW(text, 1, 1)` … 既定（割り込み）
- `PCTKPReadW(text, 0, 1)` … 旧実装（非割り込み・キュー）
- `PCTKCGuide(text)` … ガイド系（プローブで可聴・署名は要確認）

差し替えは `PcTalkerSpeech` 内の1メソッドを変更するだけで済む形にする（必要なら手段を表す内部 enum＋switch で切替点を明示）。

### 論点B（確定）：ハード失敗時のみ UIA へ退避
- PC-Talker モードで直叩きが**例外/false（DLL 関数欠落等の明確な失敗）**のときだけ UIA 通知へ退避する安全網を**入れる**。
- 無音は false を返さない（成功扱い）ため、**audibility ベースのフォールバックにはしない**。あくまでハード失敗時のみ。
- 退避先は呼び出し元 Label に束縛した UIA 通知。

## 6. 既存コードの扱い

置換/削除:
- `SpeechRouter`（毎回 try→fallback ルーティング）→ 廃止。役割は起動時1回の `SrSpeechSelector.Select` に置換。
- `SrNotify`（静的ファサード）→ 廃止 or `AnnouncerFactory` に役割移管。
- `UiaNotificationSpeech` → `UiaAnnouncer` へ吸収（`IAnnouncer` 実装化）。
- `Announcer`（薄いラッパ）→ `IAnnouncer` インターフェース＋実装へ。
- `ISpeechChannel` → 廃止（または App 内部利用に縮小）。

小改修:
- `FindReplaceDialog.RaiseNotification` / `GrepDialog.RaiseNotification` … 自分の `_status` Label でファクトリ生成した `IAnnouncer` 経由に。
- `CsvController` … コンストラクタ注入の型を `Announcer` → `IAnnouncer` に変更（呼び出しは `Say` のまま）。
- `MainForm` … `_announcer = AnnouncerFactory.Create(_announceLabel)`。

温存:
- 視覚表示（`label.Text = msg`）は全モードで無条件（晴眼/弱視も第一級）。

## 7. データフロー

```
MainForm ctor:
  AnnouncerFactory.EnsureMode()                       // 起動時1回: Select(nvda?, pctalker?) を評価しキャッシュ
  _announcer = AnnouncerFactory.Create(_announceLabel) // モードに応じ PcTalkerAnnouncer / UiaAnnouncer

何かの操作（例: モード切替）:
  _announcer.Say("CSVモード オン")
    → label.Text = "CSVモード オン"                    // 視覚（常時）
    → PcTalker モード: PcTalkerSpeech.Speak(msg)        // 既定 PCTKPReadW(msg,1,1)。ハード失敗時のみ UIA 退避
       Uia モード:      label.AccessibilityObject.RaiseAutomationNotification(...)
```

ダイアログ:
```
FindReplaceDialog ctor: _announcer = AnnouncerFactory.Create(_status)
  RaiseNotification(msg) → _announcer.Say(msg)
GrepDialog 同様
```

## 8. エラー処理

- DLL 未解決/関数欠落/例外: `PcTalkerSpeech` 内で握りつぶし、`Speak` はハード失敗（false/例外）を呼び出し側へ通知 → `PcTalkerAnnouncer` が UIA へ退避（論点B）。
- UIA 通知が非対応環境: 例外を握りつぶし、視覚表示のみ（現状踏襲）。

## 9. テスト

- Core（WinForms 非依存・単体テスト）: `SrSpeechSelector.Select` の3（実質2）分岐を網羅。
  - (nvda=T, pctalker=T) → Uia
  - (nvda=T, pctalker=F) → Uia
  - (nvda=F, pctalker=T) → PcTalker
  - (nvda=F, pctalker=F) → Uia
- 発声 I/O（P/Invoke・UIA 通知）の可聴は**実機検証**に委譲（自動化不可）。
- 既存の `SpeechRouterTests` は `SrSpeechSelector` 用テストへ置換。
- 回帰: `dotnet build` 0 警告、Core 既存テスト緑を維持。

## 10. 実機検証の進め方（PC-Talker）

1. 既定 `PCTKPReadW(text,1,1)` で yEdit を実機起動（PC-Talker 稼働下）。
2. モード切替・CSV セル移動・行ジャンプ等で**可聴を耳で確認**。
3. 無音なら `PcTalkerSpeech.Speak` の手段を `PCTKCGuide` 等へ差し替えて再検証。
4. 鳴る手段を既定化して確定。
5. NVDA でも回帰確認（UIA 通知が従来どおり読まれること）。

## 11. スコープ外（YAGNI）

- 受動読みパス（Scintilla UIA プロバイダ）との統合は対象外（別関心事）。
- SR のライブ追従（起動後の SR 起動/終了への追従）は対象外（論点A）。
- Tolk 等の追加バックエンドは対象外。
