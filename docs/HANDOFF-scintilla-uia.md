# yEdit — Scintilla × UIA 実験 引き継ぎ資料

最終更新: 2026-06-25 / 作業ディレクトリ: `<repo>`（git 未初期化）/ .NET 9 SDK (9.0.304) / Windows 11・日本語環境（ATOK）

---

## 0. 30秒サマリ（次に何をするか）

yEdit を「UIA 路線」で作り直す検討。最小 UIA 実験で **PC-Talker と NVDA の両方が、完全自作の UIA テキストコントロールを読める**ことを実証済み（←→で文字／↑↓で行／IME 日本語入力まで）。
アーキテクチャを **「Scintilla（バッファ・編集機能・大容量）＋ 我々の UIA プロバイダ層（PC-Talker 実証済）」** に決定。

### ★ Session 3（2026-06-25 続き）= Scintilla probe 実装完了・SR非依存検証 全PASS
- **`src/yEdit.ScintillaProbe`** 新規作成。`ScintillaNET.Scintilla`（desjarlais `Scintilla5.NET` 6.1.2）を継承し **WM_GETOBJECT 横取り**で我々の UIA プロバイダ層を上乗せ。`dotnet build` 0警告/0エラー。実機起動・UIA応答OK。
- **SR非依存検証 PASS**: `tools/verify-uia-sci.ps1`（要素発見＝WM_GETOBJECT成立／TextPattern公開／DocumentRange読取／**多バイト選択往復**／空行len=0）＋`tools/walk-test-sci.ps1`（**Moveスパン保持＝PC-Talker致命バグ無回帰**）。
- **重要な設計判断（§7からの逸脱）**: スレッド安全(§3.4)を精査 → Scintilla の `DirectMessage` は UI スレッド専有で **RPC スレッドから SCI_* 直読みは不可**。よって §7 の「バイト空間 provider」は採らず、**UI スレッドで UTF-16 スナップショットを保持し、provider は UTF-16 空間のまま無改変流用**（= 実証済みコードを一切触らない最小リスク）。Scintilla の UTF-8 バイト位置 ⇔ UTF-16 は `ScintillaHost` が UI スレッドで変換（`Encoding.UTF8.GetCharCount/GetByteCount`）。詳細は §12。
### ★★ Session 3 続き = 実機 SR で決着、最終アーキテクチャ確定（§13 に全詳細）
- **実機結果**: PC-Talker は我々の UIA を読む（OK）。だが **NVDA は同じ UIA で無音**だった。
- **NVDA 無音の根本原因（確定）**: NVDA は `WindowsForms10.Scintilla.app.0.NNN` を正規表現で **`"Scintilla"` に正規化**し、ネイティブ Scintilla オーバーレイを我々の UIA オブジェクトに被せて競合させる（`ScintillaTextInfo` の SCI 直叩き経路で固まる）。クラス名から "Scintilla" を消すと NVDA は純 UIA で読めたが、**今度は PC-Talker が壊れた**（ネイティブ MSAA を誤読）。＝**1つの構成で両 SR を満たせない**。
- **★最終アーキテクチャ（ユーザー決定・実装済）**: **起動時に SR を判定して構成を切替**。
  - **NVDA 起動中 → 我々は UIA も MSAA も出さない**（`ServeUiaProvider=false`/`SuppressClientMsaa=true`）。NVDA は **64bit Scintilla5 をネイティブで読める**（実機確認。Notepad++ 由来の「64bit で壊れる」予想は外れた）。
  - **それ以外（PC-Talker 等）→ 我々の UIA プロバイダを適用**。PC-Talker がそれを読む。
  - クラスは常に元の "Scintilla"（**クラス改名／NVDA-UIA 路線は破棄**）。判定の要は「**NVDA が動いているか**」だけ（`ScreenReaders.IsNvdaRunning()` = プロセス `nvda`）。SR非依存で検証済（nvda起動中→UIA非提供／`--pctalker`→提供）。
- **次の仕事 = ユーザーの自動挙動エンドツーエンド確認**（NVDA起動中にフラグ無しで読む／PC-Talker起動中にフラグ無しで読む）。その後 §6 座標・大容量・文字コードI/O へ。

---

## 1. 背景・動機

- 以前 yEdit を **WinForms + native RichEdit（RICHEDIT50W）** で全7フェーズ完成させたが、**巨大ファイルに苦慮**（RichEdit 取り込み ~80ms/MB・UIスレッド束縛・真の仮想化不可。進捗バー＋チャンク読みで 10MB快適/100MB best-effort が天井）。
- RichEdit を選んだ唯一の理由は「PC-Talker は MSAA のみ対応」という前提。**この前提を実験で再検証**し、UIA 対応なら RichEdit を捨てて柔軟な選択ができる、というのが今回の出発点。技術: C# + .NET9。

---

## 2. 実験で確定した事実（UIA 路線 = GO）

