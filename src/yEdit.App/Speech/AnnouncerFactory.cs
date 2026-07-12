namespace yEdit.App.Speech;

/// <summary>
/// 呼び出し元 Label に束縛した IAnnouncer を、起動時の PC-Talker 稼働判定に応じて生成する。
/// PC-Talker 稼働中なら PCTKUsr.dll 直叩き（PcTalkerAnnouncer）、それ以外は UIA 通知（UiaAnnouncer）。
/// <para>P7 別エージェント最終レビュー Important-1: 判定は <see cref="PcTalkerSpeech.IsRunning"/>
/// の live 呼び出し(内部の PCTKStatus は毎回問い合わせ)なので、起動後に PC-Talker 起動/停止が
/// 発生すると呼び出しタイミングで型が食い違う可能性がある。MainForm._announcer(起動時 ctor)と
/// 後発の FindReplaceDialog/GrepDialog._announcer(dialog 生成時)で同一手段を強制するため、
/// プロセス寿命の <see cref="Lazy{Boolean}"/> で起動時の判定結果を凍結する。</para>
/// </summary>
internal static class AnnouncerFactory
{
    /// <summary>起動時に一度だけ判定し全プロセス寿命で共有する PC-Talker 稼働フラグ。</summary>
    private static readonly Lazy<bool> s_isPcTalker = new(() => PcTalkerSpeech.IsRunning());

    /// <summary>指定 Label に束縛した IAnnouncer を、起動時に凍結された PC-Talker 稼働判定に応じて生成する。</summary>
    public static IAnnouncer Create(Label label) => s_isPcTalker.Value
        ? new PcTalkerAnnouncer(label)
        : new UiaAnnouncer(label);
}
