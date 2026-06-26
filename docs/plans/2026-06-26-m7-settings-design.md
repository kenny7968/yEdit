# M7 設定ダイアログ／外観 — 設計ドキュメント

最終更新: 2026-06-26 / 作業ディレクトリ: `<repo>` / .NET 9 / Windows 11・日本語環境

前提資料: `docs/plans/2026-06-26-yedit-production-architecture-design.md`（§3 M7）。M1 申し送り（FontSize 適用・外観）。

---

## 0. ゴール

設定ダイアログ（アクセシブル）でフォント・配色（ハイコントラスト／弱視向けテーマ）・既定の文字コード/改行を
選べるようにし、最近のファイルをメニューへ露出する。配色は**テーマプリセットのみ**（合意。SR/弱視に優しく単純）。

**DoD（今回・最終マイルストーン）**: 実装＋Core 自動テスト＋別エージェントのコードレビュー通過＋ビルド 0 警告。実機 SR 検証はユーザーが後日。

---

## 1. アーキテクチャ

```
Core（UI非依存・テスト可能）:
  AppearanceThemes … プリセット (Id, 表示名, 前景RGB, 背景RGB) ＋ ById（未知は標準へフォールバック）
  RecentFilesList  … Add(current, path, max)：先頭追加・PathKey で重複排除・上限クランプ（純関数）
  AppSettings 拡張 … Theme(id文字列, 既定"default")・RecentFiles(List<string>)
App:
  EditorAppearance.Apply(editor, settings) … Styles[Default]にフォント/色→StyleClearAll→キャレット色追従
  SettingsDialog … アクセシブルなモーダル（ラベル＋ニーモニック＋TabIndex・EncodingPickDialog の型）
     フォント(FontDialog)・配色テーマ(ComboBox)・既定文字コード(ComboBox)・既定改行(ComboBox)
  ファイルメニュー「最近のファイル(&Y)」サブメニュー（開いた順・クリックで再オープン）
  ファイルメニュー「設定(&P)...」→ SettingsDialog → 全タブへ適用＋保存
```

---

## 2. 配色テーマ（プリセット）

| Id | 表示名 | 前景 | 背景 |
|---|---|---|---|
| default | 標準（白地に黒） | #000000 | #FFFFFF |
| white-on-black | 黒地に白 | #FFFFFF | #000000 |
| yellow-on-black | 黒地に黄 | #FFFF00 | #000000 |
| green-on-black | 黒地に緑 | #00FF00 | #000000 |

`EditorAppearance.Apply`: `Styles[Default].Font=FontName / SizeF=FontSize / ForeColor / BackColor` を設定 →
`StyleClearAll()` で全スタイルへ伝播 → `CaretForeColor` を前景色に合わせる。弱視対応＝大フォント＋高コントラスト配色。

---

## 3. 最近のファイル

- `OpenFile`/開き直し/grep ジャンプ等の読み込み成功時（`State.Path` 確定時）に
  `_settings.RecentFiles = RecentFilesList.Add(_settings.RecentFiles, path, Max=10)`、`SettingsStore.Save`、メニュー再生成。
- サブメニューは開いた順（先頭が最新）。クリックで既存タブ再利用 or 読み込み（`OpenFile` 経路を共有）。
- 読み込み失敗（移動/削除）は既存のエラー表示。空なら「(なし)」を無効項目で表示。

---

## 4. 設定の適用範囲

- フォント・配色 → **全タブのエディタに即適用**（`EditorAppearance.Apply` をループ）。新規タブも `CreateEditor` で適用。
- 既定の文字コード/改行 → 以後の**新規ファイル**に適用（`NewFile` が `_settings` を読む）。
- 設定は OK 時に `SettingsStore.Save` で即永続化。
- バックアップ設定 UI は今回対象外（json 直編集可・M9+）。

## 5. テスト（Core）

- `AppearanceThemes`: 既知 Id 解決・未知 Id→default・全プリセットの色が妥当。
- `RecentFilesList`: 新規は先頭・既存は先頭へ移動して重複なし・上限クランプ・PathKey 正規化（大小/区切り）・空/自身のみ。

## 6. 非対象（follow-up）

- カスタム配色（任意 RGB）・シンタックス配色（M8）。
- バックアップ/その他詳細設定の UI 露出。
- 設定のインポート/エクスポート、フォントのライブプレビュー。
- 最近のファイルの欠落自動掃除（今回はクリック時にエラー表示）。

## 7. マージ前レビュー（M7）の結果と申し送り

5 レンズ（設定永続化/正確性/SR/統合/スレッド）で並列レビュー＋敵対的検証（確認 25 件・MAJOR 3＝同一原因・他 MINOR/NIT）。

**マージ前に修正済み:**
- **[MAJOR] 設定ダイアログのタブ順逆転**：`buttons` パネルに TabIndex 未設定で OK/キャンセルが先頭・初期フォーカスが OK に当たっていた。パネルに大きい TabIndex＋`ActiveControl=フォント変更ボタン`で「フォント→…→OK/キャンセル」順へ。
- `&F` ニーモニックの重複（ラベル＋ボタン）をボタンに一本化。
- **`RecentFilesList.Add` の max==1 オフバイワン**（2 件返す）を修正＋回帰テスト。
- **`SettingsStore.Load` で参照型フィールドの null 正規化**（`"RecentFiles": null` 等でも起動時 NRE を起こさない）。
- フォント現在値を SR へ（`_fontLabel` の AccessibleName を外し Text を読ませる＋変更ボタンの AccessibleName に現在値）。
- `EditorAppearance`：FontSize≤0 ガード（不可視防止）。**選択範囲を前景/背景反転で高コントラスト化**（弱視で選択視認）。
- 設定適用後に「設定を適用しました」を SR 通知。最近のファイル 10 件目に `&0`。既存タブ再利用時も最近を先頭へ繰上げ。`RebuildRecentMenu` で旧項目を Dispose。

**申し送り（M8 以降・実機 SR 検証）:**
- 既定文字コードが選択肢 5 種外（手編集等）だと OK で UTF-8 へ黙って戻る（実運用では常に対象内のため実害なし）。
- `FontDialog` の Bold/Italic は不採用（`AppSettings` に字形を持たない）。
- 最近のファイル登録で毎回 settings.json を再保存（変化なしでも）。
- タブ順/初期フォーカス/ニーモニックの最終確認は実機 SR で。
