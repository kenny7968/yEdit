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
- ControlTypeは実績の `Document` 維持で**確定**(P0トレースで Document⇔Edit はPC-Talkerの呼び出し完全同一・NVDAもDocumentで全項目OK)

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
- 成果物: 検証レポート(docs/plans)

#### P0 結果(2026-07-05・**Go判定**)

- 実機2回+UIA呼び出しトレース分析(詳細=`2026-07-05-p0-sr-probe-checklist.md` 実施記録):
  - **NVDA: 全項目問題なし**(SayAll・単語ナビ・選択読み・IME含む)=クラス名Scintilla問題からの脱出をプローブで実証
  - **PC-Talker: 基本ナビ/行読み/SayAll/IMEは実用水準**。単語ナビ(先頭1文字のみ)と空行無音の2件はトレースで**クライアント側の読み方に起因と確定**: キャレット移動毎に `Expand(Character)+GetText(1)` で着地1文字を読むだけで、TextUnit.Word はセッション全体で0回。プロバイダのWord実装は正常(SR非依存 word-sim 6パターン全PASS)でプロバイダ側では改善不能
  - **ControlType Document⇔Edit は PC-Talker の呼び出しパターン完全同一**=読みに無関係 → **Document 維持で確定**(§2-4)
  - 空行はPC-Talkerが `'\n'`/`''` を正しく受領した上での無音=App層能動発声(§2-5)で補う方針の妥当性を実証
- **P5への設計入力**: PC-Talker時の単語ナビ(Ctrl+←→)は空行と同じ仕組みのApp層能動発声で単語スパンを読む(現行yEditも同一プロバイダのため同挙動=自作化による退行ではない)
- 追加成果物: プローブ内UIA呼び出しトレース(`TracingUia.cs`・本番コード不変)と単語ナビ検証 `tools/word-sim.ps1` はP5の実機診断/回帰で再利用

### P1: TextBuffer(Core・純ロジック・TDD)
- `yEdit.Core/Buffer/`: UTF-8チャンク・永続ピース木(3統計)・スナップショット・Undo履歴(coalescing+SavePoint)・大ピース内サンプリング・TextReaderアダプタ
- **テスト**: xUnit単体+ランダム編集ファズ(素朴string実装との突合・数万操作)+性能ベンチ(1GB合成データ: 編集<1ms・スナップショットO(1)・行変換O(log n))
- **DoD**: 全緑+ファズ無差異+ベンチ目標達成

#### P1 結果(2026-07-05・**DoD全達成**)

- 実装計画=`2026-07-05-p1-textbuffer.md`(Task 1〜14・実施記録あり)。`yEdit.Core.Buffers` 名前空間に純追加(既存コード変更は csproj の InternalsVisibleTo のみ)。公開型は `TextBuffer`/`TextSnapshot`/`TextBufferBuilder`/`UndoResult` の4つ
- **テスト**: 既存289 → **415 全緑・build 0警告**(P1新規126件: Utf8Scan/Sanitizer/TextChunk格子/PieceStatsモノイド結合律/永続AVL/行照会/スナップショット/Builder/編集/Undo/Reader・WriteTo往復/サイズ上限/ファズ)
- **ファズ無差異**: 5シード{1,2,3,42,20260705}×30,000操作=計150,000操作 PASS(モデルはスナップ規則を独立実装した素朴string)
- **1GBベンチ**(`dotnet run --project tests/yEdit.Core.Bench -c Release -- --mb 1024` → EXIT 0):

| 計測 | 結果(1GB=561.8M文字/18.7M行) | 目標 |
|---|---|---|
| 構築 | 2.6s / 1025ピース | 記録 |
| ランダムsplice 10,000回 | **平均 78.8µs / p99 188µs** | 平均<1ms・p99<1ms |
| Current取得 | **2.3 ns/回**(O(1)) | O(1) |
| GetLineStart / GetLineIndexOfChar | 26.9µs / 19.2µs | <100µs |
| GetText(200) | 28.1µs | <100µs |
| 連続タイピング1万字の断片化 | Δ2ピース | 断片化しない |
| メモリ | managed 1027MB / WS 1053MB | 文書+O(ピース) |

- **計画からの主な逸脱**(詳細=p1計画書の実施記録。いずれも実測ベンチFAILを起点とした最適化+レビュー対応):
  - `TextChunk.SplitStats`(分割点+接頭辞統計を1走査)追加。ピース分割の後半統計はモノイド差分でO(1)導出
  - Splice の SnapLow(GetChar×2)廃止=実効位置を Split 結果統計から導出(スナップは Split 内に一本化)
  - `GetLineIndexOfChar` のCRLF中間判定は PrefixStats.LastIsCr+IsLfAt(1点照会・接頭辞末尾CR時のみ)
  - `MarkSaved()` は coalescing 境界(Undoが保存点を飛び越えないため)
  - InternalsVisibleTo に `yEdit.Core.Bench` も追加(internal `PieceCount` 計測用)
- **別エージェントレビュー**: Critical 0 / Important 1(文書上限2GB非強制→Builder/Spliceにガード追加で解消済み・閾値注入テスト付き)/ Minor 7(4件対応済み: XMLコメント位置・右側マージ注記・共有ブロック永続性テスト・スレッド前提文書化)
- **P2+への申し送り**:
  - Undo履歴は無制限(永続構造なので実コストは小さいが、長時間セッションでは追記ブロックが解放されない。上限方針またはApp層 `ClearUndo` 運用をP6で判断)
  - `GetChar` は GetText(pos,1) 経由でstring確保あり(`GetLineEnd(includeBreak:false)` が最大2回呼ぶ。ベンチ目標は達成済み。P5で行末照会が高頻度になるなら IsLfAt 同型のバイト照会へ)
  - EOL一括変換(ConvertEols)は未実装(計画どおりP6で Builder 再構築として実装)
  - 単語境界(WordStart/WordEnd)はバッファ責務外=P5でスナップショット上に実装

