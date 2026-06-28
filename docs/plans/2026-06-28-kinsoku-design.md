# 日本語の禁則処理付き折り返し整形コマンド — 設計

作成日: 2026-06-28
対象: yEdit（Scintilla 編集エンジン＋SR適応 a11y。ただし**晴眼/弱視の目視ユーザーも第一級**）
前提資料: `docs/plans/2026-06-28-wrap-column-design.md`（折り返し桁数・`EditorAppearance.Apply`・半角換算桁）、
`src/yEdit.Core/Settings/AppSettings.cs` / `src/yEdit.App/SettingsDialog.cs` /
`src/yEdit.App/MainForm.cs` / `src/yEdit.Editor/ScintillaHost.cs` /
`src/yEdit.Core/Reading/CharacterDescriber.cs`（文字分類の前例）。

## 0. 目的とスコープ

日本語の禁則処理を実装する。**行頭に来てはいけない文字**を設定で指定可能とし、デフォルトで
基本的な記号を入れておく。あわせて**行末禁則**と**句読点ぶら下げ**も実装する（ブレストでの合意）。

確定した仕様（ブレインストーミングでの合意）:

- **整形コマンド方式**＝ユーザーが明示的に実行するコマンド。指定桁で**実際に改行を挿入**し、
  禁則を考慮して折り位置を調整する（秀丸等の「禁則付き整形」と同等）。本文は変わる（**Undoは1回**）。
- **表示折り返し方式は不採用**（不可能と確定）。理由は §1。
- **対象** = 選択範囲があればそこ、無ければ文書全体。
- **桁数** = 既存設定 `WrapColumn`（半角換算・全角=2/半角=1・既定80・10〜1000）を流用。
  `WrapColumnEnabled`（表示折り返しのON/OFF）とは無関係に値だけ使う。
- **既存の改行は保持**し、`WrapColumn` を超える論理行だけを分割する（段落連結＝reflowはしない）。
- 禁則文字セットは**設定で変更可能**。既定は**保守的（括弧・句読点・区切り約物のみ。かな/長音/拗促音は入れない）**。

### 利用者観点（重要な前提訂正）

禁則処理は**目視で操作する人のための機能**。SR対応エディタだからといって全盲専用ではなく、
晴眼/弱視のユーザーも第一級の対象。「SRの読み上げに効くか」を機能価値のゲートにしない
（2026-06-28 ユーザー明示）。なお整形コマンドは**実改行を挿入する**ため、SRユーザーにも
「実際の行が読みやすく整形される」という実益がある（表示専用の折り返しと異なり論理行が実際に変わる）。

## 1. 技術的前提（なぜ表示折り返し方式が不可能か）

Scintilla の折り返し関連 API は次の3つのみで、**折り返し位置そのものをカスタマイズする手段が無い**
（公式ドキュメントで確認）:

| API | できること |
|---|---|
| `SCI_SETWRAPMODE` | WORD / CHAR / WHITESPACE / NONE の選択のみ |
| `SCI_SETWRAPVISUALFLAGS` | 折り返し記号（矢印）の表示 |
| `SCI_SETWRAPSTARTINDENT` | 継続行のインデント量 |

Sakura の表示折り返し禁則（**追い出し**＝禁則文字が行頭に来たら1文字前を含めて次行へ送る／
**ぶら下げ**＝句読点を折り返し位置の右へはみ出す）は、いずれも「折り返し位置を±1文字ずらす」操作で、
Scintilla 内部のレイアウトエンジンの管轄。yEdit は描画を Scintilla に委ねている以上、Scintilla 本体（C++）を
改造/差し替えしない限り同じことはできない。自動で実改行を挿入して保存時に剥がす代替案は、UIAスナップショット・
キャレット計算・検索・バイトオフセットが全部ずれるため却下。

→ 実装可能なのは**実改行を挿入する整形コマンド方式**のみ。本機能はこれを純アルゴリズムとして Core に実装する。

## 2. 設定モデル（`yEdit.Core/Settings/AppSettings.cs`）

禁則文字を3つの文字列で保持する（空文字にすればそのルールは無効）。

```csharp
/// <summary>行頭に来てはいけない文字（追い出し対象）。空で無効。</summary>
public string KinsokuLineStartChars { get; set; } = ")]}）］｝〕〉》」』】〗〙、。，．・：；！？";
/// <summary>行末に来てはいけない文字（開き括弧。次行へ送る）。空で無効。</summary>
public string KinsokuLineEndChars { get; set; } = "([{（［｛〔〈《「『【〖〘";
/// <summary>行末にぶら下げ可能な文字（句読点）。空で無効。</summary>
public string KinsokuHangChars { get; set; } = "、。，．";
```

- 既定は「基本的な記号」＝括弧類・句読点・中点/コロン/セミコロン・区切り約物のみ。
  Sakura の助言（拗促音・長音を入れると見た目が崩れる）に従い、かな/長音/繰り返し記号は**既定で入れない**。
