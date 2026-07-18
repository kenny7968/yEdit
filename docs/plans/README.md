# docs/plans — 設計書・実装計画のアーカイブ

このディレクトリは yEdit の**実装当時の判断記録(decision records)**を集めたものである。個々のファイルは書かれた時点の設計意図・トレードオフ・スコープ判断を残すためのスナップショットであり、現行コードの仕様書ではない。

## 読み方の注意

- **現行実装と乖離している可能性がある。** 各ファイルは commit された時点の情報で凍結されており、その後のリファクタ・機能追加・削除は基本的に反映されない。現在の挙動を知りたい場合は該当ソース(`src/`)およびテスト(`tests/`)を正とする。
- **索引は意図的に作っていない。** 92 本超の設計書をカタログ化しても、いずれ更新が追いつかず陳腐化する。個々の設計書はファイル名の日付(`YYYY-MM-DD-<topic>-design.md` / `<topic>.md`)で辿ればよい。ペアで `-design.md`(設計) と実装計画 が並ぶ命名規則。
- **廃止された機能・非採用となった判断は下記に集約している。** 本文中に「これは廃止された」と書いていなくても、下記に該当があれば「歴史記録」として扱ってよい。

## 廃止された機能 / 非採用となった判断

現行 main に組み込まれていない主要なトピックを、判断根拠つきで残しておく。

### PC-Talker 専用サポート — 廃止(2026-07-13)

- 該当設計書: `2026-06-28-sr-speech-design.md` / `2026-06-29-sr-announcer-redesign-design.md` / `2026-07-13-pctalker-removal-design.md`
- 関連レポート: `../report-pctalker-speech/`
- 経緯: PC-Talker 向けの検出・直叩き発声(`PCTKPReadW` / `PCTKCGuide`)・空行能動発声トリガ・イベント土台を全削除し、全 SR 共有の汎用 UIA 経路のみを残した。空行未発声バグの調査が長期化し、サポート継続コストが実利を上回ると判断したもの。将来復活する場合の技術メモは削除コミットから逆引き可能。

### Scintilla 編集エンジン → 自作 EditorControl への pivot(2026-07-13)

- 該当設計書: `2026-06-26-yedit-production-architecture-design.md` / `2026-07-05-custom-editcontrol-design.md`
- 経緯: NVDA/PC-Talker の二系統 SR 適応の根本原因が Scintilla のクラス名正規化(`"Scintilla"` → ネイティブオーバーレイ)であることが確定し、自作コントロールへ全面置換(P0〜P8)。以前の設計書に登場する `Scintilla/Lexilla/WebView2Loader` のネイティブ DLL 話や `runtimes\` フォルダの扱いは、この pivot 以前の記録である(`2026-07-03-release-workflow-design.md` 冒頭 note 参照)。

### Inno Setup インストーラー — 差し戻し(2026-07-03)

- 該当コミット: revert `8ff0255`(元マージ `b82d084`)
- 経緯: `.iss` インストーラー・起動引数対応・スモークテスト等を一度マージしたが、コード署名なしでは Windows Defender SmartScreen に阻まれることが判明したため全て revert。現在は zip 配布のみ。証明書入手後に復活する場合は元マージからチェリーピック可。

### クラウドメモ連携(OneNote バックエンド) — スパイク未着手

- 該当設計書: main には未マージ(feature/onenote-sync-design ブランチに `ac41cec` で保管)
- 経緯: Graph v1.0 / 個人 MSA / HttpClient 直+MSAL に確定したが、空行往復の Go/No-Go スパイクが未着手のため、本体には組み込まれていない。

## 現行有効な運用ドキュメント

このディレクトリの外にある、生きているドキュメントへの参照:

- `../lint-format-setup.md` — clone 後の開発環境セットアップ(csharpier / husky)
- `../../説明書/` — 利用者向け説明書(リリース zip に同梱)
- `../../SECURITY.md` — 脆弱性報告先・対応方針
- `../../tools/README.md` — スクリプト類の役割・実行タイミング