### P2: EditorControl骨格+描画(読み取り専用ビューア水準)
- 仮想化レイアウト・折り返し・行番号・選択/キャレット行強調/空白EOL可視化/セルハイライト・スクロール・システムキャレット・外観設定適用
- レイアウト計算は純ロジック分離でxUnit対象化(折り返し位置・文字⇔座標変換)
- **DoD**: 1GBファイルでスクロール滑らか(描画1フレーム16ms目標)・外観設定反映

#### P2 結果(2026-07-05・**DoD全達成**)

- 実装計画=`2026-07-05-p2-editor-control.md`(Task 1〜15・実施記録あり)。純ロジック=`yEdit.Core.Layout`名前空間の6ファイル(ICharMetrics/MonoCharMetrics/LineLayout/PixelMapper/ViewportLayout/Frame/FrameBuilder)、WinForms=`yEdit.Editor.EditorControl`(sealed Control 派生・約 800 行)+ GdiCharMetrics + NativeMethods。**ScintillaHost は無変更**(§3運用どおり並行運用しない前提)
- **テスト**: P1 415 → **470 全緑・build 0 警告**(P2 新規 55 件: MonoCharMetrics 6 / LineLayout 7 / PixelMapper 15 / ViewportLayout 8 / FrameBuilder 13 + 追加 6・selection/current/wrap 契約)。EditorControl の統合テストは WinForms 依存のため Core.Tests 対象外=smoke で目視
- **1GBベンチ(--layout)**(`dotnet run --project tests/yEdit.Core.Bench -c Release -- --layout --mb 1024` → EXIT 0):

| 計測 | 結果(1GB=561.8M文字/18.7M行) | 目標 | 判定 |
|---|---|---|---|
| ViewportLayout(wrap OFF) | **7.79 ms/回** | <16ms | PASS |
| ViewportLayout(wrap ON 80桁) | **7.81 ms/回** | <16ms | PASS |
| Frame(wrap OFF 全体) | **9.48 ms/回** | <16ms | PASS |
| PixelMapper.OffsetToPx | **41 ns/回** | <1ms | PASS |
| メモリ増分(layout) | 記録のみ(可視領域のみ計算・全体キャッシュなし) | ― | ― |

- **公開 API**(P3/P4/P5/P6 の受け口):
  - `SetSource(TextBuffer)`(1度限り)/`LineHeightPx`/`TopLine`/`WrapColumns`/`ShowLineNumbers`/`ShowWhitespace`/`HighlightCurrentLine`/`ScrollX`
  - キャレット/選択: `CaretCharOffset`/`SetCaretCharOffset(int)`/`GetSelectionCharRange()`/`SetSelectionCharRange(int,int)`
  - セルハイライト: `HighlightCharRange(int,int)`/`ClearHighlight()`
  - 座標: `PointFromCharOffset(int)`
  - 外観: `ApplyAppearance(AppSettings)`(フォント差し替え+テーマ+表示設定+CaretWidth)
- **設計判断のポイント**:
  - **描画は Frame(PaintOp オペレータ列)抽象で 2 段化**=純ロジック FrameBuilder を xUnit で検証・EditorControl.OnPaint は Frame → GDI ディスパッチのみ
  - **可視領域のみ描画・キャッシュしない**=P1 の TextSnapshot が O(log n)+64KB 走査なので毎フレーム再取得で 1GB@16ms 達成
  - **スクロール単位は論理行**(折り返し ON でも LineCount ベース)=1GB でスクロールバー計算 O(1)
  - **ICharMetrics 抽象**(MonoCharMetrics=テスト固定幅 / GdiCharMetrics=実 TextRenderer + ASCII 128 幅キャッシュ)で GDI 依存を挿げ替え
  - **P3 依存 API を先出し**(SetCaretCharOffset/SetSelectionCharRange の受け口を実装・入力ハンドラは無し=キー/マウスは効かない)
- **別エージェント最終レビュー**: Critical 0 / Important 3(いずれも Task 15 で修正済み: I-1 ShowLineNumbers setter→PositionCaret 追加 / I-2 SetSource 中フォーカスならキャレット生成 / I-3 CaretWidth 反映=弱視要件)/ Minor 9(P3+/P6 送りの申し送り)
- **P3+ への申し送り**:
  - **shift+左矢印方向の選択**(キャレット=Min・アンカー=Max)は現行 SetSelectionCharRange で表現不可 → P3 で非対称版 API を追加(アンカー概念導入)
  - **PointFromCharOffset の水平可視性**(x-_scrollX<0 や >=paintWidth の範囲判定)は未実装=P3 のキャレット追従スクロール仕様確定時に対応
  - **マウスホイール**は 3 論理行固定=P3 で SystemInformation.MouseWheelScrollLines + 蓄積へ差し替え検討
  - **EditorControl 純データ系プロパティテスト**(SetCaretCharOffset/SetSelectionCharRange のクランプ・スナップ)は Core.Tests から見えないため未実装=P3 開始時にテスト基盤検討
  - **Task 5 レビュー観測項目 I2**(EmitWhitespaceGlyphs の O(N²))は装飾ありシナリオで顕在化=P3 で長行 + 空白多めのベンチ追加
- **P5 送り**:
  - **単語境界**(WordStart/WordEnd)= P1 申し送りどおり P5 でスナップショット上に実装
  - **UIA プロバイダ / WM_GETOBJECT / IUiaTextHost 実装** = 現状の UiaProbe が参照実装
