# 自作エディットコントロール 設計書

作成: 2026-07-05 / ブランチ: `feature/custom-editcontrol-design` / ステータス: ユーザー承認済み(セクション1〜3を個別承認)

---

## 0. 背景・動機

Scintilla(Scintilla5.NET 6.1.2)から自作エディットコントロールへの置き換え。動機は2点:

1. **NVDA経路の消滅**。二系統SR適応(NVDA=ネイティブ読み/それ以外=自作UIA)の唯一の根本原因は、NVDAがクラス名 `WindowsForms10.Scintilla.app.0.NNN` を `"Scintilla"` に正規化しネイティブオーバーレイ(`ScintillaTextInfo`)を被せること(HANDOFF §13.2)。非Scintillaクラスの自作コントロールなら NVDA も PC-Talker も自作UIAを読むことは UiaProbe で実機実証済み(HANDOFF §2)。git履歴305コミット中SR関連60件超がこの二系統に起因。
2. **PC-Talker制御の向上**。PC-Talkerが読むのは既に自作UIAプロバイダであり、Scintillaはその下のバッファ+描画でしかない。自作すればスナップショット二重保持・毎編集の全文コピーが消え、ネイティブ表面(WM_GETTEXT/MSAA)まで完全掌握できる。

事前調査(2026-07-05 実施)の要点:
- SCI_*直呼びは24種、すべて `ScintillaHost.cs` に閉じている。Core・tests・Accessibility層はScintilla完全非依存
- App層のScintilla接点は10ファイルだが、大半は `ScintillaHost` 独自API(`SelectCharRange` 等)経由
- UiaProbe(自作コントロール雛形478行、IME/両SR読み実機実証済み)は `757613b^` から復元可能

## 1. 決定事項(ユーザー判断)

| 論点 | 決定 |
|---|---|
| 大容量目標 | **1GBを目標、可能ならScintilla同等(〜2GB)。難しければ数百MB級フォールバック** |
| 移行方式 | **一発置き換え**(新旧並行維持はしない。ブランチ上で完成させて置換) |
| バッファ方式 | **案C: UTF-8永続ピーステーブル**(Scintilla同形の格納エンコーディング) |
| ファイルエンコーディング | **UTF-8/Shift_JIS/EUC-JPの3種のみ。UTF-16ファイル対応は廃止** |

案Cの既知の弱点(UTF-8⇔UTF-16変換層の全域拡散)は、ピース木の二重インデックス(§2-1)で変換をバッファ内部のO(log n)照会に閉じ込めることで解消する。UTF-16は.NET/Windows境界(文字列・IME・クリップボード・UIA)の交換表現としてのみ残る。ファイルの3エンコーディング化により、UTF-8ファイルは**変換ゼロでチャンク直載せ**でき(1GB級の現実的ケースで最速・省メモリ)、将来のメモリマップ2GB路線も開く。

## 2. アーキテクチャ

### 2-1. TextBuffer: UTF-8永続ピーステーブル(yEdit.Core・純ロジック)

```
チャンク層: 不変UTF-8バイトチャンク
  - 原文チャンク: ファイル読込時のバイト列そのまま(UTF-8時。SJIS/EUC-JPは読込時変換)
  - 追記チャンク: 編集挿入テキスト(UTF-8変換して追記専用)
ピース木: 各ノードが (byteLen, charLen, lineBreaks) の3統計を持つ平衡木。
  永続(persistent)構造 = 編集は経路コピーのみ
  → バイト⇔UTF-16文字⇔行 の相互変換すべて O(log n)
  → スナップショット = 木のルート参照コピー O(1)
大ピース内サンプリング: 原文1ピース1GBでも 64KB ごとの (byte, char, line)
  対応表で内部変換を局所化
```

