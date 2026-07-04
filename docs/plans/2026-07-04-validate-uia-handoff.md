# validate/uia 検証結果と引き継ぎ事項（2026-07-04）

## 目的

NVDA の設計思想 —「アプリケーションが UIA 等のアクセシビリティを適切に実装していれば、SR への特別対応は不要。『NVDA が起動しているときだけ』行うアクセシビリティ処理は作法として不可」— が yEdit で再現できているかをコードベースで検証する。あわせて、監査で見つかった唯一の能動的な NVDA 限定挙動（クライアント MSAA 抑制）の要否を本ブランチで実測する。**PC-Talker はスコープ外**（実装の特殊性からアプリ側対応が必要であり、PC-Talker 用コンポーネントで対応済みという前提）。

## 1. コード監査結果: NVDA 限定処理の全数

`nvda` 言及のある全 21 ファイルを精査。実行時に SR 経路で挙動が分岐するのは以下の 4 箇所のみで、残りはすべて設計根拠を説明するコメントか設定 UI/永続化の文字列だった。

| 箇所 | 内容 | 評価 |
|---|---|---|
| `Program.cs:16` | 起動時 1 回の `SrContext.Detect()`（プロセス名 `nvda` 検出＋優先 SR 設定） | 分岐の根。必然性は下記 |
| `MainForm.cs:99` | `ApplySrAdaptation()` — NVDA 経路では UIA プロバイダ不提供＋クライアント MSAA 抑制 | MSAA 抑制のみ要検証（→§2） |
| `AnnouncerFactory.cs` | NVDA 経路は `UiaAnnouncer`（標準 `RaiseAutomationNotification`）、PC-Talker 経路は `PCTKPReadW` | 標準 API・作法適合 |
| `MainForm.cs:57` | 空行「空行」能動発声は PC-Talker 限定。NVDA 経路は何もせずネイティブ「ブランク」に任せる | 作法適合 |

- NVDA 専用 API（`nvdaControllerClient.dll`）は不使用。設計段階で明示的に見送り（docs/plans/2026-06-28-sr-speech-design.md §10）。
- NVDA 経路の実体は「自前 a11y 層の完全撤収」＝素のネイティブ Scintilla アプリと同形（`cd8b526` でイベントフック実測済み）＋標準 UIA Notification のみ。NVDA に対して何かを「する」コードは存在しない。
- SR 検出分岐そのものの必然性は HANDOFF §13 で実測立証済み: クラス名（正規化後 "Scintilla"）が両 SR を反転させるレバーで、PC-Talker は "Scintilla"＋UIA を要求し、NVDA は "Scintilla"＋自前 UIA の組合せで無音。単一構成では両立不能。分岐の原因は PC-Talker 対応の副作用であり、NVDA 対応の作法違反ではない。
- CSV フォーカスシンクは設計根拠こそ NVDA のネイティブ読み挙動だが、実装は SR 非依存・無条件（`CsvController` は `SrContext` を参照しない）。作法適合。

**総括: 「NVDA が起動しているときだけ行うアクセシビリティ機能」は実質 `SuppressClientMsaa` の 1 点のみ。思想は再現できている。**

## 2. MSAA 抑制なしの実測（本ブランチで実施）

### 対象

`ScintillaHost.ApplySrAdaptation` は NVDA 経路（`useNativeReading=true`）で `SuppressClientMsaa=true` とし、`WM_GETOBJECT(OBJID_CLIENT)` に 0 を返して WinForms の ControlAccessibleObject を隠す。M1（`4879ece`）以来のプローブ時代からの持ち越しで、必要性の実測記録がなかった。

### 方法

`tools/verify-msaa-client.ps1`（本ブランチで追加）。yEdit を起動し、エディタ hwnd（クラス `WindowsForms10.Scintilla.app.0.…`。WinForms が "Scintilla" を基にクローン登録した装飾名で、NVDA は `re_WindowsForms` 正規化で "Scintilla" に還元してオーバーレイを適用する）に対し、`UiaHasServerSideProvider` と `AccessibleObjectFromWindow(OBJID_CLIENT)` の表面（role/name/value/state/childCount/IAccessibleEx QI）を、空文書と本文ありの両状態で計測。NVDA 稼働中（実クライアント購読あり）の環境で、抑制あり（HEAD）と抑制なし（本変更）をビルドし比較した。

### 結果

| 計測項目 | 抑制あり（現行） | 抑制なし（実験） |
|---|---|---|
| `UiaHasServerSideProvider` | False | False（変化なし） |
| 返る MSAA オブジェクト | oleacc 既定クライアントプロキシ | WinForms ControlAccessibleObject |
| accRole | 10 (client) | 10 (client) |
| accName（本文あり時） | ウィンドウテキスト＝**本文先頭が載る**（化け文字） | **null**（`AccessibleName` 未設定のため） |
| accValue / accDescription | null / null | null / null |
| accState / accChildCount | 0x100004 / 0 | 0x100004 / 0 |
| QI IAccessibleEx / IRawElementProviderSimple | E_NOINTERFACE | E_NOINTERFACE（変化なし） |

**判定: 構造面の差はほぼゼロ。** UIA サーバー側プロバイダが新たに露出することはなく、role/state も同一。唯一の差分 accName は、抑制なしの方がむしろクリーン（既定プロキシは WM_GETTEXT 経由で本文先頭を名前として漏らすが、WinForms オブジェクトは null）。NVDA の Scintilla 読みは正規化クラス名によるオーバーレイ＋SCI メッセージ直読みであり、この差が読みを壊す可能性は低いと判断。