- **P6 送り**:
  - **弱視要件の SelectionBack / HighlightOutline テーマ追従**(現状は薄青/橙で固定): App 層 EditorAppearance の色反転(theme.ForeRgb ↔ theme.BackRgb)を移植 + FrameBuilder が「選択内テキスト色反転」オプションを受ける仕組みを追加(設計原則 yedit-sighted-users-first-class)
  - **TabWidth / TabsToSpaces** 反映(P2 は保持のみ)
  - **既存 App 層 EditorAppearance** (Scintilla 版)を EditorControl.ApplyAppearance 呼び出しに書き換え

### P3: 編集・入力
- キーボード全操作(現用サブセット)・マウス・クリップボード・Undo/Redo配線・Overtype・EOLモード
- **DoD**: Scintilla版との操作比較チェックリスト一致・編集後再レイアウトの局所性確認

#### P3 結果(2026-07-06・**DoD全達成**)

- 実装計画=`2026-07-05-p3-editor-input.md`(Task 1〜15・約 1730 行・実施記録あり)。純ロジック追加=`yEdit.Core.Editing` 名前空間(NavigationCommands / VerticalNavigation / WordBoundary の 3 系)。WinForms=EditorControl に OnKeyDown/OnKeyPress/OnMouseDown/Move/Up/DoubleClick/Wheel + 編集/Undo/Redo/クリップボード実装を追加(約 1160 行増)。**ScintillaHost は無変更**
- **テスト**: P2 470 → **P3 完了時 663 全緑・build 0 警告**(P3 新規 193 件)
  - 内訳: Core.Tests 470→521(+51 件=Editing 純ロジック 3 系: NavigationCommands 15 / VerticalNavigation 13 / WordBoundary 23)
  - Editor.Tests 0→142(+142 件=13 テストクラス・WinForms STA 基盤新設)
- **応答性ベンチ**(Task 14):

| 計測 | 結果 | 目標 | 判定 |
|---|---|---|---|
| 連続タイピング(1 文字挿入) | **0.67µs/insert** | <5µs | PASS(7.5× マージン) |

- **公開 API 追加**(Task 1〜14 の総合):
  - アンカー概念: `SelectionAnchor` / `MoveCaretWithSelection(int)` / `SetSelectionAnchored(int, int)`
  - 移動系(OnKeyDown): 全キー配線(Arrow / Ctrl-Arrow / Home / End / Ctrl-Home/End / Page / Ctrl+A / Shift 拡張)
  - キャレット追従: `BringCaretIntoView()` / `EnsureVisibleCharRange(int, int)`
  - 編集: `Overtype` / `ReadOnly` / `EolMode` / `OnKeyPress`(文字挿入・Overtype) / OnKeyDown(BackSpace / Delete / Enter / Tab / Insert)
  - Undo/Redo(P6 互換): `Modified` / `CanUndo` / `CanRedo` / `Undo()` / `Redo()` / `SetSavePoint()` / `EmptyUndoBuffer()`
  - クリップボード(P6 互換): `Cut()` / `Copy()` / `Paste()` / `SelectAll()`
  - マウス: OnMouseDown / Move / Up / DoubleClick(単語選択)/ Wheel 精度改善
  - SR 対策受け口: `event CaretEnteredEmptyLine` / `RaiseUiaSelectionEvents`(P5 で本挙動)
  - P6 互換残: `CurrentLine` / `GetColumn(int)` / `ReplaceCharRange(int, int, string)`
- **純ロジック 3 系**(`yEdit.Core.Editing/`):
  - `NavigationCommands`(Left / Right / Home / End / SmartHome・15 テスト)
  - `VerticalNavigation`(Up / Down / PageUp / PageDown + desired X 保持・13 テスト)
  - `WordBoundary`(NextWordStart / PrevWordStart・CJK 対応・23 テスト)
- **設計判断のポイント**:
  - **アンカー概念導入**(`_anchor` + `_caret` の 2 変数化)で shift+左方向の選択を保持可能に(P2 申し送り解消)
  - **純ロジック層の分離**で位置移動を xUnit で決定的テスト可(GDI 依存なし)
  - **`AfterEdit()` ヘルパ**で編集経路の共通後処理(スクロールバー再計算 / PositionCaret / BringCaretIntoView / Invalidate)を統一
  - **`BringCaretIntoView()` を全経路の共通末尾**に置いてキャレット追従を一本化
  - **`_lastCaretLine` の setter 同期**(Task 13 レビュー I-1 対応)で App 層 programmatic ジャンプ後の spurious fire を抑止
  - **P6 互換 API を先出し**(Cut / Copy / Paste / Undo / Redo / Modified / CurrentLine / GetColumn / ReplaceCharRange 等)で App 層は機械的置換で済む
  - **リフレクション経由 SendKey / SendKeyPress / SendMouse* テストヘルパ**+`Sta.Run` パターンで WinForms 表面をテスト可能に
  - **テスト直列化**(`[assembly: CollectionBehavior(DisableTestParallelization = true)]`)で Clipboard グローバル資源のフレーク対策