- **外部APIはすべてUTF-16文字オフセット**(現行 `SelectCharRange` 等と同じ通貨)。UTF-8はチャンク格納形式として内部に完全に閉じる
- **Undo/Redo**: 永続構造なので編集前ルートの保持だけで実現。連続タイプの合流(coalescing)はオペレーションログで制御。SavePoint=ルート参照一致判定。Modified=現在ルート≠保存時ルート
- **スレッド安全**: UIA RPCスレッドへは「その時点のルート」を渡すだけ。ロック不要。現行の「編集ごと全文コピー2回」が原理的に消滅
- 不正UTF-8シーケンスは読込時に検証・置換(置換文字警告の既存ノウハウ流用)

### 2-2. ファイルI/O

- 対応: **UTF-8(既定・BOM有無)/Shift_JIS(932)/EUC-JP(51932)。UTF-16廃止**(M1申し送り「UTF-16 BOM無し未検出」の弱点も同時消滅)
- UTF-8: ストリーム読みでチャンク列に直載せ(変換ゼロ)。妥当性検証のみ
- SJIS/EUC-JP: 読込時にUTF-8変換、保存時に逆変換(置換文字検出で警告)
- 保存: チャンク列を順次書き出し(全文string化しない)。原子的保存(temp→`File.Replace`)は現行踏襲

### 2-3. EditorControl: 描画・入力・IME(yEdit.Editor・WinForms `Control` 派生)

- **仮想化描画(GDI)**: 可視行+αのみレイアウト・描画。行レイアウトキャッシュは可視窓のみ保持。折り返しは現行同等の文字単位(禁則は整形コマンドでCore側・変更なし)。行番号マージン/選択/キャレット行強調/空白・EOL可視化/セルハイライト(CSV)を自前描画。レイアウト計算は純ロジックに分離してテスト対象化
- **システムキャレット**: `CreateCaret`/`SetCaretPos` 自前管理(SR位置追従に必須。probe実証済み)
- **入力**: 現行Scintillaで実際に使っている操作のみ実装(YAGNI): 矢印/単語移動/Home・End/Page/Shift選択/Ctrl+A/クリップボード/Undo・Redo/Overtype。マウスはクリック位置・ドラッグ選択・ダブルクリック単語・ホイール
- **IME**: WM_IME_* 自前処理。インライン未確定表示+候補ウィンドウ位置制御(`ImmSetCandidateWindow`)。再変換はv1スコープ外(申し送り)

### 2-4. IUiaTextHost v2(範囲ベース化)

プロバイダ層(`TextControlProvider`/`TextProviderImpl`/`TextRangeProvider`)の構造とMoveスパン保持等の実証済みロジックは温存し、テキストアクセスだけをホスト委譲に差し替える(HANDOFF §7構想の実現)。

| IUiaTextHost v2 | 現行からの変化 |
|---|---|
| `GetTextRange(start, length)` | `GetText()`全文 → 範囲取得(スナップショットから局所デコード) |
| `TextLength` / `GetSelection` / `SetSelection` | 不変(文字オフセット) |
| `NextChar/PrevChar/LineStartOf/LineEndNoBreakOf/WordStart/WordEnd` | 新設(位置→位置)。`TextNavigation`(全文string前提)を置換 |
| `GetBoundingRectangles` / `RangeFromPoint` | スタブ→本実装(レイアウトエンジンから文字矩形算出。HANDOFF §4.2解消) |
| `Handle/HasFocus/ControlTypeId/Name/AutomationId/SetFocus/BoundingRectangle` | 不変 |

- 位置の通貨はUTF-16文字オフセット。RPCスレッドは永続スナップショットのルート参照で応答
- ControlTypeは実績の `Document` 維持(`Edit` はP0プローブで再試験し良ければ切替)

### 2-5. 継承するSR対策(コードは新規・知見は流用)

SR側の癖に起因するため新コントロールへ移植する:
1. フォーカス獲得時の `TextSelectionChanged` 明示発火(PC-Talker 2秒ポーリング回避・HANDOFF §13.6)
2. `Move` のスパン保持(PC-Talker行歩き・HANDOFF §3.1)
3. 空行len=0公開+`CaretEnteredEmptyLine`→App層「空行」能動発声(PC-Talkerのみ・HANDOFF §4.1)
4. `RaiseUiaSelectionEvents` 抑止(CSVセルナビ時)
5. Announcer二形態(`PCTKPReadW`直叩き/`RaiseAutomationNotification`)と `SpeechMode` 分岐 — 存続