- 永続化は既存 `SettingsStore`（JSON ラウンドトリップ）にそのまま乗る。
- 「禁則処理を行う」マスタ ON/OFF は設けない（コマンド自体が明示実行で、無効化したければ文字セットを空にする）。

## 3. アルゴリズム（`yEdit.Core` 純関数・TDDで確定）

Scintilla 非依存の純アルゴリズムを Core に切り出し xUnit でテストする。

### 3.1 文字幅 `EastAsianWidth`（新規 `yEdit.Core`）

```csharp
public static class EastAsianWidth
{
    /// <summary>コードポイントの半角換算幅。Wide/Fullwidth=2、結合/ゼロ幅=0、その他=1。</summary>
    public static int ColumnWidth(int codePoint);
}
```

- **2**: CJK 統合漢字・拡張（`CharacterDescriber.IsCjkIdeograph` のレンジを共有）、ひらがな/カタカナ
  （0x3040–30FF）、CJK 記号・句読点（0x3000–303F）、全角形（0xFF01–FF60）、互換漢字、Hangul ほか
  East Asian Wide/Fullwidth レンジ。
- **0**: 結合文字（0x0300–036F ほか）・ゼロ幅（0x200B–200F, 0xFEFF）。
- **1**: それ以外（ASCII、半角カタカナ 0xFF61–FF9F など）。
- タブは幅関数では扱わず、累積側で**次のタブ位置まで**進める（タブ幅は引数・既定8）。
- 注記: Unicode East Asian Width の実用的近似。厳密な EAW テーブルは将来精緻化。

### 3.2 整形 `KinsokuFormatter`（新規 `yEdit.Core`）

```csharp
public static class KinsokuFormatter
{
    public static string Format(
        string text, int columns,
        string lineStartChars, string lineEndChars, string hangChars,
        string eol, int tabWidth = 8);
}
```

処理:

1. `text` を既存の行終端（`\r\n` / `\n` / `\r`）で**論理行に分割し、終端は保持**する。
2. 各論理行の本文幅（半角換算）が `columns` 以下ならそのまま。超える行だけ以下で分割する。
3. 行頭 `start` から貪欲に幅 `columns` まで詰めて分割位置 `cut` を求める（最低1文字は前進）。
   次の順に `cut` を調整:
   1. **ぶら下げ**: `cut` 位置の文字が `hangChars` なら、桁超過でも現在行に残す（`cut` を句読点の後ろまで進める）。
   2. **行頭禁則（追い出し）**: `cut`（次行先頭になる文字）が `lineStartChars` なら `cut` を1文字戻す
      （直前文字も次行へ送る）。**直前文字も `lineStartChars` なら処理しない**（Sakura準拠・連鎖防止）。
   3. **行末禁則**: 現在行末尾（`cut` の1つ前）が `lineEndChars` なら `cut` を1文字戻す（開き括弧を次行へ）。
      同様に隣も禁則なら処理しない。
4. 戻し処理は**上限付きループ**。戻すと現在行が空になる／隣も禁則で動かせない場合は**幾何的位置で折る**
   （違反を許容）。これで**必ず1文字以上前進**し停止する（長いURL・禁則文字の連続でも無限ループしない）。
5. `cut` で `eol` を挿入し、`start = cut` として行末まで繰り返す。

不変条件: サロゲートペアの内側で折らない。結合文字（幅0）の前で折らない。
本文の文字は増減しない（挿入するのは `eol` のみ・既存改行は保持）。

## 4. 設定ダイアログ UI（`yEdit.App/SettingsDialog.cs`）

折り返し行の下に「**禁則処理**」グループを追加する。

- 単一行テキストボックス3つ:
  - `行頭禁則文字(&1):` → `KinsokuLineStartChars`
  - `行末禁則文字(&2):` → `KinsokuLineEndChars`
  - `ぶら下げ文字(&3):` → `KinsokuHangChars`
  （アクセスキーは既存と衝突しない記号/英字に最終調整。）
- ラベル＋アクセスキー＋`TabIndex`（折り返し行=8..10 の後ろ・OK/Cancel 群=100 の前）。
- SR は単一行テキストボックスの値を読み、編集できる。
- 公開プロパティ3つを追加。OK 後に `OpenSettings` が読む。

## 5. コマンド連携（`yEdit.App/MainForm.cs`・既存 ScintillaHost API で完結）

```
編集(&E) → 「折り返し整形（禁則処理）(&K)」 / ホットキー Ctrl+Shift+J
   └─ FormatWithKinsoku()
        (start,end) = ed.GetSelectionCharRange()
        whole = (start==end) → 対象=全文(0..len) / それ以外=選択範囲
        target = ed.SnapshotText.Substring(start, len)
        eol = 作業中文書の改行種別（無ければ settings.DefaultLineEnding）
        formatted = KinsokuFormatter.Format(target, settings.WrapColumn,
                       settings.KinsokuLineStartChars, settings.KinsokuLineEndChars,
                       settings.KinsokuHangChars, eol)
        if (formatted != target) ed.ReplaceCharRange(start, len, formatted)  // 1 Undo
        ed.SelectCharRange(start, formatted.Length)  // 変化箇所を選択して提示
        読み上げ通知（例「整形しました」）
```