副次知見: Scintilla は `WM_SETTEXT`/`WM_GETTEXT` のペイロードを UTF-8 として扱うため、Unicode クライアントからの往復では本文が先頭 1 文字に化ける（既定プロキシの accName にもこの化けが載っていた）。

### 実機 NVDA 音声確認 = OK（2026-07-04・ユーザー実施）

**抑制なしビルドで問題ないことを実機 NVDA で確認済み。判定確定: NVDA 経路のクライアント MSAA 抑制は不要**（→ §3(3) の撤去を実施してよい）。確認に使った観点は以下のチェックリスト:

1. 通常編集: ←→で 1 文字・↑↓で 1 行・IME 日本語入力が従来どおり読まれる
2. 空行着地で「ブランク」（無変化であること）
3. **他ウィンドウへ切替→yEdit へ復帰後も読み上げ継続**（`037d9ae` で直した無音事象の再発確認。最重要）
4. タブ切替・新規タブ
5. CSV モード切替（フォーカスシンク着地「CSV表」＋セル読み）
6. メニュー開閉後の読み上げ継続

## 3. 引き継ぎ事項（別セッション・別ブランチで対応）

### (1) doc コメントと実装の食い違い修正（軽微・実装が正）

実装は実機検証の結果を反映した正であり、コメント側を実装に合わせる。

- `src/yEdit.Core/Speech/SrRoute.cs:11` —「PC-Talker 経路: 自前 UIA プロバイダ提供＋**ネイティブ MSAA 抑制**」とあるが、実装（`ApplySrAdaptation`）で MSAA 抑制は一貫して **NVDA 経路**（M1 `4879ece` 以来）。
- `src/yEdit.Editor/ScintillaHost.cs:146` — WndProc 内「ネイティブ MSAA を返さない（**PC-Talker を UIA ブリッジへ誘導する試み**）」— このフラグが立つのは NVDA 経路のみで、プローブ時代の残骸。
- 注意: 下記 (3) で MSAA 抑制自体を撤去する場合、これらのコメントは撤去とともに消える/書き直しになるため、(3) と同一ブランチでまとめて対応するのが効率的。

### (2) SR 非稼働時の既定経路の退行修正（他 SR 対応上まずい）

2026-07-04 の優先 SR 設計（`SrRouteSelector`）で「どちらも非稼働 → 優先 SR の経路」となり、既定（`PreferredScreenReader="nvda"`）では **SR なし環境で UIA プロバイダを提供しない**。旧規則は「NVDA 不在なら UIA 提供＝PC-Talker・SR なし・他 UIA 系 SR で安全」（HANDOFF §13.3）であり、ナレーター/JAWS 等のサードパーティ SR に対する退行。

- 修正方針案: `SrRouteSelector.Select` の規則を「NVDA 稼働時のみ NVDA 経路（＝UIA 撤収）。それ以外（PC-Talker 稼働・どちらも非稼働）は UIA 提供経路」へ戻す方向で再設計。ただし「優先するスクリーンリーダー」設定（`SpeechSettingsTab`）との整合（設定の意味・文言）を要検討。能動発声モード（`SpeechMode`）と受動読み（UIA 提供可否）の分離が必要になる可能性あり（現状は `SrRoute` でペア固定）。
- テスト: `SrRouteSelectorTests` の期待値更新＋SR なしケースの追加。
- **対応済み（2026-07-04・fix/sr-route-no-sr）**: 汎用 UIA 経路 `SrRoute.Uia` を新設し「検出された SR の経路。両方稼働なら優先設定。どちらも非検出なら汎用 UIA 経路」に改定。能動/受動の軸分離は不要だった（第 3 値で両導出が自然に正しくなる）。設計 = docs/plans/2026-07-04-sr-route-no-sr-design.md。

### (3) MSAA 抑制の撤去（実機 OK 確認済み・実施可）

§2 の実機 NVDA 音声確認は OK（2026-07-04）。よって:

- `ScintillaHost` から `SuppressClientMsaa` プロパティと `WndProc` の `OBJID_CLIENT` 分岐を撤去し、`ApplySrAdaptation` は `ServeUiaProvider` のみに（「NVDA 起動時だけ行う処理」の完全解消）。
- `SrRoute.cs`/`ScintillaHost.cs` の doc コメント・HANDOFF §13.3 の表（ServeUiaProvider/SuppressClientMsaa）を新実装に合わせて更新。

## 4. 本ブランチ（validate/uia）の最終状態

- コミットして main へマージするのは**本ドキュメントのみ**（検証記録＋引き継ぎ）。
- `src/yEdit.Editor/ScintillaHost.cs` の検証用変更（`SuppressClientMsaa = false` 固定）と `tools/verify-msaa-client.ps1`（MSAA 表面プローブ）は**意図的に未コミット**のまま残置 — 本実装は引き継ぎ (3) の別ブランチで正式に行う。プローブスクリプトが必要になったら本ドキュメント §2 の方法欄を基に再作成可（作業ツリーに残っていればそのまま流用可）。
- ビルド 0 警告・テスト 289 件緑。バックアップ残骸なし（プローブは強制終了だが 30 秒間隔前に終了）。
