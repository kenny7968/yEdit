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
| 対象 RID | `win-x64` のみ | ネイティブ DLL(Scintilla/Lexilla/WebView2Loader)が exe 横にフラット配置される。必要になれば matrix 化 |
| `runtimes\` フォルダ | **削らず同梱** | RID 指定でも publish 出力に `runtimes\win-*\native` が残る(実測)。Scintilla5.NET のローダーは `runtimes\` を探索する作りのため、安全側で保持(＋約5MB) |
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
- 軽量タグ(`-a` なし)の場合、`%(contents)` は指し先コミットのメッセージを返すため、「コミットメッセージ＋動作要件フッター」のノートで公開される(リリース作成自体は成功)。
- 利用者がランタイム未導入で起動した場合、OS 標準でダウンロードページ誘導ダイアログが出る。念のため毎リリースのノート末尾に動作要件(Windows 10/11、.NET 9 デスクトップランタイム x64、WebView2 ランタイム)を自動付記する。

## ローカル検証結果(2026-07-03)

- ワークフローと同一フラグの `dotnet publish` 成功。出力=exe＋DLL 並置、`.pdb` なし、FileVersion/ProductVersion に `-p:Version` が反映(0.0.1 で確認)。合計 12.1MB。
- publish 出力から `yEdit.exe` を起動し、ウィンドウ表示(「無題 1 - yEdit」)を確認=ネイティブ DLL 読み込み OK。
- `release.yml` は pyyaml で構文検証 OK。

## 申し送り(スコープ外)

- win-arm64 / x86 配布(matrix 化で追加可能)
- コード署名(SmartScreen 警告対策)
- インストーラ(M9+ の「インストーラ/配布整備」で検討)

レビュー指摘のうち現運用では実害なしとしたもの(レビュー M-2〜M-5):

- リリース既存時の再実行/タグ再 push は最終ステップ `gh release create` が「already exists」で失敗(安全側)。作り直しはタグとリリースを削除して打ち直す。頻発するなら存在チェック→`gh release upload --clobber` 分岐を導入
- `TrimStart('v')` は先頭の `v` をすべて削る(`vv1.0`→`1.0`)。非数値タグ(例 `vNext`)は publish がエラーで大声で落ちる。厳密にするならトリガーを `'v[0-9]*'` に絞る
- `Compress-Archive` は zip 内エントリ区切りが `\` になる既知仕様。Windows 利用者(Explorer/7-Zip)には実害なし。気になれば `[System.IO.Compression.ZipFile]::CreateFromDirectory` へ
- 署名付きタグ(`git tag -s`)を導入すると `%(contents)` に PGP 署名ブロックが混入。その際は `%(contents:subject)`/`%(contents:body)` に変更