- **PC-Talker・NVDA 両方が自作 UIA TextPattern を読む**: ←→で1文字、↑↓で1行、**IME オンの日本語入力**まで。
- `ControlType=Document` のまま両 SR OK（懸念した NVDA ブラウズモード問題は出ず）。
- **SR 非依存で UIA 層の正しさを自動検証可能**（別プロセスの UIAutomationClient から。`tools/verify-uia.ps1`）。

→ 「PC-Talker は UIA を読む」が実証されたので、RichEdit を我慢する理由が消えた。巨大ファイルを根治できる自作バッファ路線が現実的になった。

---

## 3. 重要なデバッグ知見（落とし穴。次でも必ず効く）

### 3.1 ★ UIA `Move` はスパン保持必須（PC-Talker 致命傷だった）
PC-Talker は「現在文字」を2系統で読む:
1. `GetSelection → Clone → ExpandToEnclosingUnit(Character) → GetText`（キャレットから直読み。NVDA もこれ）
2. **行頭から `Expand(Character)` を1回 → `Move(Character,1)` を繰り返し → `GetText`** で1文字ずつ歩く（列の特定）。

我々の `Move` が範囲を退化点（`_start=_end`）に潰していたため、**Move 後の GetText が空**になり、PC-Talker が **2文字目以降を読めず直前文字に張り付いた**。NVDA は (1) だけなので無傷だった。
**修正（実装済・`TextRangeProvider.Move`）**: 元が単位スパンを持つ範囲は、移動後に同単位を再展開してスパンを保つ（`Move` 後 `ExpandToEnclosingUnit(unit)`）。UIA Move 仕様準拠。`tools/walk-test.ps1` で SR 非依存に再現・修正確認できる（修正前 `こ,空,空…` → 修正後 `こ,れ,は, ,U,I`）。

### 3.2 システムキャレット＋UIAイベント の両方を出す
SR は位置追従に **システムキャレット（`CreateCaret`/`SetCaretPos`）** と **UIA `TextSelectionChangedEvent`** のどちらか／両方を使う。両方発火させること。**Scintilla は Win32 でシステムキャレットを自前で動かす**ので、その点は楽（§5 の研究結果）。

### 3.3 ControlType は Document より Edit を試す
Document で両SR動いたが、**Edit の方がエディタとして妥当**（NVDA がフォーカスモードになる／PC-Talker が Notepad 同様の編集フィールド扱いになり、後述の空行を読む可能性）。probe にトグルあり。**次セッションで Edit を必ず試す。**

### 3.4 スレッド安全
プロバイダのメソッドは **UIA の RPC スレッド**から呼ばれる。UI に触れない・本文はスナップショット（不変文字列参照）・ハンドル/矩形/フォーカス/ControlType はキャッシュ値で応答。トレース sink もスレッドセーフに。

### 3.5 SR 非依存で高速反復できる（重要）
- 別プロセスの **UIAutomationClient（PowerShell）** で provider を駆動・読み取りできる → スクリーンリーダー無しで反復検証。`tools/` 参照。
- **計装（`UiaDiag`）で全プロバイダ呼び出し＋システムキャレットをログ**に出し、PC-Talker が実際に何を呼ぶか採取 → ログ解析が原因特定の決め手だった（`%TEMP%\yedit-uia-probe.log`）。

### 3.6 環境の罠（ハマったところ）
- **PS 5.1 は BOM 無し UTF-8 の .ps1 を Shift-JIS と誤読**しパースが壊れる → 検証スクリプトは **ASCII のみ**で書く。
- UIA のトップレベル窓列挙で **pid 一致の先頭が ATOK パレット窓（`ATOK36PaletteCommonWnd`、子0）** のことがある → ドキュメント要素は **RootElement の Descendants を AutomationId で FindFirst** して取る。
- UIA 参照: `net9.0-windows` + `<UseWPF>true</UseWPF>` で UIAutomationProvider/Types・`System.Windows.Rect` が入る。
- `LibraryImport` は `AllowUnsafeBlocks` 必須 → キャレット P/Invoke は従来の `DllImport` を使用。
- WinForms アナライザ `WFO1000`（Control の public プロパティ）→ `[Browsable(false), DesignerSerializationVisibility(Hidden)]` を付与。

---

## 4. 未解決事項（次セッションで対応）

