using System.Threading;
using System.Windows.Forms.Automation;

namespace yEdit.App.Speech;

/// <summary>
/// UIA 通知(RaiseAutomationNotification)で読ませる Announcer。
/// 「視覚表示(label.Text)は無条件・発声は非空メッセージだけ」の契約を担保する。
/// 空メッセージは視覚クリアのみ(発声なし)=SR に空通知を撃たない。
/// UIA 非対応環境では <see cref="Raise"/> の catch で握りつぶし、視覚のみに縮退する。
/// UIA-M-4 (v0.11): 50 ms throttle + trailing timer。連打の Raise を絞り、SR キューの詰まりを防ぐ。
/// ただし dedupe (直前と同一なら skip) は意図的に行わない=同一セルの読み直しは SR の基本操作で
/// 沈黙させてはならない。trailing で最後の 1 件を必ず Raise し「今どこにいるか」を SR に伝え続ける。
/// </summary>
internal class UiaAnnouncer : IAnnouncer
{
    /// <summary>UIA-M-4: 連続 Raise の throttle 窓 (50 ms)。この窓内の 2 度目以降の Say は
    /// Raise を skip し、trailing timer に最終メッセージを bufferする。窓経過後に trailing が
    /// 発火して最後の 1 件だけを Raise する契約。既存 SR キューがフラッシュされる猶予として
    /// 50 ms は NVDA/PC-Talker の実機観測から選んだ小さめの値 (design §PR-G (3) UIA-M-4)。</summary>
    internal static readonly TimeSpan ThrottleWindow = TimeSpan.FromMilliseconds(50);

    protected readonly Label _label;
    private readonly TimeProvider _clock;

    /// <summary>Say/TrailingCallback が触る mutable state (_lastSaidUtc / _pendingMessage / _trailingTimer)
    /// の排他。Say は UI スレッド、TrailingCallback は本番では ThreadPool、テストでは Advance の呼出元
    /// スレッドから走るため、共有 field の可視性を lock で保証する。</summary>
    private readonly object _sync = new();
    private DateTime _lastSaidUtc = DateTime.MinValue;
    private string? _pendingMessage;
    private ITimer? _trailingTimer;

    /// <summary>backward-compat: 既存 <c>new UiaAnnouncer(label)</c> 経路は <paramref name="clock"/>=null
    /// で <see cref="TimeProvider.System"/> にフォールバック=本番挙動は不変。テストは FakeTimeProvider
    /// を渡し throttle 窓と trailing timer を deterministically 駆動する。</summary>
    public UiaAnnouncer(Label label, TimeProvider? clock = null)
    {
        _label = label;
        _clock = clock ?? TimeProvider.System;
    }

    public void Say(string message)
    {
        _label.Text = message ?? ""; // 視覚フィードバックは無条件(晴眼/弱視も第一級)。空はクリア
        if (string.IsNullOrEmpty(message))
            return; // 空は視覚クリアのみ(発声なし)

        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            if (now - _lastSaidUtc < ThrottleWindow)
            {
                // 窓内: Raise を skip し、pending に最終メッセージを保持する (last-write-wins)。
                // 既に trailing timer が armed 済みなら再スケジュールしない=連投で trailing が
                // 永遠に後ろへ滑る現象を避ける (最初に skip した Say の時刻+50 ms で必ず発火)。
                _pendingMessage = message;
                _trailingTimer ??= _clock.CreateTimer(
                    TrailingCallback,
                    null,
                    ThrottleWindow,
                    Timeout.InfiniteTimeSpan
                );
                return;
            }
            // 窓外: 即 Raise。lock 内で timestamp を更新し、Raise 自体は lock 外で行う
            // (RaiseAutomationNotification の I/O を lock で長時間握らないため)。
            _lastSaidUtc = now;
        }
        Raise(message);
    }

    /// <summary>trailing timer の発火ハンドラ。skip 時に buffer した最終メッセージを Raise する。
    /// 本番では ThreadPool スレッドで走るため、実際の Raise は <see cref="InvokeOnUiThread"/> 経由で
    /// UI スレッドに marshal する (RaiseAutomationNotification は UI スレッドで叩く前提)。</summary>
    private void TrailingCallback(object? state)
    {
        string? msg;
        lock (_sync)
        {
            msg = _pendingMessage;
            _pendingMessage = null;
            _trailingTimer?.Dispose();
            _trailingTimer = null;
            // trailing 発火自体が「直近の Raise」と等価。以降の Say は本発火時刻を起点に throttle 判定。
            _lastSaidUtc = _clock.GetUtcNow().UtcDateTime;
        }
        if (msg is null)
            return; // 併走で Say(null/empty) が入り pending が clear されていた場合の防御 (現契約では起きない)
        InvokeOnUiThread(() => Raise(msg));
    }

    /// <summary>trailing callback から Raise を UI スレッドで走らせる seam。
    /// 本番: <see cref="Label.BeginInvoke(Delegate)"/> で marshal (ハンドル未生成/廃棄済みなら no-op)。
    /// テスト: override で同期呼び出しに置き換え、メッセージポンプなしで観測可能にする。</summary>
    protected virtual void InvokeOnUiThread(Action action)
    {
        if (_label.IsDisposed || !_label.IsHandleCreated)
            return; // ハンドル未生成/廃棄済み=UI スレッドに marshal 不能。SR は視覚のみに縮退(安全側)
        try
        {
            _label.BeginInvoke(action);
        }
        catch
        { /* ハンドル破棄との race: 無害に握り潰す (Raise 自体も try/catch で保護済み) */
        }
    }

    /// <summary>確定済み(非空)メッセージを Label の UIA プロバイダから通知として上げる。
    /// 空ガードと視覚表示は <see cref="Say"/> が済ませているため、ここでは message が非空であることが前提。
    /// テストではオーバーライドして発声呼び出しを観測する(発声手段の seam)。</summary>
    protected virtual void Raise(string message)
    {
        try
        {
            _label.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message
            );
        }
        catch
        { /* 通知非対応環境では視覚表示のみ */
        }
    }
}
