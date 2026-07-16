using System.Diagnostics;

namespace yEdit.App;

/// <summary>
/// 既定の trace sink。System.Diagnostics.Trace.TraceWarning に流すのみで side effect なし
/// (本番挙動は Task 1b 前と同じ = 例外を握り潰す + Trace リスナが有効なら診断出力に流れる)。
/// </summary>
public sealed class DebugBackupTraceSink : IBackupTraceSink
{
    public void Warn(string category, string detail, Exception? ex)
    {
        var msg = ex is null
            ? $"[backup:{category}] {detail}"
            : $"[backup:{category}] {detail} :: {ex.GetType().Name}: {ex.Message}";
        Trace.TraceWarning(msg);
    }
}
