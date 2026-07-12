# P4 IME 実機チェックリスト(ATOK)

**目的**: P4(IME サポート)の DoD 判定 = ATOK 実機で以下 7 項目が全 OK なら合格。自動テスト(683 件緑)で検証できない領域(実 IME からの WM_IME_* メッセージ経路・候補窓の座標・入力残骸)を、ユーザー実機で最終判定する。

**関連ドキュメント**:
- 実装計画: `docs/plans/2026-07-06-p4-ime.md`(Task 14)
- 設計書: `docs/plans/2026-07-06-p4-ime-design.md`
- 自動テスト: `tests/yEdit.Editor.Tests/EditorControlImeTests.cs`

---

## 準備

1. リポジトリ ルートで smoke を起動する:
   ```powershell
   dotnet run --project tests/yEdit.Editor.Smoke -c Release -- --ime
   ```
2. Smoke ウィンドウにフォーカスを合わせる(タイトルバー `yEdit.Editor.Smoke`)。
3. ATOK を ON にする(タスクトレイの ATOK アイコンで [あ] 表示になっている状態)。
4. サンプル本文の任意の位置にキャレットを置く。

---

## 検証項目

| # | 検証項目 | 期待挙動 | 結果 |
|---|---|---|------|
| 0 | (プリフライト)サンプル本文が表示され、キャレットが点滅している | Smoke ウィンドウ内に「IME 動作確認用サンプル」以下の日本語行が可視・キャレット blinking(=Task 14 レビュー M-5) | |
| 1 | 「にほんご」タイプ→変換なし確定(Enter) | 未確定表示・下線・確定で「にほんご」挿入。タイトルバーに `[IME: にほんご]` が出る | |
| 2 | 「かんじ」タイプ→スペースで変換→Enter | 変換対象節が反転(Bold+Underline)・確定で選択候補が挿入される | |
| 3 | 「わたしはにほんじん」タイプ→連文節変換 | 節境界が視認できる(下線の切れ目)・矢印で節移動できる | |
| 4 | 候補ウィンドウが表示される | キャレット直下(=行送り位置)に候補窓が出る。行の遥か上/左などに出ない | |
| 5 | ESC 取消 | 未確定文字列が消える・TextBuffer に何も残らない(Modified フラグが立たない) | |
| 6 | 未確定中に BackSpace | 未確定文字列が 1 文字短くなる(TextBuffer は不変=Modified フラグが立たない) | |
| 7 | 未確定中に他ウィンドウへフォーカス移動→戻る | 未確定が確定される(=Scintilla 互換)・戻ってきても未確定残骸なし | |

---

## NG 時の切り分け

上の表で NG になった項目があれば、以下の一次切り分けを行ってから Task を再設計する。

- **Smoke タイトルバー `[IME: ...]` に未確定文字列が反映されない**
  → WM_IME_COMPOSITION が届いていない疑い(EditorControl の WndProc override 経路確認)。
  該当 Task: Task 5(WM_IME_STARTCOMPOSITION 受信)・Task 6(WM_IME_COMPOSITION 受信)。

- **タイトルバーには反映されるが本文に描画されない**
  → OnPaint の DrawImeOverlay 呼出漏れ。
  該当 Task: Task 9(未確定文字列の overlay 描画)。

- **確定文字列が 2 度挿入される(例: 「かんじ」→「感じ感じ」)**
  → GCS_RESULTSTR 分岐で `_ime = default` に潰す順序ミス(ApplyResult が終わってから overlay を消すべきなのに先に消している 等)。
  該当 Task: Task 7(GCS_RESULTSTR = 確定文字列の適用)。

- **候補窓がキャレットから離れた位置に出る**
  → ImmSetCandidateWindow の座標算出ミス(PositionCaret 経路で ImmSetCandidateWindow が呼ばれていない、または px 座標のスケール取り違え)。
  該当 Task: Task 12(候補窓/未確定フォント追従)。

- **未確定中に IME 内で左右矢印してもキャレットが追従しない**
  → PositionCaret の IME 分岐(未確定期間中は `_ime.Start + _ime.CursorPos` を使う)配線ミス。
  該当 Task: Task 11(未確定中のキャレット座標=IME カーソル位置追従)。

- **未確定中に読み取り専用へ切り替えると未確定文字が浮きっぱなし**
  → ReadOnly setter の CancelCompositionAndDefault 呼出漏れ。
  該当 Task: Task 8(§4-2)・Task 13(外部 API ガード)。

---

## 実施結果(2026-07-06・暫定 OK・SR 依存部分は P5 後リトライ)

- **状況**: NVDA / PC-Talker のいずれも Smoke launcher 上の新 EditorControl を「編集コントロール」として認識せず、本文/未確定文字列の読み上げが動かなかった。これは想定内で、**P5 で UIA プロバイダ(`WM_GETOBJECT`/`UiaRootObjectId` + `IUiaTextHost` v2)を新コントロールに接続してからでないと SR は何も読めない**(現行 EditorControl は WM_GETOBJECT に応答しない=Windows のデフォルト経路のまま)。
- **ATOK 目視部分**(SR 非依存の項目):未確定表示/変換対象節反転/候補窓位置/確定/ESC 取消/BackSpace 縮小/フォーカス移動 は smoke launcher 上で目視レベルの動作確認済。
- **SR 読み上げ部分**(項目 1〜7 の SR 読み依存): **P5 完了後にリトライ**。特にタイトルバー `[IME: <未確定>]` は表示上のみの補助で、SR は読まない=IME 本体の SR 適合検証は本チェックリストの再実施(P5 後)に委ねる。
- **判定**: **暫定 OK として P5 へ進む**(2026-07-06 ユーザー承認)。P5 完了時に本チェックリストを再実施し、SR 依存項目まで含めた本判定を行う。

---

## 撤退判定

- **7 項目のうち 3 項目以上 NG** → **撤退**。`git revert` で P4 コミット群を戻し、P3 完了状態(コミット `d1793b0`)へ戻す。P5 以降(UIA 接続)を先に進めるか、P4 の設計を見直す。
- **2 項目以下 NG** → 該当 Task を再設計し継続。NG になった項目に対応する Task(上記「NG 時の切り分け」の「該当 Task」)を見直し、必要なら追加の自動テストを起こす。
- **全 7 項目 OK** → **P4 DoD 達成**。`memory/custom-editcontrol.md` に「P4 IME=DoD達成」を追記し、P5(UIA 接続=NVDA/PC-Talker 対応)へ進む。

---

## 記録テンプレート(結果を書き込むとき)

```
検証日時: YYYY-MM-DD HH:MM
ATOK バージョン: (例) ATOK 2025
OS: Windows 11 Pro 26200
結果:
  1: OK / NG (メモ:)
  2: OK / NG (メモ:)
  3: OK / NG (メモ:)
  4: OK / NG (メモ:)
  5: OK / NG (メモ:)
  6: OK / NG (メモ:)
  7: OK / NG (メモ:)
総合判定: 合格 / 撤退 / 部分修正
```
