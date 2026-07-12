# P7 チェックリスト申し送り(App 層) 設計書

- **日付**: 2026-07-13
- **対象ブランチ**: 未作成(推奨: `feature/p7-followups`)
- **前提**: [[custom-editcontrol]] P0〜P8 完了・main マージ済(コミット `f4430a3`)
- **由来**: `docs/plans/2026-07-13-p6-manual-checklist-result.md`(GO 判定)から抽出した App 層申し送り

## 対応範囲

自作エディットコントロール置換プロジェクト(P0〜P8)の手動チェックリストで挙がった項目のうち、**EditorControl 責務外**の App 層 UX/バグ/機能整理 5 件を対象とする。

対象:
- **C-2**: SaveAs キーバインド未配線 + エンコード指定 UI 欠落
- **G-2**: 検索ダイアログ「次を検索」後にダイアログが残る
- **G-3**: 「置換して次へ」1 回目押しで置換されず選択のみ
- **I-5**: `Ctrl+Tab` 後にタブ列に留まる(Enter で編集エリア復帰)
- **F-3/F-4**: `Ctrl+Alt+I` 文字情報機能の廃止

対象外(P7 メモから継続):
- B-5/G-7 2GB 上限拡張(Piece の long 化=大工事)
- SR 依存 D-5/I-3/N-10([[pctalker-speech-control]] へ集約)

## 1. C-2: SaveAs キーバインド + エンコード指定 UI

### 症状
- `Ctrl+Shift+S` が動かない(キーバインド未配線・`src/yEdit.App/MainForm.cs:256` はショートカット無しで `AddMenuItem` 呼び)
- 「名前を付けて保存」→ `SaveFileDialog`(`src/yEdit.App/FileController.cs:192`)にエンコード指定欄がない

### 設計方針
自作 `SaveAsDialog`(App 層新規 Form)を作成し、パス・エンコード・改行コードを一体の 1 ダイアログで収集する。標準 `SaveFileDialog` は「参照(&B)...」ボタン内部でパス取得用途にのみ使う。

### 変更ファイル
- **新規**: `src/yEdit.App/SaveAsDialog.cs`
- **変更**: `src/yEdit.App/MainForm.cs` — ProcessCmdKey に `Ctrl+Shift+S` 追加、メニュー項目のショートカットキー表示
- **変更**: `src/yEdit.App/FileController.cs` — `SaveAsDocument` を新ダイアログ経由に改修、`WriteToPath` オーバーロード追加(エンコード/改行指定版)

### SaveAsDialog レイアウト
- ラベル「ファイル名(&F):」+ TextBox + Button「参照(&B)...」
- ラベル「文字コード(&E):」+ ComboBox(`EncodingCatalog.SelectableEncodings` を流用・現在エンコードをプリセレクト)
- ラベル「改行コード(&L):」+ ComboBox(CR/LF/CRLF・現在の改行をプリセレクト)
- OK/キャンセル

### アクセシビリティ
- TabIndex を「ファイル名 → 参照 → エンコード → 改行 → OK → キャンセル」の順に配線
- `EncodingPickDialog` と同じアクセスキー方式(ラベル → 次コントロールへ転送)

### リスク・注意
- **中**: `WriteToPath` の既存呼び出し(上書き保存経路)はドキュメント状態のエンコードを暗黙参照している。新オーバーロードで既定挙動を壊さぬよう別メソッド化する
- 「参照」ボタンからの `SaveFileDialog` 内で選ばれたパスは TextBox に書き戻す。既存の拡張子フィルタ(`.txt/.md/.csv/*`)は「参照」内部で維持

### DoD
- Ctrl+Shift+S でダイアログが開く
- ダイアログで指定したエンコード・改行でファイルが保存される
- 実機 SR(PC-Talker/NVDA/ナレーター) で各コントロールが読まれる
- 回帰テスト: 既存の SaveAs テスト(あれば)は緑を維持

## 2. G-3: 「置換して次へ」1 回目押しで即置換

### 症状
現状の `SearchController.ReplaceOne`(`src/yEdit.App/SearchController.cs:130`)は「現在の選択が今のヒットでなければ `Find(次を検索)` を呼んで終了」する。ユーザーは 1 回目押下で選択されるだけで置換されず、2 回目でようやく置換されると認識する。VSCode/秀丸/サクラ等の一般的な期待挙動と乖離。

### 設計方針
VSCode 準拠に変更:「現ヒット未選択なら検索して即置換して次へ」の 1 段動作にする。

### 変更ファイル
- **変更**: `src/yEdit.App/SearchController.cs` — `ReplaceOne` を改修
- **新規テスト**: `tests/yEdit.Core.Tests/` または App 側テストプロジェクトで回帰確認(1-2 件)

