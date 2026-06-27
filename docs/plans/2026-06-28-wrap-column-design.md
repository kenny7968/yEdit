# 折り返し桁数の設定（指定文字数での表示折り返し）— 設計

作成日: 2026-06-28
対象: yEdit（Scintilla 編集エンジン＋SR適応 a11y）
前提資料: `docs/plans/2026-06-26-m7-settings-design.md`（設定層・`EditorAppearance.Apply`）、
`docs/plans/2026-06-26-yedit-production-architecture-design.md`（アーキテクチャ・a11y の鉄則）、
`src/yEdit.Core/Settings/AppSettings.cs` / `src/yEdit.App/SettingsDialog.cs` /
`src/yEdit.App/EditorAppearance.cs` / `src/yEdit.Editor/ScintillaHost.cs` / `src/yEdit.Editor/Sci.cs`。

## 0. 目的とスコープ

設定ダイアログに「指定文字数で折り返す」チェックボックスと折り返し桁数の入力欄を新設し、
桁数が指定されていれば編集画面で**指定桁数の表示折り返し**を行う。

確定した仕様（ブレインストーミングでの合意）:

- **表示だけの折り返し**。本文・保存内容は一切変更しない（サクラエディタ「指定桁で折り返す」と同等）。
- **半角換算の桁数**（全角＝2桁／半角＝1桁）。`SCI_TEXTWIDTH` で測る半角1文字のピクセル幅を桁単位とする。
- **任意の文字で折る**（`SC_WRAP_CHAR`）。指定桁ちょうどで折り、英単語も途中で折れる。
- **初期値 80 桁**、入力範囲 10〜1000 桁。
- **全タブ共通・永続化**（既存設定と同じ扱い）。
- **上下矢印キーは表示行単位**（Scintilla 既定のまま。追加のキー処理はしない）。

## 1. 技術的前提（Scintilla の制約と採用方式）

Scintilla の表示折り返し（`SCI_SETWRAPMODE`）は **テキスト描画領域の幅** で折り返す。
「N 桁で折り返す表示」というネイティブ API は無い（桁/幅指定で改行できるのは
`SCI_LINESSPLIT` ＝実改行を挿入するハードラップのみ）。

したがって表示だけで N 桁折り返しを実現するには **描画領域の幅を N 桁分に制限** する。

採用方式＝**案A：右マージンで描画幅を制限**。
`SCI_SETMARGINRIGHT`（テキスト右側の空白マージン）でテキスト領域を縮め、
ウィンドウが目標幅より広い分を空白に充てる。ウィンドウが狭いときは右マージン0でウィンドウ幅折り返し。

- 利点: エディタは従来どおり `Dock=Fill`。ウィンドウが広いと右に余白／狭いとウィンドウ幅で折る、
  という日本語エディタの「指定桁折り返し」と同じ自然な挙動。既存の `EditorAppearance.Apply` 経路に乗る。
- 欠点: リサイズ／フォント変更時に右マージンを再計算する必要。縦スクロールバー幅等の誤差で
  右端が ±1 桁ずれ得る（表示専用機能として許容し、実機で詰める）。

不採用: 案B（コントロール幅を固定＝桁は正確だが横スクロールが出て弱視/SRに不便）、
案C（ハードラップ＝本文を書き換えるため今回の「表示だけ」要件に合わない）。

## 2. 設定モデル（`yEdit.Core/Settings/AppSettings.cs`）

チェック状態と桁数値を分けて保持する（OFF でも最後の桁数を覚える）。

```csharp
/// <summary>編集画面で指定桁数の表示折り返しを行うか（保存内容は不変）。</summary>
public bool WrapColumnEnabled { get; set; } = false;
/// <summary>表示折り返しの桁数（半角換算・全角=2桁）。既定80・範囲10〜1000。</summary>
public int WrapColumn { get; set; } = 80;
```

永続化は既存 `SettingsStore`（JSON ラウンドトリップ）にそのまま乗る。

## 3. 設定ダイアログ UI（`yEdit.App/SettingsDialog.cs`）

「既定の改行(&L)」の下に1行追加する。

