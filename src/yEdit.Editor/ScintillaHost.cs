using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using ScintillaNET;
using yEdit.Accessibility;
using yEdit.Core.Settings;
using WpfRect = System.Windows.Rect;

namespace yEdit.Editor;

/// <summary>
/// Scintilla（desjarlais/Scintilla5.NET）を継承し、WM_GETOBJECT を横取りして
/// 我々の PC-Talker 実証済み UIA プロバイダ層（yEdit.Accessibility）を上乗せするホスト。
///
/// 設計の要：UIA プロバイダのメソッドは UIA の RPC スレッドから呼ばれる。Scintilla の
/// SCI_* 直呼び（DirectMessage）は UI スレッド専有のため、RPC スレッドからは触れない。
/// よって本文・選択・矩形は <b>UI スレッドで更新したスナップショット／キャッシュ</b>から
/// 応答する（§3.4）。本文スナップショットは UTF-16 文字列で保持し、プロバイダ層は
/// UTF-16 オフセット空間のまま（= 自作 WinForms 版で検証した状態と同一・無改変）流用する。
/// Scintilla 内部の UTF-8 バイト位置との変換は本ホストが UI スレッドで担う。
/// </summary>
public sealed class ScintillaHost : Scintilla, IUiaTextHost
{
    // ---- スナップショット（UI スレッドで更新 / RPC スレッドで読む） ----
    private volatile string _snapshot = string.Empty;   // 全文（UTF-16）
    private byte[] _snapBytes = Array.Empty<byte>();     // 同内容の UTF-8 バイト（UI スレッド専用）
    private readonly object _snapSync = new();
    private volatile int _selStart;                      // 選択開始（UTF-16 オフセット）
    private volatile int _selEnd;                        // 選択終了（UTF-16 オフセット）
    private volatile int _caret;                         // キャレット位置（UTF-16 オフセット・選択端と区別）
    private volatile bool _hasFocus;
    private volatile int _controlTypeId = System.Windows.Automation.ControlType.Document.Id;
    private nint _hwnd;

    // ---- 表示折り返し（指定桁・本文不変） ----
    private int _wrapColumns;       // 0 = 無効
    private bool _wrapSizeHooked;   // SizeChanged 二重購読防止

    private readonly object _boundsSync = new();
    private WpfRect _bounds;

    private TextControlProvider? _provider;

    // ---- 診断トグル（デザイナ非使用なのでシリアライズ対象外） ----
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RaiseUiaSelectionEvents { get; set; } = true;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RaiseUiaTextEvents { get; set; } = true;

    /// <summary>
    /// WM_GETOBJECT(UiaRootObjectId) で我々の UIA プロバイダを返すか。
    /// false なら base へ素通し（Scintilla / Win32 既定の a11y のみ）。
    /// NVDA がネイティブ Scintilla 対応を使っているのか、我々の UIA を使っているのかを
    /// 切り分けるための診断スイッチ（--no-uia 起動 or 診断メニュー）。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ServeUiaProvider { get; set; } = true;

    /// <summary>
    /// WM_GETOBJECT(OBJID_CLIENT) で 0 を返し、ウィンドウのネイティブ MSAA を抑制する。
    /// クラス改名後、PC-Talker がネイティブ MSAA（本文を Name に載せた Pane）を誤読して
    /// 改行/空行しか読まない問題への対策。MSAA を消すと PC-Talker が我々の UIA(ブリッジ)を
    /// 使うことを期待する診断スイッチ（--no-msaa or 診断メニュー）。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SuppressClientMsaa { get; set; }

    /// <summary>
    /// ウィンドウクラス名から "Scintilla" を除去（クローン）するか。true=改名(NVDA向け純UIA)、
    /// false=元の "Scintilla" クラス(PC-Talker向け／NVDAネイティブ読みの検証)。
    /// ハンドル生成前（Controls 追加前）に設定すること。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UseRenamedClass { get; set; }