- **P3+ 申し送り**(将来対応):
  - **Task 4 申し送り**: 最終行 Down 挙動の実機判定(Notepad 挙動へ変更)/ 超長行 GetText 性能懸念(P7 ベンチ観測 + `GetTextSpan` 案)
  - **Task 5 申し送り**: NavigationCommands / WordBoundary 命名統一と MoveLeftCp / MoveRightCp 重複解消(将来 refactor)/ CJK 拡張範囲実機検証(P7)
  - **Task 6 申し送り**: Ctrl+A 後の可視化(Task 7 で呼ばない方針で決着済)
  - **Task 7 申し送り**: 編集後処理ヘルパ抽出(将来 AfterCaretChange)/ PageUp / Down の rows 計算統一(paintHeight ベース)
  - **Task 8 申し送り**: Tab の Plan 逸脱(Task 9 で決着済)
  - **Task 9 申し送り**: CRLF ペア BackSpace 一括削除(P6 検討候補)
  - **Task 12 申し送り**: 極端 Wheel Delta の while ループ最適化(P7)/ ドラッグ末端の空行イベント(P5 判断)/ 空白ダブルクリック挙動(P7 実機評価)
  - **Task 13 申し送り**: shift+移動系での空行イベント発火は現行仕様維持(P7 実機評価で選択なし限定に変更するかどうか判断)
- **P5 送り**:
  - 単語境界 UIA プロバイダ実装 / RaiseUiaSelectionEvents 本挙動化 / UIA イベント発火(TextSelectionChangedEvent)
- **P6 送り**:
  - TabsToSpaces / TabWidth 反映 / SelectionBack / HighlightOutline のテーマ追従(弱視要件)/ App 層 EditorAppearance の書き換え

### P4: IME
- WM_IME_*処理・インライン未確定表示・候補ウィンドウ位置・確定/取消
- **DoD**: ATOK実機(ユーザー)で実用水準。NG→撤退可能(App層無傷)

#### P4 結果(2026-07-06・**自動 DoD 全達成・ATOK 実機検証待ち**)

- 実装: Task 1〜15 全完了・29 commits(`b8175d3`〜`79f0100`、feature branch のみ・main 未変更)
- 別エージェント最終レビュー: **Critical 0 / Important 0 / Minor 6**(全て申し送り可)
- ビルド 0 警告 / テスト全緑:
  - Core.Tests 521→528(+7 件=`ImeCompositionStateTests` 純ロジック)
  - Editor.Tests 142→155(+13 件=`EditorControlImeTests`:SetContext / STARTCOMPOSITION / GCS_COMPSTR / GCS_RESULTSTR / ENDCOMPOSITION / LostFocus / ReadOnly / 外部編集 API ガード全経路)

- **overlay 方式の実装**(設計 §2 完全準拠):
  - 未確定文字列は `TextBuffer` に触れず EditorControl 内部 `_ime` フィールド(`ImeCompositionState`)で保持
  - `OnPaint` で本文描画直後に inline 合成(下線・変換対象節は Bold+SelectionBack 反転)
  - GCS_RESULTSTR で初めて `InsertConfirmedText`(Task 3 の共通経路)へ流し 1 変換=1 Splice=1 Undo
  - 空取消(ESC/BackSpace で `Text.Length==0`)は Undo 履歴不変(`!CanUndo` 検証済)

- **公開 API 追加**(P4 は internal のみ):
  - `yEdit.Core.Editing.ImeCompositionState`(Start / Text / CursorPos / Attrs / Clauses + ParseAttrs/ParseClauses/SnapCursorPos)
  - `yEdit.Core.Editing.ImeAttribute`(6 バイト定数 = WinUser.h の GCS_COMPATTR 値)
  - EditorControl の public 表面は不変(Task 13 でガード追加 = 挙動変更なし)

- **NativeMethods 拡張**: WM_IME_* 定数群 / GCS_* / NI_/CPS_ / CFS_ / ISC_SHOWUICOMPOSITIONWINDOW / POINT / RECT / CANDIDATEFORM / **LOGFONT(unsafe struct + fixed char)** / Imm* 6 P/Invoke。LOGFONT は `Font.ToLogFont` の pinning 要件から blittable 化=`AllowUnsafeBlocks` を EditorControl プロジェクトで有効化(LOGFONT 1 struct に閉じる)。

- **設計判断のポイント**:
  - **overlay 方式採用**(TextBuffer 無汚染)= Undo 純粋・GCS_RESULTSTR で 1 Splice=1 Undo が自然に成立
  - **`InsertConfirmedText` 共通化**(Task 3)で `OnKeyPress` 挙動不変(P3 §0-9 温存)+ IME 経路と 1 経路統一
  - **`CancelCompositionAndDefault` 1 経路**(Task 8)で ReadOnly / LostFocus / 外部編集 10 API / MouseDown の縁ケースを共通化
  - **Font キャッシュ**(`_underlineFontCache` / `_targetFontCache`)を ctor / `ApplyAppearance` / `Dispose(bool)` の 3 箇所で対称=打鍵毎 OnPaint の GDI HFONT リーク回避
  - **`PositionCaret` の IME 分岐**(Task 11)を `ApplyComposition`/`OnImeStartComposition` から明示配線=SR がキャレット追従できる(初期実装で漏れていた重要バグをレビューで検出→修正済)
  - **`NotifyCandidateWindow` を `PositionCaret` 経路に一本化**(Task 12 レビュー I-2 対応)=二重発火とデッド呼出を排除

- **P4+ 申し送り**(Minor 6 件・全て非ブロッカー):
  - **M-1**: `EnsureVisibleCharRange` は §4-6 未ガード(現状 App 層に未確定期間中の programmatic ジャンプ経路なし=P7 実機評価で判断)
  - **M-2**: `AllowUnsafeBlocks` は C# 12 `[InlineArray(32)]` で回避可能(P5〜P7 で他 P/Invoke に unsafe 波及したら再検討)
  - **M-3**: `ImeCompositionState` を `public` 化=設計書 §3-1 の `internal` 表記と乖離(実質 `yEdit.Core.Editing` の他型と同格の扱いで妥当・doc 側を実装踏襲に合わせる余地)
  - **M-4**: `Copy()` はミューテーションなし=composing 中でも本文不変=ガード不要(XML doc に明記の余地)
  - **M-5**: `NotifyCandidateWindow` は `PositionCaret` 経由で `ComputeCaretPoint` を 2 回呼ぶ=低頻度経路のため YAGNI(必要になったら `(x, y)` オーバーロード新設)
  - **M-6**: ATOK チェックリスト §7「戻ってきても残骸なし」の NG 診断にタイトルバー `[IME: ...]` 残存チェックを追記する余地

