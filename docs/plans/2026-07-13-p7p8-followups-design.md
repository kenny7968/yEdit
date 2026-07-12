# P7/P8 申し送り 5 項目 設計書

**作成日**: 2026-07-13
**対象**: P8 レビュー Minor-4/5(EditorControl v2/Core) + P7 チェックリスト申し送りの App 層品質 3 項目
**スコープ外**: App 層自動テスト基盤(`tests/yEdit.App.Tests` 新設)= 別セッションで単独対応
**関連メモリ**: `custom-editcontrol` `p7-post-checklist-followups`

## 1. 背景と目的

`custom-editcontrol` プロジェクト(P0〜P8)は 2026-07-13 に main へマージ済(`f4430a3`)。マージ前レビューで
挙がった Minor 群のうち、以下 5 項目は「対応する場合の受け皿」を残していた:

- **Minor-4 (P8)**: 視覚セグメント探索ヘルパの重複(3 経路)
- **Minor-5 (P8)**: UIA `LineStartOf`/`LineEndNoBreakOf`/`LineEnd` 各コールでの `string`+`List<WrapSegment>`
  アロケーションが SR の連続読みで GC 圧力になる懸念
- **F3-after-Hide 通知経路 (P7)**: `SearchController.Announce` → `_dialog.RaiseNotification` の経路が
  G-2「検索モードで次を検索後に自動 Hide」で Hidden なダイアログを経由する形
- **Event delegate 混在 (P7)**: `DocumentManager` の sibling events が `EventHandler` / `Action<T>` /
  `EventHandler<T>` の 3 種混在
- **SaveAs BOM 明示指定 UI (P7)**: 現状は `State.HasBom` 継承のみで、UTF-8 で BOM 有/無を選ばせる
  トグルがない

本設計はこの 5 項目を **1 フィーチャーブランチ(`feature/p7p8-followups`) 5 コミット** で対応する。
第 6 項目「App 層テスト基盤」は工数と設計論点が大きいため本設計スコープ外(申し送り継続)。

## 2. 方針(承認済み概要)

| 項目 | 方式 |
|---|---|
| Minor-4 | `yEdit.Core.Layout.VisualSegments.FindContaining(segs, offsetInLine)` を新設し 3 経路から抽出 |
| Minor-5 | EditorControl 内に単一エントリ「last logical line」segs キャッシュを追加 |
| F3 経路 | `SearchController` に `IAnnouncer` を注入し `_dialog?.RaiseNotification` 依存を撤去 |
| Event 統一 | `DocumentManager` の全 event を `EventHandler` / `EventHandler<T>` に統一 |
| BOM UI | `EncodingCatalog.SaveAsSelectableEncodings`(新規)で UTF-8 (BOM) / UTF-8 (BOM なし) を別エントリに展開し、`SaveAsDialog` が (CodePage, HasBom) 対で返す |

## 3. 各項目の詳細設計

### 3.1 Minor-4: `VisualSegments.FindContaining` の抽出

**新規: `src/yEdit.Core/Layout/VisualSegments.cs`**

```csharp
namespace yEdit.Core.Layout;

/// <summary>視覚セグメント列に対する共通照会。LineLayout.Wrap の結果を消費する側で共有する。</summary>
public static class VisualSegments
{
    /// <summary>offsetInLine を含む視覚セグメントの (index, segment) を返す。
    /// 行末位置(=最終 segEnd)は最終セグメント扱い。
    /// 空 segs は非対応=呼び出し側で LineLayout.Wrap の空入力契約[(0,0)]を保証する。</summary>
    public static (int Index, WrapSegment Segment) FindContaining(
        IReadOnlyList<WrapSegment> segs, int offsetInLine)
    {
        for (int i = 0; i < segs.Count; i++)
        {
            int segEnd = segs[i].OffsetInLine + segs[i].Length;
            if (offsetInLine < segEnd || i == segs.Count - 1) return (i, segs[i]);
        }
        // segs.Count == 0 の場合のみ到達=呼び出し側契約違反(LineLayout.Wrap は空入力でも [(0,0)] を返す)。
        throw new ArgumentException("segs is empty", nameof(segs));
    }
}
```

**置換対象 3 経路**:

1. **`src/yEdit.Editor/EditorControl.cs` `TryFindVisualSegmentCore`** (現行 lines 3217-3232):
   末尾ループを `var (_, seg) = VisualSegments.FindContaining(segs, offsetInLine); return seg;` に。

2. **`src/yEdit.Core/Editing/VerticalNavigation.cs` `FindSegIndex`** (現行 lines 91-100):
   `return VisualSegments.FindContaining(segs, caretInLine).Index;` に。

