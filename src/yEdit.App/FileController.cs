using System.Text;
using yEdit.Core.Backup;
using yEdit.Core.Buffers;
using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// ファイル入出力の統括（新規/開く/保存/文字コード指定の開き直し/バックアップ復元/未保存確認/
/// 最近のファイル登録）。文書メタ（Path/Encoding/EOL）と本文の載せ替えはここに集約し、
/// MainForm は配線・メニュー・表示に徹する。ダイアログを出すため UI スレッド前提（他コントローラと同様）。
/// </summary>
public sealed class FileController
{
    private const int MaxRecent = 10;

    private readonly DocumentManager _docs;
    private readonly Form _owner;
    private readonly Func<AppSettings> _settings;   // 設定ダイアログ適用で参照が差し替わるため都度解決する
    private readonly Action _saveSettings;          // 設定の永続化（保存失敗を致命にしない実装は呼び出し側）
    private readonly Action _recentChanged;         // 「最近のファイル」メニューの再構築
    private readonly Action _metaChanged;           // タイトル・ステータスの更新
    private readonly Action<Document> _openedFresh; // 開く系で新規ロード成功した直後（.csv 自動モードの判定は MainForm 側）
    private readonly IUserPrompt _prompt;              // 確認・警告の注入点（テストでは FakePrompt）
    private readonly IFileDialogService _fileDialogs;  // ファイル系ダイアログの注入点（テストでは FakeFileDialogService）
    private int _untitledSeq; // 無題タブの連番（新規作成毎に増加・セッション内で再利用しない）

    public FileController(
        DocumentManager docs, Form owner, Func<AppSettings> settings,
        Action saveSettings, Action recentChanged, Action metaChanged,
        Action<Document> openedFresh, IUserPrompt prompt, IFileDialogService fileDialogs)
    {
        _docs = docs;
        _owner = owner;
        _settings = settings;
        _saveSettings = saveSettings;
        _recentChanged = recentChanged;
        _metaChanged = metaChanged;
        _openedFresh = openedFresh;
        _prompt = prompt;
        _fileDialogs = fileDialogs;
    }

    // ==================== 新規 / 開く ====================

    /// <summary>新しい無題タブを作る（既定の文字コード・改行を設定から適用）。</summary>
    public void NewFile()
    {
        var s = _settings();
        var doc = _docs.CreateNew();
        doc.State.Path = null;
        doc.State.UntitledNumber = ++_untitledSeq; // 「無題 1」「無題 2」…で区別できるように
        doc.State.Encoding = EncodingCatalog.Get(s.DefaultCodePage);
        doc.State.HasBom = false;
        doc.State.LineEnding = (LineEnding)s.DefaultLineEnding;

        doc.Editor.Text = string.Empty;
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        doc.Editor.SetSavePoint();
        _docs.UpdateLabel(doc);
        _metaChanged();
    }

    /// <summary>「開く」ダイアログでファイルを選んで開く。</summary>
    public void OpenFileWithDialog()
    {
        var path = _fileDialogs.PickOpenPath(_owner);
        if (path is null) return;
        TryOpenOrActivate(path);
    }

    /// <summary>
    /// path を開く唯一の経路（「開く」「最近のファイル」「grep ジャンプ」共通）。
    /// 既に開いていればそのタブをアクティブ化（Q4：二重編集の上書き事故防止）し、
    /// 最近のファイルは先頭へ繰上げ。新規に開けなければ作りかけタブを破棄して
    /// 直前のアクティブへ戻し null を返す。
    /// </summary>
    public Document? TryOpenOrActivate(string path, bool suppressAutoCsv = false)
    {
        var existing = _docs.FindByPath(path);
        if (existing is not null) { _docs.Activate(existing); RegisterRecent(path); return existing; }

        var prev = _docs.Active; // 読込失敗時に戻る先（直前のアクティブタブ）
        var doc = _docs.CreateNew();
        if (LoadInto(doc, path, forcedCodePage: null))
        {
            // 開く系（開く/最近）のみ .csv 自動モードの対象。grep ジャンプは選択＋エディタフォーカスを
            // 機能させるため suppressAutoCsv=true で抑止する（設計 2026-07-04）。
            if (!suppressAutoCsv) _openedFresh(doc);
            return doc;
        }
        _docs.TryClose(doc, _ => true); // 読込失敗→作りかけタブを破棄
        if (prev is not null) _docs.Activate(prev); // 直前のアクティブへ戻す
        return null;
    }