- **暫定 OK として P5 へ進む(2026-07-06 ユーザー承認)**:
  - ATOK **目視部分**(SR 非依存 = 未確定表示 / 節反転 / 候補窓位置 / 確定 / 取消 / BackSpace / フォーカス移動): smoke launcher 上で動作確認済
  - **SR 読み上げ検証は不能**: NVDA/PC-Talker のいずれも新 EditorControl を「編集コントロール」と認識せず本文/未確定を読まなかった=**P5(UIA プロバイダ接続=`WM_GETOBJECT`/`UiaRootObjectId`/`IUiaTextHost` v2)完了後に本チェックリストを再実施**する必要あり
  - この時点では **P4 は暫定 OK**。実 SR 適合の最終判定は P5 完了時の本チェックリスト再実施に委ねる。撤退判断も P5 後に再評価(この段階で NG でも `git revert d1793b0..HEAD` で P3 完了状態へ完全復帰可能=App 層無変更・撤退安全性担保)

### P5: UIA/SR接続(第2ゲート)
- `IUiaTextHost` v2リファクタ・EditorControlのホスト実装・WM_GETOBJECT・イベント一式・空行イベント・座標API本実装・ネイティブ表面原則適用
- `tools/verify-uia*/walk-test*` を新コントロール向けに調整→SR非依存回帰(Moveスパン等)→ユーザー実機中間検証(P0と同項目を本物で)
- **DoD**: SR非依存スクリプト全PASS+実機中間OK。PC-Talker NGなら表面調整→再試行→ダメなら撤退

#### P5 結果(2026-07-06・**自動 DoD 全達成・実機中間検証待ち**)

- 実装: Task 1〜14 全完了・14 commits(`f85f245`〜`032cd04` + レビュー対応、feature branch のみ・main 未変更)
- 別エージェント最終レビュー: **Critical 0 / Important 5 / Minor 9**
  - **Important 対応済み(コード修正)**:
    - I-1: `HasFocus` を `Focused`(内部で `GetFocus()`=RPC で常に false)から `_hasFocus` キャッシュへ(v1 ScintillaHost と同形)
    - I-2: `Handle` を live プロパティ(Handle 未生成時に CreateHandle 誘発)から `_hwnd` キャッシュへ。`OnHandleCreated` で捕捉、`OnHandleDestroyed` で 0 リセット(v1 と同形)
    - I-3: `SetSelection`/`SetFocus` に `if (IsDisposed || !IsHandleCreated) return;` ガード追加(v1 と同形・破棄後 BeginInvoke で `InvalidOperationException` を防ぐ)
  - **Important 対応済み(ドキュメント整合)**:
    - I-4: 座標 API の「UI スレッド Invoke マーシャリング方式」を本結果表に明記(上記「座標 API」節)
    - I-5: 単語境界セマンティクスの不一致(WordStart/End は空白区切り・NextWordStart/PrevWordStart は Core WordBoundary 委譲)を実機チェックリスト §6 の観察点に追加
  - **Minor 対応済み**: M-2(TestHook_ForceUiaListen static bool のレース)→ `[Collection("UiaEventHook")]` 排他
  - **Minor 申し送り(P7 送り)**: M-1(`_caret`/`_anchor` volatile 化)/ M-3(`_clientToScreenX/Y` stale=フォームドラッグ時)/ M-4(逐行 Wrap の O(N²)=大容量ベンチ NG なら Frame キャッシュ)/ M-5(行頭終端 1px 矩形の設計書明記)/ M-6(`_lastFrame` を `_paintedFrameForTest` に改名検討)/ M-7(Core.Tests が net9.0-windows 限定=`tests/yEdit.Accessibility.Tests` 分離の余地)/ M-8(`GetPatternProvider`/`GetPropertyValue` の nullable annotation・v1 も同形の既存踏襲)/ M-9(`OnPaint` 内の `PointToScreen` を `OnLocationChanged` 集約=M-3 と併せて対応)
- ビルド 0 警告 / テスト全緑:
  - Core.Tests 528→540(+12=`IUiaTextHostContractStubTests`(+1)/`TextRangeProviderV2Tests`(+7)/`TextProviderImplV2Tests`(+4))
  - Editor.Tests 155→182(+27=`EditorControlUiaHostTests`(+10)/`EditorControlUiaGetObjectTests`(+2)/`EditorControlNativeSurfaceTests`(+2)/`EditorControlUiaEventsTests`(+3)/`EditorControlUiaFocusEventTests`(+1)/`EditorControlBoundingRectsTests`(+3)/`EditorControlOffsetFromPointTests`(+3)/`EditorControlWordNavEventTests`(+3))

- **v1/v2 並存アーキテクチャ**(設計 §2-3 完全準拠):
  - v1 `IUiaTextHost` は `IUiaTextHostLegacy` にリネームのみ(挙動不変)。ScintillaHost / UiaTextControl / v1 系 Provider 群のロジックは無変更
  - v2 `IUiaTextHost` を新設(範囲ベース + 位置歩き 8 メンバ + 座標 API)。全 19 メンバ
  - v2 用 Provider 3 クラス新設: `TextControlProviderV2`(public・IRawElementProviderSimple/Fragment/FragmentRoot)/ `TextProviderImplV2`(internal・ITextProvider)/ `TextRangeProviderV2`(internal・v1 Move スパン保持ロジック踏襲)
  - EditorControl が v2 を explicit 実装(19 メンバ) + `WM_GETOBJECT`(UiaRootObjectId) 応答 + `WM_GETTEXT`/`WM_GETTEXTLENGTH` 抑止(ネイティブ表面原則)

