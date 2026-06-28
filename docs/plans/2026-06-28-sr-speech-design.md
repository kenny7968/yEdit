# SR適応 音声出力層 設計

- 日付: 2026-06-28
- ブランチ: `fix/pctalker-announce`
- 状態: 設計合意済み（実装計画は別ドキュメントで作成）

## 1. 背景・根本原因

CSVモードの読み上げで、PC-Talker利用時に `Ctrl+Shift+↑/↓`（上下セル移動）と
`Ctrl+Shift+C`（列見出し読み上げ）の読み上げが聞こえない、という現場報告。
フォーカス（キャレット）は正しく動く。

### 根本原因（特定済み）

yEdit の能動的アナウンスはすべて唯一の集約点を通る:

```
Announcer.Say / FindReplaceDialog.RaiseNotification / GrepDialog.RaiseNotification
  → SrNotify.Raise(label, message)
      → label.AccessibilityObject.RaiseAutomationNotification(...)   // ＝ UIA UiaRaiseNotificationEvent
```

**PC-Talker はこの UIA 通知イベントを読み上げない**（MSAA中心の実装で、UIAの
Notification/LiveRegion対応は公的に確認できない。現場報告とも一致）。NVDAはこれを読むため、
これまで問題が表面化しなかった。

症状の差は「PC-Talkerの付随読みが偶然代役をしていたか否か」で完全に説明できる:

| 操作 | キャレット移動 | PC-Talkerでの実態 |
|---|---|---|
| `Ctrl+Shift+←/→` | 同じ行内を水平移動 | PC-Talkerがキャレット移動に付随して勝手に読む → 「効いて聞こえる」 |
| `Ctrl+Shift+↑/↓` | 別の論理行へ移動 | PC-Talkerが物理行（CSV1行まるごと）を読む → 意図したセル＋行列アナウンスは鳴らない |
| `Ctrl+Shift+C` | 移動なし | キャレット移動が無く Announcer の UIA 通知だけ → **完全に無音** |

先日の変更（セル移動を文字列選択から「枠ハイライト＋キャレットのみ」へ）で選択読みの代役も消え、
Announcer（UIA通知）依存が露呈した。**これはCSV固有ではなく、検索・grep・モード切替・照会ホットキー等、
全アナウンスに潜在する共通バグ**で、`Ctrl+Shift+C`（移動を伴わない）が最もきれいに露出させている。

## 2. 調査結果（代替手段）

- **PC-Talker 直接発声**: ベンダー（高知システム開発）公認の唯一の手段は共有DLL
  `PCTKUSR.dll` の `SoundMessage(LPCWSTR text, int flags)`。**このDLLはPC-Talker本体が
  Windowsフォルダに導入する**ため、PC-Talker稼働機では実行時に既在で、こちらが同梱しなくても
  `LoadLibrary`/`GetProcAddress` で呼べる見込み。署名の確証は中〜高（第三者ホストのSDKヘッダ由来。実機検証必須）。
- **クロスSRライブラリ（Tolk / UniversalSpeech / SRAL）**: いずれも **PC-Talker 非対応**。使えない。
- **NVDA**: 公式 `nvdaControllerClient.dll`（`nvdaController_speakText`）が堅牢だが、NVDAは
  現状の UIA 通知でも読めているため今回は不要（将来オプション）。
- **既存のSR判定**: `yEdit.Editor/ScreenReaders.cs` の `IsNvdaRunning()` / `IsPcTalkerRunning()` を流用。

## 3. 設計判断（合意済み）

