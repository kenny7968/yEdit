# P5 実機中間検証チェックリスト(UIA/SR/ATOK 統合)

**対象**: `yEdit.Editor.Smoke.exe --uia [ファイルパス]`(P5 Task 13 実装)
**目的**: 自作 EditorControl に UIA プロバイダを載せた状態で SR(NVDA / PC-Talker / ナレーター)と ATOK 実機が期待どおり動くか、P0(UiaProbe)と同じ品質水準を担保できているかを確認する。
**前提**: `dotnet build yEdit.sln` 済み。`--uia` オプション付きで起動するとタイトルバーに `[UIA]` が付く(P5 Task 13)。

## SR 起動と初期反応(NVDA / PC-Talker / ナレーター 各実施)

1. **フォーカス時発声**: smoke ウィンドウにフォーカスが入った直後、SR が「本文」(または SR 既定の Document 型読み)と発声する。
2. **単文字ナビ**: ← / → / ↑ / ↓ の各キーで、移動先の 1 文字を発声する(空行では「空行」または SR 既定)。
3. **行ナビ**: Home / End、および ↑ / ↓ での行遷移で、行内容を発声する。空行は SR 既定または「空行」明示発声(P3 の `CaretEnteredEmptyLine` が UIA v2 経路で反映される)。
4. **SayAll**: NVDA=NVDA+↓ / PC-Talker=全文読み / ナレーター=Ctrl+全文 で、先頭から末尾まで途切れず読み上げる。
5. **選択読み**: Shift+矢印 で選択スパンを読み上げる(TextSelectionChangedEvent が届いていること)。
6. **単語ナビ**: Ctrl+← / Ctrl+→ で、単語スパンを発声する。PC-Talker は `WordNavigatedEvent` → `UiaSmokeAnnouncer` の PC-Talker 直叩き経路で単語スパンを補完発声する(NVDA/ナレーターは UIA の TextUnit.Word で自然に読める)。
    - **観察点(P5 レビュー I-5)**: 日本語×英語混在テキスト(例: `hello漢字world`)で `Move(Word)` と `Expand(Word)` のスパンが一致するか(EditorControl の `WordStart`/`WordEnd` は空白区切り簡易実装なので、Latin↔Han 境界では `NextWordStart`(Core WordBoundary 委譲)と一致しない可能性=単語スパン発声が広めに読まれることを目視確認。NG なら P7 で `WordBoundary` の CharClass 露出+委譲へ変更する申し送り)
7. **空行着地**: 空行に着地したとき、`CaretEnteredEmptyLine` 経路で「空行」と発声する(全 SR で同じ)。
8. **他ウィンドウ→復帰**: 別のウィンドウにフォーカスを移し、再び smoke に戻す。フォーカス復帰時に SR が「本文」と現在キャレット位置の内容を発声する(OnGotFocus 明示発火の `AutomationFocusChangedEvent` + `TextSelectionChangedEvent` の 2 発が届く)。

## ネイティブ表面原則(P/Invoke / Spy++ で確認)

9. **本文非公開**: Spy++ の Text 欄が空、または `SendMessageW(WM_GETTEXTLENGTH)` が 0 を返す。`SendMessageW(WM_GETTEXT)` の StringBuilder バッファが空文字列で返る。
    - コマンド例(PowerShell):
      ```pwsh
      # smoke を --uia で起動しておく前提。プロセス ID からメインウィンドウの HWND を取得
      $p = Get-Process yEdit.Editor.Smoke -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
      # あるいは既に取得済みなら P/Invoke で SendMessage を叩く(WM_GETTEXTLENGTH=0x000E)
      ```
    - Spy++ で EditorControl 相当の window class を選んで Messages ペインで WM_GETTEXTLENGTH が 0 を返すことを目視。

## ATOK 実機(P4 のチェックリストを SR 併用で再実施)

以下 7 項目は `docs/plans/2026-07-06-p4-ime-checklist.md` の再掲だが、**SR が同時に何を発声するか**まで確認する。

10. **「にほんご」タイプ→変換なし確定**: 未確定・確定文字列とも SR が読む(未確定は下線+青色、確定は本文と同色)。
11. **「かんじ」→スペース変換→Enter**: 変換対象節が反転(SelectionBack)で強調、確定文字列が SR に読まれる。
12. **「わたしはにほんじん」→連文節変換**: 節境界が動くたびに SR が節ごとに読む(現行 SelectionChanged で追従できるかを実測)。
13. **候補ウィンドウ表示**: SR が候補窓を読む(NVDA は自動追従、PC-Talker は候補窓が視認できる位置=キャレット直下に表示されていることを目視)。
14. **ESC 取消**: 未確定が消える。SR が「取消」相当を発声(または無音=SR 側の設定次第で可)。
15. **未確定中に BackSpace**: 未確定が 1 文字短くなる。SR が更新後の未確定を読む。
16. **未確定中に他ウィンドウへフォーカス移動**: 未確定が確定される。復帰時に「戻ってきても残骸なし」の状態が SR に伝わる(overlay が消えている+SR がフォーカス位置の内容を発声)。

## SR 非依存 SR/UIA スクリプト(参考実行)

以下の 3 スクリプトは SR 無しで動作。実機検証の前後に実行して自動判定できる項目を先に消化する。

```pwsh
pwsh tools/verify-uia-editor.ps1
pwsh tools/walk-test-editor.ps1
pwsh tools/word-sim.ps1 -Target EditorSmokeExe
```

全 PASS(EXIT 0)であること。

## 結果記録

以下フォーマットで各 SR / ATOK の結果を追記してください(P0/P4 と同形式):

```markdown
### 実施記録(YYYY-MM-DD)
- NVDA: 1〜9 → OK / NG(NG の場合は詳細)
- PC-Talker: 1〜9 → OK / NG
- ナレーター: 1〜9 → OK / NG
- ATOK: 10〜16 → OK / NG
- SR 非依存スクリプト: verify-uia-editor / walk-test-editor / word-sim(-Target EditorSmokeExe)全 PASS
- 総合判定: 合格 / 保留(理由) / 不合格(理由)
```

## 関連

- P0 チェックリスト: `docs/plans/2026-07-05-p0-sr-probe-checklist.md`(UiaProbe 版)
- P4 チェックリスト: `docs/plans/2026-07-06-p4-ime-checklist.md`(ATOK 単独版)
- 設計書: `docs/plans/2026-07-05-custom-editcontrol-design.md` §3(全体スコープ)
- 実装計画: `docs/plans/2026-07-06-p5-uia-connect.md`