- **RPC スレッド安全性**:
  - `_bufferSnapshot`(volatile TextSnapshot?)は SetSource / AfterEdit で参照差替=RPC スレッドは不変スナップショットを読める
  - `_bounds`(WPF Rect + lock)は UI 経路(OnHandleCreated / OnSizeChanged / OnLocationChanged)で更新
  - `_lastFrame`(volatile Frame?)/ `_clientToScreenX/Y` は OnPaint 末尾 / UpdateBoundsCache で更新
  - UI 経路必須の API(SetSelection / SetFocus / GetBoundingRectangles / OffsetFromScreenPoint)は `InvokeRequired ? Invoke : 直接` パターン

- **UIA イベント発火**:
  - `RaiseUia` 共通ヘルパ+ 3 種カウンタ + `TestHook_ForceUiaListen`(テストで `ClientsAreListening=false` 環境を突破)
  - AfterEdit → TextChanged + TextSelectionChanged(RaiseUiaSelectionEvents 条件付)
  - Set*/MoveCaret* → TextSelectionChanged(同上)
  - OnGotFocus → AutomationFocusChangedEvent + TextSelectionChangedEvent(PC-Talker 2 秒ポーリング対策)
  - OnKeyDown(Ctrl+←→ かつ非 shift)→ 新設 WordNavigatedEvent(App 層 Announcer 補完受け口・P0 で確定)

- **座標 API**(**設計ドリフト解消・レビュー I-4**):
  - **当初設計**(§2-7 / §4-5 / §4-6・実装計画 Task 10): RPC スレッド上で `_lastFrame` + `PixelMapper.OffsetToPx(frame, pos)` を直接計算(=UI スレッド非依存)。
  - **実装**: `PixelMapper` に `Frame` オーバーロードが存在しないため、既存 UI スレッド専用純ロジック(`ComputeCaretPoint` / `OffsetFromClientPoint`)を `InvokeRequired ? Invoke : 直接` で再利用する方式に変更。`_lastFrame` は `OnPaint` 末尾でキャッシュされるが座標算出には使わず、Task 11 テスト用フック(`TestHook_GetLastFrame`)専用となった。
  - **選択理由**: (1)計画書の「編集直後で `_lastFrame` が古い」問題(§4-6)を自動回避できる (2)UI スレッド Invoke は稀な同期呼び出しでデッドロックリスクを持つが、UIA 座標問合せの頻度は低く、実運用では許容
  - **`GetBoundingRectangles(s, e)`**: ComputeCaretPoint 逐行分解 → client→screen オフセット加算
  - **`OffsetFromScreenPoint(x, y)`**: 既存 `OffsetFromClientPoint` 再利用 → `RangeFromPoint` が本挙動化

- **設計判断のポイント**:
  - **v1/v2 並存**: v1 挙動を一切変えないことで撤退安全性を担保(`git revert f85f245..HEAD` で P4 完了状態へ完全復帰可能)
  - **Move スパン保持ロジック**(TextRangeProviderV2): v1 と同挙動を意識的に踏襲=PC-Talker の文字歩き(Expand(Char)→Move(Char,1)→GetText の繰り返し)で 2 文字目以降が空にならない
  - **UI Invoke マーシャリング**: 座標 API 2 個は RPC スレッド上で純ロジック化するのを避け、UI スレッドの既存純ロジック(ComputeCaretPoint / OffsetFromClientPoint)を再利用。設計書「RPC スレッド安全」は満たしつつ実装コストを最小化
  - **WordNavigatedEvent 先出し**: App 層 Announcer(P6 で本実装)の受け口を EditorControl 側に用意=P6 で App 層一発置き換え時に配線するだけ

- **P5+ 申し送り**(Task 14 レビュー結果次第で更新):
  - `IUiaTextHost.WordStart`/`WordEnd` は Core `WordBoundary` へ委譲せず簡易実装(空白区切りのみ・§5-5 明記)。精度不足なら P7 で Core `WordBoundary` の CharClass 露出を検討
  - Core.Tests の TFM を `net9.0` → `net9.0-windows` に変更、`UseWPF=true`、`<Using Include="System.IO" />` 追加、Accessibility を ProjectReference 追加。実装計画に明記なかったが v2 契約テストを Core.Tests に置くために不可避
  - Task 13 `UiaSmokeAnnouncer` の UIA `RaiseNotificationEvent` は WPF Automation API に非存在=PC-Talker 直叩きのみ(NVDA/ナレーターは v2 provider の TextChanged/TextSelectionChanged で自然追従できる想定)

- **暫定 OK として P6 へ進む判定**(実機中間検証チェックリスト実施後):
  - `docs/plans/2026-07-06-p5-uia-checklist.md` 16 項目を NVDA / PC-Talker / ナレーター / ATOK で実施

#### P5 実機中間検証(2026-07-06・**合格**)

- ユーザー実機確認: **OK**(NVDA / PC-Talker / ナレーター / ATOK 全て許容範囲)
- 自動検証: build 0 警告 / 722 テスト全緑 / SR 非依存スクリプト 3 種全 PASS
- **判定=合格**。**P6 へ進む**(App 層一発置き換え + Scintilla/v1 系一括撤去)