### 2-6. SR経路の縮約(消えるもの)

新コントロールのクラス名はNVDAのScintilla正規化に該当しない → NVDAは純UIAで読む(UiaProbe実証構成)。これにより:
- `SrRoute.Nvda`・NVDAプロセス検出・`ServeUiaProvider`/`ApplySrAdaptation` → 撤去(UIA常時提供)
- `CsvFocusSink` 一式 → 撤去(NVDA生読みが存在しないため `RaiseUiaSelectionEvents` で完結)
- `SrRoute` は実質 `{PcTalker, Uia}` = 発声モード選択のみに縮約
- **撤去はP7の実機検証合格後**(切り分け手段を残す)

### 2-7. ネイティブ表面の設計原則(PC-Talker誤読の再発防止)

クラス改名Scintilla実験でPC-Talkerが壊れた(本文がNameに載ったPaneのMSAA誤読・HANDOFF §13.3)教訓から:
- **WM_GETTEXTに本文を返さない**(空/文書名のみ)
- **MSAAは自前実装せずUIA→MSAAブリッジに任せる**
- **WM_GETOBJECTはUiaRootObjectIdのみ応答**
(=読めていたUiaProbeと同条件に固定)

### 2-8. App層契約(互換API+大容量二層化)

- `EditorControl` は現行 `ScintillaHost` の独自API(`SelectCharRange`/`ReplaceCharRange`/`HighlightCharRange`/`EnsureVisibleCharRange`/`MoveCaretCharOffset`/`PointFromCharOffset`/`LineHeightPx`/`CaretCharOffset`/`GetSelectionCharRange`/`ApplyLineEnding`/`ShowLineNumbers`/`ApplyWrapColumn`/`CaretEnteredEmptyLine`/`RaiseUiaSelectionEvents`)と、App層が使用中のbase由来メンバ(`Undo/Redo/Cut/Copy/Paste/SelectAll/Modified/ReadOnly/Overtype/CurrentLine/Lines.Count/GetColumn/SetSavePoint/EmptyUndoBuffer/ConvertEols/EolMode/Goto/Focus` 相当)を同名同義で提供 → App層10ファイルの再配線を機械的置換に近づける
- **壊れる契約は `SnapshotText`/`Text`(全文string)のみ**。二層化で対応:
  - 閾値(例: 64MB)以下 → 従来どおり全文string経路(検索・禁則・CSV・Markdown・バックアップは無改修で動く)
  - 閾値超 → 大容量モード: 検索はリテラル窓照合(regexは行単位適用に制限)、CSV/Markdownモード無効化(Markdown 2MB上限と同じ閾値ゲート思想)、バックアップはチャンク直書き

## 3. フェーズ計画(実機SRゲート3箇所)

### P0: SRプローブ(第1ゲート・低コスト)
- `757613b^` からUiaProbeを復元し検証項目を拡張: 単語ナビ(Ctrl+←→)・選択読み・連続読み(SayAll)・IME読み(ATOK変換候補)・ControlType Document⇔Edit比較
- ユーザー実機検証: NVDA/PC-Talker/ナレーター(チェックリスト提供)
- **DoD**: 可否表完成。致命NG(SayAll不可等)→撤退(本番コード無傷)
- 成果物: 検証レポート(docs/plans)。ここまでmainへマージ可

### P1: TextBuffer(Core・純ロジック・TDD)
- `yEdit.Core/Buffer/`: UTF-8チャンク・永続ピース木(3統計)・スナップショット・Undo履歴(coalescing+SavePoint)・大ピース内サンプリング・TextReaderアダプタ
- **テスト**: xUnit単体+ランダム編集ファズ(素朴string実装との突合・数万操作)+性能ベンチ(1GB合成データ: 編集<1ms・スナップショットO(1)・行変換O(log n))
- **DoD**: 全緑+ファズ無差異+ベンチ目標達成。Core純増なのでmainへマージ可