### ロジック
```csharp
public void ReplaceOne()
{
    // 前段(null チェック・CsvMode ガード)は現状維持
    var span = ResolveCurrentHitOrFindNext(); // 新規: 現ヒット無ければ FindNext を呼ぶ
    if (span is null) { Announce("見つかりません"); return; }
    // 以降は現状の置換 + 次を検索ロジック
    ed.ReplaceCharRange(span.Start, span.Length, repl);
    // ... 現状のまま ...
}
```

`ResolveCurrentHitOrFindNext` は private ヘルパ:
- 現在の選択がヒットならそれを返す
- 違えば `FindNext` を呼びヒットを返す(未ヒットなら null)

### コメント更新
- 現状コメント「(標準の置換動作)」→「(VSCode 準拠: 未選択なら検索して即置換)」に書き換え

### リスク・注意
- **中**: 現状仕様に慣れているユーザーには挙動変化。ただし G-3 のユーザー期待は明確に VSCode 側=採用
- 未ヒットケース(見つからない状態で ReplaceOne 押下)は「見つかりません」通知で現状同等

### DoD
- 未ヒット状態から `ReplaceOne` を 1 回押下で置換+次を検索まで実行される
- 現ヒット状態からの `ReplaceOne` は従来通り置換+次
- 回帰テスト新規 1-2 件緑
- 既存テスト全緑維持

## 3. G-2: 検索モードで「次を検索」後にダイアログ自動閉じ

### 症状
`Ctrl+F` 検索ダイアログ(`FindReplaceDialog`)で「次を検索」を押した後にダイアログが残り、続けて検索するには「閉じる → F3」の二段階が必要。

### 設計方針
**検索モードのみ**「次を検索」後に `Hide()` を呼ぶ。置換モードは操作継続のため現状維持。

### 変更ファイル
- **変更**: `src/yEdit.App/FindReplaceDialog.cs`

### 実装
- コンストラクタで `SetMode` の状態(`_isReplaceMode` フィールド新規追加)を保持
- `_next.Click` ハンドラを分岐:
  ```csharp
  _next.Click += (_, _) =>
  {
      _controller.FindNext();
      if (!_isReplaceMode) Hide();
  };
  ```
- `ProcessCmdKey` の `Keys.Enter when _pattern.Focused` (line 85) も同様に検索モード時のみ Hide

### F3 継続動作の保証
- ダイアログを Hide しても `SearchController._dialog` 参照は残る(=CurrentOptions/Pattern が取得可能)
- `SearchController._lastHit` も保持されているので `F3`(`Keys.F3` → `_search.FindNext()`)が継続動作

### リスク・注意
- **低**: `Hide()` は既存の `Close` ボタンハンドラで実績あり
- **低**: 「前を検索」ボタンでも同じ Hide 挙動にするかは要確認 → 対称性のため同じく検索モードのみ Hide

### DoD
- Ctrl+F 検索ダイアログで「次を検索」押下でダイアログが Hide される
- Ctrl+H 置換ダイアログで「次を検索」押下では Hide されない
- Hide 後に F3 で検索継続可能

## 4. I-5: Ctrl+Tab / Ctrl+1〜9 で編集エリア直接フォーカス

### 症状
`Ctrl+Tab`(`MainForm.cs:217`)は `DocumentManager.SelectNext` を呼び、内部で `FocusTabStrip()`(`DocumentManager.cs:122`)を実行=フォーカスがタブ列に留まる。ユーザーは編集エリアへ戻るために `Enter` 押下が必要。Ctrl+1〜9(`SelectAt`)も同じ挙動。

### 設計方針
- `SelectNext`/`SelectAt` のフォーカス先を `FocusActiveEditor()` に変更
- SR がタブ切替時にファイル名を読めるよう、`MainForm` 側でキー起因の切替時に能動発声する

### 変更ファイル
- **変更**: `src/yEdit.App/DocumentManager.cs` — `SelectNext`/`SelectAt` の末尾で `FocusActiveEditor()` を呼ぶ
- **変更**: `src/yEdit.App/MainForm.cs` — `_docs.ActiveDocumentChanged` ハンドラでキー起因時のみタブ名を Announcer.Say

### タブ名能動発声のトリガ判定
- `DocumentManager.SelectNext`/`SelectAt` は既に `BeforeActiveChange?.Invoke()` を呼んでいる
- 新規: DocumentManager に `bool _keySwitchInProgress` フラグを追加、`SelectNext`/`SelectAt` の間だけ true
- または: 新規イベント `KeyBasedSwitch?.Invoke(Document)` を発火し、MainForm 側で Announcer.Say
- **推奨**: 新規イベント方式(責務分離が明確)

