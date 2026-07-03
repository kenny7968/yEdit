# リリースワークフロー設計(タグ push → GitHub Releases)

日付: 2026-07-03
ブランチ: feature/add-github-workflow

## 目的

`v*` タグの push を契機に、配布用パッケージ(zip)を自動ビルドし、GitHub のリリースページに掲載する。

## 決定事項

| 項目 | 決定 | 理由 |
|---|---|---|
| 配布形態 | **フレームワーク依存**(`--self-contained false`) | zip が数MBと軽量。利用者は .NET 9 デスクトップランタイムを別途導入 |
| DLL の扱い | exe に埋め込まない(フォルダ配置のまま zip) | 単一ファイル publish はネイティブ DLL / WebView2 との相性問題があり不採用 |
| 対象 RID | `win-x64` のみ | ネイティブ DLL(Scintilla/Lexilla/WebView2Loader)が exe 横にフラット配置され、他アーキテクチャ分が混入しない。必要になれば matrix 化 |
| 公開方法 | 即時公開。ノートは**注釈付きタグの本文**＋動作要件フッターを自動付記 | PR を使わないローカルマージ運用のため `--generate-notes` はほぼ空になる |
| バージョン刻印 | タグ `v1.2.3` → `-p:Version=1.2.3` | 配布物のファイルバージョン/製品バージョンで確認可能に |
| PDB | `-p:DebugType=embedded` | .pdb ファイルを zip に混ぜず、クラッシュログに行番号付きスタックトレースを出す |
| リリース作成手段 | ランナー同梱の `gh` CLI ＋ 既定 `GITHUB_TOKEN`(`permissions: contents: write`) | サードパーティ Action への依存(サプライチェーン面)を避ける |
| テストゲート | `dotnet test tests/yEdit.Core.Tests -c Release` をビルド前に実行 | テストが落ちたらリリースを作らせない |

## 構成

`.github/workflows/release.yml` の 1 ジョブ(windows-latest)構成:

1. checkout
2. .NET 9 SDK セットアップ
3. Core テスト(Release)
4. タグ名からバージョン抽出(`v` 除去)
5. `dotnet publish src/yEdit.App -c Release -r win-x64 --self-contained false -p:Version=... -p:DebugType=embedded -o publish`
6. `Compress-Archive` で `yEdit-<tag>-win-x64.zip` を作成
7. リリースノート組み立て: `git fetch --tags --force` でタグ注釈を取り直し(actions/checkout が注釈を落とす既知問題対策)、タグ本文＋動作要件フッターを `notes.md` へ
8. `gh release create <tag> <zip> --title "yEdit <tag>" --notes-file notes.md --verify-tag`

## 運用

- リリース手順: `git tag -a v1.0.0 -m "変更点..."` → `git push origin v1.0.0` のみ。
- 軽量タグ(`-a` なし)でも動作要件フッターのみのノートで公開される。
- 利用者がランタイム未導入で起動した場合、OS 標準でダウンロードページ誘導ダイアログが出る。念のため毎リリースのノート末尾に動作要件(Windows 10/11、.NET 9 デスクトップランタイム x64、WebView2 ランタイム)を自動付記する。

## 申し送り(スコープ外)

- win-arm64 / x86 配布(matrix 化で追加可能)
- コード署名(SmartScreen 警告対策)
- インストーラ(M9+ の「インストーラ/配布整備」で検討)
