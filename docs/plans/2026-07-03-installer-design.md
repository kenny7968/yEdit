# yEdit Windows インストーラー設計

日付: 2026-07-03
ステータス: 承認済み

## 目的

zip 配布(手動展開)に加えて、SR 利用者がワンステップで導入できる Windows インストーラーを提供する。
既存の zip 配布は従来どおり併存する。

## 技術選定

**Inno Setup 6** を採用する。

| 候補 | 判断 |
|------|------|
| Inno Setup 6 | 採用。日本語UI公式対応・ウィザードが標準 Win32 コントロールで PC-Talker / NVDA で読める・`PrivilegesRequired=lowest` でユーザー単位/UACなしが素直・GitHub Actions windows ランナーにプリインストール済み |
| WiX (MSI) | 見送り。企業GPO配布向け。記述量・保守コストが個人向け配布に見合わない |
| MSIX | 見送り。コード署名証明書が実質必須・「送る」メニュー登録不可 |

## インストール仕様

- **範囲**: ユーザー単位。`PrivilegesRequired=lowest`、UAC 昇格なし
- **インストール先**: `%LOCALAPPDATA%\Programs\yEdit`
- **中身**: 既存リリースCIと同一のフレームワーク依存 win-x64 `dotnet publish` 出力
- **セットアップ項目**:
  - スタートメニューショートカット(必須)
  - デスクトップショートカット(チェックボックス・既定オン)
  - 「送る」メニュー登録(`shell:sendto` に yEdit.lnk)
  - `.txt` / `.md` / `.csv` の「プログラムから開く」候補登録(HKCU の OpenWithProgids。既定アプリは奪わない)
  - アンインストーラー(Inno 標準)
- **アップグレード**: AppId 固定 GUID による上書きインストール。実行中の yEdit は Inno 標準の RestartManager 検出で終了を促す(アプリ側に専用 Mutex はない)
- **アンインストール時**: `%APPDATA%\yEdit`(settings.json / backups)は削除しない(ユーザーデータ保護)。レジストリ登録(関連付け・送る)は削除する

## .NET ランタイムの扱い(決定)

- 自己完結ビルドは**採用しない**(フレームワーク依存を維持)
- インストーラー起動時に .NET 9 **デスクトップ**ランタイム x64 の有無をレジストリ
  (`HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App`)で検査
- 未検出なら日本語メッセージで案内し、「はい」で Microsoft 公式ダウンロードページをブラウザで開く。
  インストール自体は続行可能(アプリ導入→後からランタイムでも成立するため中断しない)
- WebView2 はマークダウンプレビュー限定の依存のため検査しない(リリースノート記載を維持)

## CI 統合

- `release.yml` に ISCC コンパイルステップを追加(ランナーにプリインストール済みの `ISCC.exe` を使用)
- バージョンはタグから抽出した値を `/DAppVersion=` で .iss に注入
- 生成物 `yEdit-vX.Y.Z-setup.exe` を zip とともに GitHub Releases に添付
- リリースノートの SHA256 は zip / setup.exe の両方を付記
- **スモークテスト**をリリース前ゲートに追加: CI 上で `/VERYSILENT` インストール → ファイル配置検証 → サイレントアンインストール → 削除検証

## 申し送り

- 実機 SR(PC-Talker / NVDA)でウィザードが読めることの手動確認
- コード署名は当面なし(SmartScreen 警告は既知の制約として受容)