### 4.1 空行が PC-Talker で無音（NVDA は「ブランク」OK）
- 行読み取りを `LineEndNoBreak` 化して**空行を長さ0で公開**済み（`tools/verify-uia.ps1` の line 列挙で `line[2] len=0` 確認）。NVDA は「ブランク」を発火。
- だが **PC-Talker は長さ0の行を無音**。ログ上、空行で `Expand(Character)→[42,43]→GetText→'\n'`（改行自体は取れる）だが、Down 移動時の行読みは `Expand(Line)→[42,42]→GetText→''`。
- PC-Talker のネイティブな「改行」読みは**行テキスト経由ではなく別機構**（改行を含めても含めなくても無音だった）。統一読み（改行 vs ブランク）は偽テキスト注入になり不可（コピー/文字数/検索が壊れる）→ **各SRのネイティブ流儀尊重が方針**。
- **決着（2026-07-03・能動発声方式）**: UIA 側での解決（偽テキスト注入）は不可のため、**純ナビゲーション（本文変更なし・選択なし）で行が変わり空行へ着地したときだけ、App 層が Announcer 経由で「空行」を能動発声**する方式で解消。
  - Core `EmptyLineDetector.IsCaretOnEmptyLine`（純ロジック・テスト18件）／Editor `ScintillaHost.CaretEnteredEmptyLine`（SCN_UPDATEUI で検知。Content 変更・選択拡張・CSV 遷移中 `RaiseUiaSelectionEvents=false` は抑止）／App `DocumentManager`→`MainForm`（`SpeechMode.PcTalker` かつ非 CSV モードのみ `Say("空行")`）。
  - NVDA はネイティブ「ブランク」のまま（NVDA モードでは App 層が発声しない）。素の UIA モード（SR なし等）も発声なし。
  - **申し送り（v1 許容・実機で要観察）**: (a) Ctrl+G で空行へジャンプ時「行 N」と「空行」が両方割り込み priority=1 で発声され後着が勝つ。(b) タブ切替/フォーカス復帰で空行上に居る場合は行変化ゲートに掛からず従来どおり無音。(c) CSV モード終了時の遅延 SCN_UPDATEUI は両ガードをすり抜け得るが、CSV パーサが空行から行を生成しないため現行実装では実害なし（パーサ仕様への暗黙依存）。(d) 実機検証項目: 空行着地の「空行」可聴／PC-Talker の連続読みを空行通過ごとの割り込みが中断しないか／空白のみ行の受動読みが無音でないか。

### 4.2 `RangeFromPoint` / `GetBoundingRectangles` がスタブ
- 現状 `RangeFromPoint→[0,0]`、`GetBoundingRectangles→空`。PC-Talker は `GetBoundingRectangles` を呼ぶが（0矩形でも）テキスト歩きで動いた。
- Scintilla では **実装する**（`SCI_POINTXFROMPOSITION`/`SCI_POINTYFROMPOSITION`、`SCI_CHARPOSITIONFROMPOINT`）。点字や一部 SR 挙動の改善に効く可能性。ブロッカではないが対応推奨。

---

## 5. アーキテクチャ決定と Scintilla 研究結果

### 採用構成
```
Scintilla (native, ギャップバッファ)         ← 300MB余裕・上限~2GB / 編集・Undo・選択・
  + システムキャレット(Win32)                   シンタックス・折りたたみ・検索 が全部タダ
        │ WM_GETOBJECT を横取り
  yEdit.Accessibility（既存・PC-Talker実証済）   ← Scintilla に無い「Windows の UIA テキスト」を補う
        │ IUiaTextHost
  ScintillaTextHost（新規アダプタ）             ← SCI_* で本文/キャレット/選択/座標を読む
```

### Scintilla 研究の核心（一次情報）
Scintilla 公式ドキュメント:
> *"On GTK and Cocoa, platform accessibility APIs are used for screen reader compatibility. **On Win32, the system caret is manipulated to assist screen readers.**"*

→ **Windows の Scintilla は UIA も MSAA のテキストパターンも公開していない（システムキャレットを動かすだけ）**。これが Notepad++ が SR で読みにくい根本理由。`SCI_SETACCESSIBILITY` は Win32 では実質ほぼ無効。
→ だから「Scintilla を PC-Talker 対応に」＝ **欠けている UIA テキストプロバイダを足すこと**。それは我々が作って実証済みの `yEdit.Accessibility` そのもの。WM_GETOBJECT 経由の UIA プロバイダは Scintilla の Win32 a11y（システムキャレットのみ）と競合しないので、**Scintilla のキャレットは活かしつつ我々の UIA を上乗せ**できる。

### バインディング候補
- `desjarlais/Scintilla.NET`（VPKSoft 由来の保守フォーク。SciLexer.dll 同梱）。
- `jacobslusser/ScintillaNET`（オリジナル、3.x）。
- **probe 最初の確認事項**: net9.0-windows 対応か / HWND・WndProc にアクセスできるか（サブクラス化して WM_GETOBJECT 横取り可能か）。

ソース: scintilla.org/ScintillaDoc.html, scintilla.org/ScintillaWin.cxx, github.com/desjarlais/Scintilla.NET

---

## 6. 既存コード（流用資産）

ソリューション `<repo>\yEdit.sln`。`dotnet build` 0警告/0エラー。