    /// <summary>状態ログ出力（UI スレッドから呼ばれる）。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<string>? Log { get; set; }

    // ==================== SR 適応設定（確定アーキテクチャ） ====================

    /// <summary>
    /// 起動中のスクリーンリーダーに応じて UIA/MSAA の提供可否を確定する（確定アーキテクチャ）。
    /// NVDA 起動中 → 我々は引っ込む（ネイティブ Scintilla に任せる）。それ以外 → UIA 提供。
    /// ハンドル生成前に呼ぶこと（WM_GETOBJECT 前に値を確定させる）。
    /// </summary>
    public void ConfigureForCurrentScreenReader()
    {
        if (ScreenReaders.IsNvdaRunning())
        {
            ServeUiaProvider = false;
            SuppressClientMsaa = true;
        }
        else
        {
            ServeUiaProvider = true;
            SuppressClientMsaa = false;
        }
    }

    // ==================== ウィンドウクラス名から "Scintilla" を除去 ====================
    // NVDA は WindowsForms10.Scintilla.app.0.NNN を "Scintilla" に正規化し、ネイティブ
    // Scintilla オーバーレイを我々の UIA オブジェクトに被せて競合させ無音になる。そこで
    // Scintilla の登録済みクラスを別名でクローンし WinForms にそれをスーパークラス化させて、
    // 正規化後のクラス名を非 "Scintilla"（"yEditTextEdit"）にする。Scintilla は wndproc で
    // 動くのでクラス名の変更は挙動に影響しない。失敗時は元の "Scintilla" に安全フォールバック。

    private const string ClonedClassName = "yEditTextEdit";
    private static bool _clonedClassReady;

    protected override CreateParams CreateParams
    {
        get
        {
            // base.CreateParams が ScintillaNET にネイティブをロードさせ "Scintilla" クラスを
            // 登録させ、cp.ClassName = "Scintilla" を設定する。
            var cp = base.CreateParams;
            if (UseRenamedClass &&
                cp.ClassName != null &&
                cp.ClassName.Equals("Scintilla", StringComparison.OrdinalIgnoreCase) &&
                EnsureClonedClass("Scintilla"))
            {
                cp.ClassName = ClonedClassName;
            }
            return cp;
        }
    }

    private static bool EnsureClonedClass(string source)
    {
        if (_clonedClassReady) return true;

        // ソース("Scintilla")のクラス情報を、登録され得る hInstance 候補から取得。
        var wc = new NativeMethods.WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>() };
        bool got = false;
        foreach (nint hInst in new[]
        {
            NativeMethods.GetModuleHandleW("Scintilla.dll"),
            NativeMethods.GetModuleHandleW(null),
            nint.Zero,
        })
        {
            if (NativeMethods.GetClassInfoExW(hInst, source, ref wc)) { got = true; break; }
        }
        if (!got) return false;

        // EXE の hInstance＋グローバルクラスで登録し、WinForms のスーパークラス検索から
        // 確実に見つかるようにする。wndproc / cbWndExtra 等は Scintilla のものを引き継ぐ。
        wc.hInstance = NativeMethods.GetModuleHandleW(null);
        wc.style |= NativeMethods.CS_GLOBALCLASS;
        wc.lpszClassName = Marshal.StringToHGlobalUni(ClonedClassName); // クラス生存期間ぶん保持（意図的）
        ushort atom = NativeMethods.RegisterClassExW(ref wc);
        if (atom != 0 || Marshal.GetLastWin32Error() == NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
        {
            _clonedClassReady = true;
            return true;
        }
        return false;
    }

    // ==================== 初期化 ====================

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hwnd = Handle;

        // 内部コードを常に UTF-8 に固定（ラッパー既定だが明示）。
        DirectMessage(Sci.SCI_SETCODEPAGE, (nint)Sci.SC_CP_UTF8);

        _provider ??= new TextControlProvider(this);

        UpdateUI += OnUpdateUI;          // 選択／内容更新（SCN_UPDATEUI）
        TextChanged += OnTextChangedEvt; // 本文変更（SCN_MODIFIED 由来）

