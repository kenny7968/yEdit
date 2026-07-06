using yEdit.Core.Backup;
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
    private int _untitledSeq; // 無題タブの連番（新規作成毎に増加・セッション内で再利用しない）

    public FileController(
        DocumentManager docs, Form owner, Func<AppSettings> settings,
        Action saveSettings, Action recentChanged, Action metaChanged,
        Action<Document> openedFresh)
    {
        _docs = docs;
        _owner = owner;
        _settings = settings;
        _saveSettings = saveSettings;
        _recentChanged = recentChanged;
        _metaChanged = metaChanged;
        _openedFresh = openedFresh;
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
        using var dlg = new OpenFileDialog { Filter = "対応ファイル (*.txt, *.md, *.csv)|*.txt;*.md;*.csv|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(_owner) != DialogResult.OK) return;
        TryOpenOrActivate(dlg.FileName);
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
            MessageBox.Show("ファイルを開いてから実行してください。", "yEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!ConfirmDiscardIfDirty(doc)) return;
        using var dlg = new EncodingPickDialog(doc.State.Encoding.CodePage);
        if (dlg.ShowDialog(_owner) != DialogResult.OK) return;
        if (!LoadInto(doc, doc.State.Path, forcedCodePage: dlg.SelectedCodePage)) return;
        _openedFresh(doc);   // 開き直しも .csv 自動モードの対象（設計 2026-07-04）
        // CSVモード中の開き直しでは、ダイアログ閉塞時に WinForms がシンクへフォーカスを復元するため明示的に戻す。
        // 自動 CSV モードに入った場合は FocusTarget=シンク、入らなければエディタへ向く。
        doc.FocusTarget.Focus();
    }

    /// <summary>
    /// ファイルを読み込み、本文・文字コード・改行を対象タブへ反映する。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む（開き直し用）。
    /// 成否を返す（失敗は MessageBox 表示・握り潰さない）。
    /// </summary>
    private bool LoadInto(Document doc, string path, int? forcedCodePage)
    {
        try
        {
            var loaded = TextFileService.Load(path, forcedCodePage);
            doc.State.Path = path;
            doc.State.Encoding = loaded.Encoding;
            doc.State.HasBom = loaded.HasBom;
            doc.State.LineEnding = loaded.LineEnding;

            // CSV モードは自動判定しない（メニューから手動で有効化する）。
            // 既存タブへロードし直す場合に備え、読取専用とハイライトを解除しておく。
            // Scintilla の Text セッターは読取専用時 no-op のため、Text 代入前に ReadOnly を解除する必要がある。
            doc.State.CsvMode = false;
            doc.ClearCsvCache(); // 旧本文のパース結果を持ち越さない（開き直し経路のメモリ解放）
            doc.Editor.ReadOnly = false;
            doc.Editor.RaiseUiaSelectionEvents = true; // モードON時に落とした UIA 抑止をここで確実に戻す（開き直し経路）
            doc.Editor.ClearHighlight();

            doc.Editor.Text = loaded.Text;
            ApplyEol(doc);
            doc.Editor.EmptyUndoBuffer();
            doc.Editor.SetSavePoint();

            _docs.UpdateLabel(doc);
            _metaChanged();

            if (loaded.HadReplacementChar)
            {
                MessageBox.Show(
                    "このファイルには現在の文字コードで表せない文字（置換文字）が含まれています。" +
                    "別の文字コードで開き直してください。",
                    "文字コードの警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RegisterRecent(path); // 開けたファイルを最近のファイルへ
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            // 想定内の入出力エラーのみ握る。NullReference 等のロジックバグは伝播させる。
            MessageBox.Show($"開けませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    /// <summary>指定ドキュメントを名前を付けて保存。成功で State.Path とラベルを更新する。</summary>
    private bool SaveAsDocument(Document doc)
    {
        using var dlg = new SaveFileDialog { Filter = "テキスト ファイル (*.txt)|*.txt|マークダウン ファイル (*.md)|*.md|CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*" };
        if (doc.State.Path is not null) dlg.FileName = System.IO.Path.GetFileName(doc.State.Path);
        if (dlg.ShowDialog(_owner) != DialogResult.OK) return false;
        if (!WriteToPath(doc, dlg.FileName)) return false;
        doc.State.Path = dlg.FileName;
        _docs.UpdateLabel(doc);
        _metaChanged();
        RegisterRecent(dlg.FileName); // 保存先も最近のファイルへ
        return true;
    }

    /// <summary>
    /// 改行を State.LineEnding に正規化してから本文を取得し、原子的に保存する。
    /// 例外は MessageBox でエラー表示し false を返す。
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
            TextFileService.Save(path, doc.Editor.Text, doc.State.Encoding, doc.State.HasBom);
            doc.Editor.SetSavePoint();
            _docs.UpdateLabel(doc);
            _metaChanged();
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            MessageBox.Show($"保存できませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        var r = MessageBox.Show(
            $"{doc.State.DisplayName} の変更を保存しますか？",
            "yEdit", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
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

        doc.Editor.Text = rec.Content;
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
