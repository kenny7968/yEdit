# CSVモード 設計

- 日付: 2026-06-28
- ブランチ: `feature/add_csv_mode`
- 状態: 設計合意済み（実装計画は別ドキュメント `2026-06-28-csv-mode.md` で作成）

## 1. 目的・スコープ

`.csv` ファイルを開いたときに、テキスト編集はそのまま使える状態を保ちつつ、
セル単位のナビゲーションと読み上げを重ねる「CSVモード」を新設する。
スクリーンリーダー（SR）利用者が表データを行・列で把握できるようにする一方、
晴眼/弱視ユーザの通常編集も一切損なわない（メモリ方針 [[yedit-sighted-users-first-class]]）。

### 受け入れ条件（DoD）

- `.csv` を開くと、CSVとしてパースできれば CSVモードON、パースエラーなら通常テキストとして開く
- CSVモードのON/OFFを音声でアナウンスする
- CSVモード中、以下のコマンドが動作する:
  - `Ctrl+Shift+↑` / `↓`: 1つ上/下のセルへ移動
  - `Ctrl+Shift+←` / `→`: 1つ左/右のセルへ移動
  - `Ctrl+Shift+C`: 現在カーソルのある列の一番上のセル（見出し）の内容を読み上げ
- Core にパーサ・ナビゲーション・読み上げ文字列のテストがある

## 2. 設計判断（合意済み）

| 論点 | 決定 |
|---|---|
| 相互作用モデル | **オーバーレイ方式**。Scintillaの生テキストを編集しつつ、カーソルを各セルへ移動・選択して読ませる（専用グリッドビューは作らない） |
| パーサ | **Coreに自作**（オフセット追跡型・RFC4180準拠）。外部依存を増やさず、元テキスト上の文字スパンを直接返す |
| 移動時の読み上げ | **内容→位置の順**（例「田中 2行2列」）。空セルは「空 2行2列」 |
| 見出し読み上げ（Ctrl+Shift+C） | 現在列の先頭セルを**読み上げのみ**（カーソル移動なし） |
| 手動トグル | CSV(&C)メニューに **ON/OFF トグルを含める**（自動判定が外れた時の救済・任意拡張子での利用） |
| カンマ無し.csv（実質1列） | **モードON**（「パース成功ならON」の仕様に忠実。左右移動は端アナウンスのみで無害） |

## 3. アーキテクチャ

既存方針（ロジックは `yEdit.Core` に寄せテストで網羅・UIは薄く）と、マークダウンプレビュー実装を手本にする。

```
yEdit.Core/Csv/
  CsvFile.cs              … .csv 判定（MarkdownFile.cs の双子）
  CsvParser.cs            … RFC4180パーサ（オフセット追跡）→ CsvDocument
  CsvDocument.cs          … パース結果モデル＋ナビゲーション（FindCell/MoveCell/Header）
  CsvAnnounceFormatter.cs … 「内容 R行C列」等の読み上げ文字列生成（PositionFormatter方式）
yEdit.App/
  CsvController.cs        … SearchController/GrepController と同型の薄い配線
  DocumentState.cs        … bool CsvMode 追加（タブ毎）
  MainForm.cs             … ctor配線 / LoadInto検出 / ProcessCmdKey / CSVメニュー
tests/yEdit.Core.Tests/Csv/
  CsvParserTests.cs / CsvNavigationTests.cs / CsvAnnounceFormatterTests.cs
```

## 4. データモデル（Core）

### CsvField

```
public sealed class CsvField
{
    public int Start;    // 元テキスト上のUTF-16開始オフセット（引用符込み）
    public int Length;   // 同・長さ（SelectCharRange に直結）
    public string Value; // 引用符を外し ""→" 復元した論理値（読み上げ用）
}
```

- `Start/Length` は **生テキスト上のスパン**。`ed.SelectCharRange(Start, Length)` でそのままセルを選択でき、SRに読ませられる。
- `Value` は読み上げ用の論理値。

### CsvDocument

```
public sealed class CsvDocument
{
    public IReadOnlyList<IReadOnlyList<CsvField>> Rows;
    public bool Ok;   // パース成否

    public (int row, int col) FindCell(int caretOffset);    // キャレットを含む/直近セル
    public (int row, int col)? MoveCell(int row, int col, Direction dir); // 端は null
    public CsvField? GetField(int row, int col);
    public CsvField? Header(int col);   // row 0 の同列
}
```

### パーサ仕様（RFC4180準拠）

- 区切り = カンマのみ（v1。TSV/セミコロンは将来拡張）。
- 引用符 `"..."`、セル内カンマ、`""`エスケープ、**引用符内の改行（CRLF/LF/CR）**に対応。
- **行＝論理行**: セル内改行を含む場合、物理行をまたいでも1論理行とする。
  `Ctrl+Shift+↑/↓` は論理行間を移動（物理的には複数行飛ぶことがある）。