    /// <summary>アクティブタブを指定の文字コードで開き直す。Path 未確定なら案内表示して中止。</summary>
    public void ReopenWithEncoding()
    {
        var doc = _docs.Active;
        if (doc is null) return;
        if (doc.State.Path is null)
        {
            _prompt.Info("ファイルを開いてから実行してください。", "yEdit");
            return;
        }
        if (!ConfirmDiscardIfDirty(doc)) return;
        int? picked = _fileDialogs.PickEncoding(_owner, doc.State.Encoding.CodePage);
        if (picked is null) return;
        if (!LoadInto(doc, doc.State.Path, forcedCodePage: picked)) return;
        _openedFresh(doc);   // 開き直しも .csv 自動モードの対象（設計 2026-07-04）
        // CSVモード中の開き直しでは、ダイアログ閉塞時に WinForms がシンクへフォーカスを復元するため明示的に戻す。
        // 自動 CSV モードに入った場合は FocusTarget=シンク、入らなければエディタへ向く。
        doc.FocusTarget.Focus();
    }

    /// <summary>
    /// ファイルを読み込み、本文・文字コード・改行を対象タブへ反映する。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む（開き直し用）。
    /// 成否を返す（失敗は _prompt.Error で通知・握り潰さない）。
    /// </summary>
    private bool LoadInto(Document doc, string path, int? forcedCodePage)
    {
        try
        {
            // P6 Task 10: Stream I/O 経路で TextBuffer に直接読み込む(1GB 級 UTF-8 の OOM 回避)。
            // 従来の TextFileService.Load(=byte 全読み + string 全文化)は選択肢から外し、
            // LoadAsBufferAuto で prefix 検出 → LoadAsBuffer(chunk stream) → LineEnding 検出。
            var loaded = TextFileService.LoadAsBufferAuto(path, forcedCodePage);
            doc.State.Path = path;
            doc.State.Encoding = loaded.Encoding;
            doc.State.HasBom = loaded.HasBom;
            doc.State.LineEnding = loaded.LineEnding;

            // CSV モードは自動判定しない（メニューから手動で有効化する）。
            // 既存タブへロードし直す場合に備え、読取専用とハイライトを解除しておく。
            // ReplaceSource は ReadOnly 時 no-op ではないが、旧 CSV モード状態を確実に解除するため事前に落とす。
            doc.State.CsvMode = false;
            doc.ClearCsvCache(); // 旧本文のパース結果を持ち越さない（開き直し経路のメモリ解放）
            doc.Editor.ReadOnly = false;
            doc.Editor.RaiseUiaSelectionEvents = true; // モードON時に落とした UIA 抑止をここで確実に戻す（開き直し経路）
            doc.Editor.ClearHighlight();

            // P6 Task 10: TextBuffer をそのまま差し込む(string 全文化を回避)。
            // 新規タブ(SetSource 前)/開き直し(_buffer あり)の両方を SetOrReplaceSource が振り分け。
            doc.Editor.SetOrReplaceSource(loaded.Buffer);
            ApplyEol(doc);
            doc.Editor.EmptyUndoBuffer();
            doc.Editor.SetSavePoint();

            _docs.UpdateLabel(doc);
            _metaChanged();

            if (loaded.HadReplacementChar)
            {
                _prompt.Warn(
                    "このファイルには現在の文字コードで表せない文字（置換文字）が含まれています。" +
                    "別の文字コードで開き直してください。",
                    "文字コードの警告");
            }
            RegisterRecent(path); // 開けたファイルを最近のファイルへ
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            // 想定内の入出力エラーのみ握る。NullReference 等のロジックバグは伝播させる。
            _prompt.Error($"開けませんでした: {ex.Message}", "エラー");
            return false;
        }
    }