3. **`src/yEdit.Core/Editing/NavigationCommands.cs`** の `MoveHomeSmart` オーバーロード
   (現行 line 109 付近の Wrap 直後 seg 選択):
   セグメント index or 本体が要る箇所を FindContaining に寄せる。

**テスト(TDD)**:
`tests/yEdit.Core.Tests/Layout/VisualSegmentsTests.cs`(新規)

| ケース | 入力 segs | 入力 offset | 期待 |
|---|---|---|---|
| 単一 seg 内部 | [(0,10)] | 5 | (0, (0,10)) |
| 単一 seg 末尾 | [(0,10)] | 10 | (0, (0,10)) |
| 2 seg 境界(前 seg 内) | [(0,5),(5,5)] | 4 | (0, (0,5)) |
| 2 seg 境界(次 seg 先頭) | [(0,5),(5,5)] | 5 | (1, (5,5)) |
| 2 seg 境界(次 seg 内) | [(0,5),(5,5)] | 8 | (1, (5,5)) |
| 最終 seg 末尾 | [(0,5),(5,5)] | 10 | (1, (5,5)) |
| 空 segs | [] | 0 | ArgumentException |

**リスク**: 3 経路の意味的一致は行末 offset 扱いのみ違いうる=既存挙動をテストで凍結してから移す。

### 3.2 Minor-5: 論理行 segs キャッシュ

**変更対象**: `src/yEdit.Editor/EditorControl.cs`

追加フィールド(既存 `_bufferSnapshot` 近傍・UI スレッド専用):
```csharp
// P8 Minor-5: SR の Line 単位連続読み(LineStartOf/LineEndNoBreakOf/LineEnd)で
// 同一 (snap, logicalLine) が繰り返されるため単一エントリキャッシュ。
// UI スレッド上でのみ更新される(TryFindVisualSegmentCore は Invoke マーシャリング後)。
private (TextSnapshot Snap, int Line, int Wrap, IReadOnlyList<WrapSegment> Segs, string LineText)? _lastLineSegs;
```

`TryFindVisualSegmentCore` は次のように改修する:

```csharp
private WrapSegment? TryFindVisualSegmentCore(TextSnapshot snap, int line, int offsetInLine, int wrap)
{
    var metrics = _metrics;
    IReadOnlyList<WrapSegment> segs;
    string lineText;

    if (_lastLineSegs is { } c &&
        ReferenceEquals(c.Snap, snap) && c.Line == line && c.Wrap == wrap)
    {
        segs = c.Segs;
        lineText = c.LineText;
    }
    else
    {
        int ls = snap.GetLineStart(line);
        int le = snap.GetLineEnd(line, includeBreak: false);
        if (ls == le) return null;
        lineText = snap.GetText(ls, le - ls);
        int maxWidthPx = wrap * metrics.MeasureRun("0".AsSpan());
        segs = LineLayout.Wrap(lineText.AsSpan(), maxWidthPx, metrics);
        _lastLineSegs = (snap, line, wrap, segs, lineText);
    }

    var (_, seg) = VisualSegments.FindContaining(segs, offsetInLine);
    return seg;
}
```

**無効化ポイント**(既存メソッド末尾で `_lastLineSegs = null` を追加):
- `AfterEdit()` — 本文/スナップショット変化
- `SetSource()` — 全差し替え
- `ApplyAppearance(AppSettings)` — フォント/メトリクス変化
- `WrapColumns` setter — `wrap` 値変化(既存 setter 内で `_lastLineSegs = null;`)

**スレッド安全性**: `TryFindVisualSegment` は RPC 経路では `Control.Invoke` でマーシャリングするため
キャッシュ更新は UI スレッドに閉じる。無効化ポイントも UI スレッドで発火。したがって lock 不要。

**テスト**:
- 挙動不変性: 既存 UIA v2 の TextUnit.Line テストがそのまま通ること(=回帰なし)
- キャッシュヒット観測: internal テストフック `TestHook_LastLineSegsHit`(long カウンタ) を追加し、
  `LineStartOf` → `LineEndNoBreakOf` → `LineEnd` 3 連続同 offset で hit=2(初回 miss+2 hits)を検証
  (Editor.Tests に追加)

**性能効果**(概算): 100 行文書で SR が 1 行を 3 連続読み → 従来 3× (string alloc + List<WrapSegment>)、
キャッシュ後 1× のみ。GC Gen0 割当 66% 削減。

### 3.3 F3-after-Hide 通知経路

**問題**: G-2(検索モード「次を検索」後に自動 Hide)により、`SearchController.Announce` が Hide 済み
`_dialog` を経由する。実発声は `FindReplaceDialog._announcer.Say` = MainForm 共有 `_announceLabel` へ
届くので**音は出る**が、Hidden な UI のメソッドを呼ぶ経路はコード上の副作用が読みづらい。