        RefreshSnapshot();
        RefreshSelection();
        UpdateBoundsCache();
    }

    // ==================== UIA: WM_GETOBJECT 横取り ====================

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_GETOBJECT)
        {
            // WM_GETOBJECT の objid は DWORD。送信元により 64bit LPARAM へ符号拡張される
            // 場合と未拡張(0xFFFFFFF8 等)の場合があるため 32bit 符号付きに正規化する。
            int objid = unchecked((int)m.LParam.ToInt64());
            bool serve = objid == NativeMethods.UiaRootObjectId && ServeUiaProvider;
            // どの a11y クライアントが何を要求したかを採取（NVDA vs PC-Talker の切り分け）。
            UiaDiag.Log($"[WM_GETOBJECT] objid={objid} ({ObjIdName(objid)}) serveUiaProvider={serve} clientsListening={AutomationInteropProvider.ClientsAreListening}");
            if (serve)
            {
                _provider ??= new TextControlProvider(this);
                m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, m.WParam, m.LParam, _provider);
                return;
            }
            if (objid == NativeMethods.OBJID_CLIENT && SuppressClientMsaa)
            {
                // ネイティブ MSAA を返さない（PC-Talker を UIA ブリッジへ誘導する試み）。
                m.Result = nint.Zero;
                return;
            }
        }
        base.WndProc(ref m);
    }

    private static string ObjIdName(int objid) => objid switch
    {
        0 => "OBJID_WINDOW",
        -4 => "OBJID_CLIENT(MSAA)",
        -8 => "OBJID_CARET",
        -16 => "OBJID_NATIVEOM",
        -25 => "UiaRootObjectId(UIA)",
        _ => "other",
    };

    // ==================== スナップショット更新（UI スレッド） ====================

    private void RefreshSnapshot()
    {
        int len = DirectMessage(Sci.SCI_GETLENGTH).ToInt32();
        byte[] bytes;
        if (len <= 0)
        {
            bytes = Array.Empty<byte>();
        }
        else
        {
            nint buf = Marshal.AllocHGlobal(len + 1);
            try
            {
                DirectMessage(Sci.SCI_GETTEXT, (nint)(len + 1), buf);
                bytes = new byte[len];
                Marshal.Copy(buf, bytes, 0, len);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        string s = Encoding.UTF8.GetString(bytes);
        lock (_snapSync) { _snapBytes = bytes; _snapshot = s; }
    }

    private void RefreshSelection()
    {
        int bs = DirectMessage(Sci.SCI_GETSELECTIONSTART).ToInt32();
        int be = DirectMessage(Sci.SCI_GETSELECTIONEND).ToInt32();
        int bc = DirectMessage(Sci.SCI_GETCURRENTPOS).ToInt32();
        _selStart = ByteToUtf16(bs);
        _selEnd = ByteToUtf16(be);
        _caret = ByteToUtf16(bc);
    }

    // Scintilla のバイト位置 ⇔ スナップショット UTF-16 オフセット（UI スレッド専用）。
    // _snapshot は _snapBytes を UTF8 デコードしたものなので両者は整合する。
    private int ByteToUtf16(int bytePos)
    {
        byte[] b = _snapBytes;
        if (bytePos <= 0) return 0;
        if (bytePos >= b.Length) return _snapshot.Length;
        return Encoding.UTF8.GetCharCount(b, 0, bytePos);
    }

    private int Utf16ToByte(int u16)
    {
        string s = _snapshot;
        if (u16 <= 0) return 0;
        if (u16 >= s.Length) return _snapBytes.Length;
        return Encoding.UTF8.GetByteCount(s.AsSpan(0, u16));
    }

    // ==================== イベント配線 ====================

    private void OnUpdateUI(object? sender, UpdateUIEventArgs e)
    {
        if ((e.Change & UpdateChange.Content) != 0) RefreshSnapshot();
        if ((e.Change & (UpdateChange.Selection | UpdateChange.Content)) != 0)
        {
            RefreshSelection();
            if (RaiseUiaSelectionEvents) RaiseUia(TextPatternIdentifiers.TextSelectionChangedEvent);
            LogState("updateui");
        }
    }

    private void OnTextChangedEvt(object? sender, EventArgs e)
    {
        RefreshSnapshot();
        RefreshSelection();
        if (RaiseUiaTextEvents) RaiseUia(TextPatternIdentifiers.TextChangedEvent);
        LogState("textchanged");
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        // システムキャレットは Scintilla が Win32 で自前管理する（§5）。ここでは触らない。
        RefreshSelection(); // 現在のキャレットをキャッシュへ（不在中に変わっていた場合に備える）
        RaiseUia(AutomationElementIdentifiers.AutomationFocusChangedEvent);
        // リフォーカス時は選択が動かないため UpdateUI が出ず、PC-Talker は読み始めるまで
        // 約2秒のポーリング待ちに落ちる。フォーカス獲得時に選択変更イベントを明示発火し、
        // キャレット移動時と同じく即読みを促す。
        if (RaiseUiaSelectionEvents) RaiseUia(TextPatternIdentifiers.TextSelectionChangedEvent);
        LogState("focus");
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateBoundsCache();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        UpdateBoundsCache();
    }

    private void UpdateBoundsCache()
    {
        if (!IsHandleCreated) return;
        var r = RectangleToScreen(ClientRectangle);
        lock (_boundsSync) _bounds = new WpfRect(r.Left, r.Top, r.Width, r.Height);
    }

    // ==================== 診断: ControlType 切替 ====================

    /// <summary>報告する ControlType を切り替え、UIA に property-changed を通知する。</summary>
    public void SetReportedControlType(int controlTypeId)
    {
        int old = _controlTypeId;
        if (old == controlTypeId) return;
        _controlTypeId = controlTypeId;
        if (_provider != null && AutomationInteropProvider.ClientsAreListening)
        {
            try
            {
                AutomationInteropProvider.RaiseAutomationPropertyChangedEvent(
                    _provider,
                    new AutomationPropertyChangedEventArgs(
                        AutomationElementIdentifiers.ControlTypeProperty, old, controlTypeId));
            }
            catch { /* 実験中は握りつぶす */ }
        }
        RaiseUia(AutomationElementIdentifiers.AutomationFocusChangedEvent);
        LogState("ctlType");
    }

    private void RaiseUia(AutomationEvent ev)
    {
        if (_provider == null || !AutomationInteropProvider.ClientsAreListening) return;
        try { AutomationInteropProvider.RaiseAutomationEvent(ev, _provider, new AutomationEventArgs(ev)); }
        catch { /* 実験中は握りつぶす */ }
    }

    private void LogState(string tag)
    {
        int s = Math.Min(_selStart, _selEnd), en = Math.Max(_selStart, _selEnd);
        string ct = _controlTypeId == System.Windows.Automation.ControlType.Edit.Id ? "Edit" : "Document";
        Log?.Invoke(
            $"[{tag}] sel=[{s},{en}] len={_snapshot.Length} bytes={_snapBytes.Length} " +
            $"ctlType={ct} uiaSel={(RaiseUiaSelectionEvents ? 1 : 0)} uiaText={(RaiseUiaTextEvents ? 1 : 0)}");
    }

    private int Clamp16(int v) => v < 0 ? 0 : (v > _snapshot.Length ? _snapshot.Length : v);

    /// <summary>
    /// UTF-16 オフセットがサロゲートペアの途中（低サロゲートの直前）を指す場合、ペア先頭へ寄せる。
    /// 正規表現の「.」等はコードユニット単位でマッチしペアを割り得るため、バイト変換で文字の
    /// 途中を指して選択ズレ・UTF-8 破損になるのを防ぐ。通常のマッチ（コードポイント境界）では無変化。
    /// </summary>
    private int SnapToCodepoint(int u16)
    {
        string s = _snapshot;
        if (u16 > 0 && u16 < s.Length && char.IsLowSurrogate(s[u16]) && char.IsHighSurrogate(s[u16 - 1]))
            return u16 - 1;
        return u16;
    }

    // ==================== 検索・置換ヘルパ（UI スレッド・文字オフセット） ====================
    // Core は UTF-16 文字オフセットで照合する。ここで既存の Utf16ToByte を介し Scintilla の
    // バイト位置へ変換して選択/置換する（ScintillaNET の文字位置 API はサロゲートでズレ得るため不使用）。

    /// <summary>照合対象テキスト（UI スレッドで保持する UTF-16 スナップショット）。</summary>
    public string SnapshotText => _snapshot;

    /// <summary>キャレット位置（UTF-16 文字オフセット）。選択端と区別され、文字情報照会に使う。</summary>
    public int CaretCharOffset => _caret;

    /// <summary>現在の選択範囲（UTF-16 文字オフセット, Start&lt;=End）。</summary>
    public (int Start, int End) GetSelectionCharRange()
    {
        int s = _selStart, e = _selEnd;
        return (Math.Min(s, e), Math.Max(s, e));
    }

    /// <summary>文字オフセット範囲を選択しキャレットを可視化する（選択移動で SR が一致行を読む）。</summary>
    public void SelectCharRange(int start, int length)
    {
        if (!IsHandleCreated || IsDisposed) return; // タブ閉じ等でハンドル破棄後の BeginInvoke を防ぐ
        if (InvokeRequired) { BeginInvoke(new Action(() => SelectCharRange(start, length))); return; }
        int bs = Utf16ToByte(SnapToCodepoint(Clamp16(start)));
        int be = Utf16ToByte(SnapToCodepoint(Clamp16(start + length)));
        DirectMessage(Sci.SCI_SETSEL, (nint)bs, (nint)be);
        DirectMessage(Sci.SCI_SCROLLCARET);
        RefreshSelection();
    }

    /// <summary>文字オフセット範囲を replacement で置換する（SCI_REPLACETARGET = 1 アンドゥ）。</summary>
    public void ReplaceCharRange(int start, int length, string replacement)
    {
        if (!IsHandleCreated || IsDisposed) return; // ハンドル破棄後の BeginInvoke を防ぐ
        if (InvokeRequired) { BeginInvoke(new Action(() => ReplaceCharRange(start, length, replacement))); return; }
        int bs = Utf16ToByte(SnapToCodepoint(Clamp16(start)));
        int be = Utf16ToByte(SnapToCodepoint(Clamp16(start + length)));
        DirectMessage(Sci.SCI_SETTARGETSTART, (nint)bs);
        DirectMessage(Sci.SCI_SETTARGETEND, (nint)be);
        byte[] repl = Encoding.UTF8.GetBytes(replacement);
        nint buf = Marshal.AllocHGlobal(repl.Length + 1);
        try
        {
            if (repl.Length > 0) Marshal.Copy(repl, 0, buf, repl.Length);
            Marshal.WriteByte(buf, repl.Length, 0);
            DirectMessage(Sci.SCI_REPLACETARGET, (nint)repl.Length, buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
        RefreshSnapshot();
        RefreshSelection();
    }

    // ==================== 表示折り返し（指定桁・本文不変・UI スレッド専用） ====================

    /// <summary>
    /// 指定桁数の表示折り返しを適用する（本文不変・UI スレッド専用）。
    /// columns&lt;=0 で無効化。有効時は WrapMode=Char にし、半角1文字幅×桁数を目標幅として
    /// 右マージンで描画幅を制限する。ウィンドウ幅追従のため SizeChanged を購読する。
    /// </summary>
    public void ApplyWrapColumn(int columns)
    {
        if (columns <= 0)
        {
            _wrapColumns = 0;
            WrapMode = WrapMode.None;
            if (IsHandleCreated) DirectMessage(Sci.SCI_SETMARGINRIGHT, (nint)0, (nint)1); // 既定1px へ戻す
            return;
        }

        _wrapColumns = WrapGeometry.ClampColumns(columns);
        WrapMode = WrapMode.Char;
        if (!_wrapSizeHooked)
        {
            SizeChanged += (_, _) => RecomputeWrapMargin();
            _wrapSizeHooked = true;
        }
        RecomputeWrapMargin();
    }

    /// <summary>クライアント幅と桁目標から右マージン(px)を再計算して適用する（UI スレッド）。</summary>
    private void RecomputeWrapMargin()
    {
        if (_wrapColumns <= 0 || !IsHandleCreated) return;

        int halfWidth = MeasureHalfWidthPx();
        if (halfWidth <= 0) return;

        int targetPx = WrapGeometry.TargetWidthPx(_wrapColumns, halfWidth);

        // テキスト領域幅 = クライアント幅 − 左テキストマージン − 左マージン群(0..4)。
        int leftStuff = DirectMessage(Sci.SCI_GETMARGINLEFT).ToInt32();
        for (int m = 0; m < 5; m++)
            leftStuff += DirectMessage(Sci.SCI_GETMARGINWIDTHN, (nint)m).ToInt32();
        int textAreaPx = ClientSize.Width - leftStuff;

        int right = WrapGeometry.RightMargin(textAreaPx, targetPx);
        DirectMessage(Sci.SCI_SETMARGINRIGHT, (nint)0, (nint)right);
    }

    /// <summary>半角1文字（"0"）の描画幅(px)を STYLE_DEFAULT で測る。</summary>
    private int MeasureHalfWidthPx()
    {
        byte[] one = System.Text.Encoding.ASCII.GetBytes("0");
        nint buf = Marshal.AllocHGlobal(one.Length + 1);
        try
        {
            Marshal.Copy(one, 0, buf, one.Length);
            Marshal.WriteByte(buf, one.Length, 0);
            return DirectMessage(Sci.SCI_TEXTWIDTH, (nint)Sci.STYLE_DEFAULT, buf).ToInt32();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ==================== IUiaTextHost（RPC スレッドから呼ばれる） ====================

    string IUiaTextHost.GetText() => _snapshot;

    int IUiaTextHost.TextLength => _snapshot.Length;

    (int Start, int End) IUiaTextHost.GetSelection()
    {
        int s = _selStart, e = _selEnd;
        return (Math.Min(s, e), Math.Max(s, e));
    }

    void IUiaTextHost.SetSelection(int start, int end)
    {
        if (!IsHandleCreated || IsDisposed) return; // ハンドル破棄後の BeginInvoke を防ぐ
        if (InvokeRequired) { BeginInvoke(new Action(() => ((IUiaTextHost)this).SetSelection(start, end))); return; }
        int bs = Utf16ToByte(Clamp16(start));
        int be = Utf16ToByte(Clamp16(end));
        DirectMessage(Sci.SCI_SETSEL, (nint)bs, (nint)be);
        RefreshSelection();
    }

    WpfRect IUiaTextHost.BoundingRectangle { get { lock (_boundsSync) return _bounds; } }

    // §6 で SCI_POINTXFROMPOSITION / SCI_POINTYFROMPOSITION により実装予定。
    // 現状は WinForms 版同様スタブ（PC-Talker は 0 矩形でもテキスト歩きで読めた）。
    double[] IUiaTextHost.GetBoundingRectangles(int start, int end) => Array.Empty<double>();

    nint IUiaTextHost.Handle => _hwnd;

    bool IUiaTextHost.HasFocus => _hasFocus;

    int IUiaTextHost.ControlTypeId => _controlTypeId;

    void IUiaTextHost.SetFocus()
    {
        if (!IsHandleCreated || IsDisposed) return; // ハンドル破棄後の BeginInvoke を防ぐ
        if (InvokeRequired) { BeginInvoke(new Action(() => Focus())); return; }
        Focus();
    }
}