| 論点 | 決定 |
|---|---|
| スコープ | **全アナウンスを基盤化**。`SrNotify` 内部を差し替え、CSV・検索・grep・モード切替・照会ホットキーを一括対応 |
| 挿入点 | **`SrNotify.Raise` の内部のみ**を差し替え。呼び出し側（Announcer等）は無改修（集約点が既に1箇所） |
| PC-Talker発声 | **`PCTKUSR.dll` の `SoundMessage` を遅延束縛P/Invoke**。取得不可/例外時はUIAへフォールバック |
| NVDA・その他SR | **無改修**で現状の `RaiseAutomationNotification` を温存（＝常設の最終手段／フォールバック） |
| 視覚表示 | `label.Text = message` は**無条件**で従来どおり（晴眼/弱視も第一級 [[yedit-sighted-users-first-class]]） |
| `flags` 引数 | 当面 `0`。二重読み対策の割り込み指定は実機検証で調整 |

## 4. アーキテクチャ

既存方針（ロジックはテスト可能に、UI/ネイティブ依存は薄く）に従う。
`SrNotify.Raise` の中身を「視覚表示（無条件）＋ SR適応の音声出力（試行→フォールバック連鎖）」に置換する。

```
SrNotify.Raise(label, msg)
  ├─ label.Text = msg                     // 視覚表示は無条件
  └─ SpeechRouter.Speak(label, msg)       // 効いたら止める「試行→フォールバック」連鎖
        ├─ PcTalker稼働中? → PcTalkerSpeech.TrySpeak(msg)        // PCTKUSR.dll SoundMessage
        └─ いずれも未発声  → UiaNotificationSpeech.TrySpeak(label, msg)  // 現状の RaiseAutomationNotification
```

### コンポーネント配置

```
yEdit.App/Speech/
  ISpeechChannel.cs         … bool TrySpeak(...) ＝「実際に喋れたか」を返す抽象
  PcTalkerSpeech.cs         … PCTKUSR.dll SoundMessage の遅延束縛P/Invoke（可否キャッシュ）
  UiaNotificationSpeech.cs  … 既存 RaiseAutomationNotification を channel 化（常設フォールバック）
  SpeechRouter.cs           … SR判定で順序決定＋試行/フォールバック
yEdit.App/
  SrNotify.cs               … 視覚表示 ＋ SpeechRouter 呼び出しへ改修
yEdit.Editor/
  ScreenReaders.cs          … 既存（IsPcTalkerRunning を流用）
tests/yEdit.App.Tests/（or Core.Tests）/
  SpeechRouterTests.cs      … フェイク channel で「試行→フォールバック」順序を検証
```

> 補足: `SpeechRouter` の順序/フォールバック論理は純ロジックで単体テスト可能。
> 実発声を伴う `PcTalkerSpeech`／`UiaNotificationSpeech` はネイティブ/UIA依存のため実機検証で確認する。

## 5. コンポーネント詳細

### ISpeechChannel

```csharp
internal interface ISpeechChannel
{
    string Name { get; }          // ログ/デバッグ用
    bool TrySpeak(string message); // 実際に発声できたら true、未対応/失敗なら false
}
```

- 返り値の bool が**フォールバック判断の核**。「呼べたが鳴ったか不明」ではなく
  「このチャンネルで処理を完結できたか」を返す（PC-Talkerなら SoundMessage が成功 TRUE を返したか）。

### PcTalkerSpeech

- 初回 `TrySpeak` 時に `LoadLibrary("PCTKUsr.dll")` ＋ `GetProcAddress("SoundMessage")` を試み、
  デリゲートと可否を**キャッシュ**（毎回ロードしない）。
- 取得不可・例外時は `false`（→ ルータが UIA へフォールバック）。
- `SoundMessage(message, 0)` を呼ぶ。Unicode（`LPCWSTR`）。
- P/Invoke は遅延束縛（`LoadLibrary`/`GetProcAddress`＋`Marshal.GetDelegateForFunctionPointer`）。
  `[DllImport]` の静的束縛だと非PC-Talker機で `DllNotFoundException` を誘発し得るため避ける。

### UiaNotificationSpeech

- 現 `SrNotify` の中身（`label.AccessibilityObject.RaiseAutomationNotification(ActionCompleted,
  MostRecent, message)`）をそのまま移設。例外は握って `false`。
