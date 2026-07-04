# MSAA 抑制の撤去 設計（2026-07-04）

docs/plans/2026-07-04-validate-uia-handoff.md の引き継ぎ (3)（＋同一ブランチ推奨の (1)）の実施設計。
引き継ぎ (2)（SR 非稼働時の既定経路退行）は性質が異なる挙動再設計のため**別ブランチ**で行う。

## 背景（要点のみ・詳細は handoff 文書）

NVDA 経路のクライアント MSAA 抑制（`WM_GETOBJECT(OBJID_CLIENT)` に 0 を返す）は M1 以来の
プローブ時代の持ち越しで、2026-07-04 の表面計測＋実機 NVDA 音声確認により**不要と確定**した
（抑制なしの方が accName に本文が漏れずむしろクリーン）。これを撤去し、
「NVDA が起動しているときだけ行うアクセシビリティ処理」を完全解消する。

## 変更内容

1. `src/yEdit.Editor/ScintillaHost.cs`
   - `SuppressClientMsaa` プロパティを削除。
   - `WndProc` の `OBJID_CLIENT` 分岐を削除（「PC-Talker を UIA ブリッジへ誘導する試み」という
     プローブ時代の残骸コメントごと）。
   - `ApplySrAdaptation` は `ServeUiaProvider = !useNativeReading;` のみとし、検証用の
     `SuppressClientMsaa = false` 固定（validate/uia の未コミット残置）もここで解消。
     doc コメントを現仕様（NVDA 経路=UIA プロバイダ不提供／それ以外=提供）へ更新。
2. `src/yEdit.Editor/NativeMethods.cs` — 未使用になる `OBJID_CLIENT` 定数を削除。
3. `src/yEdit.Core/Speech/SrRoute.cs` — PC-Talker 経路の doc コメントから
   「ネイティブ MSAA 抑制」を削除（引き継ぎ (1)。実装が正でコメントが誤り）。
4. `docs/HANDOFF-scintilla-uia.md` — 冒頭サマリ（Session 3 の箇条書き）と §13.4 の表から
   `SuppressClientMsaa` を除去し、「2026-07-04 の実測で不要と確定し撤去済み
   （docs/plans/2026-07-04-validate-uia-handoff.md）」の注記を追加。
   §13.3「ネイティブ MSAA 抑制の試行と棄却」は実験履歴のため不変。

## 変更しないもの

- `MainForm.cs:99` の `ApplySrAdaptation` 呼び出し（シグネチャ不変）。
- SR 適応の入口を `ApplySrAdaptation` 1 メソッドに封じる構造（鉄則②の Editor 封じ込め）。
  メソッド廃止・直接プロパティ設定への変更は不採用。
- `tools/verify-msaa-client.ps1` — handoff §4 の方針どおり未コミットのまま残置。
- `installer/`・`publish/`（無関係の未追跡残骸）。

## テスト・検証

- `SuppressClientMsaa` を参照するテストは存在しない → テスト変更なし。
  ビルド 0 警告＋既存テスト全緑（289 件）を確認する。
- 実機 NVDA 音声確認は handoff §2 で完了済み（チェックリスト 6 項目 OK・「実施可」判定）のため
  再実施しない。

## フロー

`refactor/msaa-false` ブランチで実装 → 別エージェントのコードレビュー → main へ no-ff マージ。