- チェックボックス `指定文字数で折り返す(&W)` → `WrapColumnEnabled`。
- 桁数入力 `NumericUpDown`（ラベル `折り返し桁数(&K):`、Min=10 / Max=1000 / 既定値=`s.WrapColumn`）。
- アクセシビリティ: 既存作法どおりラベル＋アクセスキー＋`TabIndex` を付与。
  `NumericUpDown` は SR が値を読み、上下キーで増減できる。
- チェック OFF 時は桁数入力を `Enabled=false`、チェック ON で有効化（`CheckedChanged` で連動）。
- 公開プロパティ `bool WrapColumnEnabled` / `int WrapColumn` を追加。OK 後に `OpenSettings` が読む。
- `TabIndex` は既存行（フォント0..1／配色2..3／文字コード4..5／改行6..7）の後ろ（8..10 目安）に割り当て、
  OK/キャンセル群（`TabIndex=100`）より前にする。

## 4. 適用フロー

新規タブ生成（`CreateEditor`）と設定変更（`OpenSettings`）の両方が通る共通経路
`EditorAppearance.Apply(editor, settings)` に折り返し適用を足す。これで全タブへ自動的に行き渡る。

```
OpenSettings（OK後・全 doc ループ） / CreateEditor（新規タブ）
   └─ EditorAppearance.Apply(ed, settings)
         （フォント→StyleClearAll→色 …適用後）
         └─ ed.ApplyWrapColumn(settings.WrapColumnEnabled ? settings.WrapColumn : 0)
```

`OpenSettings` に追記:
```csharp
_settings.WrapColumnEnabled = dlg.WrapColumnEnabled;
_settings.WrapColumn        = dlg.WrapColumn;
```
（`foreach ... EditorAppearance.Apply(...)` は既存のまま再利用。）

### `ScintillaHost.ApplyWrapColumn(int columns)`（新規・`yEdit.Editor`）

- `columns <= 0`（無効）: `WrapMode = WrapMode.None`、右マージンを既定（1px）に戻す。`SizeChanged` 購読解除。
- `columns > 0`（有効）: 範囲クランプ（10〜1000）→ `WrapMode = WrapMode.Char`。
  `SCI_TEXTWIDTH(STYLE_DEFAULT, "0")` で半角1文字幅 `halfWidthPx` を測定し、`targetPx = columns * halfWidthPx`。
  `SizeChanged` を一度だけ購読し、`RecomputeWrapMargin()` を即時＋リサイズ毎に呼ぶ。
- `RecomputeWrapMargin()`:
  - 現在のテキスト領域幅 `textAreaPx`（= クライアント幅 − 左セレクタマージン群 − 左テキストマージン）を求める。
  - `right = WrapGeometry.RightMargin(textAreaPx, targetPx)`（= `max(0, textAreaPx - targetPx)`）。
  - `SCI_SETMARGINRIGHT(0, right)` を送る。
- フォント/テーマ変更後は `Apply` が再度通り `halfWidthPx` も再測定される（順序: フォント適用 → `ApplyWrapColumn`）。
- 鉄則順守: `DirectMessage`/`SCI_*` は **UI スレッドからのみ**。RPC スレッドのスナップショット応答には触れない。

### `Sci.cs` 追加定数

```csharp
public const int SCI_SETMARGINRIGHT = 2157;  // (unused, pixelWidth) テキスト右側の空白マージン（標準 Scintilla: SETMARGINLEFT=2155/GETMARGINLEFT=2156/SETMARGINRIGHT=2157/GETMARGINRIGHT=2158）
public const int SCI_TEXTWIDTH      = 2276;  // (style, const char*) 文字列のピクセル幅
public const int STYLE_DEFAULT      = 32;
```
`WrapMode` は ScintillaNET のプロパティ（`WrapMode.None` / `WrapMode.Char`）を使う。

## 5. 桁幅・右マージンの算術（`yEdit.Core`・テスト対象）

Scintilla 依存のない純関数を Core に切り出し xUnit でテストする
（Editor/App の Scintilla グルーは実機検証側）。

