# M4 複数ファイル横断 grep — 設計ドキュメント

最終更新: 2026-06-26 / 作業ディレクトリ: `<repo>` / .NET 9 / Windows 11・日本語環境

前提資料: `docs/plans/2026-06-26-yedit-production-architecture-design.md`（§3 ロードマップ M4・§4 横断的関心事）、`docs/plans/2026-06-26-m3-search-design.md`（検索コアの再利用元）。

---

## 0. ゴール

フォルダを再帰検索し、**SR（PC-Talker/NVDA）で読める結果一覧**から該当箇所へジャンプできる。文字コード混在に対応。Core 純ロジックは SR 非依存で単体テスト可能に保つ。各 M は独立ブランチ→別エージェントレビュー→no-ff マージ（プロジェクト規律）。

**DoD（今回）**: 実装＋Core 自動テスト＋別エージェントのコードレビュー通過＋ビルド 0 警告。実機 SR 検証はユーザーが後日実施（本 M の DoD 核だが私には実行不可のため明示的に後送り）。

---

## 1. アーキテクチャ（既存資産の再利用）

```
GrepDialog(モードレス)  ──実行──▶ GrepController ──Task.Run──▶ GrepService(Core/純ロジック)
   │  検索語/フォルダ/フィルタ/                          │   ├ TextFileService.DecodeBytes … ファイル毎に文字コード自動判定（混在吸収）
   │  再帰/大小/単語/正規表現                            │   └ TextSearcher(SearchOptions)   … 行ごとに先頭マッチ照合
   ▼                                                     ▼
GrepResultsWindow(モードレス・ListBox) ──Enter/DblClick──▶ MainForm.OpenAndSelect(path, offset, length)
                                                            （FindByPath 再利用 or 読込）→ SelectCharRange → エディタへフォーカス＋SR通知
```

**依存原則の順守**: `GrepService` は `yEdit.Core`（UI 非依存・スレッド非依存の純ロジック）。スレッド制御（`Task.Run`/`CancellationToken`/`IProgress`）と UI 反映は `yEdit.App` が担う。Core は SCI_* に一切触れない（§4.1 鉄則）。

---

## 2. grep のセマンティクス（確定）

- **行指向**: 1 ヒット＝1 マッチ行。行内の最初のマッチへジャンプ。件数＝**マッチ行数**（単一ファイル検索 M3 の「総マッチ数」とは別概念。UI で「N 行 / M ファイル」と明示）。
- **正規表現の `^`/`$`**: 行単位で照合するため行境界に自然に一致する（grep の期待どおり）。`.` は改行に一致しない（.NET 既定）。
- **オプション**: `SearchOptions`（大小・単語・正規表現）を M3 とそのまま共有。リテラルは `Regex.Escape`、単語は `\b`、大小無視は `IgnoreCase`＋`CultureInvariant`（M3 と同一の安全側）。
- **文字コード混在**: ファイル毎に `EncodingDetector` で自動判定して復号（UTF-8 BOM 有無 / Shift_JIS(932) / EUC-JP(51932) / UTF-16）。
- **バイナリスキップ**: 先頭バイト列（既定 8000 バイト）に NUL を検出したファイルは対象外（文字化け・誤検索の防止）。
- **オフセット空間**: ヒットの `AbsoluteOffset`/`Column` は **UTF-16 文字位置**（エディタの `string` index・`SelectCharRange` と同一空間）。多バイト/サロゲートでもジャンプが正確。

---

## 3. Core 新規型（`yEdit.Core.Search`）

```csharp
// 1 件のヒット（1 マッチ行）。オフセットは UTF-16 文字位置。
public sealed record GrepHit(
    string FilePath,        // 絶対パス
    int LineNumber,         // 1 始まり
    int Column,             // 1 始まり（行内 UTF-16 桁・最初のマッチ）
    string LineText,        // 行内容（EOL 除外。表示用、UI で長すぎる行は省略表示）
    int MatchStartInLine,   // 行内 UTF-16 オフセット
    int MatchLength,        // マッチ長（UTF-16）
    int AbsoluteOffset);    // ファイル先頭からの UTF-16 オフセット（ジャンプ用）

public sealed record GrepRequest(
    string Folder,
    string FilePatterns,    // ";"/"," 区切りの glob（例 "*.txt;*.cs"）。空＝全ファイル "*"
    bool Recursive,
    SearchOptions Options);

public sealed record GrepError(string Path, string Message);

public sealed record GrepProgress(int FilesScanned, int HitCount, string? CurrentFile);

public sealed record GrepOutcome(
    IReadOnlyList<GrepHit> Hits,
    int FilesScanned,
    int FilesMatched,
    IReadOnlyList<GrepError> Errors,
    bool Cancelled);
```