### `src/yEdit.Accessibility/`（classlib, net9.0-windows, UseWPF, Nullable=disable）＝純 UIA プロバイダ層・本番流用
- `IUiaTextHost.cs` — ホスト抽象シーム（本文/長さ/選択/矩形/ハンドル/フォーカス/ControlTypeId/SetSelection/SetFocus/GetBoundingRectangles）。**要リファクタ**（§7）: 文字/行/単語の移動を**このシームに持たせ**、provider が位置空間（Scintilla の UTF-8 バイト位置）非依存になるようにする。
- `TextNavigation.cs` — UTF-16 文字列上の単位境界（NextChar/PrevChar/LineStart/LineEnd/LineEndNoBreak/Word*）。**Scintilla では SCI_* に置換**（WinForms ホストはこれを継続使用）。
- `TextControlProvider.cs` — `IRawElementProviderSimple/Fragment/FragmentRoot`。`GetPatternProvider→Text`、`GetPropertyValue`（ControlType は host から／IsKeyboardFocusable/HasKeyboardFocus/Name/AutomationId）、`HostRawElementProvider=HostProviderFromHandle`、`GetRuntimeId`、`Navigate→null`。
- `TextProviderImpl.cs` — `ITextProvider`（GetSelection/GetVisibleRanges/RangeFromChild/**RangeFromPoint=STUB[0,0]**/DocumentRange/SupportedTextSelection.Single）。計装あり。
- `TextRangeProvider.cs` — `ITextRangeProvider` 全実装。**§3.1 の Move スパン保持修正はここ**。`GetBoundingRectangles` は host へ委譲（現状空）。`GetAttributeValue→NotSupported`。ナビは現状 `TextNavigation` 直呼び（§7 で host 委譲へ）。計装あり。
- `UiaDiag.cs` — トレース sink（`Sink` を probe が設定）。

### `src/yEdit.UiaProbe/`（WinForms, net9.0-windows, UseWPF）＝**参照実装・Scintilla ホストの雛形**
- `UiaTextControl.cs` — **WM_GETOBJECT 横取り**、システムキャレット（`CreateCaret`/`SetCaretPos`）、イベント発火（`TextSelectionChanged`/`TextChanged`/`AutomationFocusChanged`）、診断トグル、`IUiaTextHost` 実装、ヒットテスト、GDI+ 描画。**Scintilla ホストはこの構造を踏襲**。
- `MainForm.cs` — 診断メニュー（システムキャレット / UIA選択 / UIAテキスト / ControlType Document⇔Edit）、ログ配線（UI=EVT、RPC=UIA、スレッドセーフ）。
- `NativeMethods.cs` — キャレット P/Invoke と `WM_GETOBJECT`/`UiaRootObjectId=-25`。
- `Program.cs`。

---

## 7. Scintilla アダプタ設計（次セッションの中身）

- **常に `SCI_SETCODEPAGE(SC_CP_UTF8=65001)`**。Scintilla の位置は **UTF-8 バイトオフセット**。
- **リファクタ先行**: `IUiaTextHost` に移動系プリミティブ（NextChar/PrevChar/LineStart/LineEnd(NoBreak)/WordStart/WordEnd を「位置→位置」で）を追加し、`TextRangeProvider`/`ExpandToEnclosingUnit`/`Move` がそれを呼ぶ形へ。WinForms ホストは `TextNavigation` で実装、Scintilla ホストは SCI_* で実装。
- **`ScintillaTextHost : IUiaTextHost`**（SCI_* マッピング）:
  - `GetText(s,e)` = `SCI_GETTEXTRANGE`（必要なら full 版）→ UTF-8 バイト → デコード。**要求範囲だけ materialize**（巨大ファイルで全文 GetText は避ける／DocumentRange の全文 GetText はガード）。
  - `TextLength` = `SCI_GETLENGTH`（バイト長）。
  - `GetSelection` = `SCI_GETSELECTIONSTART/END`。`SetSelection` = `SCI_SETSEL`。キャレット = `SCI_GETCURRENTPOS`。
  - 文字 = `SCI_POSITIONAFTER`/`SCI_POSITIONBEFORE`（多バイト安全）。
  - 行 = `SCI_LINEFROMPOSITION`/`SCI_POSITIONFROMLINE`/`SCI_GETLINEENDPOSITION`。
  - 単語 = `SCI_WORDSTARTPOSITION`/`SCI_WORDENDPOSITION`。
  - 座標（矩形/キャレット）= `SCI_POINTXFROMPOSITION`/`SCI_POINTYFROMPOSITION` + クライアント→スクリーン変換。
  - Handle/Focus/ControlTypeId はキャッシュ。
- **WM_GETOBJECT**: ScintillaNET コントロールをサブクラス化し `WndProc` で横取り（`UiaTextControl` と同手）。
- **イベント配線**: `SCN_UPDATEUI`（`SC_UPDATE_SELECTION`/`SC_UPDATE_CONTENT`）→ `TextSelectionChanged`、`SCN_MODIFIED`（`SC_MOD_INSERTTEXT`/`SC_MOD_DELETETEXT`）→ `TextChanged`。
- **システムキャレット**: Scintilla が Win32 で出すので原則そのまま活かす。PC-Talker が満足しなければ明示管理を追加。
- `RangeFromPoint`/`GetBoundingRectangles` を Scintilla 座標 API で実装（§4.2）。