```csharp
public static class WrapGeometry
{
    /// <summary>桁数×半角幅 → 目標テキスト領域幅(px)。</summary>
    public static int TargetWidthPx(int columns, int halfWidthPx) => columns * halfWidthPx;

    /// <summary>テキスト領域幅と目標幅から右マージン(px)。広い分だけ空白に充てる。</summary>
    public static int RightMargin(int textAreaPx, int targetPx) => Math.Max(0, textAreaPx - targetPx);

    /// <summary>桁数を許容範囲にクランプ（範囲外/破損設定対策）。</summary>
    public static int ClampColumns(int columns) => Math.Clamp(columns, 10, 1000);
}
```

誤差注記: 縦スクロールバー幅・左マージン・丸めで右端が ±1 桁ずれ得る。
表示専用機能として許容し、実機で詰める（必要なら定数オフセットで較正）。

## 6. アクセシビリティ挙動

- UIA スナップショットは **論理全文（UTF-16）** のまま。表示折り返しはキャレット位置・選択・スナップショットを
  一切変えないため、**SR の文字／行読みは論理行ベースのまま不変**
  （PC-Talker＝自作 UIA、NVDA＝ネイティブ ともに影響なし）。
- 上下矢印は Scintilla 既定の **表示行単位**（長い論理行は途中の折り返し位置で停まる）。追加のキー処理はしない。
- `Home`/`End` は論理行の先頭/末尾（Scintilla 既定）。

## 7. エラー処理・エッジケース

- 破損設定（桁数 0/負/極大）: `ClampColumns` で 10〜1000 にクランプ。0 以下は無効扱い。
- プロポーショナルフォント: 半角幅は代表文字で測るため厳密一致しない（桁折り返しは等幅前提と注記）。
- ハンドル未生成: `Apply` は既存どおりハンドル生成後の経路に乗せる（`SizeChanged` 初回でも再計算される）。
- `SizeChanged` 二重購読防止: 購読フラグ（`_wrapSizeHooked`）で一度だけ購読。無効化時は購読解除せず、
  `RecomputeWrapMargin` 側の `_wrapColumns<=0` 早期 return で無害化する（再有効化時の再購読も防げる。
  ハンドラはコントロール自身のイベントで自己参照のため寿命はコントロールと一致し、リークしない）。

## 8. テスト戦略

- Core（xUnit）:
  - `AppSettings` 既定値（`WrapColumnEnabled=false` / `WrapColumn=80`）。
  - `WrapGeometry`（`TargetWidthPx` / `RightMargin` の境界、`ClampColumns` の範囲外）。
  - 設定の保存/復元ラウンドトリップ（新キーが往復すること）。
- Editor/App（実機 SR 検証）:
  - 折り返し ON/OFF・桁数変更の即時反映、フォント変更後の再計算、ウィンドウリサイズ追従。
  - 上下矢印が表示行単位で動くこと、SR の行/文字読みが論理行のまま不変であること。
  - ビルド 0 警告。

## 9. 変更ファイル一覧

- `src/yEdit.Core/Settings/AppSettings.cs` … `WrapColumnEnabled` / `WrapColumn` 追加。
- `src/yEdit.Core/Settings/WrapGeometry.cs`（新規）… 純算術。
- `src/yEdit.App/SettingsDialog.cs` … チェックボックス＋`NumericUpDown` 行、公開プロパティ。
- `src/yEdit.App/MainForm.cs` … `OpenSettings` で新キーを反映（適用ループは既存）。
- `src/yEdit.App/EditorAppearance.cs` … `Apply` 末尾で `ApplyWrapColumn` 呼び出し。
- `src/yEdit.Editor/ScintillaHost.cs` … `ApplyWrapColumn` / `RecomputeWrapMargin` 追加。
- `src/yEdit.Editor/Sci.cs` … `SCI_SETMARGINRIGHT` / `SCI_TEXTWIDTH` / `STYLE_DEFAULT` 追加。
- `tests/yEdit.Core.Tests/Settings/WrapGeometryTests.cs`（新規）ほか設定テスト追記。