**変更対象**: `src/yEdit.App/SearchController.cs`

- コンストラクタに `IAnnouncer announcer` を追加
- 現行 `Announce(msg) => _dialog?.RaiseNotification(msg);` を
  `Announce(msg) => _announcer.Say(msg);` に
- `FindReplaceDialog.RaiseNotification` は grep/内部通知用途で維持(GrepController がまだ呼ぶ)

**変更対象**: `src/yEdit.App/MainForm.cs`

`SearchController` ctor に `_announcer` を渡す。

**テスト**: SearchController のロジックが Dialog に依存しないことを凍結する軽量テストを Editor.Tests
に追加(現状 App 層テスト無しのため spike として最小 1 件のみ・本格化は別セッション)。

**注意**: `FindReplaceDialog._announcer`(dialog `_status` Label 束縛)と MainForm
`_announcer`(底部 `_announceLabel` 束縛)は別インスタンス・別 Label。SR 発声は Label
非依存なので不変だが、視覚出力先は dialog `_status` → 底部 `_announceLabel` に移動する。
本 Task で `Announce` を「_announcer.Say + dialog Visible 時 _dialog.SetStatus」の複合契約に
することで、置換モード(dialog 常時 Visible)の視覚出力が dialog 内で維持される。

### 3.4 Event delegate 統一

**変更対象**: `src/yEdit.App/DocumentManager.cs`

| 現行 | 変更後 |
|---|---|
| `event Action<Document>? EditorGotFocus` | `event EventHandler<Document>? EditorGotFocus` |
| `event Action<Document>? KeyBasedSwitch` | `event EventHandler<Document>? KeyBasedSwitch` |

発火側の変更:
- `EditorGotFocus?.Invoke(doc)` → `EditorGotFocus?.Invoke(this, doc)`
- `KeyBasedSwitch?.Invoke(d)` → `KeyBasedSwitch?.Invoke(this, d)`

購読側(`MainForm.cs`)の変更:
- `_docs.EditorGotFocus += d => …` → `(_, d) => …`
- `_docs.KeyBasedSwitch += d => …` → `(_, d) => …`

`BeforeActiveChange` は `Action?`(引数無し)のままにする — sender/args とも意味が無いため
`EventHandler` 化は形式重視のコストが上回る。ただしコメントで「引数無しの意図的例外」と明記。

**テスト**: 既存の Editor.Tests + Core.Tests が全緑継続すれば OK(形式変更のみ)。

### 3.5 SaveAs BOM 明示指定 UI

**変更対象 1**: `src/yEdit.Core/Text/EncodingCatalog.cs`

新規プロパティ:
```csharp
/// <summary>SaveAs 用の選択肢。UTF-8 のみ BOM 有無で 2 エントリ展開。他の CodePage はそのまま。</summary>
public readonly record struct SaveAsEncodingOption(int CodePage, bool HasBom, string DisplayName);

public static IReadOnlyList<SaveAsEncodingOption> SaveAsSelectableEncodings { get; } = new[]
{
    new SaveAsEncodingOption(65001, false, "UTF-8 (BOM なし)"),
    new SaveAsEncodingOption(65001, true,  "UTF-8 (BOM)"),
    new SaveAsEncodingOption(932,   false, "Shift_JIS"),
    new SaveAsEncodingOption(51932, false, "EUC-JP"),
};
```

**注意**: 既存の `SelectableEncodings`(EncodingPickDialog=開き直し / SettingsStore=設定検証 /
BasicSettingsTab=既定エンコード)は BOM 概念が意味を持たない場面なので**触らない**。

**変更対象 2**: `src/yEdit.App/SaveAsDialog.cs`

- `EncodingChoices` を `SaveAsSelectableEncodings` に差し替え
- `SelectedCodePage` に加え `bool SelectedHasBom => SaveAsEncodingChoices[_encoding.SelectedIndex].HasBom` を公開
- コンストラクタ引数に `bool currentHasBom` を追加し、既定選択を `(codePage, hasBom)` 一致行に:
  ```csharp
  int encSel = 0;
  for (int i = 0; i < SaveAsEncodingChoices.Count; i++)
  {
      var e = SaveAsEncodingChoices[i];
      _encoding.Items.Add(e.DisplayName);
      if (e.CodePage == currentCodePage && e.HasBom == currentHasBom) encSel = i;
  }
  ```
- Shift_JIS/EUC-JP は `HasBom=false` 固定のエントリしか無いので、UTF-8 以外は「現状の HasBom 値に
  関わらず」BOM なし選択肢のみが提示される(既存 App 層の HasBom 保持=UTF-8 のみ意味を持つ契約と一致)