- `ReplaceCharRange` は `SCI_REPLACETARGET`＝1アンドゥなので、整形全体が1回のUndoで戻る。
- EOL取得: 作業中 `Document` の改行種別を用いる。無ければ `settings.DefaultLineEnding`（0=CRLF/1=LF/2=CR）を
  文字列化。必要なら `ScintillaHost` に `SCI_GETEOLMODE` ゲッターを最小追加（実装計画で判断）。
- 鉄則順守: `DirectMessage`/`SCI_*` は UI スレッドからのみ（`ReplaceCharRange` は既に `InvokeRequired` ガード済）。

## 6. メニュー / ホットキー

- 編集(&E) メニュー末尾付近に「折り返し整形（禁則処理）(&K)」を追加（アクセスキー &K は編集メニュー内で未使用）。
- ホットキー **Ctrl+Shift+J**（既存ホットキー一覧と未衝突。J=整形/justify の連想）。
  既存: Ctrl+N/O/S/W, Z/Y/X/C/V/A/F/H, Ctrl+Shift+F(grep), F3/Shift+F3, Ctrl+Alt+P/I, Ctrl+G, Insert, Ctrl+Tab, Ctrl+1..9。

## 7. アクセシビリティ挙動

- 整形は**実改行を挿入**するため、UIAスナップショット・論理行・キャレットは整形結果に追従する（正しい状態に更新）。
  `ReplaceCharRange` 後に `RefreshSnapshot`/`RefreshSelection` が走る既存経路に乗る。
- 実行後に読み上げ通知を出す（既存 `Announcer`/`SrNotify` を再利用）。
- 弱視/晴眼には固定幅整形そのものが実益、SRには「実際に整った行」が実益。

## 8. エラー処理・エッジケース

- 桁数が破損（0/負/極大）: `WrapColumn` は既に 10〜1000 にクランプ運用。0以下なら整形しない（早期return）。
- 禁則文字セットが空: そのルールを無効化（追い出し/行末/ぶら下げ各々独立に）。
- 1セグメントが `columns` を超える分割不能な連続（長いURL・禁則の連続）: 幾何的位置で折り、必ず前進。
- サロゲートペア/結合文字: ペア内・結合前で折らない。
- 選択が無い＆全文が空: 何もしない。
- 整形結果が元と同一: 置換しない（無駄なUndo・イベントを避ける）。

## 9. テスト戦略

- Core(xUnit):
  - `EastAsianWidth.ColumnWidth`: 代表値（ASCII=1 / 漢字・かな・全角形=2 / 半角カナ=1 / 結合=0 / タブ累積）。
  - `KinsokuFormatter.Format`:
    - 基本: `columns` 以下は不変 / 超過行のみ分割 / 既存改行と EOL 種別（CRLF/LF/CR）の保持。
    - 行頭禁則（追い出し）: 閉じ括弧・句読点が行頭に来ない。直前も禁則なら処理せず違反許容（連鎖ガード）。
    - 行末禁則: 開き括弧が行末に来ない。
    - ぶら下げ: 句読点が桁を超えて行末にぶら下がる。
    - 優先順位: ぶら下げ vs 行頭禁則。
    - 不変条件: サロゲート非分割 / 長い連続でも前進保証（停止）。
  - `AppSettings` 既定値（3キー）＋設定の保存/復元ラウンドトリップ。
- Editor/App（実機 SR 検証）:
  - 選択範囲/全文の切替、単一Undoで全戻し、読み上げ通知、`WrapColumn` 連動、Ctrl+Shift+J。
  - ビルド 0 警告。

## 10. 変更ファイル一覧

- `src/yEdit.Core/Settings/AppSettings.cs` … 禁則3キー追加。
- `src/yEdit.Core/Text/EastAsianWidth.cs`（新規）… 半角換算幅の純関数。
- `src/yEdit.Core/Text/KinsokuFormatter.cs`（新規）… 禁則整形の純アルゴリズム。
- `src/yEdit.App/SettingsDialog.cs` … 禁則処理グループ（3テキストボックス）＋公開プロパティ。
- `src/yEdit.App/MainForm.cs` … 編集メニュー項目＋`FormatWithKinsoku`＋EOL取得、`OpenSettings` で新キー反映。
- `src/yEdit.Editor/ScintillaHost.cs` … 必要なら `SCI_GETEOLMODE` ゲッターのみ（実装計画で判断）。
- `tests/yEdit.Core.Tests/Text/KinsokuFormatterTests.cs`（新規）/ `EastAsianWidthTests.cs`（新規）/ 設定テスト追記。
