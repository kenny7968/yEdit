# M6 SR利便機能＆PC-Talker精緻化 — 設計ドキュメント

最終更新: 2026-06-26 / 作業ディレクトリ: `<repo>` / .NET 9 / Windows 11・日本語環境

前提資料: `docs/plans/2026-06-26-yedit-production-architecture-design.md`（§3 M6・§4.1 鉄則）、旧版知見（メモリ `phase6-followups` / `charinfo-cp932-next`）。

---

## 0. ゴール

SR（PC-Talker/NVDA）での日常編集を助ける照会機能を足す。**座標API は今回見送り**（ユーザー合意。
RPC スレッド×SCI_* の鉄則整合が難所で、HANDOFF いわく PC-Talker は 0 矩形でも読めるため必須でない）。

**DoD（今回）**: 実装＋Core 自動テスト＋別エージェントのコードレビュー通過＋ビルド 0 警告。実機 SR 検証はユーザーが後日。

---

## 1. アーキテクチャ（既存資産の活用）

```
Announcer（App・基盤）= 底部 Label の RaiseAutomationNotification（M3/M4 で実証済の SR 通知）
   ↑ MainForm が照会ホットキー/モード切替時に呼ぶ。M4 のジャンプ時 SR 通知（申し送り）にも転用。
Core（SR非依存・テスト可能）:
   CharacterDescriber … 1 文字→日本語説明（全角/半角空白・かな/カナ/半角カナ/全角英数/漢字/制御/記号/サロゲート・U+XXXX）
   PositionFormatter  … (行,総行,桁,総文字,選択数) → 読み上げ文字列
App:
   照会ホットキー・行ジャンプダイアログ・Insert で上書き/挿入トグル＋読み上げ。すべて ProcessCmdKey で横取り。
```

鉄則順守: 既存の `SnapshotText`/`GetSelectionCharRange`（UI スレッド・UTF-16 オフセット）から情報を取り、Core の純関数で整形して Announcer で通知する。**新規の SCI_* 呼び出し・別スレッド処理は無い。**

---

## 2. 機能と既定ホットキー（ProcessCmdKey で横取り＝エディタに食われない）

| 機能 | キー | 読み上げ例 |
|---|---|---|
| 現在位置（＋文字数） | `Ctrl+Alt+P` | 「行 12 / 全 340、桁 5、文字数 89012」（選択中は「、選択 7 文字」付加） |
| 文字情報 | `Ctrl+Alt+I` | 「全角スペース」「ひらがな あ」「全角 Ａ」「タブ」「制御文字 U+0001」「😀 (U+1F600)」 |
| 行へ移動 | `Ctrl+G` | 番号入力ダイアログ→移動。「行 N」 |
| 挿入/上書き切替 | `Insert` | Overtype をトグルし「上書きモード」「挿入モード」 |

- メニューは「読み上げ(&R)」を新設（現在位置/文字情報/行へ移動）。キーは `ProcessCmdKey` で処理し、メニューは
  `ShortcutKeyDisplayString` で表示のみ（M3 の F3 と同方式・二重発火回避）。
- 文字情報はキャレット位置のコードポイント（サロゲート結合）を説明。末尾なら「文書の末尾」。

## 3. Core 新規（`yEdit.Core.Reading`）

```csharp
public static class CharacterDescriber
{
    // text[index] のコードポイント（サロゲート考慮）を日本語で説明。
    public static string DescribeAt(string text, int index);
    public static string Describe(int codePoint);
}

public static class PositionFormatter
{
    public static string Format(int line, int totalLines, int column, int totalChars, int selectionLength);
}
```

`CharacterDescriber` の分類: 半角/全角/ノーブレークスペース・タブ・改行/復帰・その他制御（U+XXXX）／ひらがな／カタカナ／
半角カタカナ／全角英数記号（U+FF01–FF5E）／CJK 漢字／ASCII 印字可（素の文字）／その他（文字＋U+XXXX）。

## 4. 空行の PC-Talker 読み

UIA プロバイダは既に空行を長さ 0 で公開（`TextRangeProvider` の Line 単位＝`LineStart`/`LineEndNoBreak`、`Move` は
単位スパン保持）。**新規コードは無し**。実機 PC-Talker での最終決着はユーザー検証（DoD 後送り）。

## 5. テスト（Core）

- `CharacterDescriber`: 半角/全角/ノーブレークスペース・タブ・改行・制御・ひらがな/カタカナ/半角カナ・全角英数・漢字・ASCII・記号・サロゲート（絵文字）・空/範囲外。
- `PositionFormatter`: 選択有無・各値の整形。

## 6. 非対象（follow-up）

- 座標API（`GetBoundingRectangles`/`RangeFromPoint`）＝見送り（M8/M9 で安全なキャッシュ設計後）。
- CP932 外文字の明示（「Shift_JIS では保存不可」等）＝`charinfo-cp932-next` の知見で後続（describer を純粋に保つため今回は載せない）。
- 結合文字グラフェム単位の説明（今回はコードポイント単位）。
- ホットキーのカスタマイズ（M9+）。