**変更対象 3**: `src/yEdit.App/FileController.cs`

`SaveAsDialog` の戻りから `SelectedHasBom` を読み `doc.State.HasBom = dlg.SelectedHasBom;` を追加。
現行の `WriteToPath` は既に `hasBom` を渡す設計なので、State 更新だけで書き込みへ伝播する。

**ロールバック**: 既存の C-2 追補で State ロールバックロジックが入っている。BOM 値変更もその
ロールバック対象(既に Encoding とセットで扱われている `State` 全体をスナップショット済み)なので
追加改修不要。

**テスト**:
- `tests/yEdit.Core.Tests/Text/SaveAsSelectableEncodingsTests.cs`(新規):
  - 4 エントリ列挙・順序・UTF-8 のみ 2 種
  - DisplayName にコードページごとの期待文字列
- SaveAsDialog 自体の UI コンストラクタ動作凍結は **§7 スコープ外の App 層テスト基盤に集約**:
  Editor.Tests は yEdit.Editor 参照のみで yEdit.App(SaveAsDialog 所在)を参照できず、
  `tests/yEdit.App.Tests` 新設が前提となるため。呼び出し元(FileController)は 1 箇所しか
  ないため ctor シグネチャ変更はコンパイル時に確実に検出される。

## 4. ブランチ運用とコミット順

**ブランチ**: `feature/p7p8-followups`
**マージ**: `--no-ff` で main へ・push はしない(既存運用に準拠 [[phase-work-git-flow]])

**コミット順**(依存: 4 → 5 → 3 → 1 → 2 の順が最も疎結合):

1. **Event 統一** — 形式変更のみ・単独で全緑
2. **BOM UI** — SaveAsDialog に閉じる純追加
3. **F3 経路** — SearchController の Announcer 注入
4. **Minor-4 抽出** — Core Layout + 3 経路差し替え(TDD)
5. **Minor-5 キャッシュ** — Minor-4 に依存(FindContaining 呼び出しを含む)

各コミットは build 0 warning + 該当テスト緑を確認してから次へ。

## 5. DoD

- **自動**:
  - `dotnet build -c Release` = 0 warning
  - `dotnet test` = 全緑(Core 601 + Editor 226 の現行数から純増のみ・退行なし)
  - `VisualSegments` テスト 7 件全緑
  - `SaveAsSelectableEncodings` テスト全緑
  - Minor-5 キャッシュヒット観測テスト緑
- **手動**(マージ前):
  - SaveAs で UTF-8 (BOM) / UTF-8 (BOM なし) の切替が反映されること(hex エディタで先頭 3 バイト確認)
  - Ctrl+F → 次を検索 → 端到達で「これ以上見つかりません」が発声されること(F3 経路)
  - Ctrl+Tab で SR がタブ名を発声すること(Event 統一の回帰なし)
- **手動SR検証** は本項目のスコープ外(現行タブ切替・SaveAs はチェックリスト GO 判定済み・退行監視のみ)

## 6. リスクと撤退

- **Minor-5 キャッシュ**: 無効化漏れがあると "編集後に古い segs で SR が読む" 状態になる。
  無効化ポイント 4 つ (`AfterEdit`/`SetSource`/`ApplyAppearance`/`WrapColumns` setter) を明示リストで
  管理。追加ケースが判明したら該当メソッド末尾で `_lastLineSegs = null;` するだけ。
- **F3 経路**: 現状 `_dialog.RaiseNotification` 経由でも発声する=撤退時は SearchController の
  `Announce` を元に戻すだけ。
- **BOM UI**: 4 エントリ列挙のみ=撤退時は `SaveAsSelectableEncodings` の追加分を消し、
  SaveAsDialog を `SelectableEncodings` に戻す。
- **Event 統一**: 形式変更のみ・機能変更なし=撤退時は revert。
- **Minor-4 抽出**: 3 経路の挙動は既存テストで凍結してから移す=行末 offset のエッジで齟齬が
  出ればテストで検知。

各コミット単位で個別 revert 可能。

## 7. スコープ外(明示的申し送り)

- **App 層自動テスト基盤**(`tests/yEdit.App.Tests` 新設)= 別セッション。SearchController の
  現状は EditorControl / DocumentManager 依存が強く、モック設計の検討工数が本設計の他項目と
  釣り合わない
- **SaveAs BOM 以外の UI 拡張**(EOL 明示指定 UI・保存推奨スクリプト等)= YAGNI
- **Minor-5 の LRU 化**(隣接行 Line 単位読みへの対応)= 単一エントリで実用上十分と判定