### P6: App層一発置き換え
- `Document.Editor`型差し替え・EditorAppearance書き換え・FileControllerストリームI/O化(3エンコーディング・UTF-16廃止)・SearchController閾値二層化・CSV/禁則/Markdown/バックアップ/Grep/行ジャンプ/文字情報配線・Scintilla NuGet撤去
- `SrRoute.Nvda`/`CsvFocusSink` は無効化のみで残す(切り分け用)
- **DoD**: build 0警告+Core全緑+全機能手動チェックリスト
- **申し送り(BackupCoordinator の大容量チャンク書き=P6 Task 12)**: 現状の `BackupCoordinator` は `doc.Editor.SnapshotText`(全文 string 化)を経由してバックアップレコード(`BackupRecord.Content: string`)を構築する。EditorControl 側の `SnapshotText` は non-null 保証で機械的に通り、Task 12 の型置換はゼロ変更で完了(build 通過確認済)。ただし 1GB 級ファイルでは バックアップ tick(既定 300 秒)ごとに 2GB(UTF-16 で 32bit×charLen)近い string 生成が発生する=真の OOM 回避は P7 送り。P7 で `BackupStore.WriteChunked(TextSnapshot)` を追加し `BackupRecord.Content` を `TextSnapshot`/ストリーム API 化する方向(§2-8「バックアップはチャンク直書き」の完全実装)。それまでの回避策として、閾値超ファイルに対して「保存済み(=Modified=false)以外は自動バックアップを一時 skip する」オプションを設定側で用意する余地あり(P6 では未実装)。

#### P6 実装記録(2026-07-12・**自動 DoD 全達成**)

Task 1〜18 の実施結果:

| Task | 内容 | commit | 変更ファイル | 備考 |
|---|---|---|---|---|
| 1 | `Text`プロパティ + `ReplaceSource` | (P5 内) | EditorControl.cs | P5 で先出し済み |
| 2 | `SnapshotText`+`SelectCharRange(len)`+`MoveCaretCharOffset` エイリアス | (P5 内) | 同上 | 同上 |
| 3 | `LineCount`+`GoToLine(int)`+`CurrentPosition` | (P5 内) | 同上 | 同上 |
| 4 | `SavePointLeft`/`SavePointReached`/`UpdateUI` イベント | (P5 内) | 同上 | 同上 |
| 5 | `ConvertEols(LineEnding)` | `a72e78a`+`a2fb229` 相当 | 同上 | P5 完了時点で先出し |
| 6 | EncodingCatalog/Detector から UTF-16 削除 | `a75a281`+`6a2cf2d` | EncodingCatalog.cs / EncodingDetector.cs / GrepService.cs | UTF-16 3種のみ削除・SJIS fallback |
| 7 | TextFileService の Stream I/O 化 | `f0f4ba4`+`b9b61a7` | TextFileService.cs / LoadedBuffer.cs | LoadAsBuffer + Save(TextBuffer)・チャンク境界 |
| 8+9 | Document.Editor 型 + MainForm.CreateEditor 置換 | `bbdc08e`+`e3610d0` | Document.cs / MainForm.cs / DocumentManager.cs / CsvController.cs / CsvCellEditor.cs / SearchController.cs / EditorAppearance.cs / FileController.cs | App 層一発差替の核 |
| 10 | FileController を Stream I/O + ReplaceSource | `8363205`+`d733d37` | FileController.cs / EditorControl.cs(`CurrentBuffer`+`SetOrReplaceSource`) | 1GB 級 OOM 回避 |
| 11 | SearchController 64MB 閾値二層化 | `74e55e9`+`198c7ac` | SnapshotSearcher.cs(新)/ SearchController.cs / SnapshotSearcherTests.cs | 窓照合+行単位 regex |
| 12 | BackupCoordinator 動作確認+申し送り | `6e4870b` | 設計書のみ | 型置換ゼロ変更・大容量申し送り |
| 13 | CsvController + CsvCellEditor 配線 | `e1773c6` | Document.cs / CsvController.cs / CsvCellEditor.cs | FocusTarget=Editor 固定・CsvFocusSink 生成のみ残す |
| 14 | 禁則/MD/行ジャンプ/文字情報 API 適合 | `dc5ee94` | MainForm.cs | EditorGotFocus シンク退避残骸撤去+コメント整理 |
| 15 | `SrContext.UseNativeReading` false 固定 | `66d2b82` | SrContext.cs | SR 二系統機構の実質死 |
| 16 | App 層 Announcer に WordNavigated + CaretEnteredEmptyLine 配線 | `2606d5b` | DocumentManager.cs / MainForm.cs | PC-Talker 単語ナビ補完の実挙動化 |
| 17 | Scintilla5.NET / ScintillaHost.cs / Sci.cs / v1 用 tools 一括撤去 | `d115f54` | yEdit.Editor.csproj / word-sim.ps1 + 4 ファイル削除 | publish 出力にネイティブ DLL なし |
| 18 | 手動チェックリスト + 別エージェント最終レビュー + 設計書追記 | (本コミット) | 本ファイル+ manual-checklist.md +メモリ | ユーザー実機実施は P6 中間検証として別途 |