---

## 8. 次セッションの段取り（チェックリスト）

1. **ScintillaNET バインディング導入**（`desjarlais/Scintilla.NET` 第一候補）。net9.0-windows 対応・HWND/WndProc アクセス・SciLexer.dll 同梱を確認。
2. **`yEdit.ScintillaProbe`** 新規プロジェクト。Scintilla を載せ `SC_CP_UTF8`、初期テキストに**多コード混在・空行・IME 試験用**を投入。
3. **`IUiaTextHost` リファクタ**（移動系を host へ）。WinForms ホストは `TextNavigation` 継続、Scintilla ホスト追加。
4. **`ScintillaTextHost` + WM_GETOBJECT 横取り + イベント配線 + システムキャレット**。
5. **SR 非依存検証**（`tools/` を AutomationId・exe パス調整して実行）。その後 **PC-Talker 実機**: 文字/行/単語/選択/IME/空行。**ControlType=Edit を試す**。`UiaDiag` ログ採取。
6. `RangeFromPoint`/`GetBoundingRectangles` を実装。
7. ControlType 既定を決定、**空行問題を決着**。
8. （後続）文字コード I/O 層を前回資産で再利用、ファイル開閉・巨大ファイルのストリーム読み。

---

## 9. 文字コード方針（決定済）

- **Scintilla 内部は常に UTF-8 固定**。ディスク上の文字コード変換は**ファイル I/O 層（自前コード）で実施**。
- **UTF-8(BOMなし) を既定**、Shift_JIS(932)/EUC-JP(51932) を読み書き時に変換。**EUC-JP は Scintilla 内部コードにできない**が、それは「UTF-8 内部＋I/O変換」という正しい設計を強制するだけで実害なし（UTF-8/Shift_JIS/EUC-JP/UTF-16 を一様に扱える）。
- **前回 yEdit の文字コード資産が丸ごと流用可**: `EncodingDetector`（BOM＋厳格UTF-8＋`UTF.Unknown` 自動判定、EUC-JP=CP51932/Shift_JIS=932 正準マップ）、開き直しダイアログ、原子的保存。.NET 9 は `CodePagesEncodingProvider` 登録要（前回対応済）。
- 横断注意: **Shift_JIS/EUC-JP ⇔ Unicode 往復不整合**（CP932 ベンダ拡張、波ダッシュ U+301C ⇔ 全角チルダ U+FF5E、機種依存）。**全エディタ共通**の話で Scintilla 固有欠点ではない。前回の「置換文字読み上げ明示」ノウハウ流用。

---

## 10. 検証ツール（`tools/`、すべて ASCII）

- `verify-uia.ps1` — provider 総合チェック（TextPattern/GetText/GetSelection/Select 往復/行列挙）。
- `walk-test.ps1` — **PC-Talker の行歩き再現**（`Expand(Char)+Move(Char,1)+GetText`）。Move スパン修正を **SR 無し**で証明。
- `dump-uia.ps1` — probe 窓の UIA サブツリーをダンプ。
- `selftest-caret.ps1` — SendKeys でキー送出しシステムキャレットの前進をログ確認。
- 使い方: 起動 exe パスと `AutomationId`（probe 側で設定）を Scintilla probe 用に調整。`%TEMP%\yedit-uia-probe.log` を読む（`UiaDiag` トレース）。

---

## 11. 環境メモ

.NET 9 SDK 9.0.304 / Windows 11 Pro / 日本語・ATOK。`<repo>`（**git 未初期化** — 必要なら init して初回コミット）。ログ `%TEMP%\yedit-uia-probe.log`（UiaProbe）/ `%TEMP%\yedit-scintilla-probe.log`（ScintillaProbe）。

---

## 12. Session 3 実装ログ（Scintilla probe）— 2026-06-25 続き

### 12.1 Scintilla バインディング確定（§8.1 クリア）
- 採用: **`Scintilla5.NET` 6.1.2（NuGet 作者 desjarlais）**。`dotnet package search Scintilla` で確認。
  - 管理: `lib/net8.0-windows7.0/Scintilla.NET.dll`（**net9.0-windows で動く**）/ `net462`。名前空間 `ScintillaNET`、クラス `Scintilla : Control`、**sealed=false**（継承して `WndProc` override 可）。
  - ネイティブ: `runtimes/win-{x64,x86,arm64}/native/{Scintilla,Lexilla}.dll`（Scintilla 5.x。SciLexer.dll ではなく本体＋レクサ分離）。`build/scintilla5.net.targets` が `runtimes/**` を出力へコピー。AnyCPU 実行時にラッパーが arch 別ネイティブを自動ロード（出力にコピー確認済・標準起動OK）。
  - 生 `SCI_*` チャネル: `IntPtr DirectMessage(int msg, IntPtr wParam, IntPtr lParam)`（＋1,2引数版）公開。