### タブ名フォーマット
- `doc.TabLabel` をそのまま Announcer.Say
- CSV モードや変更ありマーク付きの場合も TabLabel が反映しているのでそのまま

### 救済路の維持
- `OnTabKeyDown`(`DocumentManager.cs:144-159`)は残す(Alt+Tab や直接クリック等でタブ列にフォーカスした場合の Enter でエディタ復帰)

### リスク・注意
- **中**: SR がタブ列自身を読む機会を失うため、能動発声の質が重要。実機 SR 3 種で確認必須
- **中**: `ActiveDocumentChanged` は起動時タブ生成・新規タブ・タブクローズでも発火する。**キー起因のみ**に絞る仕組みを確実に(新規イベント経由)
- 起動時ドキュメント読み上げは現状通り(この設計でも変化なし)

### DoD
- Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+1〜9 押下でエディタ直接フォーカス+SR がファイル名を読む
- 起動時/新規タブ/タブクローズでは能動発声しない
- 実機 SR 3 種でファイル名が読まれる

## 5. F-3/F-4: Ctrl+Alt+I 文字情報機能を Core も含めて全削除

### 削除方針
各 SR(NVDA/PC-Talker/ナレーター)は文字情報読みコマンドを標準搭載しているため、yEdit 独自実装は冗長。App/Core/テスト/申し送りメモリを一括削除する。

### 削除対象
1. **App**:
   - `src/yEdit.App/MainForm.cs:222` — ProcessCmdKey の `Ctrl+Alt+I` ケース
   - `src/yEdit.App/MainForm.cs:292-293` — 読み上げメニューの「文字情報(&I)」項目
   - `src/yEdit.App/MainForm.cs:442-451` — `AnnounceCharInfo` メソッド
2. **Core**:
   - `src/yEdit.Core/Reading/CharacterDescriber.cs` — クラスファイル削除
3. **テスト**:
   - `tests/yEdit.Core.Tests/Reading/CharacterDescriberTests.cs` — テストファイル削除
4. **メモリ**:
   - [[charinfo-cp932-next]] — 廃止決定でクローズ、`memory/charinfo-cp932-next.md` 削除 + MEMORY.md 索引行削除

### 依存チェック
- Grep 済: `CharacterDescriber` の呼び出しは `MainForm.cs` のみ
- `PositionFormatter`(現在位置読み)は独立=影響なし
- App 側 `AnnouncePosition`(`Ctrl+Alt+P`)は削除対象外(継続)

### リスク・注意
- **低**: 純粋な削除。副作用は削除箇所のみ
- ビルド後、`obj/` 内の参照が残らないよう `dotnet clean` を推奨

### DoD
- `dotnet build` が 0 警告
- `dotnet test` が全緑(削除分の期待テスト数減少)
- Ctrl+Alt+I 押下で無反応(既存の EditorControl のデフォルトキー処理へ流れる)

## 対応順序

工数昇順・低リスクから積み上げる。

| # | 項目 | 工数目安 | リスク | ブロッカー | 実装ポイント |
|---|---|---|---|---|---|
| 1 | **F-3/F-4 削除** | 小(1h) | 低 | なし | 削除のみ、MainForm/Core/テスト |
| 2 | **G-2 検索ダイアログ自動閉じ** | 小(1h) | 低 | なし | FindReplaceDialog に `_isReplaceMode` フィールド追加 |
| 3 | **G-3 置換して次へ改修** | 中(2-3h) | 中 | なし | SearchController.ReplaceOne 改修+回帰テスト |
| 4 | **I-5 タブ→エディタ直接フォーカス** | 中(3-4h) | 中 | なし | DocumentManager + MainForm + 実機 SR 検証 |
| 5 | **C-2 SaveAs 新ダイアログ** | 大(1 日) | 中〜高 | なし | SaveAsDialog 新規 + FileController 拡張 |

### 並列化余地
- 1/2 は完全独立(異なるファイル)=並列可
- 3/4 は SearchController と DocumentManager で独立=並列可
- 5 は最大工数のため単独最終

### ブランチ戦略
- **単一 feature ブランチ**: `feature/p7-followups` を main から切り出す
- 5 項目を上記順序で個別コミット
- 各項目完了ごとに `dotnet test` 全緑・0 警告確認
- I-5 と C-2 完了後は実機 SR 検証(PC-Talker/NVDA/ナレーター)
- 全完了 → 別エージェントレビュー → main へ no-ff マージ(既存フェーズ運用に合わせる)

## 関連メモリ

- [[custom-editcontrol]] — P0〜P8 プロジェクト
- [[p7-post-checklist-followups]] — 本対応のインプット
- [[charinfo-cp932-next]] — F-3/F-4 廃止でクローズ予定
- [[pctalker-speech-control]] — SR 依存項目は本対応対象外(別集約)
