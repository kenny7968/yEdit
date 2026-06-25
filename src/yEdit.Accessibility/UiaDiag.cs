namespace yEdit.Accessibility;

/// <summary>
/// UIA プロバイダ呼び出しのトレース用フック（デバッグ計装）。
/// プローブ側が Sink を設定すると、各プロバイダメソッドの呼び出しが記録される。
/// 呼び出しは UIA の RPC スレッドから来るため、Sink 実装はスレッドセーフにすること。
/// </summary>
public static class UiaDiag
{
    public static Action<string> Sink;

    public static void Log(string msg)
    {
        var s = Sink;
        if (s is null) return;
        try { s(msg); } catch { /* トレース失敗は無視 */ }
    }

    public static string Trunc(string s, int max = 40)
    {
        if (s is null) return "<null>";
        s = s.Replace("\r", " ").Replace("\n", "<LF>");
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