- **smoke test 実証**（scratch）: Handle非ゼロ／`SCI_SETCODEPAGE(SC_CP_UTF8)`／`SCI_GETLENGTH`=UTF-8バイト長／`SCI_POSITIONAFTER` 多バイト安全（0→3→4→…）／`SCI_GETTEXTRANGE` デコード／`SCI_POSITIONFROMLINE`/`SCI_GETLINEENDPOSITION` で**空行 len=0**／`SCI_SETSEL`+`SCI_GETSELECTIONSTART/END` のバイト位置往復 — すべて §7 の前提通り。

### 12.2 ★設計判断: スレッド安全 → snapshot 方式（§7 バイト空間 provider は不採用）
- §3.4 の通り provider メソッドは **UIA の RPC スレッド**から来る。`DirectMessage`（SCI_GETDIRECTFUNCTION 経由）は**呼び出しスレッドで Scintilla を直接実行**＝スレッド非安全。よって **RPC スレッドから SCI_* を読む §7 案は不可**。
- 解: **UI スレッドで本文スナップショットを保持**し RPC スレッドはそこから応答。最も単純な snapshot は **UTF-16 文字列**＝WinForms 版 host と同じ契約 → **provider（`yEdit.Accessibility`）は UTF-16 オフセット空間のまま完全無改変で流用**（実証済みコードを触らない＝最小リスク。Move スパン修正等の回帰ゼロ）。
- `ScintillaHost`（`Scintilla` 継承, `IUiaTextHost` 実装）の要点:
  - snapshot 更新（UI スレッド）: `SCI_GETLENGTH`→`SCI_GETTEXT` で UTF-8 バイトを取得 → `_snapBytes`、`Encoding.UTF8.GetString`→`_snapshot`（両者整合）。
  - 位置変換（UI スレッド）: `ByteToUtf16 = Encoding.UTF8.GetCharCount(_snapBytes,0,bytePos)` / `Utf16ToByte = Encoding.UTF8.GetByteCount(_snapshot[0..u16])`。
  - イベント配線: `UpdateUI`(`UpdateChange.Selection/Content`)→選択キャッシュ更新＋`TextSelectionChanged` 発火。`TextChanged`→snapshot更新＋`TextChanged` 発火。
  - `IUiaTextHost`: `GetText()`/`TextLength`/`GetSelection` は volatile キャッシュ即答（**RPC スレッドから DirectMessage を一切呼ばない**）。`SetSelection`/`SetFocus` のみ `BeginInvoke` で UI スレッドへ。
  - システムキャレットは **Scintilla が Win32 で自前管理**＝こちらは不介入（§5）。
- 帰結: 巨大ファイルでは「全文 UTF-16 snapshot」は重い（§7 が避けたかった点）。**これは probe では非問題**（GO/NO-GO が目的）。本番大容量対応時に「窓スナップショット＋バイトマップ」へ最適化する（snapshot 方式自体は維持。バイト空間直読みはスレッド安全と非両立なので復活させない）。

### 12.3 成果物
- `src/yEdit.ScintillaProbe/`: `ScintillaHost.cs`（上記）/ `MainForm.cs`（診断メニュー: UIA選択・UIAテキスト ON/OFF、ControlType Document⇔Edit、ログ）/ `Sci.cs`（SCI_* 定数）/ `NativeMethods.cs`（WM_GETOBJECT=0x3D, UiaRootObjectId=-25）/ `Program.cs`。`yEdit.sln` に追加済。
- `tools/verify-uia-sci.ps1` / `tools/walk-test-sci.ps1`: ScintillaProbe exe を指す。AutomationId は provider 共有で `uiaProbeDocument` のまま。LocalMachine ExecutionPolicy=Bypass のため `& "...ps1"` で実行可（`-ExecutionPolicy Bypass` フラグは付けない＝拒否される）。
- ログ: `%TEMP%\yedit-scintilla-probe.log`（EVT=UIスレッド / UIA=RPCスレッド・UiaDiag）。

### 12.4 SR非依存検証結果（全PASS）
- `verify-uia-sci.ps1`: 要素発見（WM_GETOBJECT 成立）/ ControlType=Document / TextPattern / DocumentRange=119文字（"Scintilla"含む）/ GetSelection / **多バイト選択往復 3文字（[0,3)文字⇔byte[0,9)）**/ 空行 line[2] len=0。
- `walk-test-sci.ps1`: `こ,れ,は,(空白),S,c` の6種別＝**Move スパン保持**（PC-Talker 致命バグ無回帰）。
- 既知の軽微アーティファクト: 行列挙ループで末尾行（改行なし）を2回読む（終端点の Expand(Line) が最終行に戻るため）。provider 無改変ゆえ WinForms 版と同一・回帰ではない。

### 12.5 次の一手 → §13 で決着済

---