### GrepService

```csharp
public static GrepOutcome Search(
    GrepRequest request,
    IProgress<GrepProgress>? progress = null,
    CancellationToken cancellationToken = default);
```

処理:
1. `TextSearcher` を構築。`!IsValid` なら 0 件＋エラー 1 件（「正規表現が正しくありません」）を返す（App でも事前検証するが Core も防御）。
2. **安全な再帰列挙**: 自前の深さ優先ウォークでファイルを列挙。各ディレクトリ走査の `UnauthorizedAccessException`/`IOException` は握って `Errors` に積み、走査を継続（`EnumerateFiles(AllDirectories)` は最初の不可ディレクトリで即例外になり全体が止まるため使わない）。`Recursive=false` ならトップのみ。
3. **glob フィルタ**: 各パターンを正規表現へ翻訳（`*`→`.*`、`?`→`.`、他はエスケープ、`^...$` 固定、Windows なので `IgnoreCase`）。ファイル名がいずれかに一致すれば対象。空パターンは全一致。
4. ファイル毎: `ct.ThrowIfCancellationRequested()` ではなく**協調キャンセル**（`ct.IsCancellationRequested` を見て break し部分結果を返す＝`Cancelled=true`）。`File.ReadAllBytes` → NUL バイナリ判定（スキップ）→ `TextFileService.DecodeBytes` で復号 → 行ごとに先頭マッチを `TextSearcher.FindNext(line, 0)` で取り、ヒットを生成。1 行に複数マッチでも 1 ヒット（行頭の最初へ）。
5. 行分割は EOL（`\r\n`/`\n`/`\r`）を跨いで絶対オフセットを厳密に積算（行頭の絶対オフセット＋行内マッチ位置＝`AbsoluteOffset`）。
6. 例外処理: ファイル毎の I/O 例外・`RegexMatchTimeoutException` は `Errors` に積んで継続（grep 全体は止めない）。
7. 進捗: 一定間隔（例: 64 ファイル毎、または最後）で `progress.Report`。

### TextFileService の小拡張（既存テストを壊さない加算）

`Load(path, forcedCodePage)` の「バイト列 → 復号して `LoadedDocument`」部分を `DecodeBytes(byte[] bytes, int? forcedCodePage)` として抽出し、`Load` はそれを呼ぶだけにする。grep はバイトを 1 回だけ読み、NUL 判定後に `DecodeBytes` を使う（二重 I/O 回避・検出ロジック再利用）。

---

## 4. App 側

### GrepController（`SearchController` と同様の統括）
- `GrepDialog` と `GrepResultsWindow` を 1 つずつ保持・再利用。
- 実行: `async void` で `await Task.Run(() => GrepService.Search(req, progress, ct))`。`Progress<GrepProgress>`（UI スレッドへマーシャリング）でステータス更新。完了で結果窓へ反映＋件数を SR 通知。
- キャンセル: `CancellationTokenSource`。実行中は「中止」可能。
- ジャンプ: 結果窓の `HitActivated` を購読し、`MainForm.OpenAndSelect(path, offset, length)`（コンストラクタで受け取るデリゲート）へ委譲。

### GrepDialog（モードレス Form）
- 検索文字列 / フォルダ（＋参照 `FolderBrowserDialog`・既定＝アクティブ文書のフォルダ or マイドキュメント）/ ファイルフィルタ（既定 `*.*`）/ サブフォルダを含む（既定 ON）/ 大小・単語・正規表現。実行・中止・閉じる。ステータス Label（`RaiseAutomationNotification` で SR 通知）。
- 実行前に `TextSearcher.IsValid` を検証し、不正なら通知して中止。

### GrepResultsWindow（モードレス Form・ListBox）
- ListBox（Dock 全面）に 1 行 1 ヒット。表示は `{相対パス} (行 {LineNumber}): {行内容（前後の空白trim・長すぎは省略）}`。パスは検索フォルダからの相対（簡潔化）、ジャンプ用の絶対パスは `GrepHit` 側に保持。
- タイトル: `grep: "{pattern}" — {N} 行 / {M} ファイル`。0 件は「見つかりません」。
- `AccessibleName="検索結果"`。Enter / ダブルクリックで `HitActivated(GrepHit)` 発火。Esc で隠す。
- 標準 Win32 ListBox のため PC-Talker/NVDA がネイティブに各項目を読む（我々の UIA 層は不要）。