    // ==================== 保存 ====================

    /// <summary>アクティブタブを上書き保存する（メニュー経由）。</summary>
    public bool Save()
    {
        var doc = _docs.Active;
        return doc is not null && SaveDocument(doc);
    }

    /// <summary>アクティブタブを名前を付けて保存する（メニュー経由）。</summary>
    public bool SaveAs()
    {
        var doc = _docs.Active;
        return doc is not null && SaveAsDocument(doc);
    }

    /// <summary>指定ドキュメントを保存。Path 未確定なら SaveAs にフォールバック。</summary>
    public bool SaveDocument(Document doc)
        => doc.State.Path is null ? SaveAsDocument(doc) : WriteToPath(doc, doc.State.Path);

    /// <summary>指定ドキュメントを名前を付けて保存。成功で State.Path/Encoding/LineEnding とラベルを更新する。</summary>
    private bool SaveAsDocument(Document doc)
    {
        var picked = _fileDialogs.PickSaveAs(_owner,
            new SaveAsRequest(doc.State.Path, doc.State.Encoding.CodePage, doc.State.HasBom, doc.State.LineEnding));
        if (picked is null) return false;
        if (string.IsNullOrWhiteSpace(picked.Path))
        {
            _prompt.Warn("ファイル名を指定してください。", "エラー");
            return false;
        }

        var newEncoding = EncodingCatalog.Get(picked.CodePage);

        // C-2 追補 I-2: 選択エンコードで表せない文字があれば警告して続行/中止を選ばせる。
        // Load 経路の HadReplacementChar 警告と対称。UTF-8(65001) は BMP+astral 全表現可でスキップ。
        if (picked.CodePage != 65001 && !CanEncodeBuffer(doc.Editor.CurrentBuffer, newEncoding))
        {
            if (!_prompt.OkCancel(
                "選択した文字コードで表せない文字が含まれています。'?' として保存されデータが失われます。続行しますか?",
                "文字コードの警告"))
            {
                return false;
            }
        }

        // 新エンコード/改行/BOM を State に反映してから WriteToPath へ(既存 WriteToPath は State を参照する)。
        // C-2 追補 I-1: WriteToPath 失敗時は元の Encoding/LineEnding/HasBom へロールバック
        // (State だけ更新済で Path が旧のままだと後続の Ctrl+S が元ファイルを別エンコードで
        // サイレント上書きする=データ破損)。
        var oldEncoding = doc.State.Encoding;
        var oldLineEnding = doc.State.LineEnding;
        var oldHasBom = doc.State.HasBom;
        doc.State.Encoding = newEncoding;
        doc.State.LineEnding = picked.LineEnding;
        doc.State.HasBom = picked.HasBom;

        if (!WriteToPath(doc, picked.Path))
        {
            doc.State.Encoding = oldEncoding;
            doc.State.LineEnding = oldLineEnding;
            doc.State.HasBom = oldHasBom;
            return false;
        }
        doc.State.Path = picked.Path;
        _docs.UpdateLabel(doc);
        _metaChanged();
        RegisterRecent(picked.Path); // 保存先も最近のファイルへ
        return true;
    }

    /// <summary>指定エンコードでバッファ全文が損失なく符号化できるかを事前判定する(SaveAs のダウングレード警告用)。</summary>
    private static bool CanEncodeBuffer(TextBuffer buffer, Encoding encoding)
    {
        try
        {
            var probeEnc = Encoding.GetEncoding(encoding.CodePage,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            using var reader = buffer.Current.CreateReader();
            char[] buf = new char[8 * 1024];
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                probeEnc.GetByteCount(buf, 0, n);
            }
            return true;
        }
        catch (EncoderFallbackException) { return false; }
    }