### P2: EditorControl骨格+描画(読み取り専用ビューア水準)
- 仮想化レイアウト・折り返し・行番号・選択/キャレット行強調/空白EOL可視化/セルハイライト・スクロール・システムキャレット・外観設定適用
- レイアウト計算は純ロジック分離でxUnit対象化(折り返し位置・文字⇔座標変換)
- **DoD**: 1GBファイルでスクロール滑らか(描画1フレーム16ms目標)・外観設定反映

### P3: 編集・入力
- キーボード全操作(現用サブセット)・マウス・クリップボード・Undo/Redo配線・Overtype・EOLモード
- **DoD**: Scintilla版との操作比較チェックリスト一致・編集後再レイアウトの局所性確認

### P4: IME
- WM_IME_*処理・インライン未確定表示・候補ウィンドウ位置・確定/取消
- **DoD**: ATOK実機(ユーザー)で実用水準。NG→撤退可能(App層無傷)

### P5: UIA/SR接続(第2ゲート)
- `IUiaTextHost` v2リファクタ・EditorControlのホスト実装・WM_GETOBJECT・イベント一式・空行イベント・座標API本実装・ネイティブ表面原則適用
- `tools/verify-uia*/walk-test*` を新コントロール向けに調整→SR非依存回帰(Moveスパン等)→ユーザー実機中間検証(P0と同項目を本物で)
- **DoD**: SR非依存スクリプト全PASS+実機中間OK。PC-Talker NGなら表面調整→再試行→ダメなら撤退

### P6: App層一発置き換え
- `Document.Editor`型差し替え・EditorAppearance書き換え・FileControllerストリームI/O化(3エンコーディング・UTF-16廃止)・SearchController閾値二層化・CSV/禁則/Markdown/バックアップ/Grep/行ジャンプ/文字情報配線・Scintilla NuGet撤去
- `SrRoute.Nvda`/`CsvFocusSink` は無効化のみで残す(切り分け用)
- **DoD**: build 0警告+Core全緑+全機能手動チェックリスト

### P7: 実機SR総合検証+撤去+リリース整備(第3ゲート)
- ユーザー実機フルマトリクス(3 SR × 主要全機能+復帰フォーカス+空行+タブ切替)
- 合格後: NVDA経路・CsvFocusSink・ServeUiaProvider完全撤去、HANDOFF/説明書更新、リリースCI調整(ネイティブDLL同梱削除)
- **DoD**: 実機OK+撤去後全緑+リリースzip起動確認 → mainへno-ffマージ

### 運用
- 各フェーズ完了時に別エージェントのコードレビュー→ブランチへコミット。P0/P1は無害な純増なのでmainへ随時no-ffマージ、P2以降は本ブランチに積みP7合格で一括マージ
- 実機SR検証はすべてユーザー実施(チェックリストと再現手順を毎回提供。SrDiagLog方式のログ判別を踏襲)

## 4. リスクと撤退基準

| リスク | 判明時期 | 撤退時の損失 |
|---|---|---|
| NVDAのUIA読み品質不足(SayAll/点字/詳細読み) | P0 | probe工数のみ |
| PC-Talkerがプローブ条件を再現しても読まない | P0/P5 | P5まで(App層無傷でScintilla継続) |
| IMEがATOKで実用水準に達しない | P4 | 同上 |
| 1GB性能目標未達 | P1ベンチ | 閾値を下げ数百MB級で妥協(設計不変・フォールバック内蔵) |

## 5. 規模見積り(参考・事前調査より)

新規コード約3,500〜5,500行(バッファ800〜1,500/描画1,000〜1,500/入力600〜1,000/IME 300〜600/SR接続約300/互換API+配線約500)+IUiaTextHost v2リファクタ。撤去対象: SR二系統機構・CsvFocusSink・UTF-8⇔UTF-16全文変換・ネイティブScintilla/Lexilla.dll同梱。