## 13. ★最終決着: NVDA 無音の根本原因と SR 適応アーキテクチャ — 2026-06-26

### 13.1 実機で起きたこと
- **PC-Talker**: 我々の UIA を読む（最初の "Scintilla" クラス＋UIA 構成で OK）。
- **NVDA**: 同じ構成で**完全に無音**。`UiaProbe`（WinForms・汎用クラス）では NVDA も読めていたのに、Scintilla 版で無音化＝回帰。

### 13.2 NVDA 無音の根本原因（調査＋計装＋実測で確定）
- 計装ログ（`[WM_GETOBJECT]` を追加）で判明: NVDA は UIA(-25) に**接続して** `DocumentRange`→`GetAttributeValue(IsReadOnly=40015)` を呼ぶが、その後 `GetSelection`/`GetText` を**一切呼ばず**無音。NativeWindowHandle は非ゼロ（要素は妥当）。
- 原因 = **ウィンドウクラス名**。ScintillaNET は WinForms で `Scintilla` をスーパークラス化し、クラス名は `WindowsForms10.Scintilla.app.0.NNN`。**NVDA は `Window.normalizeWindowClassName`（正規表現 `^WindowsForms[0-9]*\.(.*)\.app\..*$`）でこれを `"Scintilla"` に正規化** → 我々の要素は実 HWND を持つ(`UIAIsWindowElement=true`)ため、`UIA.findOverlayClasses`→`Window.findOverlayClasses` が `("Scintilla","TScintilla")` にマッチして**ネイティブ Scintilla オーバーレイ（`ScintillaTextInfo`＝SCI 直叩き）を UIA オブジェクトに被せ**、UIA 経路と競合 → 読まない。
- **決定的証拠**: 同じ provider でもクラス名に "Scintilla" が無ければ NVDA は読める。PC-Talker はこのクラス→オーバーレイ機構を持たないので両構成で読む。
- NVDA ソース参照: `source/NVDAObjects/window/__init__.py`(`normalizeWindowClassName`/`re_WindowsForms`/`findOverlayClasses`), `source/NVDAObjects/UIA/__init__.py`(`findOverlayClasses` super-call, `_getReadOnlyState`), `source/NVDAObjects/window/scintilla.py`。

### 13.3 1構成で両 SR を満たせない（試行と棄却）
- **クラス改名**（`WindowsForms10.yEditTextEdit.app...` にクローン登録）→ NVDA は純 UIA で読めた。が **PC-Talker が壊れた**（左右=改行・上下=空行）。ログ上 PC-Talker は UIA(-25) を要求するが TextPattern を使わず、**ネイティブ MSAA(-4) を多用**＝本文を Name に載せた Pane を誤読。
- **ネイティブ MSAA 抑制**（WM_GETOBJECT -4 で 0 返し）→ PC-Talker は UIA に寄らず（NG）。
- 結論: PC-Talker は "Scintilla" クラス＋UIA を好み、NVDA は "Scintilla" クラスだと壊れる。**クラス名が両 SR を反転させるレバー**で両立不能。

### 13.4 ★最終アーキテクチャ（ユーザー決定・実装済・SR非依存で検証）
**起動時に SR を判定して構成を切替**（クラスは常に元の "Scintilla"）:
| 判定 | ServeUiaProvider | SuppressClientMsaa | 読まれ方 |
|---|---|---|---|
| **NVDA 起動中** | **false** | **true** | NVDA が**ネイティブ Scintilla**を読む（64bit でも読めた・実機確認） |
| それ以外（PC-Talker 等） | **true** | false | PC-Talker が**我々の UIA**を読む |
- 判定の要は「**NVDA が動いているか**」だけ: `ScreenReaders.IsNvdaRunning()` = プロセス `nvda` の有無。NVDA があれば譲り、無ければ UIA を出す（PC-Talker・SRなし・他UIA系SR で安全）。
- 実装: `MainForm` 起動時に判定 → `_editor.ServeUiaProvider`/`SuppressClientMsaa` を設定。手動上書き `--nvda`/`--pctalker`、低レベル `--no-uia`/`--no-msaa`/`--rename-class`/`--edit` も残置。起動構成はログ `[config]` 行に出る。
- **SR非依存で検証済**: nvda 起動中＋フラグ無し → UIA 要素 served=False（NVDAモード）／`--pctalker` → served=True（PC-Talkerモード）。
- **UIA プロバイダ層（`yEdit.Accessibility`）の用途は PC-Talker 専用に確定**。クラス改名・NVDA-UIA 路線は破棄（コードは `--rename-class` 裏に参考として残置）。
- 注: Notepad++ 由来の「NVDA の 64bit Scintilla ネイティブ読みは壊れる（`CharacterRangeStructLongLong` が要る）」予想は**外れた**＝この環境の NVDA は素で読めた。別環境/版で無音なら NVDA 用に専用 appModule か、その時は再検討。