- **常に最後に試す常設チャンネル**。NVDA・その他SR・PC-Talkerフォールバックを担う。

### SpeechRouter

- `ScreenReaders.IsPcTalkerRunning()` を**呼び出し時に評価**（起動中の常駐SR切替に追従）。
  - PC-Talker稼働中 → `[PcTalkerSpeech, UiaNotificationSpeech]` の順。
  - それ以外 → `[UiaNotificationSpeech]` のみ。
- 先頭から `TrySpeak` し、`true` が返った時点で打ち切り。
- SR判定はデリゲート（`Func<bool> isPcTalker`）で注入可能にし、ルータ順序ロジックを単体テスト可能にする。

## 6. データフロー（例: Ctrl+Shift+C）

```
ProcessCmdKey(Ctrl+Shift+C)
  → CsvController.ReadColumnHeader()
  → Announcer.Say("見出し: 氏名")
  → SrNotify.Raise(label, "見出し: 氏名")
      ├─ label.Text = "見出し: 氏名"                 // 視覚表示
      └─ SpeechRouter.Speak(...)
            PC-Talker稼働 → PcTalkerSpeech.TrySpeak  // SoundMessage("見出し: 氏名", 0) 成功 → true、終了
            （PC-Talker不在/失敗）→ UiaNotificationSpeech.TrySpeak → RaiseAutomationNotification
```

## 7. エラー処理

- 各 `TrySpeak` は内部を try/catch し、例外を**握って `false` を返す**だけ（呼び出し側へ例外を飛ばさない）。
- ルータは順に試し、全チャンネルが `false` でも**視覚 `label.Text` 更新は必ず完了**済み
  （＝従来挙動への無害劣化）。
- `PCTKUsr.dll` 不在・関数不在・呼び出し失敗はすべて「PC-Talkerチャンネル不成立」＝UIAへ自動フォールバック。

## 8. テスト方針

- **単体テスト（`SpeechRouter`）**:
  - PC-Talker稼働かつ PcTalker チャンネルが `true` → UIA は呼ばれない（先頭で打ち切り）。
  - PC-Talker稼働だが PcTalker が `false` → UIA にフォールバックして発声される。
  - 非PC-Talker → UIA のみが呼ばれる。
  - フェイク `ISpeechChannel`（呼び出し記録＋戻り値制御）で検証。
- **実機SR検証（手動・必須）**:
  - PC-Talker: `Ctrl+Shift+C`／`Ctrl+Shift+↑↓←→`／検索件数／grep／モード切替が**全て聞こえる**こと。
  - NVDA: 従来どおり全アナウンスが聞こえること（無改修の退行が無いこと）。

## 9. 実機検証が必須の未確定点

1. **`SoundMessage` の正確な署名・`flags` 意味**: 公開情報は第三者ホストのSDKヘッダ由来で確証中〜高。
   `GetProcAddress("SoundMessage")` の成否、`flags=0` の挙動を実機で確認。
2. **二重読みの懸念**: `Ctrl+Shift+↑/↓` はキャレットが別の物理行へ飛ぶため、PC-Talkerが
   物理行を勝手に読む＋こちらの `SoundMessage` で二重になり得る。`flags` での割り込み指定 or
   抑制の要否を実機で調整（必要なら別タスク）。
3. **NVDA無改修の退行確認**: 設計上は無改修だが、実機で全アナウンスの読み上げを確認。

## 10. 既知の割り切り / 将来拡張（今回スコープ外）

- NVDA への `nvdaControllerClient.dll` 採用は見送り（現状UIAで足り、同梱DLLを増やさない）。将来オプション。
- `PCTKUSR.dll` の `SoundMessage` 以外（停止・優先度・読み調整等）は使わない。
- 設定での発声手段切替UIは作らない（自動判定＋フォールバックで完結）。
- JAWS等その他SRの専用APIは対象外（UIAフォールバックで読める範囲）。