**自動 DoD 達成状況**:
- build 0 警告 / 0 エラー
- Core.Tests 581 全緑(P5 の 540 → UTF-16 テスト削除 +11 = 551、+Task 11 SnapshotSearcher テスト 23 追加、+Task 7 LoadAsBuffer/Save テスト、+Task 10 CurrentBuffer 系、Task 6 fallback 差替 = 実測 581)
- Editor.Tests 210 全緑(P5 の 182 → Text setter/SelectCharRange 系テスト P5 で先出し、+P6 実測差分 = 210)
- publish 出力に `Scintilla.dll` / `Lexilla.dll` なし(WebView2Loader.dll のみ・Markdown プレビュー用で Scintilla 非依存)
- `Scintilla5.NET` PackageReference が csproj から削除済
- App 層に `Scintilla` / `ScintillaHost` / `SciNET` の実コード参照ゼロ(残るのはコメント言及のみ)
- 撤退安全性: `SrRoute.Nvda` enum / `CsvFocusSink` クラス / v1 UIA 4 ファイル / UiaProbe プロジェクトが残っている(P7 で完全撤去)

**申し送り**:
- BackupCoordinator の大容量チャンク書き(上記 Task 12 の申し送り)
- Task 11 SnapshotSearcher の追加最適化(Materialize キャッシュ・M-2 / 上位経路 WholeWord の Unicode \b vs ASCII \w 不整合・M-5 等)は 32M chars 以下の通常経路では影響なし=P7 送り
- CsvFocusSink 完全撤去(§0-8 猶予)は P7 実機総合検証後
- v1 UIA コード(IUiaTextHostLegacy / TextControlProvider / TextProviderImpl / TextRangeProvider / TextNavigation / UiaTextControl)の撤去も P7
- **P6 レビュー I-3(Save 経路の Stream I/O 未完)**: Task 10 は Load 側の 1GB OOM を回避したが、Save 側 `TextFileService.Save(TextBuffer)` は `buffer.Current.GetText(0, CharLength)` で全文化してから string 版 Save に委譲=3-4 コピー。`ConvertEols` も非 fast-path で `SnapshotText`+Replace の 2 バッファを介するため保存時ピークは 5GB 級。1GB 級ファイルの実 Save は現状 OOM リスク=P7 で `Save(TextBuffer)` を chunk write に置換し `ConvertEols` を in-place TextBuffer 変換に切替(§2-2 の Save 側完成)。
- **P6 レビュー I-4(SJIS/EUC-JP Load が ReadToEnd で全文化)**: `TextFileService.LoadAsBuffer` は UTF-8 のみ `TextBufferBuilder.Add(byte[])` チャンク経路。SJIS/EUC-JP は `StreamReader.ReadToEnd` で全文 char[] を作ってから `TextBuffer.FromString` へ渡す=数百 MB 級で 2x メモリ。P7 で `StreamReader.Read(char[], 0, len)` チャンクループ+`Encoding.UTF8.GetBytes(chunk).AsSpan()` を `TextBufferBuilder.Add` へ流す設計に統一(§2-2 の Load 側完成)。
- **P6 レビュー I-5(SnapshotSearcher regex アンカー行内化)**: 閾値超 regex は `_inner.FindNext/FindPrev/ReplaceInRange` を行単位で呼ぶため、`^` / `$` / `\A` / `\Z` / `\G` が「文書の先頭/末尾」ではなく「行の先頭/末尾」に anchor される差異が閾値以下と閾値超で発生する。`SnapshotSearcher` の「壊れる契約」docstring は「改行を跨ぐパターンは絶対にヒットしない」のみ明記=aチャー差異も追記すべき。P7 で docstring 修正+回帰テスト 1 件追加(30 秒コスト)。

#### P6 実機中間検証(**ユーザー実施予定**)

- 手動チェックリスト: `docs/plans/2026-07-06-p6-manual-checklist.md`(A〜P の 90+ 項目)
- 実施記録欄はユーザーが記入・結果を本節にリンク追記する予定
- **判定=保留**(実機実施待ち)。合格したら P7 へ進む

### P7: 実機SR総合検証+撤去+リリース整備(第3ゲート)
- ユーザー実機フルマトリクス(3 SR × 主要全機能+復帰フォーカス+空行+タブ切替)
- 合格後: NVDA経路・CsvFocusSink・ServeUiaProvider完全撤去、HANDOFF/説明書更新、リリースCI調整(ネイティブDLL同梱削除)
- **DoD**: 実機OK+撤去後全緑+リリースzip起動確認 → mainへno-ffマージ

#### P7 実装記録(2026-07-12・Part A(I-3/I-4/I-5)進行中・撤去/検証は継続中)

- P7 Task 4 bench(1GB UTF-8 ASCII / Intel Core i9-9900KF @ 3.6GHz / RAM 64GB / Win11 Pro / Release build):
  - peak0=61,920 B / peakLoad=1,079,282,152 B(delta≈1.005 GB) loadMs=2,386
  - peakConvert=1,079,338,048 B(delta≈55 KB) convertMs=744
  - peakSave=1,079,338,048 B(delta=0) saveMs=390
  - **判定**: peak がおよそ O(text)=I-3 の効能確認。Load 常駐 ≈ 本文サイズ(1.005 GB、目標 ≦1.3 GB クリア)。ConvertEols は `--gen-1gb` が LF を書く=検出 EOL=Lf=`ConvertEols(Lf)` が uniform fast-path を踏み rebuild 経路の一時割当ゼロ(byte スキャンのみ・目標 ≦1.3 GB を大幅クリア)。Save は `WriteTo(Stream)` の変換ゼロチャンク直書きで追加割当 0 バイト(目標 ≦100 MB クリア)。in=out=1,073,741,824 B・match=True で内容も正しくラウンドトリップ。

### 運用
- 各フェーズ完了時に別エージェントのコードレビュー→ブランチへコミット。**全フェーズを本ブランチ(feature/custom-editcontrol-design・ワークツリー)に閉じて積み、P7合格後に一括で main へ no-ff マージ**(ユーザー指示 2026-07-05: プロジェクト完了まで main には触れない。当初の「P0/P1は随時mainへマージ」は撤回)
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