- **パース失敗の定義**: 引用符が閉じない等の不正のみ `Ok=false`。
  カンマが1つも無い `.csv` は「1列の行が並ぶ」正常パースとして `Ok=true`。

### ナビゲーション設計

- 現在セルは **キャレットの文字オフセットから毎回 `FindCell` で導出**する。
  → 自由編集で本文が変わっても状態が陳腐化しない（「desired column」は保持しない）。
- **端**: それ以上動けない方向は移動せず端アナウンス。
- **不揃いの行**（列数が異なる）: 上下移動先の列数が足りなければ、その行の末尾セルにクランプし、実際の位置を読む（v1の割り切り）。

## 5. 操作と読み上げ（App: CsvController）

各コマンドの流れ:
`ed = _docs.Active?.Editor` → CsvMode確認 → `ed.SnapshotText` を毎回パース →
キャレットから現在セル導出 → 目標算出 → 移動なら `SelectCharRange` + アナウンス。

| キー | 動作 | 読み上げ |
|---|---|---|
| `Ctrl+Shift+↑/↓` | 上/下の論理行の同列セルへ移動・選択 | `内容 R行C列`（例「田中 2行2列」）／空セル「空 R行C列」 |
| `Ctrl+Shift+←/→` | 左/右のセルへ移動・選択 | 同上 |
| `Ctrl+Shift+C` | 現在列の先頭セル（見出し）を読み上げのみ（移動なし） | 見出し内容／空なら「空」 |

- 端アナウンス: 「左端です」「右端です」「先頭行です」「最終行です」。
- 読み上げ文字列（内容→位置の順）は `CsvAnnounceFormatter` で生成し、Coreでテストする。
- `Announcer.Say(...)`（底部Label + `RaiseAutomationNotification`）を使用。

## 6. オープン時の検出と配線（App: MainForm）

### LoadInto フック（`doc.Editor.Text` 設定後）

```csharp
if (CsvFile.IsCsvPath(path))
{
    var csv = CsvParser.Parse(doc.Editor.SnapshotText);
    if (csv.Ok) { doc.State.CsvMode = true;  _announcer.Say("CSVモード オン"); }
    else        { _announcer.Say("CSVとして解析できませんでした。テキストとして開きます"); }
}
```

- `DocumentState` に `bool CsvMode` を追加（タブ毎・既定 false）。

### キー配線（ProcessCmdKey）

- `Ctrl+Shift+矢印` と `Ctrl+Shift+C` を `ProcessCmdKey` に追加。
- **CsvModeがONのアクティブタブの時だけ `return true`**、それ以外は `base.ProcessCmdKey` に流して
  通常のScintilla挙動（Ctrl+Shift+矢印の選択拡張など）を温存する。

### CSV(&C) メニュー

- 新設トップメニュー `CSV(&C)`。
- 5コマンドを**表示専用ショートカット**（`ShortcutKeyDisplayString`、ProcessCmdKeyで処理＝二重発火回避。
  読み上げメニューと同方式）で列挙。
- `DropDownOpening` で `_docs.Active?.State.CsvMode` の時のみ各コマンドを活性化。
- **手動トグル**項目「CSVモード切替」を含める（自動判定の救済・任意拡張子での利用）。
- 発見性向上＋晴眼/弱視ユーザ配慮。

### Controller パターン

- `CsvController(_docs, _announcer)` を `MainForm` ctor で構築（SearchController/GrepController と同型）。
- メソッド: `MoveUp/MoveDown/MoveLeft/MoveRight/ReadColumnHeader/ToggleMode`。
- アクティブdocは毎回 `_docs.Active` で解決。

## 7. テスト方針

- **Core重点**（本プロジェクトの強み・既存201テスト緑）:
  - `CsvParserTests`: 引用符／セル内カンマ・改行／`""`／不正→`Ok=false`／**オフセット正確性**
  - `CsvNavigationTests`: `FindCell`／`MoveCell`（端・不揃い行）／`Header`
  - `CsvAnnounceFormatterTests`: 「内容 R行C列」「空 …」「端です」系文字列
- **App配線**（`LoadInto`検出・`ProcessCmdKey`・メニュー活性）は薄く、**実機SR検証**で確認
  （既存の実機SR検証フェーズ方針に合流）。

## 8. 既知の割り切り / 将来拡張（v1スコープ外）

- 区切りはカンマ固定（TSV/セミコロン自動判定は将来）。
- 不揃い行は末尾クランプ（「desired column」保持なし）。
- ナビゲーション毎に全文を再パース（大容量ファイルはキャッシュ最適化が将来課題）。
- セル編集ダイアログ等の高度な表編集UIは作らない（オーバーレイに徹する）。
```