    /// <summary>
    /// 改行を State.LineEnding に正規化してから本文を取得し、原子的に保存する。
    /// 例外は _prompt.Error で通知し false を返す。
    /// </summary>
    private bool WriteToPath(Document doc, string path)
    {
        try
        {
            ApplyEol(doc);
            bool wasReadOnly = doc.Editor.ReadOnly;
            if (wasReadOnly) doc.Editor.ReadOnly = false;
            try { doc.Editor.ConvertEols(doc.Editor.EolMode); }
            finally { if (wasReadOnly) doc.Editor.ReadOnly = true; }
            // P6 Task 10: TextBuffer 版 Save に切替(SnapshotText 経由の string 全文化を回避)。
            // CurrentBuffer は SetSource 前でも空 TextBuffer を返す=null チェック不要。
            TextFileService.Save(path, doc.Editor.CurrentBuffer, doc.State.Encoding, doc.State.HasBom);
            doc.Editor.SetSavePoint();
            _docs.UpdateLabel(doc);
            _metaChanged();
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            _prompt.Error($"保存できませんでした: {ex.Message}", "エラー");
            return false;
        }
    }

    // ==================== 確認 / 復元 ====================

    /// <summary>
    /// 変更があれば 保存/破棄/キャンセル を問う。Yes なら保存成否、No なら true、
    /// キャンセルなら false を返す。対象ドキュメントを明示で受ける（終了時の他タブ確認用）。
    /// </summary>
    public bool ConfirmDiscardIfDirty(Document doc)
    {
        if (!doc.Editor.Modified) return true;
        var r = _prompt.YesNoCancel($"{doc.State.DisplayName} の変更を保存しますか？", "yEdit");
        return r switch
        {
            DialogResult.Yes => SaveDocument(doc),
            DialogResult.No => true,
            _ => false,
        };
    }

    /// <summary>バックアップ記録を新タブへ復元する。本文・メタを載せ、保存点は打たず dirty のままにする。</summary>
    public Document RestoreFromBackup(BackupRecord rec)
    {
        var doc = _docs.CreateNew();
        doc.State.Path = rec.OriginalPath;
        // 無題は元の連番を保ち、ダイアログ表示と復元後タブの番号を一致させる。連番カウンタは
        // 既存の最大値以上へ進め、以後の新規無題と衝突しないようにする。
        if (rec.OriginalPath is null)
        {
            int n = rec.UntitledNumber > 0 ? rec.UntitledNumber : ++_untitledSeq;
            if (n > _untitledSeq) _untitledSeq = n;
            doc.State.UntitledNumber = n;
        }
        else
        {
            doc.State.UntitledNumber = 0;
        }
        doc.State.Encoding = EncodingCatalog.Get(rec.CodePage);
        doc.State.HasBom = rec.HasBom;
        doc.State.LineEnding = (LineEnding)rec.LineEndingId;

        // P6 Task 10: BackupRecord.Content は string 保存(Task 12 で Stream 化判断)なので、
        // ここでは TextBuffer.FromString でラップして SetOrReplaceSource に流す(パターン統一)。
        // 復元は fresh Document への初回差し込みなので実質 SetSource 経路になる。
        // レビュー M-5: 旧 Text setter は内部で `value ?? string.Empty` していた=null 耐性の
        // 対称性を戻す(BackupRecord の JSON 破損時に「タブ生成失敗」を避け「空タブ復元」で継続)。
        doc.Editor.SetOrReplaceSource(TextBuffer.FromString(rec.Content ?? string.Empty));
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        // SetSavePoint しない → Modified=true のまま（ユーザーが保存できる）。
        _docs.UpdateLabel(doc);
        _metaChanged();
        return doc;
    }

    // ==================== 内部 ====================

    /// <summary>開いた/保存したファイルを最近のファイルへ登録し、永続化＆メニュー再構築を促す。</summary>
    private void RegisterRecent(string path)
    {
        var s = _settings();
        s.RecentFiles = RecentFilesList.Add(s.RecentFiles, path, MaxRecent);
        _saveSettings();
        _recentChanged();
    }

    /// <summary>doc.State.LineEnding をそのエディタの EOL モードへ反映する。</summary>
    private static void ApplyEol(Document doc)
        => doc.Editor.EolMode = doc.State.LineEnding;
}