### 13.6 PC-Talker リフォーカス遅延の修正（実機OK）
- 症状: PC-Talker 使用中、別窓へフォーカスを移して戻すと**読み始めまで約1〜2秒**かかる。
- 計装ログで確定: リフォーカス時 `[focus]` 発火 → 最初の `GetSelection` まで**約2秒の空白**。キャレット移動時（`TextSelectionChanged` 発火）は 50〜470ms で読む。リフォーカスは選択が動かず `TextSelectionChanged` が出ない → PC-Talker がポーリング周期で気づくまで待つ。
- 修正（`ScintillaHost.OnGotFocus`）: フォーカス獲得時に `RefreshSelection()` ＋ **`TextSelectionChanged` を明示発火**。これで矢印移動時と同じ即読みになる（実機「ばっちり」）。
- 教訓: **UIA エディットで PC-Talker の即読みには、フォーカス獲得時にも `TextSelectionChanged` を出すべき**（FocusChanged だけでは ~2秒ポーリング待ち）。

### 13.5 残タスク
1. **ユーザーの自動挙動エンドツーエンド確認**: NVDA 起動中にフラグ無しで読む／PC-Talker 起動中にフラグ無しで読む。
2. PC-Talker モードでの §4.1 空行・§8.6 `RangeFromPoint`/`GetBoundingRectangles`（**§3.4 を守り UI スレッドのキャッシュから応答**。RPC から `Invoke` で SCI_POINTX/Y は SR ハングの恐れ）。
3. 文字コード I/O 層（§9）と巨大ファイルのストリーム読み（snapshot を窓化）。
4. PC-Talker の正確なプロセス名は未確定（情報目的の `IsPcTalkerRunning` は前方一致の推測）。判定の主役は NVDA 検出なので実害なし。

---

## 14. M1（v0.1 ウォーキングスケルトン）完了 — 2026-06-26

本番エディタ開発のマイルストーン M1 完了。設計は `docs/plans/2026-06-26-yedit-production-architecture-design.md`、実装計画は `docs/plans/2026-06-26-m1-walking-skeleton.md`。ブランチ `feature/m1-walking-skeleton`（18コミット）→ main へ no-ff マージ。

### 成果
- **本番プロジェクト構成を確立**: `yEdit.Core`(net9.0・UI非依存: 文字コードI/O・設定。xUnit 23件緑)／`yEdit.Editor`(net9.0-windows: probe の `ScintillaHost` を移設・本番化＝Scintilla継承＋WM_GETOBJECT横取り＋SR適応)／`yEdit.App`(WinForms シェル: 単一ドキュメント)／`yEdit.Accessibility`(既存・無改変)。probe は Editor 参照へ移行し残置。
- **機能**: 新規/開く/上書き保存/名前を付けて保存/文字コード指定で開き直す。文字コード判定（UTF-8 BOM有無・Shift_JIS(932)・EUC-JP(51932)・UTF-16、`UTF.Unknown` 流用）。改行コード（CRLF/LF/CR）判定・保持。原子的保存（temp→`File.Replace`、共有/ロック違反時のみ in-place フォールバック＝原本喪失を回避）。置換文字検出で文字コード警告。dirty 追跡・終了時確認・ウィンドウサイズ永続化（settings.json）。メニュー/ステータスバー（行桁・文字コード・改行）。
- **SR 適応**: `ScintillaHost.ConfigureForCurrentScreenReader()` をハンドル生成前に呼び、NVDA起動中→ネイティブ譲り／それ以外→自作UIA提供。鉄則4点（クラス名 "Scintilla" 固定／NVDA時UIA非提供／RPCスレッドからSCI_*非呼出／フォーカス獲得時 TextSelectionChanged）を最終レビューで遵守確認。

### 検証
- `dotnet build yEdit.sln -c Release` = 0 warning / `dotnet test` = 23/23 PASS。
- probe SR非依存検証（`verify-uia-sci.ps1`/`walk-test-sci.ps1`）は Phase C で無回帰 PASS（以降 Editor/Accessibility/probe 不変）。
- 各フェーズで別エージェント二段レビュー（仕様適合＋コード品質）＋M1全体の最終レビュー（マージ可判定）。
- **実機SR（PC-Talker/NVDA）= ユーザー確認 OK**（§13.5-1 のエンドツーエンド確認に相当）。

### M2 以降への申し送り（非ブロッキング）
- §4.1 空行の PC-Talker 読み・§8.6 座標API（`GetBoundingRectangles`/`RangeFromPoint`）は M6（PC-Talker 精緻化）へ。
- `AppSettings.FontSize` 未適用（M7 外観）。`ApplyFont()` の早期ハンドル生成は無害だが整理余地。保存毎の `ConvertEols` が undo を残す軽微UX。UTF-16 は BOM 無し未検出（開き直しで救済）。`SettingsStore.Save` 非原子的（実害小）。
- `DocumentState` のタブ化ファサード化は M2。
- 巨大ファイル（snapshot 窓化・ストリーム読み）は後続。