### MainForm 配線
- 編集メニュー「フォルダ検索(grep)(&G)...」＋ `Ctrl+Shift+F`。
- `OpenAndSelect(path, offset, length)`: `FindByPath` で既存タブ再利用 or 読込（`LoadInto` 再利用）→ アクティブエディタ `SelectCharRange(offset, length)` → エディタへフォーカス → 「{ファイル名} {行} 行目」を SR 通知。

---

## 5. テスト（xUnit・Core）

一時ディレクトリにファイルを作って検証:
- 混在文字コード（UTF-8 / Shift_JIS / UTF-16）でいずれもヒット。
- 行番号・桁・`AbsoluteOffset` の正確性（ASCII・多バイト・サロゲート・CRLF/LF 混在）。
- 再帰 on/off。サブフォルダのファイルが on のみ拾われる。
- glob フィルタ（`*.txt`・複数パターン・空＝全件）。
- バイナリ（NUL 含む）スキップ。
- 正規表現・大小・単語オプション順守（`^`/`$` が行境界）。
- 協調キャンセルで部分結果＋`Cancelled=true`。
- 0 件・空フォルダ・不正正規表現。
- アクセス不可サブディレクトリがあっても他は走査継続しエラー集約（可能なら）。
- `TextFileService.DecodeBytes` 抽出後も既存 `Load` テストが緑。

---

## 6. 非対象（follow-up）

- 開いている未保存タブの本文は検索しない（ディスクのみ）。
- 巨大ファイルのストリーミング（現状は全文読み込み。64MB 超はスキップしエラー集約）。
- 結果のファイル別グルーピング表示・正規表現の複数行モード。
- ジャンプ先が既に開かれ未保存編集済みの場合のオフセットずれ（行番号フォールバックは将来）。

## 7. マージ前レビュー（M4）の結果と申し送り

5 レンズ（正確性/SR/スレッド安全/文字コード/統合）で並列レビューし各指摘を敵対的検証（確認 26 件）。

**マージ前に修正済み:**
- 既定フィルタ `*.*`/`*` を全一致へ正規化（拡張子なしファイル Makefile/LICENSE 等の取りこぼし）＋回帰テスト。
- `GrepOutcome.Errors` をサマリ/通知/結果窓タイトルへ表出（誤った「見つかりません」防止）。
- 再帰列挙で reparse point（ジャンクション/シンボリックリンク）を辿らない（循環防止）。
- 巨大ファイル（64MB 超）をスキップし OOM で grep 全体が落ちるのを防止。
- タイムアウト時も部分ヒットがあれば FilesMatched 計上（件数の過少防止）。
- 連打リエントランシー安全化（実行毎 CTS・破棄・追い越し時は UI 不触）、進捗/完了の破棄ガード。
- ヒット有り時は結果窓フォーカスが SR を駆動（二重読み回避）、エラー時は必ず音声化、開始時に音声。
- 結果表示の 200 字省略がサロゲートを割らない。ダイアログを隠す際に実行中 grep を中止。

**申し送り（M5 以降で要否判断）:**
- **BOM 無し UTF-16（ASCII 主体）** が UTF-8 誤検出→バイナリ判定でスキップされる。これは
  `EncodingDetector` の「厳格 UTF-8 を先に試す」設計に起因する**アプリ全体の検出限界**（開く経路でも
  同様に誤判定し得る）。grep 固有でなく、検出器の改善として別途扱う。日本語 UTF-16（非 ASCII）は正しく検索される。
- **ジャンプ後のファイル名＋行の明示 SR 通知**（設計§4 記載）。現状は選択移動でエディタの UIA が一致行を
  読むため機能はするが、ファイル名は読まれない。**M6 の Announcer/照会ホットキー**で正式対応する。
- **既に開かれ未保存編集済みのタブへのジャンプ**で AbsoluteOffset がずれ得る（クランプで安全・選択が少しずれるのみ）。
  行番号ベースのフォールバックは将来。
- 非仮想化 ListBox の超大量結果での描画コスト（個人文書用途では実害低）。
- 結果項目がパス始まりで SR 読みが冗長（UX 微調整候補）。Column は UTF-16 コードユニット基準（アプリ全体と整合・文書化済み）。
